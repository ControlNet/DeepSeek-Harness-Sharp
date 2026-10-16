"""Check the built, real terminal TUI in private Unix PTYs; never use the caller's tty."""
import argparse
import errno
import fcntl
import json
import os
from pathlib import Path
import select
import shutil
import signal
import struct
import termios
import time

ROOT = Path(__file__).resolve().parents[1]


def mode(fd):
    flags = termios.tcgetattr(fd)[3]
    return {name: bool(flags & bit) for name, bit in
            (("icanon", termios.ICANON), ("echo", termios.ECHO), ("isig", termios.ISIG))}


def run(args, index):
    home = args.output / f"home-{index}"
    home.mkdir(parents=True, exist_ok=True)
    for name in ("settings.yaml", "profiles"):
        source = args.home_template / name
        if source.is_dir():
            shutil.copytree(source, home / name, dirs_exist_ok=True)
        elif source.is_file():
            shutil.copy2(source, home / name)
    master, slave = os.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 140, 0, 0))
    original = termios.tcgetattr(slave)
    before = mode(slave)
    env = dict(os.environ, DSH_PTY_CHILD="1", DSH_HARNESS_HOME=str(home),
               DSH_HOME=str(home), TERM="xterm-256color")
    command = [str(args.executable), "tui"]
    if args.strace:
        command = ["strace", "-f", "-tt", "-o", str(args.output / f"trace-{index}.txt"),
                   "-e", "trace=ioctl,process", *command]
    status_read, status_write = os.pipe()
    release_read, release_write = os.pipe()
    pid = os.fork()
    if pid == 0:
        os.close(master)
        os.close(status_read)
        os.close(release_write)
        os.setsid()
        fcntl.ioctl(slave, termios.TIOCSCTTY, 0)
        for fd in (0, 1, 2):
            os.dup2(slave, fd)
        if slave > 2:
            os.close(slave)
        # Keep a session owner alive until after the parent inspects restored termios.
        # Darwin revokes the controlling tty when its session leader exits.
        app_pid = os.fork()
        if app_pid == 0:
            os.close(status_write)
            os.close(release_read)
            os.chdir(ROOT)
            os.execvpe(command[0], command, env)
        _, app_status = os.waitpid(app_pid, 0)
        os.write(status_write, struct.pack("i", app_status))
        os.close(status_write)
        os.read(release_read, 1)
        os._exit(0)
    os.close(status_write)
    os.close(release_read)
    output = bytearray()
    timeline = []
    start = time.monotonic()
    raw_seen = False
    exit_sent = False
    status = None
    last = None

    def observe():
        nonlocal last, raw_seen
        current = mode(slave)
        if current != last:
            timeline.append({"seconds": round(time.monotonic() - start, 3), **current})
            last = current
        if not current["icanon"] and not current["echo"]:
            raw_seen = True
        if raw_seen and not exit_sent and (current["icanon"] or current["echo"]):
            raise AssertionError("TUI lost raw input before normal exit")
        ready, _, _ = select.select([master], [], [], 0.005)
        if ready:
            try:
                output.extend(os.read(master, 65536))
            except OSError as error:
                if error.errno != errno.EIO:
                    raise

    def wait_for(predicate, timeout, description):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            observe()
            if predicate():
                return
        raise AssertionError(description)

    try:
        wait_for(lambda: "快捷键".encode() in output, 90, "real TUI did not render its first frame")
        # Exercise multiple background Git refreshes, not just the startup frame.
        for probe in (b"zqx", b"abc", b"123"):
            mark = len(output)
            os.write(master, probe)
            wait_for(lambda: probe in output[mark:], 10, "typed input was not rendered")
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                observe()
            os.write(master, b"\x7f" * len(probe))
            for _ in range(40):
                observe()
        if args.llm_smoke and index == 0:
            mark = len(output)
            os.write(master, b"Spell out in English the number equal to six times seven. Do not use digits.\r")
            wait_for(lambda: b"forty-two" in output[mark:].lower(), 120,
                     "real LLM answer was not rendered in the TUI")
        exit_sent = True
        os.write(master, b"/exit\r")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            observe()
            ready, _, _ = select.select([status_read], [], [], 0)
            if ready:
                status = struct.unpack("i", os.read(status_read, struct.calcsize("i")))[0]
                break
        if status is None:
            raise AssertionError("/exit did not terminate the real TUI")
        if os.waitstatus_to_exitcode(status) != 0:
            raise AssertionError("real TUI exited unsuccessfully")
        after = mode(slave)
        if termios.tcgetattr(slave) != original:
            raise AssertionError("normal exit did not restore original termios")
        if b"\x1b[?1049l" not in output:
            raise AssertionError("normal exit did not restore the main screen")
        return {"run": index, "before": before, "timeline": timeline, "after": after,
                "input_probes": 3, "exit_code": 0, "termios_restored": True,
                "llm_smoke": args.llm_smoke and index == 0}
    except Exception:
        # Only terminal metadata is safe to publish; never print the screen, configuration or exec arguments.
        current = termios.tcgetattr(slave)
        changed = {str(i): [repr(before_value), repr(after_value)]
                   for i, (before_value, after_value) in enumerate(zip(original, current))
                   if before_value != after_value}
        print(json.dumps({"run": index, "failure_timeline": timeline,
                          "termios_differences": changed, "exit_sent": exit_sent}), flush=True)
        if args.strace:
            for line in (args.output / f"trace-{index}.txt").read_text().splitlines():
                if "TCSETS" in line or "exit_group(" in line:
                    print(line, flush=True)
        raise
    finally:
        if status is None:
            os.killpg(pid, signal.SIGKILL)
        else:
            os.write(release_write, b"x")
        os.waitpid(pid, 0)
        os.close(status_read)
        os.close(release_write)
        os.close(master)
        os.close(slave)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path,
                        default=ROOT / "DeepSeek-Harness-Sharp/bin/Debug/net10.0/dsharp")
    parser.add_argument("--home-template", type=Path, default=Path.home() / ".dsh")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/raw/regression")
    parser.add_argument("--runs", type=int, default=8)
    parser.add_argument("--strace", action="store_true")
    parser.add_argument("--llm-smoke", action="store_true",
                        help="also send a real LLM turn in the first PTY (requires configured credentials)")
    args = parser.parse_args()
    args.executable = args.executable.resolve()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    results = []
    for index in range(args.runs):
        result = run(args, index)
        results.append(result)
        print(json.dumps(result), flush=True)
    (args.output / "results.json").write_text(json.dumps(results, indent=2) + "\n")


if __name__ == "__main__":
    main()

import os
from pathlib import Path
import signal
import subprocess
import sys
import time


CAPTURE_TIMEOUT_SECONDS = 10


def stop(_signal, _frame):
    raise SystemExit(0)


signal.signal(signal.SIGTERM, stop)
runner_pid = int(sys.argv[1])
time.sleep(int(sys.argv[2]))
try:
    os.kill(runner_pid, 0)
except ProcessLookupError:
    raise SystemExit(0)

root = Path("plans/验证产物")
root.mkdir(parents=True, exist_ok=True)
print(f"CI_MACOS_STALL_CAPTURE runner={runner_pid}", flush=True)
with (root / "CI-macOS-进程状态.txt").open("w") as output:
    subprocess.run(["ps", "-p", str(runner_pid), "-o", "pid=,ppid=,rss=,comm="],
                   stdout=output, stderr=subprocess.STDOUT, timeout=CAPTURE_TIMEOUT_SECONDS, check=False)
with (root / "CI-macOS-托管栈.txt").open("w") as output:
    capture = subprocess.Popen([".artifacts/ci-diagnostics/dotnet-stack", "report", "-p", str(runner_pid)],
                               stdout=output, stderr=subprocess.STDOUT)
    try:
        capture.wait(timeout=CAPTURE_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        print("CI_MACOS_STACK_CAPTURE_TIMEOUT", flush=True)
    finally:
        if capture.poll() is None:
            capture.kill()
        capture.wait()

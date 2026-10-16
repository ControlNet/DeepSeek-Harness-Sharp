#define _DEFAULT_SOURCE
#define _DARWIN_C_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>
#ifdef __APPLE__
#include <util.h>
#else
#include <pty.h>
#endif

/* OS-only fixture: the managed tests own the scenario and assertions. No CLR runs after fork. */
static int transfer(int fd, void *buffer, size_t count, int writing)
{
    unsigned char *cursor = buffer;
    while (count > 0) {
        ssize_t size = writing ? write(fd, cursor, count) : read(fd, cursor, count);
        if (size < 0 && errno == EINTR) continue;
        if (size <= 0) return -1;
        cursor += size;
        count -= (size_t)size;
    }
    return 0;
}

static int reply(int32_t value)
{
    return transfer(STDOUT_FILENO, &value, sizeof(value), 1);
}

static int mode(const struct termios *value)
{
    return ((value->c_lflag & ICANON) ? 1 : 0)
        | ((value->c_lflag & ECHO) ? 2 : 0)
        | ((value->c_lflag & ISIG) ? 4 : 0);
}

static int differences(const struct termios *before, const struct termios *after)
{
    int result = 0;
    if (before->c_iflag != after->c_iflag) result |= 1;
    if (before->c_oflag != after->c_oflag) result |= 2;
    if (before->c_cflag != after->c_cflag) result |= 4;
    if (before->c_lflag != after->c_lflag) result |= 8;
    if (memcmp(before->c_cc, after->c_cc, sizeof(before->c_cc)) != 0) result |= 16;
    if (before->c_ispeed != after->c_ispeed || cfgetispeed(before) != cfgetispeed(after)) result |= 32;
    if (before->c_ospeed != after->c_ospeed || cfgetospeed(before) != cfgetospeed(after)) result |= 64;
#ifdef __linux__
    if (before->c_line != after->c_line) result |= 128;
#endif
    return result;
}

static void own_session(int master, int slave, int status_read, int status_write,
    int release_read, int release_write, const char *executable, const char *directory)
{
    close(master);
    close(status_read);
    close(release_write);
    if (setsid() < 0 || ioctl(slave, TIOCSCTTY, 0) < 0) _exit(125);
    for (int fd = 0; fd < 3; ++fd)
        if (dup2(slave, fd) < 0) _exit(125);
    if (slave > 2) close(slave);
    pid_t app = fork();
    if (app < 0) _exit(125);
    if (app == 0) {
        close(status_write);
        close(release_read);
        if (chdir(directory) < 0) _exit(125);
        execl(executable, executable, "tui", (char *)NULL);
        _exit(126);
    }
    int status;
    pid_t waited;
    do { waited = waitpid(app, &status, 0); } while (waited < 0 && errno == EINTR);
    if (waited < 0 || transfer(status_write, &status, sizeof(status), 1) < 0) _exit(125);
    close(status_write);
    /* Darwin revokes the tty when the session leader exits. Hold it through the parent's comparison. */
    char release;
    (void)transfer(release_read, &release, 1, 0);
    close(release_read);
    _exit(0);
}

int main(int argc, char **argv)
{
    if (argc != 4) return 125;
    int master = -1, slave = -1, status_pipe[2] = {-1, -1}, release_pipe[2] = {-1, -1};
    pid_t owner = -1;
    int status = 0, exited = 0, result = 0;
    struct termios original = {0};
    struct winsize size = { .ws_row = 40, .ws_col = 140 };
    /* The executable inherits its own private home, never the invoking test host's terminal. */
    if (setenv("DSH_PTY_CHILD", "1", 1) < 0 || setenv("DSH_HOME", argv[3], 1) < 0
        || setenv("DSH_HARNESS_HOME", argv[3], 1) < 0 || setenv("TERM", "xterm-256color", 1) < 0
        || openpty(&master, &slave, NULL, NULL, &size) < 0 || tcgetattr(slave, &original) < 0
        || pipe(status_pipe) < 0 || pipe(release_pipe) < 0) goto failure;
    int descriptors[] = {master, slave, status_pipe[0], status_pipe[1], release_pipe[0], release_pipe[1]};
    for (size_t i = 0; i < sizeof(descriptors) / sizeof(descriptors[0]); ++i)
        if (fcntl(descriptors[i], F_SETFD, FD_CLOEXEC) < 0) goto failure;
    char main_screen[] = "DSH-RAW-MAIN-SCREEN\r\n";
    if (transfer(slave, main_screen, sizeof(main_screen) - 1, 1) < 0) goto failure;
    owner = fork();
    if (owner < 0) goto failure;
    if (owner == 0)
        own_session(master, slave, status_pipe[0], status_pipe[1], release_pipe[0], release_pipe[1], argv[1], argv[2]);
    close(status_pipe[1]); status_pipe[1] = -1;
    close(release_pipe[0]); release_pipe[0] = -1;
    if (reply(0) < 0 || reply(mode(&original)) < 0) goto cleanup;

    unsigned char command, buffer[65536];
    while (transfer(STDIN_FILENO, &command, 1, 0) == 0) {
        if (command == 'O') {
            struct termios current = {0};
            if (tcgetattr(slave, &current) < 0) goto failure;
            struct pollfd descriptor = { .fd = master, .events = POLLIN };
            int ready = poll(&descriptor, 1, 5);
            if (ready < 0 && errno != EINTR) goto failure;
            ssize_t count = 0;
            if (ready > 0 && (descriptor.revents & POLLIN)) {
                count = read(master, buffer, sizeof(buffer));
                if (count < 0 && (errno == EIO || errno == EINTR)) count = 0;
                if (count < 0) goto failure;
            }
            if (reply(0) < 0 || reply(mode(&current)) < 0 || reply((int32_t)count) < 0
                || transfer(STDOUT_FILENO, buffer, (size_t)count, 1) < 0) break;
        } else if (command == 'W') {
            int32_t count;
            if (transfer(STDIN_FILENO, &count, sizeof(count), 0) < 0) break;
            if (count < 0 || count > (int32_t)sizeof(buffer)) { errno = EINVAL; goto failure; }
            if (transfer(STDIN_FILENO, buffer, (size_t)count, 0) < 0) break;
            if (transfer(master, buffer, (size_t)count, 1) < 0) goto failure;
            if (reply(0) < 0) break;
        } else if (command == 'R') {
            int32_t rows, columns;
            if (transfer(STDIN_FILENO, &rows, sizeof(rows), 0) < 0
                || transfer(STDIN_FILENO, &columns, sizeof(columns), 0) < 0) break;
            size.ws_row = (unsigned short)rows;
            size.ws_col = (unsigned short)columns;
            if (ioctl(slave, TIOCSWINSZ, &size) < 0) goto failure;
            struct winsize actual;
            if (ioctl(slave, TIOCGWINSZ, &actual) < 0) goto failure;
            if (reply(0) < 0 || reply(actual.ws_row) < 0 || reply(actual.ws_col) < 0) break;
        } else if (command == 'S') {
            if (!exited) {
                struct pollfd descriptor = { .fd = status_pipe[0], .events = POLLIN };
                int ready = poll(&descriptor, 1, 0);
                if (ready < 0 && errno != EINTR) goto failure;
                if (ready > 0) {
                    if (transfer(status_pipe[0], &status, sizeof(status), 0) < 0) { errno = ECHILD; goto failure; }
                    exited = 1;
                }
            }
            int code = !exited ? -1 : WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
            if (reply(0) < 0 || reply(code) < 0) break;
        } else if (command == 'V') {
            struct termios current = {0};
            if (tcgetattr(slave, &current) < 0) goto failure;
            int pending = (current.c_lflag & PENDIN) != 0;
#ifdef __APPLE__
            /* XNU sets PENDIN on raw -> canonical. FIONREAD invokes ttypend without consuming input. */
            if (pending) {
                int available;
                if (ioctl(slave, FIONREAD, &available) < 0 || tcgetattr(slave, &current) < 0) goto failure;
            }
#endif
            if (reply(0) < 0 || reply(differences(&original, &current)) < 0 || reply(pending) < 0) break;
        } else {
            errno = EINVAL;
            goto failure;
        }
    }
    goto cleanup;

failure:
    result = 1;
    (void)reply(errno ? errno : EIO);
cleanup:
    if (owner > 0) {
        if (exited) {
            char release = 'x';
            (void)transfer(release_pipe[1], &release, 1, 1);
        } else {
            (void)kill(-owner, SIGKILL);
            (void)kill(owner, SIGKILL);
        }
        while (waitpid(owner, NULL, 0) < 0 && errno == EINTR) { }
    }
    if (master >= 0) close(master);
    if (slave >= 0) close(slave);
    for (int i = 0; i < 2; ++i) {
        if (status_pipe[i] >= 0) close(status_pipe[i]);
        if (release_pipe[i] >= 0) close(release_pipe[i]);
    }
    return result;
}

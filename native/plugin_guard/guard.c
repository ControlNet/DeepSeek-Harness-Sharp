#include "guard.h"

#if defined(_WIN32)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* 只吞"确实是插件指令引发、且可继续"的故障; 栈溢出等不可安全恢复的交回系统, 不假装遏制。 */
static int dsh_guard_filter(EXCEPTION_POINTERS* info)
{
    switch (info->ExceptionRecord->ExceptionCode)
    {
    case EXCEPTION_ACCESS_VIOLATION:
    case EXCEPTION_ILLEGAL_INSTRUCTION:
    case EXCEPTION_PRIV_INSTRUCTION:
    case EXCEPTION_INT_DIVIDE_BY_ZERO:
    case EXCEPTION_FLT_DIVIDE_BY_ZERO:
    case EXCEPTION_IN_PAGE_ERROR:
        return EXCEPTION_EXECUTE_HANDLER;
    default:
        return EXCEPTION_CONTINUE_SEARCH;
    }
}

int dsh_guard_invoke_tool(dsh_invoke_tool_fn fn, void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity, int* result)
{
    __try
    {
        *result = fn(context, handle, input, output, capacity);
        return DSH_GUARD_OK;
    }
    __except (dsh_guard_filter(GetExceptionInformation()))
    {
        return DSH_GUARD_FAULTED;
    }
}

int dsh_guard_call_void_int(dsh_void_int_fn fn, void* context, int* result)
{
    __try
    {
        *result = fn(context);
        return DSH_GUARD_OK;
    }
    __except (dsh_guard_filter(GetExceptionInformation()))
    {
        return DSH_GUARD_FAULTED;
    }
}

int dsh_guard_call_void(dsh_void_fn fn, void* context)
{
    __try
    {
        fn(context);
        return DSH_GUARD_OK;
    }
    __except (dsh_guard_filter(GetExceptionInformation()))
    {
        return DSH_GUARD_FAULTED;
    }
}

void* dsh_guard_suspend(void)
{
    return 0;
}

void dsh_guard_resume(void* saved)
{
    (void)saved;
}

#else

#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <string.h>

#if defined(__linux__)
/* 处理器里首次读动态 TLS 可能经 __tls_get_addr 分配内存, 不是异步信号安全的; initial-exec 走静态 TLS。 */
#define DSH_GUARD_TLS __thread __attribute__((tls_model("initial-exec")))
#else
#define DSH_GUARD_TLS __thread
#endif

#define DSH_GUARD_SIGNAL_COUNT 4

/* 每线程一个跳回点: 只有"正处在受护调用里"的线程才被信号处理器接住。 */
static DSH_GUARD_TLS sigjmp_buf* dsh_guard_jump;
static struct sigaction dsh_guard_previous[DSH_GUARD_SIGNAL_COUNT];
static const int dsh_guard_signals[DSH_GUARD_SIGNAL_COUNT] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE };
static pthread_once_t dsh_guard_once = PTHREAD_ONCE_INIT;

static const struct sigaction* dsh_guard_previous_for(int number)
{
    for (int index = 0; index < DSH_GUARD_SIGNAL_COUNT; index++)
    {
        if (dsh_guard_signals[index] == number)
            return &dsh_guard_previous[index];
    }
    return 0;
}

/*
 * 不属任何受护调用的故障交还安装前的处理器(CoreCLR 据此把托管空引用变成 NullReferenceException)。
 * 之前是默认处置时恢复默认并返回: 故障指令重新执行即按默认终止; kill 发来的信号则重放一次。
 */
static void dsh_guard_forward(int number, siginfo_t* info, void* context)
{
    const struct sigaction* previous = dsh_guard_previous_for(number);
    if (previous != 0 && (previous->sa_flags & SA_SIGINFO) != 0 && previous->sa_sigaction != 0)
    {
        previous->sa_sigaction(number, info, context);
        return;
    }
    if (previous != 0 && previous->sa_handler != SIG_DFL && previous->sa_handler != SIG_IGN)
    {
        previous->sa_handler(number);
        return;
    }
    signal(number, SIG_DFL);
    if (info->si_code <= 0)
        raise(number);
}

static void dsh_guard_handler(int number, siginfo_t* info, void* context)
{
    if (dsh_guard_jump != 0 && info->si_code > 0)
        siglongjmp(*dsh_guard_jump, 1);
    dsh_guard_forward(number, info, context);
}

/* 只装一次且先取旧处理器: 叠在运行时的处理器之上, 并在其备用信号栈上运行(栈溢出时仍可回链)。 */
static void dsh_guard_install_once(void)
{
    for (int index = 0; index < DSH_GUARD_SIGNAL_COUNT; index++)
    {
        sigaction(dsh_guard_signals[index], 0, &dsh_guard_previous[index]);
        struct sigaction action;
        memset(&action, 0, sizeof(action));
        action.sa_sigaction = dsh_guard_handler;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        action.sa_mask = dsh_guard_previous[index].sa_mask;
        sigaction(dsh_guard_signals[index], &action, 0);
    }
}

static void dsh_guard_install(void)
{
    pthread_once(&dsh_guard_once, dsh_guard_install_once);
}

void* dsh_guard_suspend(void)
{
    sigjmp_buf* saved = dsh_guard_jump;
    dsh_guard_jump = 0;
    return saved;
}

void dsh_guard_resume(void* saved)
{
    dsh_guard_jump = (sigjmp_buf*)saved;
}

int dsh_guard_invoke_tool(dsh_invoke_tool_fn fn, void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity, int* result)
{
    dsh_guard_install();
    sigjmp_buf* outer = dsh_guard_jump;
    sigjmp_buf jump;
    if (sigsetjmp(jump, 1) != 0)
    {
        dsh_guard_jump = outer;
        return DSH_GUARD_FAULTED;
    }
    dsh_guard_jump = &jump;
    *result = fn(context, handle, input, output, capacity);
    dsh_guard_jump = outer;
    return DSH_GUARD_OK;
}

int dsh_guard_call_void_int(dsh_void_int_fn fn, void* context, int* result)
{
    dsh_guard_install();
    sigjmp_buf* outer = dsh_guard_jump;
    sigjmp_buf jump;
    if (sigsetjmp(jump, 1) != 0)
    {
        dsh_guard_jump = outer;
        return DSH_GUARD_FAULTED;
    }
    dsh_guard_jump = &jump;
    *result = fn(context);
    dsh_guard_jump = outer;
    return DSH_GUARD_OK;
}

int dsh_guard_call_void(dsh_void_fn fn, void* context)
{
    dsh_guard_install();
    sigjmp_buf* outer = dsh_guard_jump;
    sigjmp_buf jump;
    if (sigsetjmp(jump, 1) != 0)
    {
        dsh_guard_jump = outer;
        return DSH_GUARD_FAULTED;
    }
    dsh_guard_jump = &jump;
    fn(context);
    dsh_guard_jump = outer;
    return DSH_GUARD_OK;
}

#endif

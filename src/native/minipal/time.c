// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <assert.h>
#include <minipal/time.h>

#ifdef HOST_WINDOWS

#include <Windows.h>

int64_t minipal_hires_ticks()
{
    LARGE_INTEGER ts;
    QueryPerformanceCounter(&ts);
    return ts.QuadPart;
}

int64_t minipal_hires_tick_frequency()
{
    LARGE_INTEGER ts;
    QueryPerformanceFrequency(&ts);
    return ts.QuadPart;
}

#else // HOST_WINDOWS

#include "minipalconfig.h"

#include <time.h> // nanosleep
#include <sched.h> // sched_yield
#include <errno.h>

inline void YieldProcessor()
{
#if defined(HOST_X86) || defined(HOST_AMD64)
    __asm__ __volatile__(
        "rep\n"
        "nop");
#elif defined(HOST_ARM)
    __asm__ __volatile__( "yield");
#elif defined(HOST_ARM64)
    __asm__ __volatile__(
        "dmb ishst\n"
        "yield"
        );
#elif defined(HOST_LOONGARCH64)
    __asm__ volatile( "dbar 0;  \n");
#elif defined(HOST_RISCV64)
    // TODO-RISCV64-CQ: When Zihintpause is supported, replace with `pause` instruction.
    __asm__ __volatile__(".word 0x0100000f");
#else
    return;
#endif
}

inline void SwitchToThread()
{
    sched_yield();
}

#define tccSecondsToNanoSeconds 1000000000      // 10^9
int64_t minipal_hires_tick_frequency()
{
    return tccSecondsToNanoSeconds;
}

int64_t minipal_hires_ticks()
{
#if HAVE_CLOCK_GETTIME_NSEC_NP
    return (int64_t)clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
#elif HAVE_CLOCK_MONOTONIC
    struct timespec ts;
    int result = clock_gettime(CLOCK_MONOTONIC, &ts);
    if (result != 0)
    {
        assert(!"clock_gettime(CLOCK_MONOTONIC) failed");
        __UNREACHABLE();
    }

    return ((int64_t)(ts.tv_sec) * (int64_t)(tccSecondsToNanoSeconds)) + (int64_t)(ts.tv_nsec);
#else
    #error "The minipal requires either clock_gettime_nsec_np() or clock_gettime(CLOCK_MONOTONIC) to be supported."
#endif
}

#endif // !HOST_WINDOWS

void minipal_microsleep(uint32_t usecs, uint32_t* usecsSinceYield)
{
    int64_t startTicks = minipal_hires_ticks();
    int64_t ticksPerSecond = minipal_hires_tick_frequency();
    int64_t endTicks = startTicks + (usecs * ticksPerSecond) / 1000000;

#ifdef HOST_WINDOWS
    if (usecs > 1000)
    {
        SleepEx(usecs / 1000, FALSE);
        if (usecsSinceYield)
        {
            usecsSinceYield = 0;
        }

        return;
    }
#else
    if (usecs > 10)
    {
        struct timespec requested;
        requested.tv_sec = usecs / tccSecondsToNanoSeconds;
        requested.tv_nsec = (usecs - requested.tv_sec * tccSecondsToNanoSeconds) * tccSecondsToNanoSeconds;

        struct timespec remaining;
        while (nanosleep(&requested, &remaining) == EINTR)
        {
            requested = remaining;
        }

        if (usecsSinceYield)
        {
            usecsSinceYield = 0;
        }

        return;
    }
#endif

    // start with 16 yields and then double up until we hit the limit
    // this way we should not overshoot by more than 2x.
    for (int i = 4; i < 30; i++)
    {
        for (int j = 0; j < (1 << i); j++)
        {
            YieldProcessor();
        }

        int64_t currentTicks = minipal_hires_ticks();
        if (currentTicks > endTicks)
        {
            break;
        }
    }

    if (usecsSinceYield)
    {
        *usecsSinceYield += usecs;

        // make sure our spining is not starving other threads, but not too often,
        // as this can cause a 1-15 msec delay, depending on OS
        if (*usecsSinceYield > 1000)
        {
            SwitchToThread();
            *usecsSinceYield = 0;
        }
    }
}

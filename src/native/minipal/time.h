// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef HAVE_MINIPAL_TIME_H
#define HAVE_MINIPAL_TIME_H

#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif // __cplusplus

    // Returns current count of high resolution monotonically increasing timer ticks
    int64_t minipal_hires_ticks();

    // Returns the frequency of high resolution timer ticks in Hz
    int64_t minipal_hires_tick_frequency();

    // Delays execution of current thread by usec microseconds.
    // 
    // The delay is best-effort and depending on platform could take longer.
    // Some delays, depending on OS and duration, could be implemented via busy waiting.
    //
    // If the containing algorithm calls this function in a loop, without intervening calls
    // that could yield the CPU (i.e. sleep, SwitchToThread,...), pass not-NULL usecsSinceYield.
    // 
    // If not NULL, usecsSinceYield is used to keep track of busy-waiting time and perform
    // CPU-yielding calls when accumulated busy-waiting time is considered too long.
    void minipal_microsleep(uint32_t usecs, uint32_t* usecsSinceYield);

#ifdef __cplusplus
}
#endif // __cplusplus

#endif /* HAVE_MINIPAL_TIME_H */

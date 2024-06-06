// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef HAVE_MINIPAL_TIME_H
#define HAVE_MINIPAL_TIME_H

#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif // __cplusplus

    int64_t minipal_hires_ticks();

    int64_t minipal_hires_tick_frequency();

    int64_t minipal_microseconds();

    void minipal_microsleep(uint32_t usecs, uint32_t* usecsSinceYield);

#ifdef __cplusplus
}
#endif // __cplusplus

#endif /* HAVE_MINIPAL_TIME_H */

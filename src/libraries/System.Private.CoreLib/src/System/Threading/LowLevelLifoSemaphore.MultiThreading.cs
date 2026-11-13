// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Threading;

internal sealed partial class LowLevelLifoSemaphore
{
    private bool WaitSlow(int timeoutMs)
    {
        RuntimeFeature.ThrowIfBlockingIsNotSupported(timeoutMs);

        bool fullSpin = false;
        int spinLimitUsec = Environment.IsSingleProcessor ? 0 : GetSpinLimitUsec(out fullSpin);
        if (spinLimitUsec > 0)
        {
            long spinLimitTicks = Stopwatch.Frequency * spinLimitUsec / 1_000_000;
            long spinStartTick = Stopwatch.GetTimestamp();
            uint iteration = 0;
            int totalSpinWaits = 0;

            // The initial busy-wait is not timed, see SpinSleep0Threshold.
            do
            {
                totalSpinWaits += Backoff.Exponential(iteration++);
                if (totalSpinWaits >= SpinSleep0Threshold)
                {
                    Thread.UninterruptibleSleep0();
                }

                Counts counts = _separated._counts;
                if (counts.SignalCount != 0)
                {
                    Counts newCounts = counts;
                    newCounts.DecrementSignalCount();
                    Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                    if (countsBeforeUpdate == counts)
                    {
                        // we've consumed a signal
                        if (_spinFeedback)
                        {
                            RecordSpinOutcome(hit: true);
                        }

                        t_claimedBySpin = true;
                        return true;
                    }
                }
            }
            while (totalSpinWaits < SpinSleep0Threshold || Stopwatch.GetTimestamp() - spinStartTick < spinLimitTicks);

            if (_spinFeedback && timeoutMs != 0)
            {
                // The outcome is decided when the park ends, see RecordMissedSpinOutcome. A wait with a timeout of 0 returns
                // without parking, and then no wake tells the outcome.
                t_missedSpinUsec = (int)Math.Max(1, ToUnits(Stopwatch.GetTimestamp() - spinStartTick, 1_000_000));
                t_missedSpinWasFull = fullSpin;
            }
        }

        return WaitNoSpin(timeoutMs);
    }
}

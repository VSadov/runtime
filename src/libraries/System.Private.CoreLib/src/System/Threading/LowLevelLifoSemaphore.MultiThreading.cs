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

        int spinLimitScaled = Environment.IsSingleProcessor ? 0 : SpinLimitScaled();
        long spinStartTick = 0;
        if (spinLimitScaled > 0)
        {
            // While the thread spins it is counted as a spinner, so that a signal it can claim does not wake a parked thread.
            // A thread that registers beyond the spinner cap does not spin: it stops being a spinner right away, there is no spin
            // to record, and its park is a trial of one more spinner (see TrialCapStep).
            Counts registeredCounts = _separated._counts.InterlockedIncrementSpinnerCount();
            bool capped = IsBeyondSpinnerCap(registeredCounts);
            long refusalTick = 0;
            if (capped)
            {
                refusalTick = Stopwatch.GetTimestamp();
            }
            else
            {
                spinStartTick = Stopwatch.GetTimestamp();
                long spinLimitTicks = ScaledUsecToTicks(spinLimitScaled);

                // Each iteration spin-waits with the exponential backoff, whose iterations are capped at a few microseconds,
                // and then tries to claim a signal. After the first InitialSpinIterations, an iteration also ends the spin at
                // the deadline and otherwise yields the processor. The iteration after a yield checks the deadline.
                uint iteration = 0;
                while (true)
                {
                    Backoff.Exponential(iteration++);

                    Counts counts = _separated._counts;
                    if (counts.SignalCount != 0)
                    {
                        Counts newCounts = counts;
                        newCounts.DecrementSignalCount();
                        newCounts.DecrementSpinnerCount();
                        Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                        if (countsBeforeUpdate == counts)
                        {
                            // we've consumed a signal
                            return ClaimedBySpin(spinStartTick, spinLimitScaled);
                        }
                    }

                    if (iteration > InitialSpinIterations)
                    {
                        if (Stopwatch.GetTimestamp() - spinStartTick >= spinLimitTicks)
                        {
                            break;
                        }

                        Thread.UninterruptibleSleep0();
                    }
                }
            }

            // Stop being a spinner. In the same update, claim a signal that arrived since the last try (a signal that a spinner
            // could claim did not wake a waiter), or else become a waiter, so there is no signal that would need a wake.
            uint collisionCount = 0;
            while (true)
            {
                Counts counts = _separated._counts;
                Counts newCounts = counts;
                newCounts.DecrementSpinnerCount();
                if (counts.SignalCount != 0)
                {
                    newCounts.DecrementSignalCount();
                }
                else if (timeoutMs != 0)
                {
                    newCounts.IncrementWaiterCount();
                }

                Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                if (countsBeforeUpdate == counts)
                {
                    if (counts.SignalCount != 0)
                    {
                        return capped || ClaimedBySpin(spinStartTick, spinLimitScaled);
                    }

                    if (!capped)
                    {
                        RecordSpinRunOut(spinLimitScaled);
                    }

                    if (timeoutMs == 0)
                    {
                        return false;
                    }

                    return WaitAsWaiter(timeoutMs, spinStartTick, refusalTick);
                }

                Backoff.Exponential(collisionCount++);
            }
        }

        return WaitNoSpin(timeoutMs, spinStartTick);
    }

    private bool ClaimedBySpin(long spinStartTick, int spinLimitScaled)
    {
        long arrivalTicks = Stopwatch.GetTimestamp() - spinStartTick;
        RecordSpinArrival(arrivalTicks);

        // The time spun was wasted if the dispatch that follows finds no work (see RecordClaimOutcome). A spin ends about at its
        // limit, so time beyond the limit is time that the thread was descheduled, not spinning.
        t_claimCapStepScaled = -Math.Max(1, Math.Min(ToScaledUsec(arrivalTicks), spinLimitScaled));
        return true;
    }

    // Converts scaled microseconds to a Stopwatch interval.
    private static long ScaledUsecToTicks(int scaled) =>
        Stopwatch.Frequency * scaled / (1_000_000L << UsecScaleShift);
}

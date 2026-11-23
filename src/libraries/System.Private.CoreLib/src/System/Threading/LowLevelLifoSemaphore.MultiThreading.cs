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
            _separated._counts.InterlockedIncrementSpinnerCount();
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
                        return ClaimedBySpin(spinStartTick);
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
                        return ClaimedBySpin(spinStartTick);
                    }

                    if (timeoutMs == 0)
                    {
                        return false;
                    }

                    return WaitAsWaiter(timeoutMs, spinStartTick);
                }

                Backoff.Exponential(collisionCount++);
            }
        }

        return WaitNoSpin(timeoutMs, spinStartTick);
    }

    private bool ClaimedBySpin(long spinStartTick)
    {
        RecordSpinArrival(Stopwatch.GetTimestamp() - spinStartTick);
        return true;
    }

    // The limit of a spin that starts now, in scaled microseconds: the estimate of a useful spin, at most the cut-off of
    // pointless spinning, and at least MinSpinLimitScaled. Zero when spinning is turned off.
    private int SpinLimitScaled() =>
        _maxSpinLimitScaled == 0 ? 0 : Math.Max(MinSpinLimitScaled, Math.Min(_dynamicSpinLimitScaled, WakeBoundUpperLimitScaled()));

    // Converts scaled microseconds to a Stopwatch interval.
    private static long ScaledUsecToTicks(int scaled) =>
        Stopwatch.Frequency * scaled / (1_000_000L << UsecScaleShift);
}

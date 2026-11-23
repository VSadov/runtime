// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System.Threading;

internal sealed partial class LowLevelLifoSemaphore
{
    private bool WaitSlow(int timeoutMs)
    {
        RuntimeFeature.ThrowIfBlockingIsNotSupported(timeoutMs);

        int spinsRemaining = Environment.IsSingleProcessor ? 0 : _maxSpinCount;
        if (spinsRemaining > 0)
        {
            // While the thread spins it is counted as a spinner, so that a signal it can claim does not wake a parked thread.
            _separated._counts.InterlockedIncrementSpinnerCount();

            uint iteration = 0;
            while (spinsRemaining > 0)
            {
                spinsRemaining -= Backoff.Exponential(iteration++);

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
                        return true;
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
                        return true;
                    }

                    if (timeoutMs == 0)
                    {
                        return false;
                    }

                    return WaitAsWaiter(timeoutMs);
                }

                Backoff.Exponential(collisionCount++);
            }
        }

        return WaitNoSpin(timeoutMs);
    }
}

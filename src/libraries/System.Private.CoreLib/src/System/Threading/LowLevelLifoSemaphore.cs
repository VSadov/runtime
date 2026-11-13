// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Internal;

namespace System.Threading
{
    /// <summary>
    /// A LIFO semaphore.
    /// Waits on this semaphore are uninterruptible.
    /// </summary>
    internal sealed partial class LowLevelLifoSemaphore
    {
        // === The semaphore and its spin ===
        //
        // A waiter that finds no signal spins in WaitSlow before it parks, so that a signal that comes soon is claimed
        // without a wake. After a brief initial spin-wait phase, spinning yields the processor time slice on each iteration
        // in addition to exponential spin-waits, bounded by a microsecond spin limit. Three statistics, each in a section
        // below, shape the spin:
        // 1. the wake latency sets the full limit;
        // 2. the hit rate of spins chooses between the full limit and the minimum limit;
        // 3. the share of spin claims that find no work turns on the spinner cap of the thread pool workers.

        // The highest spin limit, in microseconds, unless UnfairSemaphoreSpinLimit configures another. Deep sleep states of
        // CPU cores are common and often enabled by default. While a context switch itself may take around 10 us, waking a
        // thread that is scheduled to a sleeping core may take 100 us or more. 256 us is as long as the spin may sometimes
        // need to be, and still affordable.
        private const int DefaultHighestSpinLimitUsec = 256;
        private readonly short _highestSpinLimitUsec;

        // The lowest spin limit that the wake latency computes (see DefaultSpinWakeLatencyFactor). Where wakes are fast
        // (under 10 us with the default factor), the wake latency times the factor would give a shorter spin, and a shorter
        // spin there leaves fewer threads spinning between bursts of work: the pool keeps fewer processors busy and
        // completes less work. The minimum limit of the hit-rate feedback (MinSpinLimitUsec) is below it: there the spins
        // have been found to miss.
        private const int LowestComputedSpinLimitUsec = 40;

        // The lower bound of the spin, which is the minimum limit that the spin has while spins miss (statistic 2): a few
        // times the cost of a park and unpark pair of system calls (on the order of 5 us), which a shorter spin cannot pay
        // for. It also keeps the limit above the first few backoff iterations, which together take several microseconds.
        private const int MinSpinLimitUsec = 16;

        // The spin starts with a short busy-wait and then also yields the processor on every iteration, so that a
        // runnable thread is not starved by the spinner. One spin-wait unit of Backoff.Exponential is tens of ns,
        // so 10 units are well under a microsecond: enough to catch a hand-off that is already in flight without a
        // system call, and small compared to the cost of the yield that follows.
        private const int SpinSleep0Threshold = 10;

        // As a matter of self-tuning, we measure and collect the following statistics:

        // === Statistic 1: the wake latency ===
        //
        // The time from issuing an OS wake to the woken thread running: WakeOneCore stamps the blocker node when it issues
        // the wake, and RecordWakeLatency takes the sample when the woken thread returns from the wait in Block. The full
        // spin limit is the moving average times a factor (see DefaultSpinWakeLatencyFactor).
        //
        // Samples under 2 us are left out, the thread did not really park. Samples are clamped at _highestSpinLimitUsec / factor,
        // where the limit reaches its maximum, so that a single outlier, such as a preempted thread, cannot hold the limit
        // at the maximum. The average starts there, so the spin has the configured limit until wakes are observed. Wakes
        // are timed when the factor or the hit-rate feedback needs the time: the hit rate takes the time the wake was
        // issued as the arrival of the item (statistic 2).

        // The spin limit is the measured wake latency times this factor, bounded by [LowestComputedSpinLimitUsec,
        // _highestSpinLimitUsec]. The factor of 0 disables the adaptation and always spins for _highestSpinLimitUsec.
        //
        // Spinning only pays while it is cheaper than parking and waking a thread, so the factor bounds the CPU that is
        // spent to avoid one wake. Spinning for as long as one wake takes is never more than twice as expensive as the
        // best choice, whatever the arrival pattern. The factor is larger than 1 because arrivals are bursty (an item is
        // usually followed by more), so a longer spin catches more of them, and because the measured latency leaves out
        // part of the cost of parking (the park and unpark system calls, cold caches). A factor of 4 reaches the
        // configured limit when wakes take 64 us or more.
        // A larger factor, such as 6, buys lower latency for sparse arrivals at several times the CPU of a spin of
        // LowestComputedSpinLimitUsec, and where wakes are fast it outlasts that spin and costs CPU per operation without
        // more throughput. 4 keeps the gains that come from covering slow wakes, where the spin is several times longer
        // than LowestComputedSpinLimitUsec.
        private const int DefaultSpinWakeLatencyFactor = 4;

        // A new wake latency sample has the weight of 1 / 2^WakeLatencyAverageShift in the moving average.
        // 1/8 follows a lasting change of the latency within about 8-16 wakes, while still smoothing out single
        // samples, which vary several-fold with the idle state of the woken processor.
        private const int WakeLatencyAverageShift = 3;

        private readonly short _spinWakeLatencyFactor;

        // Moving average of the time from issuing an OS wake to the woken thread running. Written without
        // synchronization, a lost update only makes the estimate slightly stale.
        // It can be just a few microseconds, thus we use nanosecond scale.
        private long _wakeLatencyNs;

        // Wakes are timed when the wake latency or the feedback needs the time.
        private readonly bool _recordWakeTimes;

        // === Statistic 2: the hit rate of spins ===
        //
        // Used as a feedback to dial the length of the spin.
        //
        // A spin that misses costs the whole limit, so spinning pays only while most spins catch an item. The semaphore
        // keeps a moving average of the outcomes of spins, the hit rate, and has two levels of spin limit: the full
        // limit, which is the limit the spin would have without the feedback, and the minimum limit while the hit rate
        // is low. The feedback is on by default and UnfairSemaphoreSpinFeedback=0 turns it off.
        //
        // A spin that claims a signal is a hit, and a full spin that runs out is a miss. A spin at the minimum limit that runs
        // out and parks is judged when its thread is woken (RecordMissedSpinOutcome): it was a hit if the wake was issued
        // within the full limit from the start of the spin, that is, if a full spin would have caught the item. Judging a
        // spin against the limit that it had would hold a short limit in place, because the short spin keeps missing items
        // that a longer one would catch.
        //
        // The limit has two levels rather than being scaled by the hit rate. A limit scaled by the rate can settle at a
        // length that catches almost nothing and still costs the CPU of spinning.
        //
        // The arrival of the item is taken from the time the wake was issued. Subtracting the average wake latency from
        // the end of the park would overestimate the gaps between items on hosts where wakes are slow: the latency
        // samples are clamped, so the average stays below the true latency.

        // The hit rate is in fixed point, SpinHitRateOne is every spin a hit. A new outcome has the weight of
        // 1 / 2^SpinHitRateShift. The spin drops to the minimum limit when the rate falls below SpinHitRateLow and
        // returns to the full limit when it rises above SpinHitRateHigh. The gap between the two keeps the spin from
        // switching back and forth when the rate hovers around a single threshold.
        private const int SpinHitRateOne = 1024;
        private const int SpinHitRateShift = 4;
        private const int SpinHitRateLow = SpinHitRateOne / 4;
        private const int SpinHitRateHigh = SpinHitRateOne / 2;

        // The hit rate of spins and whether the spin is at the minimum limit. They are written when spins end, without
        // synchronization, a lost update only makes them slightly stale. They are outside of _separated, so these writes
        // do not touch the cache line of the counts.
        private int _spinHitRate = SpinHitRateOne;
        private int _spinLow;
        private readonly bool _spinFeedback;

        // How long the spin that ran out lasted, until the park that follows ends and the spin is judged. Zero when no spin
        // waits to be judged.
        [ThreadStatic]
        private static int t_missedSpinUsec;

        // Whether the spin that waits to be judged was a full spin.
        [ThreadStatic]
        private static bool t_missedSpinWasFull;

        // The time the wake that ended the last park of the thread was issued. Zero if there was none.
        [ThreadStatic]
        private static long t_lastWakeIssuedTimestamp;

        // === Statistic 3: the spin claims that find no work ===
        //
        // Used as a feedback to dial the number of thrads that are allowed to spin.
        //
        // Of the signals that were claimed by spinning, the share whose first dispatch found no work. WaitSlow marks a claim
        // by spinning, and the worker takes the mark when it starts to dispatch (TakeSpinClaim) and records whether its first
        // dispatch found work (RecordSpinClaimOutcome). When most of the claims find nothing the spinners are mostly waiting
        // for work that other workers hand to each other in turn, and more than a few of them only burn CPU. Then the share
        // turns on the spinner cap (SpinCapActive): an empty dispatch parks without spinning when enough other workers are
        // already spinning (EstimateSpinners, see TooManySpinners in PortableThreadPool.WorkerThread). The share is tracked
        // also when the cap is off, so that the setting changes only the decision.

        // The share is in fixed point, SpinClaimShareOne is every spin claim finding nothing. A claim has the weight of
        // 1 / 2^SpinClaimShareShift, which makes the average follow the last few hundred claims. The share going to
        // SpinClaimShareHigh or above turns the spinner cap on, and it is turned off again at SpinClaimShareLow or below.
        // The levels sit around one half, so that the cap is on only while finding nothing is the usual outcome of a spin
        // claim, and the gap keeps the cap from switching back and forth.
        private const int SpinClaimShareOne = 1 << 16;
        private const int SpinClaimShareShift = 8;
        private const int SpinClaimShareHigh = SpinClaimShareOne * 55 / 100;
        private const int SpinClaimShareLow = SpinClaimShareOne * 45 / 100;

        [StructLayout(LayoutKind.Explicit, Size = 2 * PaddingHelpers.CACHE_LINE_SIZE)]
        private struct CacheLineSeparatedSpinClaims
        {
            /// <safety>The fields are cache-line padded and overlap nothing; they are plain integers, so accessing them cannot forge a managed reference or read out of bounds.</safety>
            [FieldOffset(PaddingHelpers.CACHE_LINE_SIZE)]
            public safe int _emptyShare;

            /// <safety>The fields overlap nothing and are plain integers, so accessing them cannot forge a managed reference or read out of bounds.</safety>
            [FieldOffset(PaddingHelpers.CACHE_LINE_SIZE + sizeof(int))]
            public safe int _capActive;
        }

        // The share of spin claims that found no work and whether the spinner cap is on. Every spin claim writes them, so they
        // have a cache line of their own, away from the counts and from the fields that are only read.
        private CacheLineSeparatedSpinClaims _spinClaims;

        // Set when the thread claims a signal while spinning, taken by the worker when it starts to dispatch.
        [ThreadStatic]
        private static bool t_claimedBySpin;

        // The cooldown roughly serves as detection that the thread did not spend time being blocked.
        // If it woke in under 4 microseconds, it was likely a fast/trivial wake without blocking.
        // We use this threshold to ensure that when we park a thread it does not iimmediately come back.
        private const int DefaultWakeCooldown = 4;
        private readonly short _threadWakeCooldownUsec;

        // Semaphore state.
        private CacheLineSeparatedCounts _separated;

        private readonly Action _onWait;

        // When we need to block threads we use a linked list of per-thread blockers.
        // When we need to wake a worker, we pop the topmost blocker and release it.
        private sealed class LifoBlockerNode
        {
            internal LifoBlockerNode? _next;
            internal LowLevelThreadBlocker _blocker = new LowLevelThreadBlocker();
            internal long _wakeIssuedTimestamp;

            ~LifoBlockerNode()
            {
                _blocker.Dispose();
            }
        }

        [ThreadStatic]
        private static LifoBlockerNode? t_blockerNode;

        private readonly LowLevelLock _blockerStackLock = new LowLevelLock();
        private LifoBlockerNode? _blockerStack;

        // Sometimes due to races we may see nonzero waiter count, but no blockers to wake.
        // That happens if threads that added themselves to waiter count, have not yet blocked themselves.
        // In such case we increment _racingUnblocks and the waiter will simply
        // decrement the counter and return without blocking.
        private int _racingUnblocks;

        // If a _blockerStackLock is locked by other thread, like someone is inserting itself into blocker list,
        // we cannot proceed with a wake, but we do not want to wait while releasing, thus we do it in two-stages:
        // - we register an intent to wake, then
        // - try waking and if _blockerStackLock is locked the waking becomes
        //   a responsibility of the thread that holds the lock.
        // The main goal here is that the threads who release other threads do not get themselves blocked as
        // the releasers are the hot threads that do the actual work (as opposed to threads who are parking/unparking).
        private int _pendingWake;



        // --- Waiting and waking ---

        public LowLevelLifoSemaphore(Action onWait)
        {
            _separated = default;
            _onWait = onWait;

            _highestSpinLimitUsec = AppContextConfigHelper.GetInt16ComPlusOrDotNetConfig(
                "System.Threading.ThreadPool.UnfairSemaphoreSpinLimit",
                "ThreadPool_UnfairSemaphoreSpinLimit",
                DefaultHighestSpinLimitUsec,
                false);

            _spinWakeLatencyFactor = AppContextConfigHelper.GetInt16ComPlusOrDotNetConfig(
                "System.Threading.ThreadPool.UnfairSemaphoreSpinWakeFactor",
                "ThreadPool_UnfairSemaphoreSpinWakeFactor",
                DefaultSpinWakeLatencyFactor,
                false);

            // Start at the configured spin limit until wake latency is observed.
            _wakeLatencyNs = _spinWakeLatencyFactor > 0 ? _highestSpinLimitUsec * 1000L / _spinWakeLatencyFactor : 0;

            _spinFeedback = AppContextConfigHelper.GetBooleanComPlusOrDotNetConfig(
                "System.Threading.ThreadPool.UnfairSemaphoreSpinFeedback",
                "ThreadPool_UnfairSemaphoreSpinFeedback",
                true);

            _recordWakeTimes = _spinWakeLatencyFactor > 0 || _spinFeedback;

            _threadWakeCooldownUsec = AppContextConfigHelper.GetInt16ComPlusOrDotNetConfig(
                "System.Threading.ThreadPool.UnfairSemaphoreWakeCooldown",
                "ThreadPool_UnfairSemaphoreWakeCooldown",
                DefaultWakeCooldown,
                false);
        }

        public bool Wait(int timeoutMs)
        {
            Debug.Assert(timeoutMs >= -1);

            // Try one-shot acquire first
            Counts counts = _separated._counts;
            if (counts.SignalCount != 0)
            {
                Counts newCounts = counts;
                newCounts.DecrementSignalCount();
                Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                if (countsBeforeUpdate == counts)
                {
                    // we've consumed a signal
                    return true;
                }
            }

            return WaitSlow(timeoutMs);
        }

        public bool WaitNoSpin(int timeoutMs)
        {
            if (timeoutMs == 0)
                return false;

            Counts counts = _separated._counts.InterlockedIncrementWaiterCount();

            // If there are pending signals, we may end in a condition that requires
            // waking a waiter.
            // Perhaps the current thread will be such waiter, but we should still
            // go through wait/wake routine (vs. just claiming the signal) as the
            // caller wants to park the thread.
            MaybeWakeWaiter(counts);

            return WaitAsWaiter(timeoutMs);
        }

        public void Signal()
        {
            // Increment signal count. This enables one-shot acquire.
            Counts counts = _separated._counts.InterlockedIncrementSignalCount();
            MaybeWakeWaiter(counts);
        }

        // If we have signals and have waiters, we need to make sure at least one is waking.
        // We wake one waiter at a time. If it finds a signal it will wake another worker, unless other workers consume
        // the additional signals first.

        // It is generally unusual to have > 1 signal. That only happens when the count of desired workers had a forced change.
        // In any case, we would prefer that extra signals be consumed by active workers, but must guarantee that signals
        // are consumed eventually thus we release waiters one by one.
        private static bool HasWaitersToWake(Counts counts) =>
            counts.CountOfWaitersSignaledToWake == 0 &&
            counts.SignalCount > 0 &&
            counts.WaiterCount > 0;

        private void MaybeWakeWaiter(Counts counts)
        {
            if (!HasWaitersToWake(counts))
            {
                // No waiters to wake. This is the most common case.
                return;
            }

            MaybeWakeWaiterSlow(counts);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void MaybeWakeWaiterSlow(Counts counts)
        {
            Debug.Assert(HasWaitersToWake(counts));

            uint collisionCount = 0;
            do
            {
                Counts newCounts = counts;
                newCounts.AddCountOfWaitersSignaledToWake(1);
                Debug.Assert(newCounts.CountOfWaitersSignaledToWake == 1);
                Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                if (countsBeforeUpdate == counts)
                {
                    WakeOne();
                    break;
                }

                if (!HasWaitersToWake(countsBeforeUpdate))
                    break;

                // CAS collision, but still have waiters to wake, try again.
                Backoff.Exponential(collisionCount++);
                counts = _separated._counts;
            }
            while (HasWaitersToWake(counts));
        }

        private bool WaitAsWaiter(int timeoutMs)
        {
            Debug.Assert(timeoutMs > 0 || timeoutMs == -1);

            _onWait();

            while (true)
            {
                long waitStartTick = Stopwatch.GetTimestamp();

                // In the context of this semaphore the purpose of timeoutMs is just to age out
                // workers that have not been woken for very long time.
                // We do not need to reduce the timeout after spurious wakes as that will only result
                // in workers that are woken spuriously to eventually exit and be replaced by new workers.
                t_lastWakeIssuedTimestamp = 0;
                if (!Block(timeoutMs))
                {
                    t_missedSpinUsec = 0;

                    // Unregister the waiter, but do not decrement wake count, the thread did not observe a wake.
                    _separated._counts.InterlockedDecrementWaiterCount();
                    return false;
                }

                if (t_missedSpinUsec != 0)
                {
                    RecordMissedSpinOutcome(waitStartTick);
                }

                // The thread could not obtain work for quite a while. We will require a 4 usec
                // cooldown before reintroducing the thread. The sleep/wake transition typically
                // takes care of the wait, but the blocker has fast wake paths and the underlying
                // OS API may have trivial/spinning wake paths as well and fast wakeups can happen
                // and are hard to avoid completely.
                // So, if a fast wake happened when parking was desired, we hold up the thread a bit
                // before releasing.
                long cooldown = Stopwatch.Frequency * _threadWakeCooldownUsec / 1000000;
                while (Stopwatch.GetTimestamp() - waitStartTick < cooldown)
                {
                    Thread.UninterruptibleSleep0();
                    Thread.SpinWait(1);
                }

                uint collisionCount = 0;
                while (true)
                {
                    Counts counts = _separated._counts;
                    Counts newCounts = counts;

                    Debug.Assert(counts.WaiterCount != 0);

                    // we consumed a wake, decrement the count
                    Debug.Assert(counts.CountOfWaitersSignaledToWake == 1);
                    newCounts.DecrementCountOfWaitersSignaledToWake();

                    // If there is a signal, try claiming it and stop waiting.
                    if (newCounts.SignalCount != 0)
                    {
                        newCounts.DecrementSignalCount();
                        newCounts.DecrementWaiterCount();
                    }

                    Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                    if (countsBeforeUpdate == counts)
                    {
                        if (counts.SignalCount != 0)
                        {
                            // success

                            // If there are remaining signals, wake another waiter to ensure signals are eventually consumed.
                            // In a saturated pool there may be little new semaphore traffic, and we'd otherwise keep
                            // sleeping workers counted as running for too long.
                            MaybeWakeWaiter(newCounts);
                            return true;
                        }

                        // We've consumed a wake, but there was no signal.
                        // The semaphore is unfair and spurious/stolen wakes can happen.
                        // We will have to wait again.
                        break;
                    }

                    // CAS collision, try again.
                    Backoff.Exponential(collisionCount++);
                }
            }
        }

        private bool Block(int timeoutMs)
        {
            Debug.Assert(timeoutMs >= -1);

            LifoBlockerNode? blockerNode = t_blockerNode;
            if (blockerNode == null)
            {
                try
                {
                    t_blockerNode = blockerNode = new LifoBlockerNode();
                }
                catch (OutOfMemoryException)
                {
                    // Treat OOM as a timeout.
                    // The thread will try to exit.
                    return false;
                }
            }

            _blockerStackLock.Acquire();
            if (_racingUnblocks != 0)
            {
                Debug.Assert(_blockerStack == null);
                Debug.Assert(_racingUnblocks > 0);
                _racingUnblocks--;
                blockerNode = null;
            }
            else
            {
                blockerNode._next = _blockerStack;
                _blockerStack = blockerNode;
            }

            _blockerStackLock.Release();

            // LowLevelLock release is a full fence thus ordinary read of _pendingWake is ok
            if (_pendingWake > 0)
                WakeOneCore();

            if (blockerNode != null)
            {
#if TARGET_WINDOWS
                // Disable the priority boost that Windows would normally apply
                // when this thread is unblocked. The semaphore is used to park workers.
                // A transient priority boost on wake provides no benefit here and can
                // result in woken thread preempting already working threads.
                // GetCurrentThread() returns a pseudo-handle (-2) that is valid
                // only on the calling thread and does not need to be closed.
                Interop.Kernel32.SetThreadPriorityBoost(Interop.Kernel32.GetCurrentThread(), bDisablePriorityBoost: true);
#endif

                try
                {
                    while (!blockerNode._blocker.TimedWait(timeoutMs))
                    {
                        if (TryRemove(blockerNode))
                        {
                            return false;
                        }

                        // We timed out, but our waiter is already popped. Someone is waking
                        // our blocker. This is a very rare case.
                        // We can't leave or the wake could be lost, so let's wait again.
                        // The blocker is likely woken already, but give it some extra time,
                        // just so we do not keep coming here again.
                        timeoutMs = 10;
                    }

                    if (_recordWakeTimes)
                    {
                        RecordWakeLatency(blockerNode);
                    }
                }
                finally
                {
#if TARGET_WINDOWS
                    // restore the default.
                    Interop.Kernel32.SetThreadPriorityBoost(Interop.Kernel32.GetCurrentThread(), bDisablePriorityBoost: false);
#endif
                }
            }

            return true;
        }

        private void WakeOne()
        {
            // Use Interlocked. This assignment must happen before trying to acquire the _blockerStackLock
            int origWake = Interlocked.Exchange(ref _pendingWake, 1);
            Debug.Assert(origWake == 0);
            WakeOneCore();
        }

        // Turn any pending wakes into actual thread wakes, but use TryAcquire to acquire the _blockerStackLock.
        // If someone acquires _blockerStackLock, it becomes its responsibility to check for pending wakes after
        // releasing and call here if needed.
        private void WakeOneCore()
        {
            while (true)
            {
                if (!_blockerStackLock.TryAcquire())
                {
                    // The lock holder will pick up _pendingWake on release.
                    // NOTE: both setting _pendingWake and releasing the lock are done via full fence atomic
                    // operations, thus the holder is guaranteed to observe the wake that it is blocking.
                    return;
                }

                if (_pendingWake == 0)
                {
                    _blockerStackLock.Release();

                    // Loop: handle any wakes that arrived while we were holding the lock
                    //       it is highly unlikely, but not impossible.
                    //
                    // LowLevelLock release is a full fence thus ordinary read of _pendingWake is ok
                    if (_pendingWake != 0)
                        continue;

                    // no pending wakes
                    return;
                }

                // We use only one pending wake at a time and this is the only place when we clear it.
                // We are also holding the _blockerStackLock and whoever we are unparking cannot acknowledge
                // the wake while we are holding the lock.
                // Until the wake is acknowledged _pendingWake cannot be changed by any thread except the current.
                // Therefore we can use an ordinary --
                Debug.Assert(_pendingWake == 1);
                _pendingWake--;

                LifoBlockerNode? top = _blockerStack;
                if (top != null)
                {
                    _blockerStack = top._next;
                    top._next = null;
                }
                else
                {
                    _racingUnblocks++;
                    Debug.Assert(_racingUnblocks != ushort.MaxValue);
                }

                // No new wakes can be pended while we are holding the lock for the purpose of
                // clearing an existing pending wake.
                // Thus we do not check _pendingWake after releasing the lock in this case.
                Debug.Assert(_pendingWake == 0);
                _blockerStackLock.Release();

                if (top != null)
                {
                    if (_recordWakeTimes)
                    {
                        top._wakeIssuedTimestamp = Stopwatch.GetTimestamp();
                    }

                    top._blocker.WakeOne();
                }

                return;
            }
        }

        // Used when waiter times out
        private bool TryRemove(LifoBlockerNode node)
        {
            bool removed = false;
            _blockerStackLock.Acquire();

            LifoBlockerNode? current = _blockerStack;
            if (current == node)
            {
                _blockerStack = node._next;
                node._next = null;
                removed = true;
            }
            else
            {
                while (current != null)
                {
                    if (current._next == node)
                    {
                        current._next = node._next;
                        node._next = null;
                        removed = true;
                        break;
                    }

                    current = current._next;
                }
            }

            _blockerStackLock.Release();

            // LowLevelLock release is a full fence thus ordinary read of _pendingWake is ok
            if (_pendingWake > 0)
                WakeOneCore();

            return removed;
        }

        // --- The spin limit ---

        private int GetSpinLimitUsec(out bool fullSpin)
        {
            if (_spinFeedback && Volatile.Read(ref _spinLow) != 0)
            {
                // At the minimum limit the feedback has found that spins miss, and a spin of LowestComputedSpinLimitUsec
                // would only add to their cost.
                fullSpin = false;
                return Math.Min(MinSpinLimitUsec, (int)_highestSpinLimitUsec);
            }

            fullSpin = true;
            return FullSpinLimitUsec();
        }

        // The spin limit without the feedback: the measured wake latency times the factor, or the fixed limit. A limit
        // that is configured below LowestComputedSpinLimitUsec holds.
        private int FullSpinLimitUsec()
        {
            if (_spinWakeLatencyFactor == 0)
            {
                return _highestSpinLimitUsec;
            }

            long usec = _spinWakeLatencyFactor * Volatile.Read(ref _wakeLatencyNs) / 1000;
            return (int)Math.Clamp(usec, Math.Min(LowestComputedSpinLimitUsec, (int)_highestSpinLimitUsec), _highestSpinLimitUsec);
        }

        // Converts a Stopwatch interval to units of 1 / unitsPerSecond, with the interval capped at one second. Every limit
        // that wakes and parks are compared with is far below a second, and the cap keeps the product from overflowing
        // when a wake or a park takes very long, for example while the process is suspended.
        private static long ToUnits(long ticks, long unitsPerSecond) =>
            Math.Min(ticks, Stopwatch.Frequency) * unitsPerSecond / Stopwatch.Frequency;

        // --- Statistic 1: the wake latency ---

        private void RecordWakeLatency(LifoBlockerNode node)
        {
            long issued = node._wakeIssuedTimestamp;
            t_lastWakeIssuedTimestamp = issued;
            if (issued == 0)
            {
                return;
            }

            node._wakeIssuedTimestamp = 0;
            if (_spinWakeLatencyFactor == 0)
            {
                return;
            }

            long ns = ToUnits(Stopwatch.GetTimestamp() - issued, 1_000_000_000);

            // Under 2 us the thread did not really park, for example it woke itself.
            if (ns < 2_000)
            {
                return;
            }

            // The spin limit is at its maximum once the average reaches _highestSpinLimitUsec / factor, so larger samples carry
            // no further information. Clamping keeps a single outlier, such as a preempted thread, from holding the
            // limit at the maximum for dozens of wakes: with a true latency of 10 us, one 4 ms sample would take 42
            // wakes to bring the average back to 12 us (a limit of 48 us, 20% above the steady 40 us), versus 10 when
            // clamped.
            ns = Math.Min(ns, _highestSpinLimitUsec * 1000L / _spinWakeLatencyFactor);

            long average = Volatile.Read(ref _wakeLatencyNs);
            Volatile.Write(ref _wakeLatencyNs, average + ((ns - average) >> WakeLatencyAverageShift));
        }

        // --- Statistic 2: the hit rate of spins ---

        private void RecordSpinOutcome(bool hit)
        {
            int rate = Volatile.Read(ref _spinHitRate);
            int newRate = hit ? rate + ((SpinHitRateOne - rate) >> SpinHitRateShift) : rate - (rate >> SpinHitRateShift);
            if (newRate != rate)
            {
                // The rate stops changing near the ends of its range, then there is nothing to write.
                Volatile.Write(ref _spinHitRate, newRate);
            }

            bool low = Volatile.Read(ref _spinLow) != 0;
            if (!low && newRate < SpinHitRateLow)
            {
                Volatile.Write(ref _spinLow, 1);
            }
            else if (low && newRate > SpinHitRateHigh)
            {
                Volatile.Write(ref _spinLow, 0);
            }
        }

        private void RecordMissedSpinOutcome(long waitStartTick)
        {
            int spunUsec = t_missedSpinUsec;
            t_missedSpinUsec = 0;

            // The arrival is when the wake was issued, measured from the start of the park. No wake time was recorded
            // if the thread did not park (a racing unblock), then there is nothing to learn from this spin.
            long issued = t_lastWakeIssuedTimestamp;
            if (issued == 0)
            {
                return;
            }

            if (t_missedSpinWasFull)
            {
                // A full spin that ran out is a miss: a full spin did not catch the item.
                RecordSpinOutcome(hit: false);
                return;
            }

            long idleTicks = Math.Max(0, issued - waitStartTick);
            long arrivalUsec = spunUsec + ToUnits(idleTicks, 1_000_000);
            RecordSpinOutcome(arrivalUsec <= FullSpinLimitUsec());
        }

        // --- Statistic 3: the spin claims that find no work ---

        // Whether the signal that the calling thread claimed was claimed by spinning. The worker takes it when it starts to
        // dispatch, so the flag does not outlive the claim.
        internal static bool TakeSpinClaim()
        {
            bool claimedBySpin = t_claimedBySpin;
            if (claimedBySpin)
            {
                t_claimedBySpin = false;
            }

            return claimedBySpin;
        }

        // Records whether the first dispatch of a worker that claimed its signal by spinning found no work. A lost update only
        // makes the average slightly stale, so there is no synchronization, and nothing is written when the average does
        // not change.
        internal void RecordSpinClaimOutcome(bool empty)
        {
            int share = Volatile.Read(ref _spinClaims._emptyShare);
            int updated = share + (((empty ? SpinClaimShareOne : 0) - share) >> SpinClaimShareShift);
            if (updated != share)
            {
                Volatile.Write(ref _spinClaims._emptyShare, updated);
            }

            int capActive = Volatile.Read(ref _spinClaims._capActive);
            if (capActive == 0 && updated >= SpinClaimShareHigh)
            {
                Volatile.Write(ref _spinClaims._capActive, 1);
            }
            else if (capActive != 0 && updated <= SpinClaimShareLow)
            {
                Volatile.Write(ref _spinClaims._capActive, 0);
            }
        }

        // Whether most of the recent spin claims found no work, which is when the spinners are capped if the cap is on.
        internal bool SpinCapActive => Volatile.Read(ref _spinClaims._capActive) != 0;

        // Estimates how many workers are spinning in WaitSlow, from the number of existing worker threads and the number
        // of workers processing work. A worker that exists, is not processing work and is not registered as a waiter is
        // spinning, or is on its way between spinning and waiting.
        // The processing count includes a reservation for every unclaimed signal, because MaybeAddWorkingWorker raises it
        // before any worker has claimed the signal. Those reservations are not busy workers, hence the added signal count.
        // The counts are read at different times and a worker that moves between states can be counted twice or not at
        // all, so the result is accurate to about one worker.
        internal int EstimateSpinners(int numExistingThreads, int numProcessingWork)
        {
            Counts counts = _separated._counts;
            return numExistingThreads - numProcessingWork - counts.WaiterCount + counts.SignalCount;
        }

        // === The counts ===

        private struct Counts : IEquatable<Counts>
        {
            private const byte SignalCountShift = 0;
            private const byte WaiterCountShift = 16;
            private const byte CountOfWaitersSignaledToWakeShift = 32;

            private ulong _data;

            private Counts(ulong data) => _data = data;

            private ushort GetUInt16Value(byte shift) => (ushort)(_data >> shift);

            public ushort SignalCount
            {
                get => GetUInt16Value(SignalCountShift);
            }

            public Counts InterlockedIncrementSignalCount()
            {
                var countsAfterUpdate = new Counts(Interlocked.Add(ref _data, 1ul << SignalCountShift));
                Debug.Assert(countsAfterUpdate.SignalCount != ushort.MaxValue); // overflow check
                return countsAfterUpdate;
            }

            public void DecrementSignalCount()
            {
                Debug.Assert(SignalCount != 0);
                _data -= (ulong)1 << SignalCountShift;
            }

            public ushort WaiterCount
            {
                get => GetUInt16Value(WaiterCountShift);
            }

            public void DecrementWaiterCount()
            {
                Debug.Assert(WaiterCount != 0);
                _data -= (ulong)1 << WaiterCountShift;
            }

            public void IncrementWaiterCount()
            {
                _data += (ulong)1 << WaiterCountShift;
                Debug.Assert(WaiterCount != 0);
            }

            public void InterlockedDecrementWaiterCount()
            {
                var countsAfterUpdate = new Counts(Interlocked.Add(ref _data, unchecked((ulong)-1) << WaiterCountShift));
                Debug.Assert(countsAfterUpdate.WaiterCount != ushort.MaxValue); // underflow check
            }

            public Counts InterlockedIncrementWaiterCount()
            {
                var countsAfterUpdate = new Counts(Interlocked.Add(ref _data, unchecked((ulong)1) << WaiterCountShift));
                Debug.Assert(countsAfterUpdate.WaiterCount != ushort.MaxValue); // overflow check
                return countsAfterUpdate;
            }

            public ushort CountOfWaitersSignaledToWake
            {
                get => GetUInt16Value(CountOfWaitersSignaledToWakeShift);
            }

            public void AddCountOfWaitersSignaledToWake(uint value)
            {
                _data += (ulong)value << CountOfWaitersSignaledToWakeShift;
                var countsAfterUpdate = new Counts(_data);
                Debug.Assert(countsAfterUpdate.CountOfWaitersSignaledToWake != ushort.MaxValue); // overflow check
            }

            public void DecrementCountOfWaitersSignaledToWake()
            {
                Debug.Assert(CountOfWaitersSignaledToWake != 0);
                _data -= (ulong)1 << CountOfWaitersSignaledToWakeShift;
            }

            public Counts InterlockedCompareExchange(Counts newCounts, Counts oldCounts) =>
                new Counts(Interlocked.CompareExchange(ref _data, newCounts._data, oldCounts._data));

            public static bool operator ==(Counts lhs, Counts rhs) => lhs.Equals(rhs);
            public static bool operator !=(Counts lhs, Counts rhs) => !lhs.Equals(rhs);

            public override bool Equals([NotNullWhen(true)] object? obj) => obj is Counts other && Equals(other);
            public bool Equals(Counts other) => _data == other._data;
            public override int GetHashCode() => (int)_data + (int)(_data >> 32);
        }

        [StructLayout(LayoutKind.Explicit, Size = 2 * PaddingHelpers.CACHE_LINE_SIZE)]
        private struct CacheLineSeparatedCounts
        {
            /// <safety>The struct's single field is cache-line padded and overlaps nothing; Counts wraps only a ulong, so accessing it cannot forge a managed reference or read out of bounds.</safety>
            [FieldOffset(PaddingHelpers.CACHE_LINE_SIZE)]
            public safe Counts _counts;
        }
    }
}

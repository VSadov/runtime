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
        // The spin is bounded by three limits, each within the one before:
        // 1. The absolute limit, UnfairSemaphoreSpinLimit (DefaultMaxSpinLimitUsec unless configured; 0 turns spinning off).
        //    It is the highest the other two can be.
        // 2. The cut-off of pointless spinning (WakeBoundUpperLimitScaled): SpinUpperLimitWakeFactor times the measured wake
        //    latency, which is the time from issuing an OS wake to the woken thread running, a moving average of the wakes that
        //    the semaphore issues (see RecordWakeLatency). A spin longer than a few wakes costs more than parking and waking
        //    would, so a signal that arrives later is not worth spinning for.
        // 3. The estimate of a useful spin (_dynamicSpinLimitScaled), which is the limit a spin actually has. It follows the
        //    arrival of signals: the time from the start of a spin to the signal that it claimed, or for a spin that ran out and
        //    parked, to the wake that ended the park (see RecordSpinArrival). Each outcome moves the estimate by what it is worth:
        //    an arrival that a longer spin could have caught, later than half of the estimate but before the cut-off, steps it up
        //    by what catching it is worth (the wake latency); an arrival after the cut-off steps it down by the spin that
        //    it wasted, the estimate itself; an earlier arrival leaves it unchanged, since a shorter spin would not have saved
        //    anything. The estimate rises while the late catches pay for the wasted spins, and settles where they break even.
        //
        // The limit of a spin is a deadline that the spin honors. Only its first InitialSpinIterations iterations, which take
        // about as long as the lowest estimate, do not read the clock; after them, the spin checks the deadline after every
        // iteration, so also after every yield of the processor, and ends at most one iteration and one yield past it.
        //
        // The times are in scaled microseconds, microseconds times 2^UsecScaleShift, so that small steps are not lost.
        private const int UsecScaleShift = 3;

        // The lowest estimate of a useful spin, and its starting value. The untimed busy-wait at the start of the spin (see
        // InitialSpinIterations) takes about as long.
        private const int MinSpinLimitUsec = 16;
        private const int MinSpinLimitScaled = MinSpinLimitUsec << UsecScaleShift;

        // The steps of the estimate of a useful spin are 1/2^DynamicSpinLimitStepShift of what an outcome is worth: up by what a
        // late catch is worth, the wake latency it saves; down by the spin that an arrival after the cut-off wasted.
        private const int DynamicSpinLimitStepShift = 4;

        // The cut-off of pointless spinning, in wake latencies.
        private const int SpinUpperLimitWakeFactor = 4;

        // Until a wake is observed, the wake latency is DefaultInitialWakeLatencyUsec, which puts the cut-off at 140 us.
        private const int DefaultInitialWakeLatencyUsec = 35;

        // The absolute limit, in microseconds, unless UnfairSemaphoreSpinLimit configures another (0 turns spinning off).
        // Waking a thread that is scheduled to a core in a deep sleep state may take 100 us or more.
        private const int DefaultMaxSpinLimitUsec = 250;

        // A new wake latency sample has the weight of 1 / 2^WakeLatencyAverageShift in the moving average.
        private const int WakeLatencyAverageShift = 3;

        // The first iterations of the spin are a busy-wait that does not read the clock or yield: with the exponential backoff,
        // 10 iterations spin-wait 255-501 units, about 8-17 us, enough to catch a hand-off that is already in flight. Time that
        // the thread spends descheduled cannot cut them short. Later iterations check the deadline and yield the processor.
        private const int InitialSpinIterations = 10;

        // The cooldown roughly serves as detection that the thread did not spend time being blocked.
        // If it woke in under 4 microseconds, it was likely a fast/trivial wake without blocking.
        private const int DefaultWakeCooldown = 4;

        private CacheLineSeparatedCounts _separated;

        // The absolute limit (UnfairSemaphoreSpinLimit) in scaled microseconds, at least 0.
        private readonly int _maxSpinLimitScaled;
        private readonly short _threadWakeCooldownUsec;
        private readonly Action _onWait;

        // The moving average of the wake latency in scaled microseconds: with UsecScaleShift equal to WakeLatencyAverageShift,
        // adding a sample in microseconds and taking away 1/2^WakeLatencyAverageShift of the value updates the average. Written
        // without synchronization; a lost update only makes the average slightly stale.
        private int _wakeLatencyUsecScaled;

        // The estimate of a useful spin in scaled microseconds. Written without synchronization; a lost update only makes the
        // estimate slightly stale.
        private int _dynamicSpinLimitScaled = MinSpinLimitScaled;

        // When we need to block threads we use a linked list of per-thread blockers.
        // When we need to wake a worker, we pop the topmost blocker and release it.
        private sealed class LifoBlockerNode
        {
            internal LifoBlockerNode? _next;
            internal LowLevelThreadBlocker _blocker = new LowLevelThreadBlocker();

            // The time the wake of this blocker was issued, zero if none is pending. See RecordWakeLatency.
            internal long _wakeIssuedTimestamp;

            // The start of the spin that preceded the park, zero if there was none. Written before the blocker is pushed and
            // read by the thread that pops it to wake it. See RecordSpinArrival.
            internal long _spinStartTimestamp;

            // Whether another blocker was pushed above this one while it was parked. Wakes are LIFO, so a signal that arrived
            // while it was covered woke the blocker above it instead, and its own wake does not show when the first signal that a
            // longer spin could have caught arrived. Written under _blockerStackLock.
            internal bool _covered;

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

        public LowLevelLifoSemaphore(Action onWait)
        {
            _separated = default;
            _onWait = onWait;

            short maxSpinLimitUsec = AppContextConfigHelper.GetInt16ComPlusOrDotNetConfig(
                "System.Threading.ThreadPool.UnfairSemaphoreSpinLimit",
                "ThreadPool_UnfairSemaphoreSpinLimit",
                DefaultMaxSpinLimitUsec,
                false);
            _maxSpinLimitScaled = Math.Max(0, (int)maxSpinLimitUsec) << UsecScaleShift;

            _wakeLatencyUsecScaled = DefaultInitialWakeLatencyUsec << UsecScaleShift;

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

        public bool WaitNoSpin(int timeoutMs) => WaitNoSpin(timeoutMs, spinStartTimestamp: 0);

        // spinStartTimestamp is the start of the spin that preceded this wait, zero if there was none.
        private bool WaitNoSpin(int timeoutMs, long spinStartTimestamp)
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

            return WaitAsWaiter(timeoutMs, spinStartTimestamp);
        }

        // If we have signals and have waiters, we need to make sure at least one is waking.
        // We wake one waiter at a time. If it finds a signal it will wake another worker, unless other workers consume
        // the additional signals first.

        // It is generally unusual to have > 1 signal. That only happens when the count of desired workers had a forced change.
        // In any case, we would prefer that extra signals be consumed by active workers, but must guarantee that signals
        // are consumed eventually thus we release waiters one by one.
        //
        // A spinning thread claims a signal within a few microseconds, so a waiter is woken only for the signals beyond one per
        // spinner. A spinner that stops spinning claims a signal or becomes a waiter in the same update, so the signals it was
        // counted for are not left without a wake.
        private static bool HasWaitersToWake(Counts counts) =>
            counts.CountOfWaitersSignaledToWake == 0 &&
            counts.SignalCount > counts.SpinnerCount &&
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
                counts = _separated._counts.ReadAtomic();
            }
            while (HasWaitersToWake(counts));
        }

        private bool WaitAsWaiter(int timeoutMs, long spinStartTimestamp)
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
                if (!Block(timeoutMs, spinStartTimestamp))
                {
                    // Unregister the waiter, but do not decrement wake count, the thread did not observe a wake.
                    _separated._counts.InterlockedDecrementWaiterCount();
                    return false;
                }

                // A park after a spurious wake does not follow the spin, so its wake tells nothing about the spin.
                spinStartTimestamp = 0;

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

        public void Signal()
        {
            // Increment signal count. This enables one-shot acquire.
            Counts counts = _separated._counts.InterlockedIncrementSignalCount();
            MaybeWakeWaiter(counts);
        }

        private bool Block(int timeoutMs, long spinStartTimestamp)
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
                // Written under the lock, so the thread that pops the blocker to wake it sees the value.
                blockerNode._spinStartTimestamp = spinStartTimestamp;
                blockerNode._covered = false;
                if (_blockerStack != null)
                {
                    _blockerStack._covered = true;
                }

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

                    RecordWakeLatency(blockerNode);
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

        // Takes a sample of the wake latency when the thread returns from a wait that a wake ended: the time from the wake
        // being issued to now.
        private void RecordWakeLatency(LifoBlockerNode node)
        {
            long issued = node._wakeIssuedTimestamp;
            if (issued == 0)
            {
                return;
            }

            node._wakeIssuedTimestamp = 0;

            // The cut-off reaches the absolute limit at a latency of 1/SpinUpperLimitWakeFactor of it, so longer samples carry
            // no further information. Capping them also keeps a single outlier, such as a preempted thread, from holding the
            // limits high for long.
            int sampleScaled = Math.Min(ToScaledUsec(Stopwatch.GetTimestamp() - issued), _maxSpinLimitScaled / SpinUpperLimitWakeFactor);

            int scaled = _wakeLatencyUsecScaled;
            _wakeLatencyUsecScaled = scaled - (scaled >> WakeLatencyAverageShift) + (sampleScaled >> UsecScaleShift);
        }

        // The cut-off of pointless spinning in scaled microseconds: SpinUpperLimitWakeFactor times the wake latency, at most the
        // absolute limit.
        private int WakeBoundUpperLimitScaled() =>
            Math.Min(_wakeLatencyUsecScaled * SpinUpperLimitWakeFactor, _maxSpinLimitScaled);

        // Records the arrival of a signal, as the time from the start of a spin: the time of the claim for a spin that claimed it,
        // or the time the wake was issued for a spin that ran out and parked. An arrival later than half of the estimate of a
        // useful spin and before the cut-off could have been caught by a somewhat longer spin, and steps the estimate up by a
        // fraction of what catching it is worth; an arrival after the cut-off steps it down by the same fraction of the
        // spin that it wasted; an earlier arrival leaves it unchanged. A parked thread that was covered by another (see
        // LifoBlockerNode._covered) did not see the signals that arrived meanwhile, so its arrival after the cut-off says nothing
        // about the spin and is left out. The estimate stays within [MinSpinLimitScaled, cut-off], and is written only when it
        // changes.
        private void RecordSpinArrival(long arrivalTicks, bool covered = false)
        {
            int arrival = ToScaledUsec(arrivalTicks);
            int upper = WakeBoundUpperLimitScaled();
            int limit = _dynamicSpinLimitScaled;

            int newLimit;
            if (arrival >= upper)
            {
                if (covered)
                {
                    return;
                }

                newLimit = limit - Math.Max(1, limit >> DynamicSpinLimitStepShift);
            }
            else if (arrival > limit / 2)
            {
                newLimit = Math.Min(limit + Math.Max(1, _wakeLatencyUsecScaled >> DynamicSpinLimitStepShift), upper);
            }
            else
            {
                return;
            }

            newLimit = Math.Max(MinSpinLimitScaled, newLimit);
            if (newLimit != limit)
            {
                _dynamicSpinLimitScaled = newLimit;
            }
        }

        // Converts a Stopwatch interval to scaled microseconds, with the interval capped at one second. Every limit is far below
        // a second, and the cap keeps the product from overflowing.
        private static int ToScaledUsec(long ticks) =>
            (int)(Math.Clamp(ticks, 0, Stopwatch.Frequency) * (1_000_000L << UsecScaleShift) / Stopwatch.Frequency);

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
                    // Written before the wake, so the woken thread sees it when it returns from the wait. The spin start is read
                    // before the wake too, because once woken, the thread may park again and overwrite it.
                    long now = Stopwatch.GetTimestamp();
                    long spinStart = top._spinStartTimestamp;
                    bool covered = top._covered;
                    top._wakeIssuedTimestamp = now;
                    top._blocker.WakeOne();

                    if (spinStart != 0)
                    {
                        RecordSpinArrival(now - spinStart, covered);
                    }
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

        private struct Counts : IEquatable<Counts>
        {
            private const byte SignalCountShift = 0;
            private const byte WaiterCountShift = 16;
            private const byte CountOfWaitersSignaledToWakeShift = 32;
            private const byte SpinnerCountShift = 48;

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

            public ushort SpinnerCount
            {
                get => GetUInt16Value(SpinnerCountShift);
            }

            public void DecrementSpinnerCount()
            {
                Debug.Assert(SpinnerCount != 0);
                _data -= (ulong)1 << SpinnerCountShift;
            }

            public void InterlockedIncrementSpinnerCount()
            {
                var countsAfterUpdate = new Counts(Interlocked.Add(ref _data, 1ul << SpinnerCountShift));
                Debug.Assert(countsAfterUpdate.SpinnerCount != 0); // overflow check
            }

            // A plain read of the 64-bit field can tear on 32-bit platforms.
            public readonly Counts ReadAtomic() =>
#if TARGET_64BIT
                new Counts(_data);
#else
                new Counts(Volatile.Read(in _data));
#endif

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

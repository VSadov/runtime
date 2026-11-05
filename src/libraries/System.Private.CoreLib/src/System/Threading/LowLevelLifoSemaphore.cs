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
        // After a brief initial spin-wait phase, spinning yields the processor time slice on each iteration
        // in addition to exponential spin-waits, bounded by a microsecond spin limit.

        // The upper bound of the spin, in microseconds. The previous bound was a spin count worth about 35 us, which is
        // shorter than a thread wake on virtualized hosts (about 40-190 us measured). Where work arrives in short
        // bursts (runtime async continuations, request/response workloads) most of the throughput was lost when every
        // empty dispatch parked. Fixed limits of 128 us and above recovered it in measurements, 64 us recovered
        // noticeably less. 256 us keeps a margin of about 1.3x over the longest wake that was measured.
        private const int DefaultSemaphoreSpinLimitUsec = 256;

        // The spin starts with a short busy-wait and then also yields the processor on every iteration, so that a
        // runnable thread is not starved by the spinner. One spin-wait unit of Backoff.Exponential is about 35 ns,
        // so 10 units are well under a microsecond: enough to catch a hand-off that is already in flight without a
        // system call, and small compared to the cost of the yield that follows.
        private const int SpinSleep0Threshold = 10;
        // The cooldown roughly serves as detection that the thread did not spend time being blocked.
        // If it woke in under 4 microseconds, it was likely a fast/trivial wake without blocking.
        private const int DefaultWakeCooldown = 4;

        // The spin limit is the measured wake latency times this factor, bounded by [MinSpinLimitUsec, _spinLimitUsec].
        // The factor of 0 disables the adaptation and always spins for _spinLimitUsec.
        //
        // Spinning only pays while it is cheaper than parking and waking a thread. Spinning for as long as one wake
        // takes is never more than twice as expensive as the best choice, whatever the arrival pattern. The factor is
        // larger than 1 because arrivals are bursty (an item is usually followed by more), so a longer spin catches
        // more of them, and because the measured latency leaves out part of the cost of parking (the park and unpark
        // system calls, cold caches). A factor of 5 reaches the configured limit when wakes take 51 us or more.
        // Factors of 3 to 6 were measured: lower factors were better on Windows, where spinners compete with workers
        // for processor resources, higher factors were better on virtualized Linux hosts, where wakes are slow.
        // 5 is a compromise between the two.
        // A missed spin costs up to the whole limit, so the longer spin relies on most spins succeeding. Sparse
        // arrivals (about one item per millisecond) cost more CPU than not spinning.
        private const int DefaultSpinWakeLatencyFactor = 5;

        // The lower bound of the spin: a few times the cost of a park and unpark pair of system calls (on the order
        // of 5 us), which a shorter spin cannot pay for. It also keeps the limit above the first few backoff
        // iterations, which together take several microseconds, on hosts where wakes are very fast.
        private const int MinSpinLimitUsec = 16;

        // A new wake latency sample has the weight of 1 / 2^WakeLatencyAverageShift in the moving average.
        // 1/8 follows a lasting change of the latency within about 8-16 wakes, while still smoothing out single
        // samples, which vary several-fold with the idle state of the woken processor.
        private const int WakeLatencyAverageShift = 3;

        private CacheLineSeparatedCounts _separated;

        private readonly short _spinLimitUsec;
        private readonly short _spinWakeLatencyFactor;

        // Moving average of the time from issuing an OS wake to the woken thread running. Written without
        // synchronization, a lost update only makes the estimate slightly stale.
        private long _wakeLatencyNs;
        private readonly short _threadWakeCooldownUsec;
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

        public LowLevelLifoSemaphore(Action onWait)
        {
            _separated = default;
            _onWait = onWait;

            _spinLimitUsec = AppContextConfigHelper.GetInt16ComPlusOrDotNetConfig(
                "System.Threading.ThreadPool.UnfairSemaphoreSpinLimit",
                "ThreadPool_UnfairSemaphoreSpinLimit",
                DefaultSemaphoreSpinLimitUsec,
                false);

            _spinWakeLatencyFactor = AppContextConfigHelper.GetInt16ComPlusOrDotNetConfig(
                "System.Threading.ThreadPool.UnfairSemaphoreSpinWakeFactor",
                "ThreadPool_UnfairSemaphoreSpinWakeFactor",
                DefaultSpinWakeLatencyFactor,
                false);

            // Start at the configured spin limit until wake latency is observed.
            _wakeLatencyNs = _spinWakeLatencyFactor > 0 ? _spinLimitUsec * 1000L / _spinWakeLatencyFactor : 0;

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

            RuntimeFeature.ThrowIfMultithreadingIsNotSupported();

            return WaitSlow(timeoutMs);
        }

        private int GetSpinLimitUsec()
        {
            if (_spinWakeLatencyFactor == 0)
            {
                return _spinLimitUsec;
            }

            long usec = _spinWakeLatencyFactor * Volatile.Read(ref _wakeLatencyNs) / 1000;
            return (int)Math.Clamp(usec, Math.Min(MinSpinLimitUsec, (int)_spinLimitUsec), _spinLimitUsec);
        }

        private void RecordWakeLatency(LifoBlockerNode node)
        {
            long issued = node._wakeIssuedTimestamp;
            if (issued == 0)
            {
                return;
            }

            node._wakeIssuedTimestamp = 0;
            long ns = (Stopwatch.GetTimestamp() - issued) * 1_000_000_000 / Stopwatch.Frequency;

            // Under 2 us the thread did not really park, for example it woke itself.
            if (ns < 2_000)
            {
                return;
            }

            // The spin limit is at its maximum once the average reaches _spinLimitUsec / factor, so larger samples carry
            // no further information. Clamping keeps a single outlier, such as a preempted thread, from holding the
            // limit at the maximum for dozens of wakes: with a true latency of 10 us, one 4 ms sample would take 42
            // wakes to recover from, versus 8 when clamped.
            ns = Math.Min(ns, _spinLimitUsec * 1000L / _spinWakeLatencyFactor);

            long average = Volatile.Read(ref _wakeLatencyNs);
            Volatile.Write(ref _wakeLatencyNs, average + ((ns - average) >> WakeLatencyAverageShift));
        }

        private bool WaitSlow(int timeoutMs)
        {
            int spinLimitUsec = Environment.IsSingleProcessor ? 0 : GetSpinLimitUsec();
            if (spinLimitUsec > 0)
            {
                long spinLimitTicks = Stopwatch.Frequency * spinLimitUsec / 1_000_000;
                long spinStartTick = Stopwatch.GetTimestamp();
                uint iteration = 0;
                int totalSpinWaits = 0;
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
                            return true;
                        }
                    }
                }
                while (Stopwatch.GetTimestamp() - spinStartTick < spinLimitTicks);
            }

            return WaitNoSpin(timeoutMs);
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
                if (!Block(timeoutMs))
                {
                    // Unregister the waiter, but do not decrement wake count, the thread did not observe a wake.
                    _separated._counts.InterlockedDecrementWaiterCount();
                    return false;
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

        public void Signal()
        {
            // Increment signal count. This enables one-shot acquire.
            Counts counts = _separated._counts.InterlockedIncrementSignalCount();
            MaybeWakeWaiter(counts);
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

                    if (_spinWakeLatencyFactor > 0)
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
                    if (_spinWakeLatencyFactor > 0)
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

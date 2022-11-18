// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace System.Threading
{
    /// <summary>
    /// A LIFO semaphore.
    /// Waits on this semaphore are uninterruptible.
    /// </summary>
    internal sealed partial class LowLevelLifoSemaphore : IDisposable
    {
        private CacheLineSeparatedCounts _separated;

        private readonly int _maximumSignalCount;
        private readonly Action _onWait;

        public LowLevelLifoSemaphore(int initialSignalCount, int maximumSignalCount, Action onWait)
        {
            Debug.Assert(initialSignalCount >= 0);
            Debug.Assert(initialSignalCount <= maximumSignalCount);
            Debug.Assert(maximumSignalCount > 0);

            _separated = default;
            _separated._counts.SignalCount = (uint)initialSignalCount;
            _maximumSignalCount = maximumSignalCount;
            _onWait = onWait;

            Create(maximumSignalCount);
        }

        public int CurrentCount => (int)_separated._counts.SignalCount;

        public int WaitingThreads => _separated._counts.WaitersAndSpinnersCount;

        public bool Wait(int timeoutMs, bool spinWait)
        {
            Debug.Assert(timeoutMs >= -1);

            // Try to acquire the semaphore or
            // a) register as a spinner if spinCount > 0 and timeoutMs > 0
            // b) register as a waiter if there's already too many spinners or spinCount == 0 and timeoutMs > 0
            // c) bail out if timeoutMs == 0 and return false
            while (true)
            {
                Counts counts = _separated._counts;
                Debug.Assert(counts.SignalCount <= _maximumSignalCount);
                Counts newCounts = counts;
                if (counts.SignalCount != 0)
                {
                    newCounts.DecrementSignalCount();
                }
                else if (timeoutMs != 0)
                {
                    if (spinWait && newCounts.SpinnerCount < byte.MaxValue)
                    {
                        newCounts.IncrementSpinnerCount();
                    }
                    else
                    {
                        // Maximum number of spinners reached, register as a waiter instead
                        newCounts.IncrementWaiterCount();
                    }
                }

                Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                if (countsBeforeUpdate == counts)
                {
                    if (counts.SignalCount != 0)
                    {
                        return true;
                    }
                    if (newCounts.WaiterCount != counts.WaiterCount)
                    {
                        return WaitForSignal(timeoutMs);
                    }
                    if (timeoutMs == 0)
                    {
                        return false;
                    }
                    break;
                }

                // chill out a bit between attempts if have many spinners.
                Thread.SpinWait(countsBeforeUpdate.SpinnerCount);
            }

            // assuming process dispatch time to be in the order of 10-100 usec
            // we will spin for 20+ usec before blocking the thread - in case a thread is needed soon
            Stopwatch sw = Stopwatch.StartNew();
            long spinLimit = Stopwatch.Frequency / 50000;
            do
            {
                // Try to acquire the semaphore and unregister as a spinner
                Counts counts = _separated._counts;
                if (counts.SignalCount > 0)
                {
                    Counts newCounts = counts;
                    newCounts.DecrementSignalCount();
                    newCounts.DecrementSpinnerCount();

                    Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                    if (countsBeforeUpdate == counts)
                    {
                        return true;
                    }

                    counts = countsBeforeUpdate;
                }

                // chill out a bit between attempts if have many spinners.
                Thread.SpinWait(counts.SpinnerCount);
            } while (sw.ElapsedTicks < spinLimit);

            // Unregister as spinner, and acquire the semaphore or register as a waiter
            while (true)
            {
                Counts counts = _separated._counts;
                Counts newCounts = counts;
                newCounts.DecrementSpinnerCount();
                if (counts.SignalCount != 0)
                {
                    newCounts.DecrementSignalCount();
                }
                else
                {
                    newCounts.IncrementWaiterCount();
                }

                Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                if (countsBeforeUpdate == counts)
                {
                    return counts.SignalCount != 0 || WaitForSignal(timeoutMs);
                }

                // chill out a bit between attempts if have many spinners.
                Thread.SpinWait(countsBeforeUpdate.SpinnerCount);
            }
        }

        public void Release(int releaseCount)
        {
            Debug.Assert(releaseCount > 0);
            Debug.Assert(releaseCount <= _maximumSignalCount);

            int countOfWaitersToWake;
            Counts counts = _separated._counts;
            while (true)
            {
                Counts newCounts = counts;

                // Increase the signal count. The addition doesn't overflow because of the limit on the max signal count in constructor.
                newCounts.AddSignalCount((uint)releaseCount);

                // Determine how many waiters to wake, taking into account how many spinners and waiters there are and how many waiters
                // have previously been signaled to wake but have not yet woken
                countOfWaitersToWake =
                    (int)Math.Min(newCounts.SignalCount, (uint)counts.WaiterCount + counts.SpinnerCount) -
                    counts.SpinnerCount -
                    counts.CountOfWaitersSignaledToWake;

                if (countOfWaitersToWake > 0)
                {
                    // Ideally, limiting to a maximum of releaseCount would not be necessary and could be an assert instead, but since
                    // WaitForSignal() does not have enough information to tell whether a woken thread was signaled, and due to the cap
                    // below, it's possible for countOfWaitersSignaledToWake to be less than the number of threads that have actually
                    // been signaled to wake.
                    if (countOfWaitersToWake > releaseCount)
                    {
                        countOfWaitersToWake = releaseCount;
                    }

                    // Cap countOfWaitersSignaledToWake to its max value. It's ok to ignore some woken threads in this count, it just
                    // means some more threads will be woken next time. Typically, it won't reach the max anyway.
                    newCounts.AddUpToMaxCountOfWaitersSignaledToWake((uint)countOfWaitersToWake);
                }

                Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                if (countsBeforeUpdate == counts)
                {
                    Debug.Assert(releaseCount <= _maximumSignalCount - counts.SignalCount);
                    if (countOfWaitersToWake > 0)
                        ReleaseCore(countOfWaitersToWake);

                    return;
                }

                counts = countsBeforeUpdate;
            }
        }

        private bool WaitForSignal(int timeoutMs)
        {
            Debug.Assert(timeoutMs > 0 || timeoutMs == -1);

            _onWait();

            while (true)
            {
                if (!WaitCore(timeoutMs))
                {
                    // Unregister the waiter. The wait subsystem used above guarantees that a thread that wakes due to a timeout does
                    // not observe a signal to the object being waited upon.
                    _separated._counts.InterlockedDecrementWaiterCount();
                    return false;
                }

                // Unregister the waiter if this thread will not be waiting anymore, and try to acquire the semaphore
                Counts counts = _separated._counts;
                while (true)
                {
                    Debug.Assert(counts.WaiterCount != 0);
                    Counts newCounts = counts;
                    if (counts.SignalCount != 0)
                    {
                        newCounts.DecrementSignalCount();
                        newCounts.DecrementWaiterCount();
                    }

                    // This waiter has woken up and this needs to be reflected in the count of waiters signaled to wake
                    if (counts.CountOfWaitersSignaledToWake != 0)
                    {
                        newCounts.DecrementCountOfWaitersSignaledToWake();
                    }

                    Counts countsBeforeUpdate = _separated._counts.InterlockedCompareExchange(newCounts, counts);
                    if (countsBeforeUpdate == counts)
                    {
                        if (counts.SignalCount != 0)
                        {
                            return true;
                        }
                        break;
                    }

                    counts = countsBeforeUpdate;
                }
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct Counts : IEquatable<Counts>
        {
            private const byte WaiterCountShift = 32;

            [FieldOffset(0)]
            private ulong _data;

            [FieldOffset(0)]
            internal uint SignalCount;

            [FieldOffset(sizeof(uint))]
            internal ushort WaiterCount;

            [FieldOffset(sizeof(uint) + sizeof(ushort))]
            internal byte SpinnerCount;

            [FieldOffset(sizeof(uint) + sizeof(ushort) + sizeof(byte))]
            internal byte CountOfWaitersSignaledToWake;

            private Counts(ulong data) => _data = data;

            public void AddSignalCount(uint value)
            {
                Debug.Assert(value <= uint.MaxValue - SignalCount);
                SignalCount += value;
            }

            public void IncrementSignalCount() => AddSignalCount(1);

            public void DecrementSignalCount()
            {
                Debug.Assert(SignalCount != 0);
                SignalCount--;
            }

            public void IncrementWaiterCount()
            {
                Debug.Assert(WaiterCount < ushort.MaxValue);
                WaiterCount++;
            }

            public void DecrementWaiterCount()
            {
                Debug.Assert(WaiterCount != 0);
                WaiterCount--;
            }

            public void InterlockedDecrementWaiterCount()
            {
                var countsAfterUpdate = new Counts(Interlocked.Add(ref _data, unchecked((ulong)-1) << WaiterCountShift));
                Debug.Assert(countsAfterUpdate.WaiterCount != ushort.MaxValue); // underflow check
            }

            public void IncrementSpinnerCount()
            {
                Debug.Assert(SpinnerCount < byte.MaxValue);
                SpinnerCount++;
            }

            public void DecrementSpinnerCount()
            {
                Debug.Assert(SpinnerCount != 0);
                SpinnerCount--;
            }

            public void AddUpToMaxCountOfWaitersSignaledToWake(uint value)
            {
                uint availableCount = (uint)(byte.MaxValue - CountOfWaitersSignaledToWake);
                if (value > availableCount)
                {
                    value = availableCount;
                }

                CountOfWaitersSignaledToWake += (byte)value;
            }

            public void DecrementCountOfWaitersSignaledToWake()
            {
                Debug.Assert(CountOfWaitersSignaledToWake != 0);
                CountOfWaitersSignaledToWake--;
            }

            public int WaitersAndSpinnersCount
            {
                get
                {
                    Counts counts = new Counts(this._data);
                    return counts.WaiterCount + counts.SpinnerCount;
                }
            }


            public Counts InterlockedCompareExchange(Counts newCounts, Counts oldCounts) =>
                new Counts(Interlocked.CompareExchange(ref _data, newCounts._data, oldCounts._data));

            public static bool operator ==(Counts lhs, Counts rhs) => lhs.Equals(rhs);
            public static bool operator !=(Counts lhs, Counts rhs) => !lhs.Equals(rhs);

            public override bool Equals([NotNullWhen(true)] object? obj) => obj is Counts other && Equals(other);
            public bool Equals(Counts other) => _data == other._data;
            public override int GetHashCode() => _data.GetHashCode();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CacheLineSeparatedCounts
        {
            private readonly Internal.PaddingFor64 _pad1;
            public Counts _counts;
            private readonly Internal.PaddingFor32 _pad2;
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace System.Threading
{
    /// <summary>
    /// A semaphore optimized for the thread pool.
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
            _separated._sCounts.SignalCount = initialSignalCount;
            _maximumSignalCount = maximumSignalCount;
            _onWait = onWait;

            Create(maximumSignalCount);
        }

        public int CurrentCount => (int)_separated._sCounts.SignalCount;

        public int WaitingThreads =>_separated._sCounts.SpinnerCount + _separated._wCounts.WaiterCount;

        public bool Wait(int timeoutMs, bool spinWait)
        {
            Debug.Assert(timeoutMs >= -1);

            int signalCount = _separated._sCounts.SignalCount;
            if (signalCount != 0 && Interlocked.CompareExchange(ref _separated._sCounts.SignalCount, signalCount - 1, signalCount) == signalCount)
                return true;

            if (spinWait && !Environment.IsSingleProcessor)
            {
                Interlocked.Increment(ref _separated._sCounts.SpinnerCount);

                // assuming process dispatch time to be in the order of 10-100 usec
                // we will spin for 20+ usec before blocking the thread - in case a thread is needed soon
                Stopwatch sw = Stopwatch.StartNew();
                long spinLimit = Stopwatch.Frequency / 50000;
                do
                {
                    // Try to acquire the semaphore and unregister as a spinner
                    SignalSpinnerCounts counts = _separated._sCounts;
                    if (counts.SignalCount > 0)
                    {
                        SignalSpinnerCounts newCounts = counts;
                        newCounts.SignalCount--;
                        newCounts.SpinnerCount--;

                        SignalSpinnerCounts countsBeforeUpdate = _separated._sCounts.InterlockedCompareExchange(newCounts, counts);
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
                Interlocked.Decrement(ref _separated._sCounts.SpinnerCount);
            }

            signalCount = _separated._sCounts.SignalCount;
            if (signalCount != 0 && Interlocked.CompareExchange(ref _separated._sCounts.SignalCount, signalCount - 1, signalCount) == signalCount)
                return true;

            if (timeoutMs == 0)
                return false;

            Interlocked.Increment(ref _separated._wCounts.WaiterCount);
            return WaitForSignal(timeoutMs);
        }

        public void Release(int releaseCount)
        {
            Debug.Assert(releaseCount > 0);
            Debug.Assert(releaseCount <= _maximumSignalCount);

            // Increase the signal count. The addition doesn't overflow because of the limit on the max signal count in constructor.
            SignalSpinnerCounts sCounts = _separated._sCounts.InterlockedAddSignalCount(releaseCount);

            while (true)
            {
                // Determine how many waiters to wake, taking into account how many spinners and waiters there are and how many waiters
                // have previously been signaled to wake but have not yet woken
                WaiterCounts wCounts = _separated._wCounts;
                int wouldWantToWake = (int)sCounts.SignalCount - sCounts.SpinnerCount - wCounts.CountOfWaitersSignaledToWake;
                int canWake = wCounts.WaiterCount - wCounts.CountOfWaitersSignaledToWake;
                int countOfWaitersToWake = Math.Min(wouldWantToWake, canWake);

                if (countOfWaitersToWake <= 0)
                    return;

                WaiterCounts newCounts = wCounts;
                newCounts.CountOfWaitersSignaledToWake += countOfWaitersToWake;

                // must be interlocked, so that woken waiters will not exceed waiters.
                WaiterCounts countsBeforeUpdate = _separated._wCounts.InterlockedCompareExchange(newCounts, wCounts);

                if (countsBeforeUpdate == wCounts)
                {
                    Debug.Assert(releaseCount <= _maximumSignalCount - sCounts.SignalCount);
                    ReleaseCore(countOfWaitersToWake);
                    return;
                }
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
                    Interlocked.Decrement(ref _separated._wCounts.WaiterCount);
                    return false;
                }

                // Unregister the waiter if this thread will not be waiting anymore, and try to acquire the semaphore
                while (true)
                {
                    WaiterCounts wCounts = _separated._wCounts;
                    Debug.Assert(wCounts.WaiterCount != 0);
                    Debug.Assert(wCounts.CountOfWaitersSignaledToWake != 0);

                    WaiterCounts newCounts = wCounts;
                    newCounts.WaiterCount--;
                    newCounts.CountOfWaitersSignaledToWake--;

                    WaiterCounts countsBeforeUpdate = _separated._wCounts.InterlockedCompareExchange(newCounts, wCounts);
                    if (countsBeforeUpdate == wCounts)
                    {
                        break;
                    }
                }

                int signalCount = _separated._sCounts.SignalCount;
                if (signalCount != 0 && Interlocked.CompareExchange(ref _separated._sCounts.SignalCount, signalCount - 1, signalCount) == signalCount)
                    return true;

                // other threads are keeping up with releases, so this can become a waiter again.
                Interlocked.Increment(ref _separated._wCounts.WaiterCount);
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct SignalSpinnerCounts : IEquatable<SignalSpinnerCounts>
        {
            [FieldOffset(0)]
            private long _data;

            [FieldOffset(0)]
            internal int SignalCount;

            [FieldOffset(sizeof(int))]
            internal int SpinnerCount;

            private SignalSpinnerCounts(long data) => _data = data;

            public SignalSpinnerCounts InterlockedAddSignalCount(int toAdd)
            {
                return new SignalSpinnerCounts(Interlocked.Add(ref _data, toAdd));
            }

            public SignalSpinnerCounts InterlockedCompareExchange(SignalSpinnerCounts newCounts, SignalSpinnerCounts oldCounts) =>
                new SignalSpinnerCounts(Interlocked.CompareExchange(ref _data, newCounts._data, oldCounts._data));

            public static bool operator ==(SignalSpinnerCounts lhs, SignalSpinnerCounts rhs) => lhs.Equals(rhs);
            public static bool operator !=(SignalSpinnerCounts lhs, SignalSpinnerCounts rhs) => !lhs.Equals(rhs);

            public override bool Equals([NotNullWhen(true)] object? obj) => obj is SignalSpinnerCounts other && Equals(other);
            public bool Equals(SignalSpinnerCounts other) => _data == other._data;
            public override int GetHashCode() => _data.GetHashCode();
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct WaiterCounts : IEquatable<WaiterCounts>
        {
            [FieldOffset(0)]
            private long _data;

            [FieldOffset(0)]
            internal int WaiterCount;

            [FieldOffset(sizeof(int))]
            internal int CountOfWaitersSignaledToWake;

            private WaiterCounts(long data) => _data = data;

            public WaiterCounts InterlockedCompareExchange(WaiterCounts newCounts, WaiterCounts oldCounts) =>
                new WaiterCounts(Interlocked.CompareExchange(ref _data, newCounts._data, oldCounts._data));

            public static bool operator ==(WaiterCounts lhs, WaiterCounts rhs) => lhs.Equals(rhs);
            public static bool operator !=(WaiterCounts lhs, WaiterCounts rhs) => !lhs.Equals(rhs);

            public override bool Equals([NotNullWhen(true)] object? obj) => obj is WaiterCounts other && Equals(other);
            public bool Equals(WaiterCounts other) => _data == other._data;
            public override int GetHashCode() => _data.GetHashCode();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CacheLineSeparatedCounts
        {
            private readonly Internal.PaddingFor64 _pad0;
            public SignalSpinnerCounts _sCounts;
            private readonly Internal.PaddingFor64 _pad1;
            public WaiterCounts _wCounts;
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Threading
{
    /// <summary>
    /// A lightweight non-recursive mutex. Waits on this lock are uninterruptible.
    /// </summary>
    internal sealed class LowLevelLock : IDisposable
    {
        // The following constants define characteristics of spinning logic in the Lock class
        private const int SpinningNotInitialized = 0;
        private const int SpinningDisabled = -1;
        private const int MaxSpinningValue = 10000;

        // NOTE: Lock must not have a static (class) constructor, as Lock itself may be used to synchronize
        // class construction.  If Lock has its own class constructor, this can lead to infinite recursion.
        // All static data in Lock must be pre-initialized.
        private static int s_maxSpinCount;

        //
        // m_state layout:
        //
        // bit 0: True if the lock is held, false otherwise.
        //
        // bit 1: True if we've set the event to wake a waiting thread.  The waiter resets this to false when it
        //        wakes up.  This avoids the overhead of setting the event multiple times.
        //
        // everything else: A count of the number of threads waiting on the event.
        //
        private const int Uncontended = 0;
        private const int Locked = 1;
        private const int WaiterWoken = 2;
        private const int WaiterCountIncrement = 4;

        private int _state;
        private AutoResetEvent? _lazyEvent;

#if DEBUG
        private Thread? _ownerThread;
#endif

        public LowLevelLock()
        {
            if (s_maxSpinCount == SpinningNotInitialized)
            {
                // avoid Environment.ProcessorCount->ClassConstructorRunner->Lock->Environment.ProcessorCount cycle
                // call GetProcessorCount() dirtectly
                s_maxSpinCount = Environment.GetProcessorCount() > 1 ? MaxSpinningValue : SpinningDisabled;
            }
        }

        private AutoResetEvent Event
        {
            get
            {
                if (_lazyEvent == null)
                    Interlocked.CompareExchange(ref _lazyEvent, new AutoResetEvent(false), null);

                return _lazyEvent;
            }
        }

        public void Dispose()
        {
            _lazyEvent?.Dispose();
        }

        ~LowLevelLock() => Dispose();

        public void Acquire()
        {
            // Make one quick attempt to acquire an uncontended lock
            if (Interlocked.CompareExchange(ref _state, Locked, Uncontended) == Uncontended)
            {
#if DEBUG
                _ownerThread = Thread.CurrentThread;
#endif
                return;
            }

            // Fall back to the slow path for contention
            AcquireContended();
        }

        public bool TryAcquire()
        {
            // Make one quick attempt to acquire an uncontended lock
            if (Interlocked.CompareExchange(ref _state, Locked, Uncontended) == Uncontended)
            {
#if DEBUG
                _ownerThread = Thread.CurrentThread;
#endif
                return true;
            }

            return false;
        }

        private void AcquireContended()
        {
            int spins = 1;

            while (true)
            {
                // Try to grab the lock.  We may take the lock here even if there are existing waiters.  This creates the possibility
                // of starvation of waiters, but it also prevents lock convoys from destroying perf.
                int oldState = _state;
                if ((oldState & Locked) == 0 && Interlocked.CompareExchange(ref _state, oldState | Locked, oldState) == oldState)
                    goto GotTheLock;

                // Back off by a factor of 2 for each attempt, up to MaxSpinCount
                if (spins <= s_maxSpinCount)
                {
                    Thread.SpinWait(spins);
                    spins *= 2;
                }
                else if (oldState != 0)
                {
                    // We reached our spin limit, and need to wait.  Increment the waiter count.
                    // Note that we do not do any overflow checking on this increment.  In order to overflow,
                    // we'd need to have about 1 billion waiting threads, which is inconceivable anytime in the
                    // forseeable future.
                    int newState = (oldState + WaiterCountIncrement) & ~WaiterWoken;
                    if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                        break;
                }
            }

            //
            // Now we wait.
            //

            AutoResetEvent ev = Event;
            while (true)
            {
                Debug.Assert(_state >= WaiterCountIncrement);

                bool waitSucceeded = ev.WaitOne();
                Debug.Assert(waitSucceeded);

                while (true)
                {
                    int oldState = _state;
                    Debug.Assert(oldState >= WaiterCountIncrement);

                    // Clear the "waiter woken" bit.
                    int newState = oldState & ~WaiterWoken;

                    if ((oldState & Locked) == 0)
                    {
                        // The lock is available, try to get it.
                        newState |= Locked;
                        newState -= WaiterCountIncrement;

                        if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                            goto GotTheLock;
                    }
                    else
                    {
                        // The lock is not available. We're going to wait again.
                        if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                            break;
                    }
                }
            }

        GotTheLock:
            Debug.Assert((_state | Locked) != 0);
#if DEBUG
            _ownerThread = Thread.CurrentThread;
#endif
        }

        public void Release()
        {
#if DEBUG
            _ownerThread = null;
#endif

            // Make one quick attempt to release an uncontended lock
            if (Interlocked.CompareExchange(ref _state, Uncontended, Locked) == Locked)
            {
                return;
            }

            // We have waiters; take the slow path.
            ReleaseContended();
        }

        private void ReleaseContended()
        {
            while (true)
            {
                int oldState = _state;

                // clear the lock bit.
                int newState = oldState & ~Locked;

                if (oldState >= WaiterCountIncrement && (oldState & WaiterWoken) == 0)
                {
                    // there are waiters, and nobody has woken one.
                    newState |= WaiterWoken;
                    if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                    {
                        Event.Set();
                        return;
                    }
                }
                else
                {
                    // no need to wake a waiter.
                    if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                        return;
                }
            }
        }

        [Conditional("DEBUG")]
        public void VerifyIsLocked()
        {
#if DEBUG
            Debug.Assert(_ownerThread == Thread.CurrentThread);
#endif
            Debug.Assert(IsLocked);
        }

        [Conditional("DEBUG")]
        public void VerifyIsNotLocked()
        {
#if DEBUG
            Debug.Assert(_ownerThread == null);
#endif
            Debug.Assert(!IsLocked);
        }

        public bool IsLocked => (_state & Locked) != 0;
    }
}

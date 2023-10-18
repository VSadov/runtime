// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace System.Threading
{
    /// <summary>
    /// Provides a way to get mutual exclusion in regions of code between different threads. A lock may be held by one thread at
    /// a time.
    /// </summary>
    /// <remarks>
    /// Threads that cannot immediately enter the lock may wait for the lock to be exited or until a specified timeout. A thread
    /// that holds a lock may enter the lock repeatedly without exiting it, such as recursively, in which case the thread should
    /// eventually exit the lock the same number of times to fully exit the lock and allow other threads to enter the lock.
    /// </remarks>
    [Runtime.Versioning.RequiresPreviewFeatures]
    public sealed partial class Lock
    {
        private const short SpinCountNotInitialized = short.MinValue;
        private const short SpinningDisabled = 0;

        private const short DefaultMaxSpinCount = 22;
        private const short DefaultAdaptiveSpinPeriod = 100;

        private static long s_contentionCount;

        //
        // We will use exponential backoff in rare cases when we need to change state atomically and cannot
        // make progress due to concurrent state changes by other threads.
        // While we cannot know the ideal amount of wait needed before making a successfull attempt,
        // the exponential backoff will generally be not more than 2X worse than the perfect guess and
        // will do a lot less attempts than an simple retry. On multiprocessor machine fruitless attempts
        // will cause unnecessary sharing of the contended state which may make modifying the state more expensive.
        // To protect against degenerate cases we will cap the per-iteration wait to 1024 spinwaits.
        //
        private const uint MaxExponentialBackoffBits = 10;

        //
        // This lock is unfair and permits acquiring a contended lock by a nonwaiter in the presence of waiters.
        // It is possible for one thread to keep holding the lock long enough that waiters go to sleep and
        // then release and reacquire fast enough that waiters have no chance to get the lock.
        // In extreme cases one thread could keep retaking the lock starving everybody else.
        // If we see woken waiters not able to take the lock for too long we will ask nonwaiters to wait.
        //
        private const uint WaiterWatchdogTicks = 100;

        // The field's type is not ThreadId to try to retain the relative order of fields of intrinsic types. The type system
        // appears to place struct fields after fields of other types, in which case there can be a greater chance that
        // _owningThreadId is not in the same cache line as _state.
#if TARGET_OSX && !NATIVEAOT
        private ulong _owningThreadId;
#else
        private uint _owningThreadId;
#endif

        //
        // m_state layout:
        //
        // bit 0: True if the lock is held, false otherwise.
        //
        // bit 1: True if we've set the event to wake a waiting thread.  The waiter resets this to false when it
        //        wakes up.  This avoids the overhead of setting the event multiple times.
        //
        // bit 2: True if nonwaiters must not get ahead of waiters when acquiring a contended lock.
        //
        // everything else: A count of the number of threads waiting on the event.
        //
        private const uint Locked = 1;
        private const uint WaiterWoken = 2;
        private const uint YieldToWaiters = 4;
        private const uint WaiterCountIncrement = 8;

        private uint _state; // see State for layout
        private uint _recursionCount;
        private short _spinCount;
        private short _wakeWatchDog;
        private AutoResetEvent? _waitEvent;

        /// <summary>
        /// Initializes a new instance of the <see cref="Lock"/> class.
        /// </summary>
        public Lock() => _spinCount = SpinCountNotInitialized;

        /// <summary>
        /// Enters the lock. Once the method returns, the calling thread would be the only thread that holds the lock.
        /// </summary>
        /// <remarks>
        /// If the lock cannot be entered immediately, the calling thread waits for the lock to be exited. If the lock is
        /// already held by the calling thread, the lock is entered again. The calling thread should exit the lock as many times
        /// as it had entered the lock to fully exit the lock and allow other threads to enter the lock.
        /// </remarks>
        /// <exception cref="LockRecursionException">
        /// The lock has reached the limit of recursive enters. The limit is implementation-defined, but is expected to be high
        /// enough that it would typically not be reached when the lock is used properly.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Enter()
        {
            bool success = TryEnter_Inlined(timeoutMs: -1);
            Debug.Assert(success);
        }

        /// <summary>
        /// Enters the lock and returns a <see cref="Scope"/> that may be disposed to exit the lock. Once the method returns,
        /// the calling thread would be the only thread that holds the lock. This method is intended to be used along with a
        /// language construct that would automatically dispose the <see cref="Scope"/>, such as with the C# <code>using</code>
        /// statement.
        /// </summary>
        /// <returns>
        /// A <see cref="Scope"/> that may be disposed to exit the lock.
        /// </returns>
        /// <remarks>
        /// If the lock cannot be entered immediately, the calling thread waits for the lock to be exited. If the lock is
        /// already held by the calling thread, the lock is entered again. The calling thread should exit the lock, such as by
        /// disposing the returned <see cref="Scope"/>, as many times as it had entered the lock to fully exit the lock and
        /// allow other threads to enter the lock.
        /// </remarks>
        /// <exception cref="LockRecursionException">
        /// The lock has reached the limit of recursive enters. The limit is implementation-defined, but is expected to be high
        /// enough that it would typically not be reached when the lock is used properly.
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Scope EnterScope()
        {
            Enter();
            return new Scope(this);
        }

        /// <summary>
        /// A disposable structure that is returned by <see cref="EnterScope()"/>, which when disposed, exits the lock.
        /// </summary>
        public ref struct Scope
        {
            private Lock? _lockObj;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Scope(Lock lockObj)
            {
                _lockObj = lockObj;
            }

            /// <summary>
            /// Exits the lock.
            /// </summary>
            /// <remarks>
            /// If the calling thread holds the lock multiple times, such as recursively, the lock is exited only once. The
            /// calling thread should ensure that each enter is matched with an exit.
            /// </remarks>
            /// <exception cref="SynchronizationLockException">
            /// The calling thread does not hold the lock.
            /// </exception>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose()
            {
                Lock? lockObj = _lockObj;
                if (lockObj != null)
                {
                    _lockObj = null;
                    lockObj.Exit();
                }
            }
        }

        /// <summary>
        /// Tries to enter the lock without waiting. If the lock is entered, the calling thread would be the only thread that
        /// holds the lock.
        /// </summary>
        /// <returns>
        /// <code>true</code> if the lock was entered, <code>false</code> otherwise.
        /// </returns>
        /// <remarks>
        /// If the lock cannot be entered immediately, the method returns <code>false</code>. If the lock is already held by the
        /// calling thread, the lock is entered again. The calling thread should exit the lock as many times as it had entered
        /// the lock to fully exit the lock and allow other threads to enter the lock.
        /// </remarks>
        /// <exception cref="LockRecursionException">
        /// The lock has reached the limit of recursive enters. The limit is implementation-defined, but is expected to be high
        /// enough that it would typically not be reached when the lock is used properly.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool TryEnter() => TryEnter_Inlined(timeoutMs: 0);

        /// <summary>
        /// Tries to enter the lock, waiting for roughly the specified duration. If the lock is entered, the calling thread
        /// would be the only thread that holds the lock.
        /// </summary>
        /// <param name="millisecondsTimeout">
        /// The rough duration in milliseconds for which the method will wait if the lock is not available. A value of
        /// <code>0</code> specifies that the method should not wait, and a value of <see cref="Timeout.Infinite"/> or
        /// <code>-1</code> specifies that the method should wait indefinitely until the lock is entered.
        /// </param>
        /// <returns>
        /// <code>true</code> if the lock was entered, <code>false</code> otherwise.
        /// </returns>
        /// <remarks>
        /// If the lock cannot be entered immediately, the calling thread waits for roughly the specified duration for the lock
        /// to be exited. If the lock is already held by the calling thread, the lock is entered again. The calling thread
        /// should exit the lock as many times as it had entered the lock to fully exit the lock and allow other threads to
        /// enter the lock.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="millisecondsTimeout"/> is less than <code>-1</code>.
        /// </exception>
        /// <exception cref="LockRecursionException">
        /// The lock has reached the limit of recursive enters. The limit is implementation-defined, but is expected to be high
        /// enough that it would typically not be reached when the lock is used properly.
        /// </exception>
        public bool TryEnter(int millisecondsTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, -1);
            return TryEnter_Outlined(millisecondsTimeout);
        }

        /// <summary>
        /// Tries to enter the lock, waiting for roughly the specified duration. If the lock is entered, the calling thread
        /// would be the only thread that holds the lock.
        /// </summary>
        /// <param name="timeout">
        /// The rough duration for which the method will wait if the lock is not available. The timeout is converted to a number
        /// of milliseconds by casting <see cref="TimeSpan.TotalMilliseconds"/> of the timeout to an integer value. A value
        /// representing <code>0</code> milliseconds specifies that the method should not wait, and a value representing
        /// <see cref="Timeout.Infinite"/> or <code>-1</code> milliseconds specifies that the method should wait indefinitely
        /// until the lock is entered.
        /// </param>
        /// <returns>
        /// <code>true</code> if the lock was entered, <code>false</code> otherwise.
        /// </returns>
        /// <remarks>
        /// If the lock cannot be entered immediately, the calling thread waits for roughly the specified duration for the lock
        /// to be exited. If the lock is already held by the calling thread, the lock is entered again. The calling thread
        /// should exit the lock as many times as it had entered the lock to fully exit the lock and allow other threads to
        /// enter the lock.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="timeout"/>, after its conversion to an integer millisecond value, represents a value that is less
        /// than <code>-1</code> milliseconds or greater than <see cref="int.MaxValue"/> milliseconds.
        /// </exception>
        /// <exception cref="LockRecursionException">
        /// The lock has reached the limit of recursive enters. The limit is implementation-defined, but is expected to be high
        /// enough that it would typically not be reached when the lock is used properly.
        /// </exception>
        public bool TryEnter(TimeSpan timeout) => TryEnter_Outlined(WaitHandle.ToTimeoutMilliseconds(timeout));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryEnter_Outlined(int timeoutMs) => TryEnter_Inlined(timeoutMs);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryEnter_Inlined(int timeoutMs)
        {
            Debug.Assert(timeoutMs >= -1);

            ThreadId currentThreadId = ThreadId.Current_NoInitialize;
            if (currentThreadId.IsInitialized && State.TryLock(this))
            {
                Debug.Assert(!new ThreadId(_owningThreadId).IsInitialized);
                Debug.Assert(_recursionCount == 0);
                _owningThreadId = currentThreadId.Id;
                return true;
            }

            return TryEnterSlow(timeoutMs, currentThreadId);
        }

        /// <summary>
        /// Exits the lock.
        /// </summary>
        /// <remarks>
        /// If the calling thread holds the lock multiple times, such as recursively, the lock is exited only once. The
        /// calling thread should ensure that each enter is matched with an exit.
        /// </remarks>
        /// <exception cref="SynchronizationLockException">
        /// The calling thread does not hold the lock.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Exit()
        {
            var owningThreadId = new ThreadId(_owningThreadId);
            if (!owningThreadId.IsInitialized || owningThreadId.Id != ThreadId.Current_NoInitialize.Id)
            {
                ThrowHelper.ThrowSynchronizationLockException_LockExit();
            }

            if (_recursionCount == 0)
            {
                ReleaseCore();
                return;
            }

            _recursionCount--;
        }

        private static unsafe void ExponentialBackoff(uint iteration)
        {
            if (iteration > 0)
            {
                // no need for much randomness here, we will just hash the stack address + iteration.
                uint rand = ((uint)&iteration + iteration) * 2654435769u;
                // set the highmost bit to ensure minimum number of spins is exponentialy increasing
                // that is in case some stack location results in a sequence of very low spin counts
                // it basically gurantees that we spin at least 1, 2, 4, 8, 16, times, and so on
                rand |= (1u << 31);
                uint spins = rand >> (byte)(32 - Math.Min(iteration, MaxExponentialBackoffBits));
                Thread.SpinWait((int)spins);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
#if !NATIVEAOT
        private bool TryEnterSlow(int timeoutMs, ThreadId currentThreadId)
#else
        private bool TryEnterSlow(int timeoutMs, ThreadId currentThreadId, object associatedObject)
#endif
        {
            Debug.Assert(timeoutMs >= -1);

            if (!currentThreadId.IsInitialized)
            {
                // The thread info hasn't been initialized yet for this thread, and the fast path hasn't been tried yet. After
                // initializing the thread info, try the fast path first.
                currentThreadId.InitializeForCurrentThread();
                Debug.Assert(_owningThreadId != currentThreadId.Id);
                if (State.TryLock(this))
                {
                    Debug.Assert(!new ThreadId(_owningThreadId).IsInitialized);
                    Debug.Assert(_recursionCount == 0);
                    _owningThreadId = currentThreadId.Id;
                    return true;
                }
            }
            else if (_owningThreadId == currentThreadId.Id)
            {
                Debug.Assert(new State(this).IsLocked);

                uint newRecursionCount = _recursionCount + 1;
                if (newRecursionCount != 0)
                {
                    _recursionCount = newRecursionCount;
                    return true;
                }

                throw new LockRecursionException(SR.Lock_Enter_LockRecursionException);
            }

            if (timeoutMs == 0)
            {
                return false;
            }

            if (LazyInitializeOrEnter() == TryLockResult.Locked)
            {
                Debug.Assert(!new ThreadId(_owningThreadId).IsInitialized);
                Debug.Assert(_recursionCount == 0);
                _owningThreadId = currentThreadId.Id;
                return true;
            }

            bool isSingleProcessor = IsSingleProcessor;
            short maxSpinCount = s_maxSpinCount;

            // since we have just made an attempt to accuire and failed, do a small pause
            Thread.SpinWait(1);

            if (_spinCount == SpinCountNotInitialized)
            {
                _spinCount = (IsSingleProcessor) ? s_minSpinCount : SpinningDisabled;
            }

            bool hasWaited = false;
            // we will retry after waking up
            while (true)
            {
                uint iteration = 0;

                // We will count when we failed to change the state of the lock and increase pauses
                // so that bursts of activity are better tolerated. This should not happen often.
                uint collisions = 0;

                // We will track the changes of ownership while we are trying to acquire the lock.
                var oldOwner = _owningThreadId;
                uint ownerChanged = 0;

                int localSpinLimit = _spinCount;
                // inner loop where we try acquiring the lock or registering as a waiter
                while (true)
                {
                    //
                    // Try to grab the lock.  We may take the lock here even if there are existing waiters.  This creates the possibility
                    // of starvation of waiters, but it also prevents lock convoys and preempted waiters from destroying perf.
                    // However, if we do not see _wakeWatchDog cleared for long enough, we go into YieldToWaiters mode to ensure some
                    // waiter progress.
                    //
                    uint oldState = _state;
                    bool canAcquire = ((oldState & Locked) == 0) &&
                        (hasWaited || ((oldState & YieldToWaiters) == 0));

                    if (canAcquire)
                    {
                        uint newState = oldState | Locked;
                        if (hasWaited)
                            newState = (newState - WaiterCountIncrement) & ~(WaiterWoken | YieldToWaiters);

                        if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                        {
                            // GOT THE LOCK!!
                            if (hasWaited)
                                _wakeWatchDog = 0;

                            // now we can estimate how busy the lock is and adjust spinning accordingly
                            short spinLimit = _spinCount;
                            if (ownerChanged != 0)
                            {
                                // The lock has changed ownership while we were trying to acquire it.
                                // It is a signal that we might want to spin less next time.
                                // Pursuing a lock that is being "stolen" by other threads is inefficient
                                // due to cache misses and unnecessary sharing of state that keeps invalidating.
                                if (spinLimit > s_minSpinCount)
                                {
                                    _spinCount = (short)(spinLimit - 1);
                                }
                            }
                            else if (spinLimit < s_maxSpinCount && iteration > spinLimit / 2)
                            {
                                // we used more than 50% of allowed iterations, but the lock does not look very contested,
                                // we can allow a bit more spinning.
                                _spinCount = (short)(spinLimit + 1);
                            }

                            Debug.Assert((_state | Locked) != 0);
                            Debug.Assert(_owningThreadId == 0);
                            Debug.Assert(_recursionCount == 0);
                            _owningThreadId = currentThreadId.Id;
                            return true;
                        }
                    }

                    if (iteration++ < localSpinLimit)
                    {
                        uint newOwner = _owningThreadId;
                        if (newOwner != 0 && newOwner != oldOwner)
                        {
                            ownerChanged++;
                            oldOwner = newOwner;
                        }

                        if (canAcquire)
                        {
                            collisions++;
                        }

                        // We failed to acquire the lock and want to retry after a pause.
                        // Ideally we will retry right when the lock becomes free, but we cannot know when that will happen.
                        // We will use a pause that doubles up on every iteration. It will not be more than 2x worse
                        // than the ideal guess, while minimizing the number of retries.
                        // We will allow pauses up to 64~128 spinwaits, or more if there are collisions.
                        ExponentialBackoff(Math.Min(iteration, 6) + collisions);
                        continue;
                    }
                    else if (!canAcquire)
                    {
                        //
                        // We reached our spin limit, and need to wait.  Increment the waiter count.
                        // Note that we do not do any overflow checking on this increment.  In order to overflow,
                        // we'd need to have about 1 billion waiting threads, which is inconceivable anytime in the
                        // forseeable future.
                        //
                        uint newState = oldState + WaiterCountIncrement;
                        if (hasWaited)
                            newState = (newState - WaiterCountIncrement) & ~WaiterWoken;

                        if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                            break;

                        collisions++;
                    }

                    ExponentialBackoff(collisions);
                }

                //
                // Now we wait.
                //
#if NATIVEAOT
                using var debugBlockingScope =
                    new Monitor.DebugBlockingScope(
                        associatedObject,
                        Monitor.DebugBlockingItemType.MonitorCriticalSection,
                        timeoutMs,
                        out _);
#endif

                Interlocked.Increment(ref s_contentionCount);

                bool areContentionEventsEnabled =
                NativeRuntimeEventSource.Log.IsEnabled(
                    EventLevel.Informational,
                    NativeRuntimeEventSource.Keywords.ContentionKeyword);
                AutoResetEvent waitEvent = _waitEvent ?? CreateWaitEvent(areContentionEventsEnabled);

                TimeoutTracker timeoutTracker = TimeoutTracker.Start(timeoutMs);
                Debug.Assert(_state >= WaiterCountIncrement);
                bool waitSucceeded = waitEvent.WaitOne(timeoutMs);
                Debug.Assert(_state >= WaiterCountIncrement);

                if (!waitSucceeded)
                    break;

                // we did not time out and will try acquiring the lock
                hasWaited = true;
                timeoutMs = timeoutTracker.Remaining;
            }

            // TODO: VS events
            // TODO: VS catch

            // We timed out.  We're not going to wait again.
            {
                uint iteration = 0;
                while (true)
                {
                    uint oldState = _state;
                    Debug.Assert(oldState >= WaiterCountIncrement);

                    uint newState = oldState - WaiterCountIncrement;

                    // We could not have consumed a wake, or the wait would've succeeded.
                    // If we are the last waiter though, we will clear WaiterWoken and YieldToWaiters
                    // just so that lock would not look like contended.
                    if (newState < WaiterCountIncrement)
                        newState = newState & ~WaiterWoken & ~YieldToWaiters;

                    if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                        return false;

                    ExponentialBackoff(iteration++);
                }
            }
        }

        internal struct TimeoutTracker
        {
            private int _start;
            private int _timeout;

            public static TimeoutTracker Start(int timeout)
            {
                TimeoutTracker tracker = default;
                tracker._timeout = timeout;
                if (timeout != Timeout.Infinite)
                    tracker._start = Environment.TickCount;
                return tracker;
            }

            public int Remaining
            {
                get
                {
                    if (_timeout == Timeout.Infinite)
                        return Timeout.Infinite;
                    int elapsed = Environment.TickCount - _start;
                    if (elapsed > _timeout)
                        return 0;
                    return _timeout - elapsed;
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private unsafe AutoResetEvent CreateWaitEvent(bool areContentionEventsEnabled)
        {
            var newWaitEvent = new AutoResetEvent(false);
            AutoResetEvent? waitEventBeforeUpdate = Interlocked.CompareExchange(ref _waitEvent, newWaitEvent, null);
            if (waitEventBeforeUpdate == null)
            {
                // Also check NativeRuntimeEventSource.Log.IsEnabled() to enable trimming
                if (areContentionEventsEnabled && NativeRuntimeEventSource.Log.IsEnabled())
                {
                    NativeRuntimeEventSource.Log.ContentionLockCreated(this);
                }

                return newWaitEvent;
            }

            newWaitEvent.Dispose();
            return waitEventBeforeUpdate;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReleaseCore()
        {
            Debug.Assert(_recursionCount == 0);
            _owningThreadId = 0;
            uint origState = Interlocked.Decrement(ref _state);
            if (origState < WaiterCountIncrement || (origState & WaiterWoken) != 0)
            {
                return;
            }

            //
            // We have waiters; take the slow path.
            //
            AwakeWaiterIfNeeded();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AwakeWaiterIfNeeded()
        {
            uint iteration = 0;
            while (true)
            {
                uint oldState = _state;
                if (oldState >= WaiterCountIncrement && (oldState & WaiterWoken) == 0)
                {
                    // there are waiters, and nobody has woken one.
                    uint newState = oldState | WaiterWoken;

                    short lastWakeTicks = _wakeWatchDog;
                    if (lastWakeTicks != 0 && (short)Environment.TickCount - lastWakeTicks > WaiterWatchdogTicks)
                    {
                        newState |= YieldToWaiters;
                    }

                    if (Interlocked.CompareExchange(ref _state, newState, oldState) == oldState)
                    {
                        if (lastWakeTicks == 0)
                        {
                            // nonzero timestamp of the last wake
                            _wakeWatchDog = (short)(Environment.TickCount | 1);
                        }

                        _waitEvent!.Set();
                        return;
                    }
                }
                else
                {
                    // no need to wake a waiter.
                    return;
                }

                ExponentialBackoff(iteration++);
            }
        }

        /// <summary>
        /// <code>true</code> if the lock is held by the calling thread, <code>false</code> otherwise.
        /// </summary>
        public bool IsHeldByCurrentThread
        {
            get
            {
                var owningThreadId = new ThreadId(_owningThreadId);
                bool isHeld = owningThreadId.IsInitialized && owningThreadId.Id == ThreadId.Current_NoInitialize.Id;
                Debug.Assert(!isHeld || new State(this).IsLocked);
                return isHeld;
            }
        }

        internal static long ContentionCount => s_contentionCount;
        internal void Dispose() => _waitEvent?.Dispose();

        internal nint LockIdForEvents
        {
            get
            {
                Debug.Assert(_waitEvent != null);
                return _waitEvent.SafeWaitHandle.DangerousGetHandle();
            }
        }

        internal unsafe nint ObjectIdForEvents
        {
            get
            {
                Lock lockObj = this;
                return *(nint*)Unsafe.AsPointer(ref lockObj);
            }
        }

        internal ulong OwningThreadId => _owningThreadId;

        private static short DetermineMaxSpinCount() =>
            AppContextConfigHelper.GetInt16Config(
                "System.Threading.Lock.SpinCount",
                "DOTNET_Lock_SpinCount",
                DefaultMaxSpinCount,
                allowNegative: false);

        private static short DetermineMinSpinCount()
        {
            // The config var can be set to -1 to disable adaptive spin
            short adaptiveSpinPeriod =
                AppContextConfigHelper.GetInt16Config(
                    "System.Threading.Lock.AdaptiveSpinPeriod",
                    "DOTNET_Lock_AdaptiveSpinPeriod",
                    DefaultAdaptiveSpinPeriod,
                    allowNegative: true);
            if (adaptiveSpinPeriod < -1)
            {
                adaptiveSpinPeriod = DefaultAdaptiveSpinPeriod;
            }

            return (short)-adaptiveSpinPeriod;
        }

        private struct State : IEquatable<State>
        {
            // Layout constants for Lock._state
            private const uint IsLockedMask = (uint)1 << 0; // bit 0
            private const uint IsWaiterSignaledToWakeMask = (uint)1 << 1; // bit 1
            private const uint ShouldNotPreemptWaitersMask = (uint)1 << 2; // bit 2
            private const byte WaiterCountShift = 3;
            private const uint WaiterCountIncrement = (uint)1 << WaiterCountShift; // bits 3-31

            private uint _state;

            public State(Lock lockObj) : this(lockObj._state) { }
            private State(uint state) => _state = state;

            public static uint InitialStateValue => 0;
            public static uint LockedStateValue => IsLockedMask;
            private static uint Neg(uint state) => (uint)-(int)state;
            public bool IsInitialState => this == default;
            public bool IsLocked => (_state & IsLockedMask) != 0;

            private void SetIsLocked()
            {
                Debug.Assert(!IsLocked);
                _state += IsLockedMask;
            }

            private bool ShouldNotPreemptWaiters => (_state & ShouldNotPreemptWaitersMask) != 0;

            private void SetShouldNotPreemptWaiters()
            {
                Debug.Assert(!ShouldNotPreemptWaiters);
                Debug.Assert(HasAnyWaiters);

                _state += ShouldNotPreemptWaitersMask;
            }

            private void ClearShouldNotPreemptWaiters()
            {
                Debug.Assert(ShouldNotPreemptWaiters);
                _state -= ShouldNotPreemptWaitersMask;
            }

            private bool ShouldNonWaiterAttemptToAcquireLock
            {
                get
                {
                    Debug.Assert(HasAnyWaiters || !ShouldNotPreemptWaiters);
                    return (_state & (IsLockedMask | ShouldNotPreemptWaitersMask)) == 0;
                }
            }

            private static bool HasAnySpinners => false;

            private bool IsWaiterSignaledToWake => (_state & IsWaiterSignaledToWakeMask) != 0;

            private void SetIsWaiterSignaledToWake()
            {
                Debug.Assert(HasAnyWaiters);
                Debug.Assert(NeedToSignalWaiter);

                _state += IsWaiterSignaledToWakeMask;
            }

            private void ClearIsWaiterSignaledToWake()
            {
                Debug.Assert(IsWaiterSignaledToWake);
                _state -= IsWaiterSignaledToWakeMask;
            }

            public bool HasAnyWaiters => _state >= WaiterCountIncrement;

            private bool TryIncrementWaiterCount()
            {
                uint newState = _state + WaiterCountIncrement;
                if (new State(newState).HasAnyWaiters) // overflow check
                {
                    _state = newState;
                    return true;
                }
                return false;
            }

            private void DecrementWaiterCount()
            {
                Debug.Assert(HasAnyWaiters);
                _state -= WaiterCountIncrement;
            }

            public bool NeedToSignalWaiter
            {
                get
                {
                    Debug.Assert(HasAnyWaiters);
                    return (_state & IsWaiterSignaledToWakeMask) == 0;
                }
            }

            public static bool operator ==(State state1, State state2) => state1._state == state2._state;
            public static bool operator !=(State state1, State state2) => !(state1 == state2);

            bool IEquatable<State>.Equals(State other) => this == other;
            public override bool Equals(object? obj) => obj is State other && this == other;
            public override int GetHashCode() => (int)_state;

            private static State CompareExchange(Lock lockObj, State toState, State fromState) =>
                new State(Interlocked.CompareExchange(ref lockObj._state, toState._state, fromState._state));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool TryLock(Lock lockObj)
            {
                // The lock is mostly fair to release waiters in a typically FIFO order (though the order is not guaranteed).
                // However, it allows non-waiters to acquire the lock if it's available to avoid lock convoys.
                //
                // Lock convoys can be detrimental to performance in scenarios where work is being done on multiple threads and
                // the work involves periodically taking a particular lock for a short time to access shared resources. With a
                // lock convoy, once there is a waiter for the lock (which is not uncommon in such scenarios), a worker thread
                // would be forced to context-switch on the subsequent attempt to acquire the lock, often long before the worker
                // thread exhausts its time slice. This process repeats as long as the lock has a waiter, forcing every worker
                // to context-switch on each attempt to acquire the lock, killing performance and creating a positive feedback
                // loop that makes it more likely for the lock to have waiters. To avoid the lock convoy, each worker needs to
                // be allowed to acquire the lock multiple times in sequence despite there being a waiter for the lock in order
                // to have the worker continue working efficiently during its time slice as long as the lock is not contended.
                //
                // This scheme has the possibility to starve waiters. Waiter starvation is mitigated by other means, see
                // TryLockBeforeSpinLoop() and references to ShouldNotPreemptWaiters.

                var state = new State(lockObj);
                if (!state.ShouldNonWaiterAttemptToAcquireLock)
                {
                    return false;
                }

                State newState = state;
                newState.SetIsLocked();

                return CompareExchange(lockObj, newState, state) == state;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static State Unlock(Lock lockObj)
            {
                Debug.Assert(IsLockedMask == 1);

                var state = new State(Interlocked.Decrement(ref lockObj._state));
                Debug.Assert(!state.IsLocked);
                return state;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static TryLockResult TryLockInsideSpinLoop(Lock lockObj)
            {
                // This method is called from inside a spin loop, it must unregister the spinner if the lock is acquired

                var state = new State(lockObj);
                while (true)
                {
                    if (!state.ShouldNonWaiterAttemptToAcquireLock)
                    {
                        return state.ShouldNotPreemptWaiters ? TryLockResult.Wait : TryLockResult.Spin;
                    }

                    State newState = state;
                    newState.SetIsLocked();

                    State stateBeforeUpdate = CompareExchange(lockObj, newState, state);
                    if (stateBeforeUpdate == state)
                    {
                        return TryLockResult.Locked;
                    }

                    state = stateBeforeUpdate;
                }
            }
        }

        private enum TryLockResult
        {
            Locked,
            Spin,
            Wait
        }
    }
}

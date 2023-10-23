// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace System.Threading
{
    public sealed partial class Lock
    {
        // NOTE: Lock must not have a static (class) constructor, as Lock itself is used to synchronize
        // class construction.  If Lock has its own class constructor, this can lead to infinite recursion.
        // All static data in Lock must be lazy-initialized.
        private static int s_staticsInitializationStage;
        private static int s_processorCount;
        private static short s_maxSpinCount;
        private static short s_minSpinCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryEnterOneShot(int currentManagedThreadId)
        {
            Debug.Assert(currentManagedThreadId != 0);

            if (this.TryLock())
            {
                Debug.Assert(_owningThreadId == 0);
                Debug.Assert(_recursionCount == 0);
                _owningThreadId = (uint)currentManagedThreadId;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Exit(int currentManagedThreadId)
        {
            Debug.Assert(currentManagedThreadId != 0);

            if (_owningThreadId != (uint)currentManagedThreadId)
            {
                ThrowHelper.ThrowSynchronizationLockException_LockExit();
            }

            ExitImpl();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryEnterSlow(int timeoutMs, ThreadId currentThreadId) =>
            TryEnterSlow(timeoutMs, currentThreadId, this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryEnterSlow(int timeoutMs, int currentManagedThreadId, object associatedObject) =>
            TryEnterSlow(timeoutMs, new ThreadId((uint)currentManagedThreadId), associatedObject);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool GetIsHeldByCurrentThread(int currentManagedThreadId)
        {
            Debug.Assert(currentManagedThreadId != 0);

            bool isHeld = _owningThreadId == (uint)currentManagedThreadId;
            Debug.Assert(!isHeld || this.IsLocked);
            return isHeld;
        }

        internal uint ExitAll()
        {
            Debug.Assert(IsHeldByCurrentThread);

            uint recursionCount = _recursionCount;
            _recursionCount = 0;

            ReleaseCore();

            return recursionCount;
        }

        internal void Reenter(uint previousRecursionCount)
        {
            Debug.Assert(!IsHeldByCurrentThread);

            Enter();
            _recursionCount = previousRecursionCount;
        }

        // Returns false until the static variable is lazy-initialized
        internal static bool IsSingleProcessor => s_processorCount == 1;

        internal static void LazyInit()
        {
            s_maxSpinCount = DefaultMaxSpinCount;
            s_minSpinCount = DefaultMinSpinCount;
            s_processorCount = RuntimeImports.RhGetProcessCpuCount();

            // the rest is optional, but let's try once
            if (s_staticsInitializationStage != (int)StaticsInitializationStage.Complete)
            {
                InitStatics();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool InitStatics()
        {
            if (s_staticsInitializationStage != (int)StaticsInitializationStage.Started)
            {
                // prevent reentrancy on the same thread
                s_staticsInitializationStage = (int)StaticsInitializationStage.Started;
                try
                {
                    s_minSpinCount = DetermineMinSpinCount();
                    s_maxSpinCount = DetermineMaxSpinCount();
                    NativeRuntimeEventSource.Log.IsEnabled();

                    Volatile.Write(ref s_staticsInitializationStage, (int)StaticsInitializationStage.Complete);
                    return true;
                }
                catch
                {
                    // Callers can't handle this failure and also guarantee not coming here again because anything may take locks.
                    // However initializing statics is optional, so just ignore the failure.
                    s_staticsInitializationStage = (int)StaticsInitializationStage.NotStarted;
                }
            }

            return false;
        }

        internal static bool StaticsInitComplete()
        {
            if (Volatile.Read(ref s_staticsInitializationStage) == (int)StaticsInitializationStage.Complete)
            {
                return true;
            }

            return InitStatics();
        }

        // Used to transfer the state when inflating thin locks
        internal void InitializeLocked(int managedThreadId, uint recursionCount)
        {
            Debug.Assert(recursionCount == 0 || managedThreadId != 0);

            _state = managedThreadId == 0 ? Unlocked : Locked;
            _owningThreadId = (uint)managedThreadId;
            _recursionCount = recursionCount;
        }

        internal struct ThreadId
        {
            private uint _id;

            public ThreadId(uint id) => _id = id;
            public uint Id => _id;
            public bool IsInitialized => _id != 0;
            public static ThreadId Current_NoInitialize => new ThreadId((uint)ManagedThreadId.CurrentManagedThreadIdUnchecked);

            public void InitializeForCurrentThread()
            {
                Debug.Assert(!IsInitialized);
                _id = (uint)ManagedThreadId.Current;
                Debug.Assert(IsInitialized);
            }
        }

        private enum StaticsInitializationStage
        {
            NotStarted,
            Started,
            Complete
        }
    }
}

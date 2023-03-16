// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Threading
{
    /// <summary>
    /// Thin wrapper over native semaphore.
    /// </summary>
    internal sealed partial class LowLevelSemaphore : IDisposable
    {
        public LowLevelSemaphore(int initialCount, int maximumCount)
        {
            _nativeSemaphore = Interop.Sys.NativeSemaphore_Create(initialCount, maximumCount);
            if (_nativeSemaphore == IntPtr.Zero)
            {
                throw new OutOfMemoryException();
            }
        }

        private void DisposeNative()
        {
            Interop.Sys.NativeSemaphore_Destroy(_nativeSemaphore);
        }

        public bool Wait(int timeoutMs)
        {
            Debug.Assert(timeoutMs >= -1);

            if (timeoutMs == -1)
            {
                Wait();
                return true;
            }

            Thread.CurrentThread.SetWaitSleepJoinState();
            bool result = Interop.Sys.NativeSemaphore_TimedWait(_nativeSemaphore, timeoutMs);
            Thread.CurrentThread.ClearWaitSleepJoinState();

            return result;
        }

        public void Wait()
        {
            Thread.CurrentThread.SetWaitSleepJoinState();
            Interop.Sys.NativeSemaphore_Wait(_nativeSemaphore);
            Thread.CurrentThread.ClearWaitSleepJoinState();
        }

        public void Release(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (!Interop.Sys.NativeSemaphore_Release(_nativeSemaphore))
                {
                    throw new SemaphoreFullException();
                }
            }
        }
    }
}

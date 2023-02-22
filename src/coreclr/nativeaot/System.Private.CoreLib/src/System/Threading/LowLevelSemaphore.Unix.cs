// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Threading
{
    /// <summary>
    /// Thin wrapper over native semaphore.
    /// </summary>
    internal sealed class LowLevelSemaphore : IDisposable
    {
        private IntPtr _nativeSemaphore;

        public LowLevelSemaphore(int initialCount, int maximumCount)
        {
            _nativeSemaphore = Interop.Sys.NativeSemaphore_Create(initialCount, maximumCount);
            if (_nativeSemaphore == IntPtr.Zero)
            {
                throw new OutOfMemoryException();
            }
        }

        public void Dispose()
        {
            if (_nativeSemaphore == IntPtr.Zero)
            {
                return;
            }

            Interop.Sys.NativeSemaphore_Destroy(_nativeSemaphore);
            _nativeSemaphore = IntPtr.Zero;
        }

        public bool Wait(int timeoutMs)
        {
            Debug.Assert(timeoutMs > 0 || timeoutMs == -1);

            if (timeoutMs == -1)
            {
                Wait();
                return true;
            }

            int ret = Interop.Sys.NativeSemaphore_TimedWait(_nativeSemaphore, timeoutMs);
            switch (ret)
            {
                case (int)Interop.Error.SUCCESS:
                    return true;
                case (int)Interop.Error.ETIMEDOUT:
                    return false;
                default:
                    throw new InvalidOperationException();
            }
        }

        public void Wait()
        {
            int ret = Interop.Sys.NativeSemaphore_Wait(_nativeSemaphore);
            if (ret != (int)Interop.Error.SUCCESS)
                throw new InvalidOperationException();
        }

        public void Release(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int ret = Interop.Sys.NativeSemaphore_Release(_nativeSemaphore);
                switch (ret)
                {
                    case (int)Interop.Error.SUCCESS:
                        continue;
                    case (int)Interop.Error.EOVERFLOW:
                        throw new SemaphoreFullException();
                    default:
                        throw new InvalidOperationException();
                }
            }
        }
    }
}

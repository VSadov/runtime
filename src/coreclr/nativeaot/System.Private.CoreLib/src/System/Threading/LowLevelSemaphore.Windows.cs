// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Win32.SafeHandles;
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
            _nativeSemaphore = Interop.Kernel32.CreateSemaphore(IntPtr.Zero, initialCount, maximumCount, name: null);
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

            Interop.Kernel32.CloseHandle(_nativeSemaphore);
            _nativeSemaphore = IntPtr.Zero;
        }

        public bool Wait(int timeoutMs)
        {
            Debug.Assert(_nativeSemaphore != IntPtr.Zero);
            Debug.Assert(timeoutMs > 0 || timeoutMs == -1);

            uint ret = Interop.Kernel32.WaitForSingleObject(_nativeSemaphore, (uint)timeoutMs);
            Debug.Assert(ret == Interop.Kernel32.WAIT_OBJECT_0 || ret == Interop.Kernel32.WAIT_TIMEOUT);

            return ret == Interop.Kernel32.WAIT_OBJECT_0;
        }

        public void Release(int count)
        {
            Debug.Assert(_nativeSemaphore != IntPtr.Zero);

            if (!Interop.Kernel32.ReleaseSemaphore(_nativeSemaphore, count, out int _))
                throw new SemaphoreFullException();
        }
    }
}

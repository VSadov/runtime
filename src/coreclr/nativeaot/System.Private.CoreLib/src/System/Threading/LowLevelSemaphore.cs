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
        private IntPtr _nativeSemaphore;

        public void Dispose()
        {
            if (_nativeSemaphore != IntPtr.Zero)
            {
                DisposeNative();
                _nativeSemaphore = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }

        ~LowLevelSemaphore() => Dispose();
    }
}

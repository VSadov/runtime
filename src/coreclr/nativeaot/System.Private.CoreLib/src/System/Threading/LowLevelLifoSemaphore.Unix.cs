// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace System.Threading
{
    /// <summary>
    /// A LIFO semaphore.
    /// Waits on this semaphore are uninterruptible.
    /// </summary>
    internal sealed partial class LowLevelLifoSemaphore : IDisposable
    {
        private LowLevelSemaphore _semaphore;

        private void Create(int maximumSignalCount)
        {
            _semaphore = new LowLevelSemaphore(0, maximumSignalCount);
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }

        private bool WaitCore(int timeoutMs)
        {
            return _semaphore.Wait(timeoutMs);
        }

        private void ReleaseCore(int count)
        {
            _semaphore.Release(count);
        }
    }
}

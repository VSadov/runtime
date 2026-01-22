// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    // gate on which threads may wait.
    internal unsafe partial struct LowLevelGate
    {
        private const int Open = 1;
        private const int Blocking = 0;

        private int* _pState;

        public LowLevelGate()
        {
            _pState = (int*)Marshal.AllocHGlobal(sizeof(int));
            *_pState = Blocking;
        }

        internal void DisposeCore()
        {
            if (_pState == null)
            {
                return;
            }

            Marshal.FreeHGlobal((nint)_pState);
            _pState = null;
        }

        internal void Wait()
        {
            int blocking = Blocking;
            // sleep if the gate is blocked
            Interop.BOOL result = Interop.Kernel32.WaitOnAddress(_pState, &blocking, sizeof(int), -1);
            Debug.Assert(result == Interop.BOOL.TRUE);

            // close the gate after us (consume the wake)
            *_pState = Blocking;
        }

        internal bool TimedWait(int timeoutMs)
        {
            int blocking = Blocking;
            // sleep if the gate is blocked
            Interop.BOOL result = Interop.Kernel32.WaitOnAddress(_pState, &blocking, sizeof(int), timeoutMs);
            Debug.Assert(result == Interop.BOOL.TRUE || Interop.Kernel32.GetLastError() == Interop.Errors.ERROR_TIMEOUT);

            bool woken = result == Interop.BOOL.TRUE;
            if (woken)
            {
                // close the gate after us (consume the wake)
                *_pState = Blocking;
            }

            return woken;
        }

        internal void WakeOne()
        {
            // open the gate
            *_pState = Open;
            // ping the wait queue
            Interop.Kernel32.WakeByAddressSingle(_pState);
        }
    }

    internal sealed partial class LowLevelLifoSemaphore : IDisposable
    {
        private LowLevelGate _gate;

        private void Create()
        {
            _gate = new LowLevelGate();
        }

        ~LowLevelLifoSemaphore()
        {
            Dispose();
        }

        public bool WaitCore(int timeoutMs)
        {
            Debug.Assert(timeoutMs >= -1);
            return _gate.TimedWait(timeoutMs);
        }

        private void ReleaseCore(int count)
        {
            Debug.Assert(count > 0);

            for (int i = 0; i < count; i++)
            {
                _gate.WakeOne();
            }
        }

        public void Dispose()
        {
            _gate.DisposeCore();
            GC.SuppressFinalize(this);
        }
    }
}

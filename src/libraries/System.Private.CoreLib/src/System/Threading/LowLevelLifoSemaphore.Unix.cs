// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#define USE_MONITOR

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal unsafe partial struct LowLevelGate
    {
        private int* _pState;

#if USE_MONITOR
        private LowLevelMonitor _monitor;
#endif

        public LowLevelGate()
        {
            _pState = (int*)Marshal.AllocHGlobal(sizeof(int));
            *_pState = 0;

#if USE_MONITOR
            _monitor.Initialize();
#endif
        }

        internal void DisposeCore()
        {
            if (_pState == null)
            {
                return;
            }

            Marshal.FreeHGlobal((nint)_pState);
            _pState = null;

#if USE_MONITOR
            _monitor.Dispose();
#endif
        }

#if USE_MONITOR
        internal void Wait()
        {
            _monitor.Acquire();

            int originalState = *_pState;
            while (originalState == 0)
            {
                _monitor.Wait();
                originalState = *_pState;
            }

            *_pState = originalState - 1;
            _monitor.Release();
        }

        internal bool TimedWait(int timeoutMs)
        {
            _ = timeoutMs;

            Wait();

            //long deadline = Environment.TickCount64 + timeoutMs;
            //int originalState = *_pState;
            //while (originalState == 0)
            //{
            //    _monitor.Wait(timeoutMs);

            //    long current = Environment.TickCount64;
            //    if (current >= deadline)
            //    {
            //        return false;
            //    }
            //    else
            //    {
            //        timeoutMs = (int)(deadline - current);
            //    }

            //    originalState = *_pState;
            //}

            //*_pState = originalState - 1;
            //_monitor.Release();
            return true;
        }

        internal void WakeOne()
        {
            _monitor.Acquire();
            *_pState = *_pState + 1;
            _monitor.Signal_Release();
        }

        internal void Reset()
        {
            _monitor.Acquire();
            *_pState = 0;
            _monitor.Release();
        }

#else
        internal void Wait()
        {
            while (true)
            {
                int originalState = *_pState;
                while (originalState == 0)
                {
                    Interop.Kernel32.WaitOnAddress(&*_pState, &originalState, sizeof(int), -1);
                    originalState = *_pState;
                }

                if (Interlocked.CompareExchange(ref *_pState, originalState - 1, originalState) == originalState)
                {
                    return;
                }
            }
        }

        internal bool TimedWait(int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                int originalState = *_pState;
                while (originalState == 0)
                {
                    if (Interop.Kernel32.WaitOnAddress(&*_pState, &originalState, sizeof(int), timeoutMs) != Interop.BOOL.TRUE)
                    {
                        return false;
                    }

                    long current = Environment.TickCount64;
                    if (current >= deadline)
                    {
                        return false;
                    }
                    else
                    {
                        timeoutMs = (int)(deadline - current);
                    }

                    originalState = *_pState;
                }

                if (Interlocked.CompareExchange(ref *_pState, originalState - 1, originalState) == originalState)
                {
                    return true;
                }
            }
        }

        internal void WakeOne()
        {
            Interlocked.Increment(ref *_pState);
            Interop.Kernel32.WakeByAddressSingle(_pState);
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref *_pState, 0);
        }
#endif
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

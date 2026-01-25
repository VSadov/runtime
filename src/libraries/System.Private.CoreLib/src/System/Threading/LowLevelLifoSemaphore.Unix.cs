// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// #define USE_MONITOR

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal static unsafe class LowLevelFutex
    {
        internal static void WaitOnAddress(int* address, int comparand)
        {
            Interop.Sys.LowLevelFutex_WaitOnAddress(address, comparand);
        }

        internal static bool WaitOnAddressTimeout(int* address, int comparand, int milliseconds)
        {
            return Interop.Sys.LowLevelFutex_WaitOnAddressTimeout(address, comparand, milliseconds);
        }

        internal static void WakeByAddressSingle(int* address)
        {
            Interop.Sys.LowLevelFutex_WakeByAddressSingle(address);
        }
    }

    internal sealed unsafe class LowLevelGate : IDisposable
    {
        private int* _pState;

#if USE_MONITOR
        private LowLevelMonitor _monitor;
#endif

        internal LowLevelGate? _next;

        public LowLevelGate()
        {
            _pState = (int*)Marshal.AllocHGlobal(sizeof(int));
            *_pState = 0;

#if USE_MONITOR
            _monitor.Initialize();
#endif
        }

        ~LowLevelGate()
        {
            Dispose();
        }

        public void Dispose()
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

            GC.SuppressFinalize(this);
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
            long deadline = Environment.TickCount64 + timeoutMs;
            _monitor.Acquire();

            int originalState = *_pState;
            while (originalState == 0)
            {
                if (!_monitor.Wait(timeoutMs) ||
                    (timeoutMs = (int)(deadline - Environment.TickCount64)) < 0)
                {
                    _monitor.Release();
                    return false;
                }

                originalState = *_pState;
            }

            *_pState = originalState - 1;
            _monitor.Release();
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

            // Last chance for the waking thread to wake us before we block, so lets spin a bit.
            // This spinning is on a per-thread state, thus not too costly.
            // The number of spins is somewhat arbitrary.
            for (int i = 0; i < 1000; i++)
            {
                int originalState = *_pState;
                if (originalState != 0 &&
                    Interlocked.CompareExchange(ref *_pState, originalState - 1, originalState) == originalState)
                {
                    return;
                }

                Thread.SpinWait(1);
            }

            while (true)
            {
                int originalState = *_pState;
                while (originalState == 0)
                {
                    LowLevelFutex.WaitOnAddress(_pState, originalState);
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

            // Last chance for the waking thread to wake us before we block, so lets spin a bit.
            // This spinning is on a per-thread state, thus not too costly.
            // The number of spins is somewhat arbitrary.
            for (int i = 0; i < 1000; i++)
            {
                int originalState = *_pState;
                if (originalState != 0 &&
                    Interlocked.CompareExchange(ref *_pState, originalState - 1, originalState) == originalState)
                {
                    return true;
                }

                Thread.SpinWait(1);
            }

            while (true)
            {
                int originalState = *_pState;
                while (originalState == 0)
                {
                    if (!LowLevelFutex.WaitOnAddressTimeout(_pState, originalState, timeoutMs))
                    {
                        return false;
                    }

                    timeoutMs = (int)(deadline - Environment.TickCount64);
                    if (timeoutMs <= 0)
                    {
                        return false;
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
            LowLevelFutex.WakeByAddressSingle(_pState);
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref *_pState, 0);
        }
#endif
    }

    internal sealed partial class LowLevelLifoSemaphore : IDisposable
    {
        private Lock _stackLock = new Lock(useTrivialWaits: true);
        private LowLevelGate? _stack;
        private int _signals;

        private void Remove(LowLevelGate item)
        {
            using (_stackLock.EnterScope())
            {
                LowLevelGate? current = _stack;
                if (current == item)
                {
                    _stack = item._next;
                    return;
                }

                while (current != null && current._next != item)
                {
                    current = current._next;
                }

                current?._next = current._next!._next;
            }
        }

        private void Create()
        {
            _ = this;
        }

        ~LowLevelLifoSemaphore()
        {
            Dispose();
        }

        [ThreadStatic]
        private static LowLevelGate? t_gate;

        private enum WaitResult
        {
            Retry,
            Woken,
            TimedOut,
        }

        private WaitResult WaitCore(int timeoutMs)
        {
            Debug.Assert(timeoutMs >= -1);

            LowLevelGate? gate = t_gate;
            if (gate == null)
            {
                t_gate = gate = new LowLevelGate();
            }

            if (_stackLock.TryEnter())
            {
                if (_signals != 0)
                {
                    _signals--;
                    gate = null;
                }
                else
                {
                    gate._next = _stack;
                    _stack = gate;
                }
                _stackLock.Exit();
            }
            else
            {
                return WaitResult.Retry;
            }

            //if (gate != null)
            //{
            //    bool result = gate.TimedWait(timeoutMs);
            //    if (!result)
            //    {
            //        // we did not consume a wake.
            //        // TODO: VS do we ever reset?
            //        return WaitResult.TimedOut;
            //    }
            //}

            gate?.Wait();

            // we consumed a wake
            return WaitResult.Woken;
        }

        private void WakeOne()
        {
            LowLevelGate? head;
            using (_stackLock.EnterScope())
            {
                head = _stack;
                if (head != null)
                {
                    _stack = head._next;
                    head._next = null;
                }
                else
                {
                    _signals++;
                }
            }

            head?.WakeOne();
        }

        private void ReleaseCore(int count)
        {
            Debug.Assert(count > 0);

            for (int i = 0; i < count; i++)
            {
                WakeOne();
            }
        }

        public void Dispose()
        {
            //_gate.Dispose();
            //GC.SuppressFinalize(this);
        }
    }
}

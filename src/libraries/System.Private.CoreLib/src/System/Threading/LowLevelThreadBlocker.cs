// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !TARGET_LINUX && !TARGET_WINDOWS
#define USE_MONITOR
#endif

using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal unsafe class LowLevelThreadBlocker : IDisposable
    {
        private int* _pState;

#if USE_MONITOR
        private LowLevelMonitor _monitor;
#endif

        public LowLevelThreadBlocker()
        {
            _pState = (int*)NativeMemory.AlignedAlloc(Internal.PaddingHelpers.CACHE_LINE_SIZE, Internal.PaddingHelpers.CACHE_LINE_SIZE);
            *_pState = 0;

#if USE_MONITOR
            _monitor.Initialize();
#endif
        }

        ~LowLevelThreadBlocker()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_pState == null)
            {
                return;
            }

            NativeMemory.AlignedFree(_pState);
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

        private const int spins = 5;

        internal void Wait()
        {
            // Last chance for the waking thread to wake us before we block, so lets spin briefly.
            // The number of spins is somewhat arbitrary. (approx 1-5 usec)
            for (int i = 0; i < spins; i++)
            {
                int originalState = *_pState;
                if (originalState != 0 &&
                    Interlocked.CompareExchange(ref *_pState, originalState - 1, originalState) == originalState)
                {
                    return;
                }

                Backoff.Exponential((uint)i);
//                Thread.SpinWait(1);
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

            // Last chance for the waking thread to wake us before we block, so lets spin briefly.
            // The number of spins is somewhat arbitrary. (approx 1-5 usec)
            for (int i = 0; i < spins; i++)
            {
                int originalState = *_pState;
                if (originalState != 0 &&
                    Interlocked.CompareExchange(ref *_pState, originalState - 1, originalState) == originalState)
                {
                    return true;
                }

                Backoff.Exponential((uint)i);
                //                Thread.SpinWait(1);
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
}

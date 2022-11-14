// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static Interop.Sys;

namespace System.Net.Sockets
{
    internal sealed unsafe class SocketAsyncEngine : IThreadPoolWorkItem
    {
        private const int EventBufferCount =
#if DEBUG
            32;
#else
            1024;
#endif

        // Socket continuations are dispatched to the ThreadPool from the event thread.
        // This avoids continuations blocking the event handling.
        // Setting PreferInlineCompletions allows continuations to run directly on the event thread.
        // PreferInlineCompletions defaults to false and can be set to true using the DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS envvar.
        internal static readonly bool InlineSocketCompletionsEnabled = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS") == "1";

        private static int GetEngineCount()
        {
            // The responsibility of SocketAsyncEngine is to get notifications from epoll|kqueue
            // and schedule corresponding work items to ThreadPool (socket reads and writes).
            //
            // Using TechEmpower benchmarks that generate a LOT of SMALL socket reads and writes under a VERY HIGH load
            // we have observed that a single engine is capable of keeping busy up to thirty x64 and eight ARM64 CPU Cores.
            //
            // The vast majority of real-life scenarios is never going to generate such a huge load (hundreds of thousands of requests per second)
            // and having a single producer should be almost always enough.
            //
            // We want to be sure that we can handle extreme loads and that's why we have decided to use these values.
            //
            // It's impossible to predict all possible scenarios so we have added a possibility to configure this value using environment variables.
            if (uint.TryParse(Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT"), out uint count))
            {
                return (int)count;
            }

            // When inlining continuations, we default to ProcessorCount to make sure event threads cannot be a bottleneck.
            if (InlineSocketCompletionsEnabled)
            {
                return Environment.ProcessorCount;
            }

            Architecture architecture = RuntimeInformation.ProcessArchitecture;
            int coresPerEngine = architecture == Architecture.Arm64 || architecture == Architecture.Arm
                ? 8
                : 30;

            return Math.Max(1, (int)Math.Round(Environment.ProcessorCount / (double)coresPerEngine));
        }

        private static readonly SocketAsyncEngine[] s_engines = CreateEngines();
        private static int s_allocateFromEngine = -1;

        private static SocketAsyncEngine[] CreateEngines()
        {
            int engineCount = GetEngineCount();

            var engines = new SocketAsyncEngine[engineCount];

            for (int i = 0; i < engineCount; i++)
            {
                engines[i] = new SocketAsyncEngine();
            }

            return engines;
        }

        private readonly IntPtr _port;
        private readonly Interop.Sys.SocketEvent* _buffer;

        //
        // Maps handle values to SocketAsyncContext instances.
        //
        private readonly ConcurrentDictionary<IntPtr, SocketAsyncContextWrapper> _handleToContextMap = new ConcurrentDictionary<IntPtr, SocketAsyncContextWrapper>();

        //
        // Registers the Socket with a SocketAsyncEngine, and returns the associated engine.
        //
        public static bool TryRegisterSocket(IntPtr socketHandle, SocketAsyncContext context, out SocketAsyncEngine? engine, out Interop.Error error)
        {
            int engineIndex = Math.Abs(Interlocked.Increment(ref s_allocateFromEngine) % s_engines.Length);
            SocketAsyncEngine nextEngine = s_engines[engineIndex];
            bool registered = nextEngine.TryRegisterCore(socketHandle, context, out error);
            engine = registered ? nextEngine : null;
            return registered;
        }

        private bool TryRegisterCore(IntPtr socketHandle, SocketAsyncContext context, out Interop.Error error)
        {
            bool added = _handleToContextMap.TryAdd(socketHandle, new SocketAsyncContextWrapper(context));
            if (!added)
            {
                // Using public SafeSocketHandle(IntPtr) a user can add the same handle
                // from a different Socket instance.
                throw new InvalidOperationException(SR.net_sockets_handle_already_used);
            }

            error = Interop.Sys.TryChangeSocketEventRegistration(_port, socketHandle, Interop.Sys.SocketEvents.None,
                Interop.Sys.SocketEvents.Read | Interop.Sys.SocketEvents.Write, socketHandle);
            if (error == Interop.Error.SUCCESS)
            {
                return true;
            }

            _handleToContextMap.TryRemove(socketHandle, out _);
            return false;
        }

        public void UnregisterSocket(IntPtr socketHandle)
        {
            _handleToContextMap.TryRemove(socketHandle, out _);
        }

        private SocketAsyncEngine()
        {
            _port = (IntPtr)(-1);
            try
            {
                //
                // Create the event port and buffer
                //
                Interop.Error err;
                fixed (IntPtr* portPtr = &_port)
                {
                    err = Interop.Sys.CreateSocketEventPort(portPtr);
                    if (err != Interop.Error.SUCCESS)
                    {
                        throw new InternalException(err);
                    }
                }

                fixed (Interop.Sys.SocketEvent** bufferPtr = &_buffer)
                {
                    err = Interop.Sys.CreateSocketEventBuffer(EventBufferCount, bufferPtr);
                    if (err != Interop.Error.SUCCESS)
                    {
                        throw new InternalException(err);
                    }
                }

                var thread = new Thread(static s => ((SocketAsyncEngine)s!).EventLoop())
                {
                    IsBackground = true,
                    Name = ".NET Sockets"
                };
                thread.UnsafeStart(this);
            }
            catch
            {
                FreeNativeResources();
                throw;
            }
        }

        private ManualResetEventSlim _blockingPollerRelease = new ManualResetEventSlim(false);

        private void EventLoop()
        {
            try
            {
                while (true)
                {
                    int numEvents = EventBufferCount;
                    Interop.Error err = Interop.Sys.WaitForSocketEvents(_port, _buffer, &numEvents, -1);
                    if (err != Interop.Error.SUCCESS)
                    {
                        throw new InternalException(err);
                    }

                    // The native shim is responsible for ensuring this condition.
                    Debug.Assert(numEvents > 0, $"Unexpected numEvents: {numEvents}");

                    HandleSocketEvents(_buffer, numEvents);
                    _blockingPollerRelease.Wait();
                    _blockingPollerRelease.Reset();
                }
            }
            catch (Exception e)
            {
                Environment.FailFast("Exception thrown from SocketAsyncEngine event loop: " + e.ToString(), e);
            }
        }

        private void HelpOnce()
        {
            var localBuffer = stackalloc Interop.Sys.SocketEvent[EventBufferCount];
            try
            {
                int numEvents = EventBufferCount;
                Interop.Error err = Interop.Sys.WaitForSocketEvents(_port, localBuffer, &numEvents, 0);
                if (err != Interop.Error.SUCCESS)
                {
                    throw new InternalException(err);
                }

                if (numEvents > 0)
                {
                    HandleSocketEvents(localBuffer, numEvents);
                }
                else
                {
                    _blockingPollerRelease.Set();
                }
            }
            catch (Exception e)
            {
                Environment.FailFast("Exception thrown from SocketAsyncEngine helper: " + e.ToString(), e);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void HandleSocketEvents(Interop.Sys.SocketEvent* buffer, int numEvents)
        {
            int scheduled = 0;
            int schedAt = Math.Min(numEvents, Environment.ProcessorCount);
            for (int i = 0; i < numEvents; i++)
            {
                var socketEvent = buffer[i];

                if (_handleToContextMap.TryGetValue(socketEvent.Data, out SocketAsyncContextWrapper contextWrapper))
                {
                    SocketAsyncContext context = contextWrapper.Context;

                    if (context.PreferInlineCompletions)
                    {
                        context.HandleEventsInline(socketEvent.Events);
                    }
                    else
                    {
                        Interop.Sys.SocketEvents events = context.HandleSyncEventsSpeculatively(socketEvent.Events);

                        if (events != Interop.Sys.SocketEvents.None)
                        {
                            scheduled += context.ProcessSyncScheduleAsyncEvents(events, schedLocal: false);
                        }
                    }
                }
            }

            AskForHelp();
        }

        private void AskForHelp()
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        void IThreadPoolWorkItem.Execute()
        {
            HelpOnce();
        }

        private void FreeNativeResources()
        {
            if (_buffer != null)
            {
                Interop.Sys.FreeSocketEventBuffer(_buffer);
            }
            if (_port != (IntPtr)(-1))
            {
                Interop.Sys.CloseSocketEventPort(_port);
            }
        }

        // struct wrapper is used in order to improve the performance of the epoll thread hot path by up to 3% of some TechEmpower benchmarks
        // the goal is to have a dedicated generic instantiation and using:
        // System.Collections.Concurrent.ConcurrentDictionary`2[System.IntPtr,System.Net.Sockets.SocketAsyncContextWrapper]::TryGetValueInternal(!0,int32,!1&)
        // instead of:
        // System.Collections.Concurrent.ConcurrentDictionary`2[System.IntPtr,System.__Canon]::TryGetValueInternal(!0,int32,!1&)
        private readonly struct SocketAsyncContextWrapper
        {
            public SocketAsyncContextWrapper(SocketAsyncContext context) => Context = context;

            internal SocketAsyncContext Context { get; }
        }

        private readonly struct SocketIOEvent
        {
            public SocketAsyncContext Context { get; }
            public Interop.Sys.SocketEvents Events { get; }

            public SocketIOEvent(SocketAsyncContext context, Interop.Sys.SocketEvents events)
            {
                Context = context;
                Events = events;
            }
        }
    }
}

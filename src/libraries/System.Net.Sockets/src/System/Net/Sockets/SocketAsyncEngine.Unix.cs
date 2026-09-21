// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets
{
    internal sealed unsafe class SocketAsyncEngine
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

        // The events of a batch are handed to the thread pool as a single work item per batch, which
        // claims and runs them one at a time. Draining a run of events inside one work item, rather
        // than returning to the pool between them, keeps a batch from being interleaved with the
        // continuations its own handlers produce - those land on the worker's local queue and, being
        // LIFO, would otherwise run before the rest of the batch.
        // Capping the batch size bounds how long one work item can hold a worker, at the cost of
        // posting more of them - which is fine, since it only happens for batches that are large to
        // begin with. Anything in the 8 - 64 range performs the same.
        private const int MaxBatchSize = 32;

        // Set when some socket is given a PreferInlineCompletions value that differs from the
        // process-wide default above. That is done through an experimental API and virtually never
        // happens, so until it does, the event loop can use the default without reading per-context state.
        // This is a one-way latch - it is never reset back to false.
        private static bool s_anyInlineCompletionsOverride;

        internal static void OnInlineCompletionsOverride() => s_anyInlineCompletionsOverride = true;

        private static bool PrefersInlineCompletions(SocketAsyncContext context) =>
            // InlineSocketCompletionsEnabled is a static readonly bool, so in the common case this
            // folds into a constant and the context is not touched at all.
            s_anyInlineCompletionsOverride ? context.PreferInlineCompletions : InlineSocketCompletionsEnabled;

        private static int GetEngineCount()
        {
            // The responsibility of SocketAsyncEngine is to get notifications from epoll|kqueue
            // and schedule corresponding work items to ThreadPool (socket reads and writes).
            //
            // Using TechEmpower benchmarks that generate a LOT of SMALL socket reads and writes under a VERY HIGH load
            // we have observed that a single engine is capable of keeping busy up to thirty x64 and twelve ARM64 CPU Cores.
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
                ? 12
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

        /// <summary>
        /// Each <see cref="SocketAsyncContext"/> is assigned an index into this table while registered with a <see cref="SocketAsyncEngine"/>.
        /// <para>The index is used as the <see cref="Interop.Sys.SocketEvent.Data"/> to quickly map events to <see cref="SocketAsyncContext"/>s.</para>
        /// <para>It is also stored in <see cref="SocketAsyncContext.GlobalContextIndex"/> so that we can efficiently remove it when unregistering the socket.</para>
        /// </summary>
        private static SocketAsyncContext?[] s_registeredContexts = [];
        private static readonly Queue<int> s_registeredContextsFreeList = [];

        private readonly IntPtr _port;
        private readonly Interop.Sys.SocketEvent* _buffer;

        //
        // Pool of reusable batches to avoid allocating one per event batch.
        //
        private readonly ConcurrentQueue<SocketIOEventBatch> _batchPool = new ConcurrentQueue<SocketIOEventBatch>();

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
            Debug.Assert(context.GlobalContextIndex == -1);

            lock (s_registeredContextsFreeList)
            {
                if (!s_registeredContextsFreeList.TryDequeue(out int index))
                {
                    int previousLength = s_registeredContexts.Length;
                    int newLength = Math.Max(4, 2 * previousLength);

                    Array.Resize(ref s_registeredContexts, newLength);

                    for (int i = previousLength + 1; i < newLength; i++)
                    {
                        s_registeredContextsFreeList.Enqueue(i);
                    }

                    index = previousLength;
                }

                Debug.Assert(s_registeredContexts[index] is null);

                s_registeredContexts[index] = context;
                context.GlobalContextIndex = index;
            }

            error = Interop.Sys.TryChangeSocketEventRegistration(_port, socketHandle, Interop.Sys.SocketEvents.None,
                Interop.Sys.SocketEvents.Read | Interop.Sys.SocketEvents.Write, context.GlobalContextIndex);
            if (error == Interop.Error.SUCCESS)
            {
                return true;
            }

            UnregisterSocket(context);
            return false;
        }

        public static void UnregisterSocket(SocketAsyncContext context)
        {
            Debug.Assert(context.GlobalContextIndex >= 0);
            Debug.Assert(ReferenceEquals(s_registeredContexts[context.GlobalContextIndex], context));

            lock (s_registeredContextsFreeList)
            {
                s_registeredContexts[context.GlobalContextIndex] = null;
                s_registeredContextsFreeList.Enqueue(context.GlobalContextIndex);
            }

            context.GlobalContextIndex = -1;
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

        private void EventLoop()
        {
            try
            {
                while (true)
                {
                    int numEvents = EventBufferCount;
                    Interop.Error err = Interop.Sys.WaitForSocketEvents(_port, _buffer, &numEvents);
                    if (err != Interop.Error.SUCCESS)
                    {
                        throw new InternalException(err);
                    }

                    // The native shim is responsible for ensuring this condition.
                    Debug.Assert(numEvents > 0, $"Unexpected numEvents: {numEvents}");

                    HandleAndDispatchSocketEvents(numEvents);
                }
            }
            catch (Exception e)
            {
                Environment.FailFast("Exception thrown from SocketAsyncEngine event loop: " + e.ToString(), e);
            }
        }

        // Handles the socket events currently in the buffer, collecting the ones that need to be
        // completed asynchronously into batches and posting each batch to the thread pool queue as one
        // item. A batch is drained by the workers that pick it up, one event at a time.
        //
        // The JIT is allowed to arbitrarily extend the lifetime of locals, which may retain SocketAsyncContext references,
        // indirectly preventing Socket instances to be finalized, despite being no longer referenced by user code.
        // To avoid this, the event handling logic is delegated to a non-inlined processing method so that the
        // SocketAsyncContext references held in its locals do not extend onto the EventLoop frame across the
        // (potentially long) WaitForSocketEvents wait.
        // See discussion: https://github.com/dotnet/runtime/issues/37064
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HandleAndDispatchSocketEvents(int numEvents)
        {
            SocketIOEventBatch? batch = null;
            int count = 0;

            foreach (var socketEvent in new ReadOnlySpan<Interop.Sys.SocketEvent>(_buffer, numEvents))
            {
                Debug.Assert((uint)socketEvent.Data < (uint)s_registeredContexts.Length);

                // The context may be null if the socket was unregistered right before the event was processed.
                // The slot in s_registeredContexts may have been reused by a different context, in which case the
                // incorrect socket will notice that no information is available yet and harmlessly retry, waiting for new events.
                SocketAsyncContext? context = s_registeredContexts[(uint)socketEvent.Data];

                if (context is not null)
                {
                    if (PrefersInlineCompletions(context))
                    {
                        context.HandleEventsInline(socketEvent.Events);
                    }
                    else
                    {
                        Interop.Sys.SocketEvents events = context.HandleSyncEventsSpeculatively(socketEvent.Events);

                        if (events != Interop.Sys.SocketEvents.None)
                        {
                            // Fill a pooled batch in place - no scratch buffer and no copy.
                            batch ??= RentBatch();
                            batch.Set(count++, context, events);

                            if (count == MaxBatchSize)
                            {
                                DispatchBatch(batch, count);
                                batch = null;
                                count = 0;
                            }
                        }
                    }
                }
            }

            if (batch is not null)
            {
                DispatchBatch(batch, count);
            }
        }

        private SocketIOEventBatch RentBatch() =>
            _batchPool.TryDequeue(out SocketIOEventBatch? batch) ?
                batch :
                new SocketIOEventBatch(_batchPool);

        private static void DispatchBatch(SocketIOEventBatch batch, int count)
        {
            batch.Prepare(count);
            ThreadPool.UnsafeQueueUserWorkItem(batch, preferLocal: false);
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

        private readonly struct PendingEvent(SocketAsyncContext context, Interop.Sys.SocketEvents events)
        {
            public readonly SocketAsyncContext Context = context;
            public readonly Interop.Sys.SocketEvents Events = events;
        }

        // A batch of socket events, dispatched to the thread pool as a single work item.
        //
        // Workers claim one event at a time with an interlocked increment, so a handler that blocks
        // holds up exactly the event it claimed and no others. Before running a handler a worker makes
        // sure another worker is coming for the remainder, so progress never depends on the current one
        // returning. That request is deduped and taken once per dequeue, so a batch costs about as many
        // enqueues as there are workers willing to help, not one per event.
        //
        // The instance is pooled and reused. Reuse is safe because it is reference counted rather than
        // versioned: _refs counts the queued work items plus the workers currently inside Execute, and
        // the batch only returns to the pool when that reaches zero. Nothing can reference it at that
        // point, so the engine can refill it and reset the cursor with plain writes, and a stale helper
        // can never claim a slot belonging to a later batch.
        private sealed class SocketIOEventBatch : IThreadPoolWorkItem
        {
            // A batch holds MaxBatchSize events inline, so it is much larger than a single event would
            // be. Cap the pool so it cannot grow without bound in edge cases; the number in flight per
            // engine is normally a handful.
            private const int MaxBatchPoolCount = 1024;

            // A limiter rather than a tuning knob: sized so a batch of normal-cost handlers finishes
            // well inside it and only a pathologically slow handler trips it.
            private static readonly long TicksPer50Us = Stopwatch.Frequency / 20_000;
            private static readonly long TicksPer1Ms = Stopwatch.Frequency / 1_000;

            private readonly ConcurrentQueue<SocketIOEventBatch> _pool;

            // Fixed size, allocated once with the instance.
            private readonly PendingEvent[] _items = new PendingEvent[MaxBatchSize];

            private int _current;
            private int _end;
            private int _refs;
            private int _helperRequested;

            public SocketIOEventBatch(ConcurrentQueue<SocketIOEventBatch> pool)
            {
                _pool = pool;
            }

            // Called by the engine thread, which holds the only reference to a pooled batch.
            public void Set(int index, SocketAsyncContext context, Interop.Sys.SocketEvents events)
            {
                _items[index] = new PendingEvent(context, events);
            }

            // Also engine-thread only. The enqueue that follows publishes these writes: the thread pool
            // queue provides the release, and the worker's dequeue the matching acquire.
            public void Prepare(int count)
            {
                _current = 0;
                _end = count;

                // One reference for the enqueue that is about to happen, which is also the first helper.
                _refs = 1;
                _helperRequested = 1;
            }

            void IThreadPoolWorkItem.Execute()
            {
                // The requested helper has arrived - allow another one to be requested.
                Volatile.Write(ref _helperRequested, 0);

                // Insurance is taken once per dequeue. Without this, a worker looping through the batch
                // would request a fresh helper every time an arriving helper cleared the flag, which
                // approaches one enqueue per event.
                bool insured = false;

                int remaining = _end - Volatile.Read(ref _current);
                long deadline =
                    Stopwatch.GetTimestamp() +
                    Math.Min((long)Math.Max(remaining, 1) * TicksPer50Us, TicksPer1Ms);

                while (true)
                {
                    // Full barrier, so the reads below cannot be hoisted above the claim.
                    int i = Interlocked.Increment(ref _current) - 1;
                    if (i >= _end)
                    {
                        break;
                    }

                    // HandleEvents may run user code, which may block or even wait on another event in
                    // this same batch, so make sure someone is coming for the rest before running it.
                    if (!insured && i + 1 < _end)
                    {
                        EnsureHelperRequested();
                        insured = true;
                    }

                    PendingEvent item = _items[i];

                    // Don't keep the context alive once it has been dispatched. Every slot below _end is
                    // claimed exactly once, so the array is fully cleared by the time the batch is reused.
                    _items[i] = default;

                    item.Context.HandleEvents(item.Events);

                    if (Stopwatch.GetTimestamp() >= deadline)
                    {
                        // Hand the rest off rather than keep deferring this worker's own continuations.
                        // The dedupe stays correct here: the flag being set means a helper is queued and
                        // has not started, and if it had started it cleared the flag on entry.
                        if (Volatile.Read(ref _current) < _end)
                        {
                            EnsureHelperRequested();
                        }

                        break;
                    }
                }

                Release();
            }

            private void EnsureHelperRequested()
            {
                if (Volatile.Read(ref _helperRequested) == 0 &&
                    Interlocked.Exchange(ref _helperRequested, 1) == 0)
                {
                    // Count the queued item before publishing it. The caller is inside Execute and so
                    // holds a reference, which means _refs cannot be resurrected from zero here.
                    Interlocked.Increment(ref _refs);
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
                }
            }

            private void Release()
            {
                // Not run under a finally: if a handler throws, the process is going down through the
                // thread pool's unhandled exception path, and leaking the batch is preferable to
                // returning a possibly inconsistent one to the pool.
                if (Interlocked.Decrement(ref _refs) == 0 && _pool.Count < MaxBatchPoolCount)
                {
                    _pool.Enqueue(this);
                }
            }
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Measures how long the runtime takes to suspend threads for a GC while worker threads run code that is
// adversarial to thread suspension: loops without calls, loops with calls, deep recursion, mutual tail calls,
// spin waits that depend on other threads, exceptions, irreducible loops and so on.
//
// Usage: corerun SuspensionLatency.dll [workload[,workload...]|all] [threads] [seconds] [gcIntervalMs]
//
// Compare the default mode with signalless mode (DOTNET_signalless=1) by running the same command with and
// without the environment variable. The reported suspension time is the time between the GCSuspendEEBegin and
// GCSuspendEEEnd runtime events. Throughput is reported as millions of inner iterations per second per thread.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

public static class SuspensionLatency
{
    private const int InnerIterations = 100_000;

    private static volatile bool s_stop;
    private static long[] s_iterations = Array.Empty<long>();
    private static int s_sink;

    private static readonly Dictionary<string, Action<int>> s_workloads = new Dictionary<string, Action<int>>
    {
        ["idle"] = Idle,
        ["loop"] = TightLoop,
        ["array"] = ArraySum,
        ["leafcall"] = LeafCallLoop,
        ["nonleafcall"] = NonLeafCallLoop,
        ["virtcall"] = VirtualCallLoop,
        ["multicall"] = MultiCallLoop,
        ["fib"] = Fibonacci,
        ["unwind"] = UnwindRecursion,
        ["descent"] = DescentRecursion,
        ["tailcall"] = MutualTailCalls,
        ["spinwait"] = SpinWaitPingPong,
        ["alloc"] = AllocationLoop,
        ["tryfinally"] = TryFinallyLoop,
        ["exceptions"] = ExceptionLoop,
        ["irreducible"] = IrreducibleLoop,
        ["switch"] = SwitchLoop,
        ["r2rsort"] = CoreLibSort,
        ["memcopy"] = LargeCopy,
    };

    public static int Main(string[] args)
    {
        string workloadArg = args.Length > 0 ? args[0] : "all";
        int threads = args.Length > 1 ? int.Parse(args[1]) : Math.Max(1, Environment.ProcessorCount / 2);
        double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 3;
        int gcIntervalMs = args.Length > 3 ? int.Parse(args[3]) : 10;

        string[] workloads = workloadArg == "all" ? s_workloads.Keys.ToArray() : workloadArg.Split(',');

        Console.WriteLine($"signalless={Environment.GetEnvironmentVariable("DOTNET_signalless") ?? "0"} threads={threads} seconds={seconds} gcIntervalMs={gcIntervalMs}");
        Console.WriteLine($"{"workload",-12} {"GCs",5} {"p50us",8} {"p90us",8} {"p99us",8} {"maxus",9} {"avgus",8} {"maxGCus",9} {"Mit/s/thr",10}");

        using var listener = new SuspensionListener();
        bool ok = true;
        foreach (string workload in workloads)
        {
            ok &= Measure(workload, s_workloads[workload], threads, seconds, gcIntervalMs, listener);
        }

        return ok ? 100 : 101;
    }

    private static bool Measure(string name, Action<int> workload, int threadCount, double seconds, int gcIntervalMs, SuspensionListener listener)
    {
        s_stop = false;
        s_iterations = new long[threadCount * 16];

        var threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            threads[i] = new Thread(() => workload(index), 256 * 1024 * 1024) { IsBackground = true };
            threads[i].Start();
        }

        // Let the code tier up before measuring.
        Thread.Sleep(1000);

        listener.Reset();
        long startIterations = TotalIterations();
        var total = Stopwatch.StartNew();
        long maxCollectTicks = 0;
        int collections = 0;
        while (total.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(gcIntervalMs);
            long before = Stopwatch.GetTimestamp();
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            maxCollectTicks = Math.Max(maxCollectTicks, Stopwatch.GetTimestamp() - before);
            collections++;
        }

        double elapsedSeconds = total.Elapsed.TotalSeconds;
        long iterations = TotalIterations() - startIterations;

        s_stop = true;
        foreach (Thread thread in threads)
        {
            if (!thread.Join(TimeSpan.FromSeconds(30)))
            {
                Console.WriteLine($"{name}: worker thread did not stop");
                return false;
            }
        }

        // Let the listener receive the last events.
        Thread.Sleep(200);
        double[] suspensions = listener.Suspensions();
        Array.Sort(suspensions);

        double Percentile(double p) => suspensions.Length == 0 ? double.NaN : suspensions[Math.Min(suspensions.Length - 1, (int)(p * suspensions.Length))];
        double maxCollectUs = maxCollectTicks * 1_000_000.0 / Stopwatch.Frequency;
        double throughput = iterations * (double)InnerIterations / elapsedSeconds / threadCount / 1_000_000;

        Console.WriteLine($"{name,-12} {suspensions.Length,5} {Percentile(0.5),8:F1} {Percentile(0.9),8:F1} {Percentile(0.99),8:F1} {(suspensions.Length == 0 ? double.NaN : suspensions[^1]),9:F1} {(suspensions.Length == 0 ? double.NaN : suspensions.Average()),8:F1} {maxCollectUs,9:F1} {throughput,10:F2}");
        return suspensions.Length > 0;
    }

    private static long TotalIterations()
    {
        long sum = 0;
        for (int i = 0; i < s_iterations.Length; i += 16)
        {
            sum += Volatile.Read(ref s_iterations[i]);
        }

        return sum;
    }

    private static void Count(int thread) => Interlocked.Increment(ref s_iterations[thread * 16]);

    private static void Idle(int thread)
    {
        while (!s_stop)
        {
            Thread.Sleep(1);
        }
    }

    private static void TightLoop(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations; i++)
            {
                x = x * 31 + i;
            }

            Count(thread);
        }

        s_sink = x;
    }

    private static void ArraySum(int thread)
    {
        int[] data = new int[InnerIterations];
        data.AsSpan().Fill(thread);
        long sum = 0;
        while (!s_stop)
        {
            for (int i = 0; i < data.Length; i++)
            {
                sum += data[i];
            }

            Count(thread);
        }

        s_sink = (int)sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Leaf(int a, int b) => a * 31 + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int NonLeaf(int a, int b) => Leaf(a, b) + 1;

    private static void LeafCallLoop(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations; i++)
            {
                x = Leaf(x, i);
            }

            Count(thread);
        }

        s_sink = x;
    }

    private static void NonLeafCallLoop(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations; i++)
            {
                x = NonLeaf(x, i);
            }

            Count(thread);
        }

        s_sink = x;
    }

    private interface IOperation
    {
        int Apply(int a, int b);
    }

    private sealed class Multiply : IOperation
    {
        public int Apply(int a, int b) => a * 31 + b;
    }

    private sealed class Add : IOperation
    {
        public int Apply(int a, int b) => a + b + 7;
    }

    private static void VirtualCallLoop(int thread)
    {
        IOperation[] operations = { new Multiply(), new Add() };
        int x = thread;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations; i++)
            {
                x = operations[i & 1].Apply(x, i);
            }

            Count(thread);
        }

        s_sink = x;
    }

    private static void MultiCallLoop(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations; i++)
            {
                x = Leaf(Leaf(Leaf(Leaf(x, i), 1), 2), 3);
            }

            Count(thread);
        }

        s_sink = x;
    }

    private static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

    private static void Fibonacci(int thread)
    {
        // Fib(16) makes about as many calls as there are inner iterations in the other workloads.
        int x = 0;
        while (!s_stop)
        {
            for (int i = 0; i < 31; i++)
            {
                x += Fib(16);
            }

            Count(thread);
        }

        s_sink = x;
    }

    // The work happens after the recursive call returns, while unwinding.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Unwind(int depth)
    {
        if (depth == 0)
        {
            return 0;
        }

        int x = Unwind(depth - 1);
        return Leaf(Leaf(Leaf(Leaf(Leaf(x, depth), 1), 2), 3), 4);
    }

    private static void UnwindRecursion(int thread)
    {
        int x = 0;
        while (!s_stop)
        {
            x += Unwind(InnerIterations / 5);
            Count(thread);
        }

        s_sink = x;
    }

    // The work happens before the recursive call, while descending.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Descent(int depth, int x)
    {
        if (depth == 0)
        {
            return x;
        }

        return Descent(depth - 1, Leaf(Leaf(Leaf(Leaf(Leaf(x, depth), 1), 2), 3), 4)) + 1;
    }

    private static void DescentRecursion(int thread)
    {
        int x = 0;
        while (!s_stop)
        {
            x += Descent(InnerIterations / 5, thread);
            Count(thread);
        }

        s_sink = x;
    }

    // Opportunistic tail calls between these two make a loop without any returns or back edges. Calling through
    // mutable function pointers keeps the JIT from inlining them, without the NoInlining that prevents tail calls.
    private static unsafe delegate*<int, int, int> s_pingTail = &PingTail;
    private static unsafe delegate*<int, int, int> s_pongTail = &PongTail;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int PingTail(int n, int x) => n == 0 ? x : s_pongTail(n - 1, x * 31 + n);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int PongTail(int n, int x) => n == 0 ? x : s_pingTail(n - 1, x + 7);

    private static unsafe void MutualTailCalls(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            x = s_pingTail(InnerIterations, x);
            Count(thread);
        }

        s_sink = x;
    }

    private static int[] s_turns = new int[64 * 16];

    // Pairs of threads take turns. A thread that waits for its turn spins in a loop without calls, while the other
    // thread allocates and so triggers GCs that need the spinning thread to stop.
    private static void SpinWaitPingPong(int thread)
    {
        int pair = thread / 2 * 16;
        int me = thread % 2;
        object keep = null;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations / 100; i++)
            {
                while (Volatile.Read(ref s_turns[pair]) != me)
                {
                    if (s_stop)
                    {
                        Volatile.Write(ref s_turns[pair], 1 - me);
                        return;
                    }
                }

                keep = new byte[64];
                Volatile.Write(ref s_turns[pair], 1 - me);
            }

            Count(thread);
        }

        Volatile.Write(ref s_turns[pair], 1 - me);
        GC.KeepAlive(keep);
    }

    private static void AllocationLoop(int thread)
    {
        object keep = null;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations; i++)
            {
                keep = new object();
            }

            Count(thread);
        }

        GC.KeepAlive(keep);
    }

    private static int s_finallyCount;

    private static void TryFinallyLoop(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations; i++)
            {
                try
                {
                    x = x * 31 + i;
                    if (x == 42)
                    {
                        break;
                    }
                }
                finally
                {
                    if (x == 17)
                    {
                        s_finallyCount++;
                    }
                }
            }

            Count(thread);
        }

        s_sink = x;
    }

    private static readonly Exception s_exception = new InvalidOperationException();

    private static void ExceptionLoop(int thread)
    {
        int x = 0;
        while (!s_stop)
        {
            for (int i = 0; i < InnerIterations / 1000; i++)
            {
                try
                {
                    throw s_exception;
                }
                catch (InvalidOperationException)
                {
                    x++;
                }
            }

            Count(thread);
        }

        s_sink = x;
    }

    // Two blocks that jump to each other, entered at either one: an irreducible loop.
    private static int Irreducible(int start, int count)
    {
        int x = start;
        int i = 0;
        if ((start & 1) != 0)
        {
            goto Second;
        }

    First:
        x = x * 31 + i;
        if (++i >= count)
        {
            return x;
        }

    Second:
        x = x + 7 + i;
        if (++i >= count)
        {
            return x;
        }

        goto First;
    }

    private static void IrreducibleLoop(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            x = Irreducible(x, InnerIterations);
            Count(thread);
        }

        s_sink = x;
    }

    private static int StateMachine(int seed, int count)
    {
        int state = seed & 3;
        int x = seed;
        for (int i = 0; i < count; i++)
        {
            switch (state)
            {
                case 0: x = x * 31 + i; state = 1; break;
                case 1: x = x + 7; state = 2; break;
                case 2: x = x ^ i; state = 3; break;
                default: x = x - 3; state = 0; break;
            }
        }

        return x;
    }

    private static void SwitchLoop(int thread)
    {
        int x = thread;
        while (!s_stop)
        {
            x = StateMachine(x, InnerIterations);
            Count(thread);
        }

        s_sink = x;
    }

    private static void CoreLibSort(int thread)
    {
        var random = new Random(thread);
        int[] source = new int[InnerIterations];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = random.Next();
        }

        int[] data = new int[source.Length];
        while (!s_stop)
        {
            source.CopyTo(data, 0);
            Array.Sort(data);
            Count(thread);
        }
    }

    private static void LargeCopy(int thread)
    {
        byte[] source = new byte[64 * 1024 * 1024];
        byte[] destination = new byte[source.Length];
        while (!s_stop)
        {
            source.AsSpan().CopyTo(destination);
            Count(thread);
        }
    }

    // Records the duration of each runtime suspension from the GCSuspendEEBegin/GCSuspendEEEnd events.
    private sealed class SuspensionListener : EventListener
    {
        private const int GCSuspendEEEndEventId = 8;
        private const int GCSuspendEEBeginEventId = 9;
        private const EventKeywords GCKeyword = (EventKeywords)0x1;

        private readonly List<double> _suspensions = new List<double>();
        private DateTime _suspendBegin;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Informational, GCKeyword);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            switch (eventData.EventId)
            {
                case GCSuspendEEBeginEventId:
                    _suspendBegin = eventData.TimeStamp;
                    break;

                case GCSuspendEEEndEventId:
                    if (_suspendBegin != default)
                    {
                        lock (_suspensions)
                        {
                            _suspensions.Add((eventData.TimeStamp - _suspendBegin).Ticks / 10.0);
                        }

                        _suspendBegin = default;
                    }

                    break;
            }
        }

        public void Reset()
        {
            lock (_suspensions)
            {
                _suspensions.Clear();
            }
        }

        public double[] Suspensions()
        {
            lock (_suspensions)
            {
                return _suspensions.ToArray();
            }
        }
    }
}

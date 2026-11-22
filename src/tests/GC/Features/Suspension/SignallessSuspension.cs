// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using TestLibrary;
using Xunit;

// Runs with DOTNET_signalless=1 (see the project file). In signalless mode the runtime never interrupts a thread to
// suspend it for a GC, so all kinds of long running managed code must reach a GC poll on their own. A missing poll
// makes the GC wait until the code completes, which these workloads never do while the GCs run. The threads stop for
// the GCs at the polls with references in registers, which the compacting GCs must find and update.
public static class SignallessSuspension
{
    private static volatile bool s_stop;
    private static volatile string s_failure;
    private static int s_sink;
    private static readonly int[] s_turns = new int[2 * 16];

    [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
    public static void GCsCompleteWhileThreadsRunCodeWithoutTransitions()
    {
        Action[] workloads =
        {
            TightLoop,
            LeafCallLoop,
            MutualTailCalls,
            DeepRecursion,
            IrreducibleLoop,
            TryFinallyLoop,
            SwitchLoop,
            ListWalkLoop,
            ArrayWalkLoop,
            HandlerLoop,
            ClearLoop,
            ClearInCalleeLoop,
            () => SpinWaitPingPong(0),
            () => SpinWaitPingPong(1),
        };

        var threads = new Thread[workloads.Length];
        for (int i = 0; i < workloads.Length; i++)
        {
            threads[i] = new Thread(new ThreadStart(workloads[i]), 64 * 1024 * 1024) { IsBackground = true };
            threads[i].Start();
        }

        try
        {
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(10);

                var stopwatch = Stopwatch.StartNew();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"GC {i} took {stopwatch.Elapsed}");
            }
        }
        finally
        {
            s_stop = true;
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }

        Assert.True(s_failure is null, s_failure);
    }

    private static void Fail(string message)
    {
        s_failure ??= message;
        s_stop = true;
    }

    // ControlledExecution aborts the thread at a GC poll, and exception handling unwinds the frames above the poll
    // starting from the registers that the poll captured. The caller keeps doubles in callee saved registers across
    // the abort, which must not get the values of registers that the poll did not capture.
    [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsCoreCLR), nameof(PlatformDetection.IsMultithreadingSupported))]
    public static void AbortAtPollPreservesCalleeSavedFloatingPointRegisters()
    {
        Assert.Equal(3 * 40.0, SumAcrossAborts(1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5));
    }

    // As the loop uses the doubles after every call, they stay in the callee saved floating point registers.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double SumAcrossAborts(double a, double b, double c, double d, double e, double f, double g, double h)
    {
        double sum = 0;
        for (int i = 0; i < 3; i++)
        {
            RunAndAbort();
            sum += a + b + c + d + e + f + g + h;
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunAndAbort()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(100);
        try
        {
#pragma warning disable SYSLIB0046 // ControlledExecution.Run is obsolete
            ControlledExecution.Run(FillStackAndLoop, cancellation.Token);
#pragma warning restore SYSLIB0046
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The poll saves the registers on the stack below this method, where FillStack leaves NaNs in place of any that
    // it does not save. Optimizing the method right away avoids OSR, which would use the stack there too.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FillStackAndLoop()
    {
        FillStack();
        while (true)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FillStack()
    {
        Span<byte> bytes = stackalloc byte[4096];
        bytes.Fill(0xFF);
    }

    private static void TightLoop()
    {
        int x = 0;
        while (!s_stop)
        {
            x = x * 31 + 7;
        }

        s_sink = x;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Leaf(int a, int b) => a * 31 + b;

    private static void LeafCallLoop()
    {
        int x = 0;
        while (!s_stop)
        {
            x = Leaf(x, 7);
        }

        s_sink = x;
    }

    // Opportunistic tail calls between these two make a loop without returns or back edges. Calling through
    // mutable function pointers keeps the JIT from inlining them, without the NoInlining that prevents tail calls.
    private static unsafe delegate*<int, int, int> s_pingTail = &PingTail;
    private static unsafe delegate*<int, int, int> s_pongTail = &PongTail;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int PingTail(int n, int x) => (n == 0) || s_stop ? x : s_pongTail(n - 1, x * 31 + n);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int PongTail(int n, int x) => (n == 0) || s_stop ? x : s_pingTail(n - 1, x + 7);

    private static unsafe void MutualTailCalls()
    {
        int x = 0;
        while (!s_stop)
        {
            x = s_pingTail(100_000, x);
        }

        s_sink = x;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int depth) => depth == 0 ? 0 : Leaf(Recurse(depth - 1), depth);

    private static void DeepRecursion()
    {
        int x = 0;
        while (!s_stop)
        {
            x += Recurse(100_000);
        }

        s_sink = x;
    }

    private static void IrreducibleLoop()
    {
        int x = 0;
        int i = 0;
        if (Environment.ProcessorCount > 0)
        {
            goto Second;
        }

    First:
        x = x * 31 + i++;
        if (s_stop)
        {
            s_sink = x;
            return;
        }

    Second:
        x = x + 7 + i++;
        goto First;
    }

    private static void TryFinallyLoop()
    {
        int x = 0;
        int finallyCount = 0;
        while (!s_stop)
        {
            try
            {
                x = x * 31 + 7;
                if (x == 42)
                {
                    break;
                }
            }
            finally
            {
                if (x == 17)
                {
                    finallyCount++;
                }
            }
        }

        s_sink = x + finallyCount;
    }

    private static void SwitchLoop()
    {
        int x = 0;
        int state = 0;
        while (!s_stop)
        {
            switch (state)
            {
                case 0: x = x * 31 + 1; state = 1; break;
                case 1: x = x + 7; state = 2; break;
                case 2: x = x ^ 5; state = 3; break;
                default: x = x - 3; state = 0; break;
            }
        }

        s_sink = x;
    }

    private sealed class Node
    {
        public Node Next;
        public int Value;
    }

    private const int ListLength = 1000;
    private const int ListSum = ListLength * (ListLength - 1) / 2;

    // Lists and arrays are rebuilt from new objects all the time, so that the compacting GCs keep moving them.
    private static Node NewList()
    {
        Node head = null;
        for (int i = 0; i < ListLength; i++)
        {
            head = new Node { Next = head, Value = i };
        }

        return head;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SumList(Node node)
    {
        int sum = 0;
        for (; node != null; node = node.Next)
        {
            sum += node.Value;
        }

        return sum;
    }

    private static void ListWalkLoop()
    {
        while (!s_stop)
        {
            Node head = NewList();
            for (int i = 0; i < 100; i++)
            {
                int sum = SumList(head);
                if (sum != ListSum)
                {
                    Fail($"{nameof(SumList)} returned {sum} instead of {ListSum}");
                }
            }
        }
    }

    // The JIT walks the array with an interior pointer.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long SumArray(int[] array)
    {
        long sum = 0;
        for (int i = 0; i < array.Length; i++)
        {
            sum += array[i];
        }

        return sum;
    }

    private static void ArrayWalkLoop()
    {
        const int Length = 10_000;
        const long Expected = (long)Length * (Length - 1) / 2;
        while (!s_stop)
        {
            int[] array = new int[Length];
            for (int i = 0; i < array.Length; i++)
            {
                array[i] = i;
            }

            for (int i = 0; i < 100; i++)
            {
                long sum = SumArray(array);
                if (sum != Expected)
                {
                    Fail($"{nameof(SumArray)} returned {sum} instead of {Expected}");
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw() => throw new InvalidOperationException();

    // Walks the list in a catch funclet.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SumListInHandler(Node head, int rounds)
    {
        try
        {
            Throw();
        }
        catch (InvalidOperationException)
        {
            int sum = 0;
            for (int i = 0; i < rounds; i++)
            {
                for (Node node = head; node != null; node = node.Next)
                {
                    sum += node.Value;
                }
            }

            return sum;
        }

        return 0;
    }

    private static void HandlerLoop()
    {
        while (!s_stop)
        {
            int sum = SumListInHandler(NewList(), 100);
            if (sum != 100 * ListSum)
            {
                Fail($"{nameof(SumListInHandler)} returned {sum} instead of {100 * ListSum}");
            }
        }
    }

    // Clearing a buffer of a constant size is a call that the JIT expands into stores late, so the loop does not
    // make any calls.
    private static void ClearLoop()
    {
        Span<byte> buffer = stackalloc byte[32];
        while (!s_stop)
        {
            buffer.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Clear32(ref byte destination) => MemoryMarshal.CreateSpan(ref destination, 32).Clear();

    private static void ClearInCalleeLoop()
    {
        byte[] buffer = new byte[32];
        while (!s_stop)
        {
            Clear32(ref buffer[0]);
        }
    }

    // Two threads take turns: the one that waits spins without making any calls, while the other one allocates,
    // which triggers GCs that need the spinning thread to stop.
    private static void SpinWaitPingPong(int me)
    {
        object keep = null;
        while (!s_stop)
        {
            while (Volatile.Read(ref s_turns[0]) != me)
            {
                if (s_stop)
                {
                    break;
                }
            }

            keep = new byte[128];
            Volatile.Write(ref s_turns[0], 1 - me);
        }

        Volatile.Write(ref s_turns[0], 1 - me);
        GC.KeepAlive(keep);
    }
}

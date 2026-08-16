// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Xunit;

// Stresses the window where a runtime async method suspends on an IValueTaskSource while the
// dispatcher loop is on the stack, and the source completes between the awaiter's IsCompleted
// check and OnCompleted. In that case the source would normally queue the continuation to avoid
// stack diving; instead it can hand the continuation back to the dispatcher.
public class Runtime_AsyncVtsInlineResume
{
    private const int Iterations = 20000;

    [Fact]
    public static void TestEntryPoint()
    {
        Assert.Equal(Iterations, RaceCompletion().GetAwaiter().GetResult());
    }

    private static async Task<int> RaceCompletion()
    {
        int observed = 0;

        for (int i = 0; i < Iterations; i++)
        {
            var source = new RacingSource();

            var completer = new Thread(() => source.Complete(i)) { IsBackground = true };
            completer.Start();

            // Ensure we are running under the dispatcher loop, so the inline resume
            // path is reachable for the await below.
            await Task.Yield();

            observed += await source.ValueTask().ConfigureAwait(false) == i ? 1 : 0;

            completer.Join();
        }

        return observed;
    }

    private sealed class RacingSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };

        public ValueTask<int> ValueTask() => new ValueTask<int>(this, _core.Version);

        public void Complete(int value) => _core.SetResult(value);

        public int GetResult(short token) => _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}

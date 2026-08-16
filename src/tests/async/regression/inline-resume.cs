// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

// Stresses the window where a runtime async method suspends on a Task while the
// dispatcher loop is on the stack, and that Task completes between the awaiter's
// IsCompleted check and AddTaskContinuation. In that case the continuation cannot
// be registered on the antecedent and is resumed by the dispatcher directly.
public class Runtime_AsyncInlineResume
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
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Complete the task from another thread so that completion can land in the
            // narrow window between the IsCompleted check and the continuation registration.
            var completer = new Thread(() => tcs.TrySetResult(i)) { IsBackground = true };
            completer.Start();

            // Ensure we are running under the dispatcher loop (a real suspension happened),
            // so the inline-resume path is reachable for the await below.
            await Task.Yield();

            observed += await tcs.Task == i ? 1 : 0;

            completer.Join();
        }

        return observed;
    }

    [Fact]
    public static void ConfigureAwaitFalseIsHonored()
    {
        Assert.True(ConfiguredRace().GetAwaiter().GetResult());
    }

    private static async Task<bool> ConfiguredRace()
    {
        for (int i = 0; i < 2000; i++)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completer = new Thread(() => tcs.TrySetResult()) { IsBackground = true };
            completer.Start();

            await Task.Yield();
            await tcs.Task.ConfigureAwait(false);

            completer.Join();
        }

        return true;
    }
}

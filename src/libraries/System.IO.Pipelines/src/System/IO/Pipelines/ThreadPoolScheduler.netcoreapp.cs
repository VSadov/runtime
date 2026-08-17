// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Pipelines
{
    internal sealed class ThreadPoolScheduler : PipeScheduler
    {
        public override void Schedule(Action<object?> action, object? state)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(action, state, preferLocal: false);
        }

        internal override void UnsafeSchedule(Action<object?> action, object? state)
        {
            // Scheduling here means the continuation was not to run on the completing stack, so do
            // not bias it back towards this thread: the local queue is LIFO and this thread is the
            // most likely to pick it up next.
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(action, state, preferLocal: false);
        }

        internal override void UnsafeSchedule(Action<object?> action, object? state, bool preferLocal)
        {
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(action, state, preferLocal);
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading.Channels
{
    internal partial class AsyncOperation : IThreadPoolWorkItem
    {
        void IThreadPoolWorkItem.Execute() => SetCompletionAndInvokeContinuation();

        // The continuation was explicitly asked not to inline, so do not bias
        // it towards this thread.
        private void UnsafeRunContinuationAsynchronously() =>
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);

        private static void Unregister(CancellationTokenRegistration registration) =>
            registration.Unregister();
    }
}

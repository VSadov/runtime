// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Tracing;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        /// <summary>
        /// The worker thread infastructure for the CLR thread pool.
        /// </summary>
        private static class WorkerThread
        {

            // This value represents an assumption of how much uncommitted stack space a worker thread may use in the future.
            // Used in calculations to estimate when to throttle the rate of thread injection to reduce the possibility of
            // preexisting threads from running out of memory when using new stack space in low-memory situations.
            public const int EstimatedAdditionalStackUsagePerThreadBytes = 64 << 10; // 64 KB

            private static readonly ThreadStart s_workerThreadStart = WorkerThreadStart;

            private static void WorkerThreadStart()
            {
                Thread.CurrentThread.SetThreadPoolWorkerThreadName();

                PortableThreadPool threadPoolInstance = ThreadPoolInstance;

                if (NativeRuntimeEventSource.Log.IsEnabled())
                {
                    NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadStart(
                        (uint)threadPoolInstance._separated.counts.VolatileRead().NumExistingThreads);
                }

                LowLevelLock threadAdjustmentLock = threadPoolInstance._threadAdjustmentLock;
                LowLevelLifoSemaphore semaphore = threadPoolInstance._semaphore;

                while (true)
                {
                    bool spinWait = true;
                    while (semaphore.Wait(ThreadPoolThreadTimeoutMs, spinWait))
                    {
                        spinWait = true;
                        while (TakeActiveRequest(threadPoolInstance))
                        {
                            if (!ThreadPoolWorkQueue.Dispatch())
                            {
                                // Don't spin-wait on the semaphore next time if the thread was actively stopped from processing work,
                                // as it's unlikely that the worker thread count goal would be increased again so soon afterwards that
                                // the semaphore would be released within the spin-wait window
                                spinWait = false;
                                break;
                            }

                            if (threadPoolInstance._separated.numRequestedWorkers <= 0)
                            {
                                break;
                            }

                            // In highly bursty cases with short bursts of work, especially in the portable thread pool
                            // implementation, worker threads are being released and entering Dispatch very quickly, not finding
                            // much work in Dispatch, and soon afterwards going back to Dispatch, causing extra thrashing on
                            // data and some interlocked operations, and similarly when the thread pool runs out of work. Since
                            // there is a pending request for work, introduce a slight delay before serving the next request.
                            // The spin-wait is mainly for when the sleep is not effective due to there being no other threads
                            // to schedule.
                            Thread.UninterruptibleSleep0();
                            if (!Environment.IsSingleProcessor)
                            {
                                Thread.SpinWait(1);
                            }
                        }
                    }

                    threadAdjustmentLock.Acquire();
                    try
                    {
                        // At this point, the thread's wait timed out. We are shutting down this thread.
                        // We are going to decrement the number of existing threads to no longer include this one
                        // and then change the max number of threads in the thread pool to reflect that we don't need as many
                        // as we had. Finally, we are going to tell hill climbing that we changed the max number of threads.
                        ThreadCounts counts = threadPoolInstance._separated.counts;
                        while (true)
                        {
                            // Since this thread is currently registered as an existing thread, if more work comes in meanwhile,
                            // this thread would be expected to satisfy the new work. Ensure that NumExistingThreads is not
                            // decreased below NumProcessingWork, as that would be indicative of such a case.
                            if (counts.NumExistingThreads <= threadPoolInstance.SemaphoreCount)
                            {
                                // In this case, enough work came in that this thread should not time out and should go back to work.
                                break;
                            }

                            ThreadCounts newCounts = counts;
                            short newNumExistingThreads = --newCounts.NumExistingThreads;
                            short newNumThreadsGoal =
                                Math.Max(
                                    threadPoolInstance.MinThreadsGoal,
                                    Math.Min(newNumExistingThreads, counts.NumThreadsGoal));
                            newCounts.NumThreadsGoal = newNumThreadsGoal;

                            ThreadCounts oldCounts =
                                threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);
                            if (oldCounts == counts)
                            {
                                if (NativeRuntimeEventSource.Log.IsEnabled())
                                {
                                    NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadStop((uint)newNumExistingThreads);
                                }
                                return;
                            }

                            counts = oldCounts;
                        }
                    }
                    finally
                    {
                        threadAdjustmentLock.Release();
                    }
                }
            }

            internal static void MaybeAddWorkingWorker(PortableThreadPool threadPoolInstance)
            {
                int numExistingThreads, newNumExistingThreads;
                ThreadCounts counts = threadPoolInstance._separated.counts;
                while (true)
                {
                    int semCount = (short)threadPoolInstance.SemaphoreCount;
                    if (semCount >= counts.NumThreadsGoal)
                    {
                        return;
                    }

                    numExistingThreads = counts.NumExistingThreads;
                    newNumExistingThreads = Math.Max(numExistingThreads, semCount + 1);

                    ThreadCounts newCounts = counts;
                    newCounts.NumExistingThreads = (short)newNumExistingThreads;

                    ThreadCounts oldCounts = threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);

                    if (oldCounts == counts)
                    {
                        break;
                    }

                    counts = oldCounts;
                }

                int toCreate = newNumExistingThreads - numExistingThreads;
                int toRelease = 1;

                if (toRelease > 0)
                {
                    threadPoolInstance._semaphore.Release(toRelease);
                }

                while (toCreate > 0)
                {
                    if (TryCreateWorkerThread())
                    {
                        toCreate--;
                        continue;
                    }

                    counts = threadPoolInstance._separated.counts;
                    while (true)
                    {
                        ThreadCounts newCounts = counts;
                        newCounts.NumExistingThreads -= (short)toCreate;

                        ThreadCounts oldCounts = threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);
                        if (oldCounts == counts)
                        {
                            break;
                        }
                        counts = oldCounts;
                    }
                    break;
                }
            }

            /// <summary>
            /// Returns if the current thread should stop processing work on the thread pool.
            /// A thread should stop processing work on the thread pool when work remains only when
            /// there are more worker threads in the thread pool than we currently want.
            /// </summary>
            /// <returns>Whether or not this thread should stop processing work even if there is still work in the queue.</returns>
            internal static bool ShouldStopProcessingWorkNow(PortableThreadPool threadPoolInstance)
            {
                ThreadCounts counts = threadPoolInstance._separated.counts;
                if (threadPoolInstance.SemaphoreCount > counts.NumThreadsGoal)
                {
                    return true;
                }

                return false;
            }

            private static bool TakeActiveRequest(PortableThreadPool threadPoolInstance)
            {
                int count = threadPoolInstance._separated.numRequestedWorkers;
                while (count > 0)
                {
                    int prevCount = Interlocked.CompareExchange(ref threadPoolInstance._separated.numRequestedWorkers, count - 1, count);
                    if (prevCount == count)
                    {
                        return true;
                    }
                    count = prevCount;
                }
                return false;
            }

            private static bool TryCreateWorkerThread()
            {
                try
                {
                    // Thread pool threads must start in the default execution context without transferring the context, so
                    // using UnsafeStart() instead of Start()
                    Thread workerThread = new Thread(s_workerThreadStart);
                    workerThread.IsThreadPoolThread = true;
                    workerThread.IsBackground = true;
                    // thread name will be set in thread proc
                    workerThread.UnsafeStart();
                }
                catch (ThreadStartException)
                {
                    return false;
                }
                catch (OutOfMemoryException)
                {
                    return false;
                }

                return true;
            }
        }
    }
}

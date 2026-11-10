// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        /// <summary>
        /// The worker thread infrastructure for the CLR thread pool.
        /// </summary>
        private static partial class WorkerThread
        {
            private static readonly short ThreadsToKeepAlive = DetermineThreadsToKeepAlive();

            private static readonly short SpuriousDispatchNoSpinThreshold = DetermineSpuriousDispatchNoSpinThreshold();

            // Park an empty dispatch without spinning when at least this many other workers are already spinning.
            // 0 disables the check, except for the cap and the limit on fast wakes below; a non-zero value replaces both.
            private static readonly short SpuriousDispatchSpinnerLimit = DetermineSpuriousDispatchSpinnerLimit();

            // While most of the recent spin claims find no work (see LowLevelLifoSemaphore.SpinCapActive), the limit above
            // is SpinCapSpinners when it is not set. On by default, SpinCapOnEmpty=0 turns it off.
            //
            // What the cap and the two thresholds of LowLevelLifoSemaphore (SpinClaimShareHigh, SpinClaimShareLow) rest on, from
            // the measurements of this change. The share is that of the spin claims whose first dispatch found no work:
            // - Mix cells (an async chain next to an external producer): shares of 0.69-0.79, with the cap on for the whole
            //   window and no switching. The cap cut the CPU per chain operation by 13-76% on WSL and by 21-60% on Windows.
            // - WSL async cells: medians of 0.36-0.46, single runs from 0.29 to 0.62. With the cap on, it was on for at most a
            //   quarter of a window; without it the flag was on for the whole window in one run of the state-machine chain.
            // - Windows JSON 16/16: 0.30. Windows WS 16/24: 0.24.
            // - Every other cell that was measured: a median of 0.19 or lower.
            // A static limit of SpinCapSpinners (SpuriousDispatchSpinnerLimit=2) also saved CPU in cells whose share stayed below
            // SpinClaimShareHigh: Windows JSON 16/16, Windows WS 16/24, the 4-chain mix and the async state-machine chain. The
            // cap does not engage there.
            private static readonly bool SpinCapOnEmpty = DetermineSpinCapOnEmpty();

            // While most of the wakes after long parks are fast (see LowLevelLifoSemaphore.SpinLimitEngaged), the limit above is
            // SpinCapSpinners when it is not set. On by default, SpinLimitOnFastWakes=0 turns it off. Where workers that were
            // parked for a long time come back quickly, the spinners that are kept to avoid a wake cost more CPU than the wakes
            // do; where those wakes are slow, the spinners are what keeps the work from waiting for them. The semaphore decides
            // from the wakes it measures, not from the operating system or the processor.
            private static readonly bool SpinLimitOnFastWakes = DetermineSpinLimitOnFastWakes();

            // The spinners that are left while the cap is on. 2 is the value that was measured with the cap, and as the
            // static limit in the sweeps.
            private const short SpinCapSpinners = 2;

            // Stopwatch ticks; -1 when the recency check is disabled.
            private static readonly long SpuriousDispatchExternalWorkItemRecencyTicks = DetermineSpuriousDispatchExternalWorkItemRecencyTicks();

            // Producers publish the external work item timestamp at most once per this many Stopwatch ticks to limit
            // writes to the shared cache line, so the recency check may expire up to this much early.
            // -1 when the recency check is disabled and producers do not need to publish anything.
            internal static readonly long ExternalWorkItemTimestampGranularity =
                SpuriousDispatchExternalWorkItemRecencyTicks < 0 ? -1 :
                Math.Max(SpuriousDispatchExternalWorkItemRecencyTicks / 8, Stopwatch.Frequency / 100_000);

            // This value represents an assumption of how much uncommitted stack space a worker thread may use in the future.
            // Used in calculations to estimate when to throttle the rate of thread injection to reduce the possibility of
            // preexisting threads from running out of memory when using new stack space in low-memory situations.
            public const int EstimatedAdditionalStackUsagePerThreadBytes = 64 << 10; // 64 KB

            private static short DetermineThreadsToKeepAlive()
            {
                const short DefaultThreadsToKeepAlive = 0;

                // The number of worker threads to keep alive after they are created. Set to -1 to keep all created worker
                // threads alive. When the ThreadTimeoutMs config value is also set, for worker threads the timeout applies to
                // worker threads that are in excess of the number configured for ThreadsToKeepAlive.
                short threadsToKeepAlive =
                    AppContextConfigHelper.GetInt16Config(
                        "System.Threading.ThreadPool.ThreadsToKeepAlive",
                        "DOTNET_ThreadPool_ThreadsToKeepAlive",
                        DefaultThreadsToKeepAlive);
                return threadsToKeepAlive >= -1 ? threadsToKeepAlive : DefaultThreadsToKeepAlive;
            }

            private static short DetermineSpuriousDispatchNoSpinThreshold()
            {
                // default to 2/3 of proc count.
                // At more than this working threads we start parking threads after a spurious dispatch.
                short DefaultSpuriousDispatchNoSpinThreshold = (short)(Environment.ProcessorCount * 2 / 3);

                // After a recent external thread request, park an empty dispatch without spinning when more than
                // this number of workers are still processing work. Set to 0 to park when at least one other worker
                // is processing work. With the recency check enabled, empty dispatches without a recent external
                // request park at any worker count.
                short threshold =
                    AppContextConfigHelper.GetInt16Config(
                        "System.Threading.ThreadPool.SpuriousDispatchNoSpinThreshold",
                        "DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold",
                        DefaultSpuriousDispatchNoSpinThreshold);
                return threshold >= 0 ? threshold : DefaultSpuriousDispatchNoSpinThreshold;
            }

            private static short DetermineSpuriousDispatchSpinnerLimit()
            {
                const short DefaultSpinnerLimit = 0;

                // An empty dispatch parks right away, instead of spinning, when at least this many other workers are
                // already spinning: they are in the same position and one more spinner is unlikely to find work that
                // they would not. The number of spinners is estimated from the thread and semaphore counts.
                short limit =
                    AppContextConfigHelper.GetInt16Config(
                        "System.Threading.ThreadPool.SpuriousDispatchSpinnerLimit",
                        "DOTNET_ThreadPool_SpuriousDispatchSpinnerLimit",
                        DefaultSpinnerLimit);
                return limit >= 0 ? limit : DefaultSpinnerLimit;
            }

            private static bool DetermineSpinCapOnEmpty() =>
                AppContextConfigHelper.GetInt16Config(
                    "System.Threading.ThreadPool.SpinCapOnEmpty",
                    "DOTNET_ThreadPool_SpinCapOnEmpty",
                    1) != 0;

            private static bool DetermineSpinLimitOnFastWakes() =>
                AppContextConfigHelper.GetInt16Config(
                    "System.Threading.ThreadPool.SpinLimitOnFastWakes",
                    "DOTNET_ThreadPool_SpinLimitOnFastWakes",
                    1) != 0;

            private static long DetermineSpuriousDispatchExternalWorkItemRecencyTicks()
            {
                const int DefaultRecencyUsec = 250;

                // This window, in microseconds, controls spinning after a spurious dispatch: within this long after an
                // external work item is observed, a spurious dispatch spins before parking.
                // Set to -1 to disable the recency check and retain only the processing-worker threshold.
                int recencyUsec =
                    AppContextConfigHelper.GetInt32Config(
                        "System.Threading.ThreadPool.SpuriousDispatchExternalWorkItemRecencyUsec",
                        "DOTNET_ThreadPool_SpuriousDispatchExternalWorkItemRecencyUsec",
                        DefaultRecencyUsec);
                if (recencyUsec < -1)
                {
                    recencyUsec = DefaultRecencyUsec;
                }

                return recencyUsec < 0 ? -1 : Stopwatch.Frequency * recencyUsec / 1_000_000;
            }

            /// <summary>
            /// Semaphore for controlling how many threads are currently working.
            /// </summary>
            private static readonly LowLevelLifoSemaphore s_semaphore =
                new LowLevelLifoSemaphore(
                    onWait: () =>
                    {
                        if (NativeRuntimeEventSource.Log.IsEnabled())
                        {
                            NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadWait(
                                (uint)ThreadPoolInstance._separated.counts.VolatileRead().NumExistingThreads);
                        }
                    });

            private static readonly ThreadStart s_workerThreadStart = WorkerThreadStart;

            private static void CreateWorkerThread()
            {
                // Thread pool threads must start in the default execution context without transferring the context, so
                // using UnsafeStart() instead of Start()
                Thread workerThread = new Thread(s_workerThreadStart);
                workerThread.IsThreadPoolThread = true;
                workerThread.IsBackground = true;
                workerThread.SetThreadPoolWorkerThreadName();
                workerThread.UnsafeStart();
            }

            private static void WorkerThreadStart()
            {
                PortableThreadPool threadPoolInstance = ThreadPoolInstance;

                if (NativeRuntimeEventSource.Log.IsEnabled())
                {
                    NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadStart(
                        (uint)threadPoolInstance._separated.counts.VolatileRead().NumExistingThreads);
                }

                LowLevelLock threadAdjustmentLock = threadPoolInstance._threadAdjustmentLock;
                LowLevelLifoSemaphore semaphore = s_semaphore;

                // Determine the idle timeout to use for this thread. Some threads may always be kept alive based on config.
                int timeoutMs = ThreadPoolThreadTimeoutMs;
                if (ThreadsToKeepAlive != 0)
                {
                    if (ThreadsToKeepAlive < 0)
                    {
                        timeoutMs = Timeout.Infinite;
                    }
                    else
                    {
                        int count = threadPoolInstance._numThreadsBeingKeptAlive;
                        while (count < ThreadsToKeepAlive)
                        {
                            int countBeforeUpdate =
                                Interlocked.CompareExchange(ref threadPoolInstance._numThreadsBeingKeptAlive, count + 1, count);
                            if (countBeforeUpdate == count)
                            {
                                timeoutMs = Timeout.Infinite;
                                break;
                            }

                            count = countBeforeUpdate;
                        }
                    }
                }

                while (true)
                {
                    bool noSpin = false;
                    while (noSpin ? semaphore.WaitNoSpin(timeoutMs) : semaphore.Wait(timeoutMs))
                    {
                        noSpin = WorkerDoWork(threadPoolInstance);
                    }

                    // We've timed out waiting on the semaphore. Time to exit.
                    // In rare cases we may be asked to keep running/waiting.
                    if (ShouldExitWorker(threadPoolInstance, threadAdjustmentLock))
                    {
                        break;
                    }
                }
            }

            // returns true if the worker should Wait without spinning.
            private static bool WorkerDoWork(PortableThreadPool threadPoolInstance)
            {
                bool spurious = false;
                bool didWork = false;
                short numProcessingWork;
                short numExistingThreads;

                // Whether the signal this worker claimed was claimed by spinning; the share of such claims that lead to no work
                // is recorded by the first dispatch.
                bool claimedBySpin = LowLevelLifoSemaphore.TakeSpinClaim();

                do
                {
                    bool foundWork = false;

                    // We generally avoid spurious wakes by requesting one thread at a time. We nearly always should see a request.
                    // However, we allow external wakes when thread goals change, which can result in "stolen" requests.
                    // Therefore we check for request before clearing it and dispatching workitems.
                    if (threadPoolInstance._separated._hasOutstandingThreadRequest != 0 &&
                        Interlocked.Exchange(ref threadPoolInstance._separated._hasOutstandingThreadRequest, 0) != 0)
                    {
                        // We took the request, now we must Dispatch some work items.
                        threadPoolInstance.NotifyDispatchProgress(Environment.TickCount);
                        switch (ThreadPoolWorkQueue.Dispatch())
                        {
                            case ThreadPoolWorkQueue.DispatchResult.Spurious:
                                // We were invited but found no work. This is counterproductive. We may want to park.
                                spurious = !didWork;
                                break;

                            case ThreadPoolWorkQueue.DispatchResult.ShouldStop:
                                // We are above goal and this worker is already removed in the counts.
                                // Chances to be invited back right away are low, so just park.
                                if (claimedBySpin)
                                {
                                    s_semaphore.RecordSpinClaimOutcome(empty: true);
                                }

                                return true;

                            default:
                                // We did some work, but then there was nothing to do.
                                // Spin a bit before parking in case we are invited back.
                                didWork = true;
                                foundWork = true;
                                spurious = false;
                                break;
                        }
                    }
                    else
                    {
                        // Not a common case. This can happen when worker goal was increased and invited extra threads.
                        // We will spin in case there is work for all and another request will soon follow.
                        spurious = false;
                    }

                    if (claimedBySpin)
                    {
                        // Whether the signal that brought this worker here led to work, by the first dispatch.
                        s_semaphore.RecordSpinClaimOutcome(empty: !foundWork);
                        claimedBySpin = false;
                    }

                    // We could not find more work in the queue and will try to stop being active.
                    // One caveat - in Saturated state we have seen a thread request but could not signal for a worker
                    // to come and see to it. Thus in Saturated state, one thread will clear the state and will come
                    // back for another try to clear the thread request and do Dispatch - without consuming a signal.
                    // See `TryIncrementProcessingWork` for details about Saturated state.
                } while (!TryRemoveWorkingWorker(threadPoolInstance, out numProcessingWork, out numExistingThreads));

                // Spinners have already left NumProcessingWork, so a low processing count alone does not
                // imply that another poller is useful. Preserve polling capacity for recent external producers,
                // which can introduce another burst after the queues drain. For work produced entirely by
                // workers, park empty dispatches promptly instead of competing with the productive workers.
                // The clock is read only on this path, after an empty dispatch. A timestamp published after the
                // clock read gives a negative difference, which counts as recent.
                // Recency tells whether more work is coming, the spinner count whether other workers are already
                // waiting for it. Even with recent external work, a worker that finds the others already spinning
                // parks instead of adding to them.
                return spurious && (numProcessingWork > SpuriousDispatchNoSpinThreshold ||
                        (SpuriousDispatchExternalWorkItemRecencyTicks >= 0 &&
                         Stopwatch.GetTimestamp() - Volatile.Read(ref threadPoolInstance._separated.lastExternalWorkItemTimestamp)
                            > SpuriousDispatchExternalWorkItemRecencyTicks) ||
                        TooManySpinners(numExistingThreads, numProcessingWork));
            }

            // Whether the other workers that are already spinning reach the spinner limit, which is the configured limit, or
            // SpinCapSpinners while the cap is on and most of the recent spin claims found no work, or while the spinners are
            // limited because the wakes after long parks are fast and SpinLimitOnFastWakes is on.
            private static bool TooManySpinners(short numExistingThreads, short numProcessingWork)
            {
                short limit = SpuriousDispatchSpinnerLimit;
                if (limit == 0 &&
                    ((SpinCapOnEmpty && s_semaphore.SpinCapActive) || (SpinLimitOnFastWakes && s_semaphore.SpinLimitEngaged)))
                {
                    limit = SpinCapSpinners;
                }

                // The current worker has already left NumProcessingWork and is not one of the other spinners.
                return limit > 0 && s_semaphore.EstimateSpinners(numExistingThreads, numProcessingWork) - 1 >= limit;
            }

            // returns true if the worker is shutting down
            // returns false if we should do another iteration
            private static bool ShouldExitWorker(PortableThreadPool threadPoolInstance, LowLevelLock threadAdjustmentLock)
            {
                // The thread cannot exit if it has IO pending, otherwise the IO may be canceled
                if (IsIOPending)
                {
                    return false;
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
                        if (counts.NumExistingThreads <= counts.NumProcessingWork)
                        {
                            // In this case, enough work came in that this thread should not time out and should go back to work.
                            return false;
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
                            HillClimbing.ThreadPoolHillClimber.ForceChange(
                                newNumThreadsGoal,
                                HillClimbing.StateOrTransition.ThreadTimedOut);
                            if (NativeRuntimeEventSource.Log.IsEnabled())
                            {
                                NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadStop((uint)newNumExistingThreads);
                            }
                            return true;
                        }

                        counts = oldCounts;
                    }
                }
                finally
                {
                    threadAdjustmentLock.Release();
                }
            }

            /// <summary>
            /// Tries to reduce the number of working workers by one.
            /// If we are in a Saturated state, clears the state instead and returns false.
            /// Returns true if number of active threads was actually reduced.
            /// <paramref name="numProcessingWork"/> receives the resulting number of workers processing work.
            /// <paramref name="numExistingThreads"/> receives the number of existing worker threads at the same moment.
            /// See `TryDecrementProcessingWork` for details about Saturated state.
            /// </summary>
            private static bool TryRemoveWorkingWorker(PortableThreadPool threadPoolInstance, out short numProcessingWork, out short numExistingThreads)
            {
                uint collisionCount = 0;
                while (true)
                {
                    ThreadCounts oldCounts = threadPoolInstance._separated.counts;
                    ThreadCounts newCounts = oldCounts;
                    bool decremented = newCounts.TryDecrementProcessingWork();
                    if (threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, oldCounts) == oldCounts)
                    {
                        numProcessingWork = newCounts.NumProcessingWork;
                        numExistingThreads = newCounts.NumExistingThreads;
                        return decremented;
                    }

                    // This can be fairly contentious when threadpool runs out of work and all threads try to leave.
                    Backoff.Exponential(collisionCount++);
                }
            }

            /// In Saturated state does nothing.
            /// Otherwise increments the active worker count and signals the semaphore.
            /// Incrementing the count turns on the Saturated state if the active thread limit is reached.
            /// See `TryIncrementProcessingWork` for details about Saturated state.
            internal static void MaybeAddWorkingWorker(PortableThreadPool threadPoolInstance)
            {
                ThreadCounts oldCounts, newCounts;
                bool incremented;
                uint collisionCount = 0;
                while (true)
                {
                    oldCounts = threadPoolInstance._separated.counts;
                    newCounts = oldCounts;
                    incremented = newCounts.TryIncrementProcessingWork();
                    newCounts.NumExistingThreads = Math.Max(newCounts.NumProcessingWork, newCounts.NumExistingThreads);
                    if (threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, oldCounts) == oldCounts)
                    {
                        break;
                    }

                    // This is less contentious than Remove as reasons to add threads are more complex to avoid adding too many too fast.
                    // We can still see some amount of failed interlocked operations here when a burst of work is scheduled.
                    Backoff.Exponential(collisionCount++);
                }

                if (!incremented)
                {
                    return;
                }

                Debug.Assert(newCounts.NumProcessingWork - oldCounts.NumProcessingWork == 1);
                s_semaphore.Signal();

                int toCreate = newCounts.NumExistingThreads - oldCounts.NumExistingThreads;
                Debug.Assert(toCreate == 0 || toCreate == 1);
                if (toCreate != 0)
                {
                    CreateWorkerThread();
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
                while (true)
                {
                    // When there are more threads processing work than the thread count goal, it may have been decided
                    // to decrease the number of threads. Stop processing if the counts can be updated. We may have more
                    // threads existing than the thread count goal and that is ok, the cold ones will eventually time out if
                    // the thread count goal is not increased again.
                    if (counts.NumProcessingWork <= counts.NumThreadsGoal)
                    {
                        return false;
                    }

                    ThreadCounts newCounts = counts;
                    newCounts.NumProcessingWork--;

                    ThreadCounts oldCounts = threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);

                    if (oldCounts == counts)
                    {
                        return true;
                    }
                    counts = oldCounts;
                }
            }
        }
    }
}

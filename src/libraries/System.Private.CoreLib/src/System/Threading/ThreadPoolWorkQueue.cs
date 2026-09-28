// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DispatchResult = System.Threading.IThreadPoolWorkQueue.DispatchResult;

#if FEATURE_MULTITHREADING
using WorkQueue = System.Collections.Concurrent.ConcurrentQueue<object>;
#else
using WorkQueue = System.Collections.Generic.Queue<object>;
#endif


namespace System.Threading
{
    /// <summary>
    /// The default thread pool work queue implementation.
    /// </summary>
    internal sealed partial class ThreadPoolWorkQueue : IThreadPoolWorkQueue
    {
        internal static class WorkStealingQueueList
        {
#pragma warning disable CA1825, IDE0300 // avoid the extra generic instantiation for Array.Empty<T>(); this is the only place we'll ever create this array
            private static WorkStealingQueue[] s_queues = new WorkStealingQueue[0];
#pragma warning restore CA1825, IDE0300

            public static WorkStealingQueue[] Queues => s_queues;

            public static void Add(WorkStealingQueue queue)
            {
                Debug.Assert(queue != null);
                while (true)
                {
                    WorkStealingQueue[] oldQueues = s_queues;
                    Debug.Assert(Array.IndexOf(oldQueues, queue) < 0);

                    var newQueues = new WorkStealingQueue[oldQueues.Length + 1];
                    Array.Copy(oldQueues, newQueues, oldQueues.Length);
                    newQueues[^1] = queue;
                    if (Interlocked.CompareExchange(ref s_queues, newQueues, oldQueues) == oldQueues)
                    {
                        break;
                    }
                }
            }

            public static void Remove(WorkStealingQueue queue)
            {
                Debug.Assert(queue != null);
                while (true)
                {
                    WorkStealingQueue[] oldQueues = s_queues;
                    if (oldQueues.Length == 0)
                    {
                        return;
                    }

                    int pos = Array.IndexOf(oldQueues, queue);
                    if (pos < 0)
                    {
                        Debug.Fail("Should have found the queue");
                        return;
                    }

                    var newQueues = new WorkStealingQueue[oldQueues.Length - 1];
                    if (pos == 0)
                    {
                        Array.Copy(oldQueues, 1, newQueues, 0, newQueues.Length);
                    }
                    else if (pos == oldQueues.Length - 1)
                    {
                        Array.Copy(oldQueues, newQueues, newQueues.Length);
                    }
                    else
                    {
                        Array.Copy(oldQueues, newQueues, pos);
                        Array.Copy(oldQueues, pos + 1, newQueues, pos, newQueues.Length - pos);
                    }

                    if (Interlocked.CompareExchange(ref s_queues, newQueues, oldQueues) == oldQueues)
                    {
                        break;
                    }
                }
            }
        }

        internal sealed class WorkStealingQueue
        {
            private const int INITIAL_SIZE = 32;
            internal volatile object?[] m_array = new object[INITIAL_SIZE]; // SOS's ThreadPool command depends on this name
            private volatile int m_mask = INITIAL_SIZE - 1;

#if DEBUG
            // in debug builds, start at the end so we exercise the index reset logic.
            private const int START_INDEX = int.MaxValue;
#else
            private const int START_INDEX = 0;
#endif

            private volatile int m_headIndex = START_INDEX;
            private volatile int m_tailIndex = START_INDEX;

            private SpinLock m_foreignLock = new SpinLock(enableThreadOwnerTracking: false);

            public void LocalPush(object obj)
            {
                int tail = m_tailIndex;

                // We're going to increment the tail; if we'll overflow, then we need to reset our counts
                if (tail == int.MaxValue)
                {
                    tail = LocalPush_HandleTailOverflow();
                }

                // When there are at least 2 elements' worth of space, we can take the fast path.
                if (tail < m_headIndex + m_mask)
                {
                    m_array[tail & m_mask] = obj;
                    // The following write makes the slot to "appear" in the queue.
                    // It must happen after the write of the item, and it does, since m_tailIndex is volatile.
                    // NOTE: we also must be sure this write is not delayed past our check for a
                    // pending thread request.
                    m_tailIndex = tail + 1;
                }
                else
                {
                    // We need to contend with foreign pops, so we lock.
                    bool lockTaken = false;
                    try
                    {
                        m_foreignLock.Enter(ref lockTaken);

                        int head = m_headIndex;
                        int count = m_tailIndex - m_headIndex;

                        // If there is still space (one left), just add the element.
                        if (count >= m_mask)
                        {
                            // We're full; expand the queue by doubling its size.
                            var newArray = new object?[m_array.Length << 1];
                            for (int i = 0; i < m_array.Length; i++)
                                newArray[i] = m_array[(i + head) & m_mask];

                            // Reset the field values, incl. the mask.
                            m_array = newArray;
                            m_headIndex = 0;
                            m_tailIndex = tail = count;
                            m_mask = (m_mask << 1) | 1;
                        }

                        m_array[tail & m_mask] = obj;
                        // The following write makes the slot to "appear" in the queue.
                        // It must happen after the write of the item, and it does, since m_tailIndex is volatile.
                        // NOTE: we also must be sure this write is not delayed past our check for a
                        // pending thread request.
                        m_tailIndex = tail + 1;
                    }
                    finally
                    {
                        if (lockTaken)
                            m_foreignLock.Exit(useMemoryBarrier: false);
                    }
                }

                // Our caller will check for a thread request now (with an ordinary read),
                // make sure the check happens after the new slot appears in the queue.
                Interlocked.MemoryBarrier();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private int LocalPush_HandleTailOverflow()
            {
                bool lockTaken = false;
                try
                {
                    m_foreignLock.Enter(ref lockTaken);

                    int tail = m_tailIndex;
                    if (tail == int.MaxValue)
                    {
                        //
                        // Rather than resetting to zero, we'll just mask off the bits we don't care about.
                        // This way we don't need to rearrange the items already in the queue; they'll be found
                        // correctly exactly where they are.  One subtlety here is that we need to make sure that
                        // if head is currently < tail, it remains that way.  This happens to just fall out from
                        // the bit-masking, because we only do this if tail == int.MaxValue, meaning that all
                        // bits are set, so all of the bits we're keeping will also be set.  Thus it's impossible
                        // for the head to end up > than the tail, since you can't set any more bits than all of
                        // them.
                        //
                        m_headIndex &= m_mask;
                        m_tailIndex = tail = m_tailIndex & m_mask;
                        Debug.Assert(m_headIndex <= m_tailIndex);
                    }

                    return tail;
                }
                finally
                {
                    if (lockTaken)
                        m_foreignLock.Exit(useMemoryBarrier: true);
                }
            }

            public bool LocalFindAndPop(object obj)
            {
                // Fast path: check the tail. If equal, we can skip the lock.
                if (m_array[(m_tailIndex - 1) & m_mask] == obj)
                {
                    object? unused = LocalPop();
                    Debug.Assert(unused == null || unused == obj);
                    return unused != null;
                }

                // Else, do an O(N) search for the work item. The theory of work stealing and our
                // inlining logic is that most waits will happen on recently queued work.  And
                // since recently queued work will be close to the tail end (which is where we
                // begin our search), we will likely find it quickly.  In the worst case, we
                // will traverse the whole local queue; this is typically not going to be a
                // problem (although degenerate cases are clearly an issue) because local work
                // queues tend to be somewhat shallow in length, and because if we fail to find
                // the work item, we are about to block anyway (which is very expensive).
                for (int i = m_tailIndex - 2; i >= m_headIndex; i--)
                {
                    if (m_array[i & m_mask] == obj)
                    {
                        // If we found the element, block out steals to avoid interference.
                        bool lockTaken = false;
                        try
                        {
                            m_foreignLock.Enter(ref lockTaken);

                            // If we encountered a race condition, bail.
                            if (m_array[i & m_mask] == null)
                                return false;

                            // Otherwise, null out the element.
                            Volatile.Write(ref m_array[i & m_mask], null);

                            // And then check to see if we can fix up the indexes (if we're at
                            // the edge).  If we can't, we just leave nulls in the array and they'll
                            // get filtered out eventually (but may lead to superfluous resizing).
                            if (i == m_tailIndex)
                                m_tailIndex--;
                            else if (i == m_headIndex)
                                m_headIndex++;

                            return true;
                        }
                        finally
                        {
                            if (lockTaken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }

                return false;
            }

            public object? LocalPop() => m_headIndex < m_tailIndex ? LocalPopCore() : null;

            private object? LocalPopCore()
            {
                while (true)
                {
                    int tail = m_tailIndex;
                    if (m_headIndex >= tail)
                    {
                        return null;
                    }

                    // Decrement the tail using a fence to ensure subsequent read doesn't come before.
                    tail--;
                    Interlocked.Exchange(ref m_tailIndex, tail);

                    // If there is no interaction with a take, we can head down the fast path.
                    if (m_headIndex <= tail)
                    {
                        int idx = tail & m_mask;
                        object? obj = Volatile.Read(ref m_array[idx]);

                        // Check for nulls in the array.
                        if (obj == null) continue;

                        m_array[idx] = null;
                        return obj;
                    }
                    else
                    {
                        // Interaction with takes: 0 or 1 elements left.
                        bool lockTaken = false;
                        try
                        {
                            m_foreignLock.Enter(ref lockTaken);

                            if (m_headIndex <= tail)
                            {
                                // Element still available. Take it.
                                int idx = tail & m_mask;
                                object? obj = Volatile.Read(ref m_array[idx]);

                                // Check for nulls in the array.
                                if (obj == null) continue;

                                m_array[idx] = null;
                                return obj;
                            }
                            else
                            {
                                // If we encountered a race condition and element was stolen, restore the tail.
                                m_tailIndex = tail + 1;
                                return null;
                            }
                        }
                        finally
                        {
                            if (lockTaken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }
            }

            public bool CanSteal => m_headIndex < m_tailIndex;

            public object? TrySteal(ref bool missedSteal)
            {
                while (true)
                {
                    if (CanSteal)
                    {
                        bool taken = false;
                        try
                        {
                            m_foreignLock.TryEnter(ref taken);
                            if (taken)
                            {
                                // Increment head, and ensure read of tail doesn't move before it (fence).
                                int head = m_headIndex;
                                Interlocked.Exchange(ref m_headIndex, head + 1);

                                if (head < m_tailIndex)
                                {
                                    int idx = head & m_mask;
                                    object? obj = Volatile.Read(ref m_array[idx]);

                                    // Check for nulls in the array.
                                    if (obj == null) continue;

                                    m_array[idx] = null;
                                    return obj;
                                }
                                else
                                {
                                    // Failed, restore head.
                                    // This write must complete before we return with a missed steal and check if
                                    // there is a pending thread request because the thread that responds
                                    // to the request must see the write to not conclude that the queue is empty.
                                    Interlocked.Exchange(ref m_headIndex, head);
                                }
                            }
                        }
                        finally
                        {
                            if (taken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }

                        missedSteal = true;
                    }

                    return null;
                }
            }

            public int Count
            {
                get
                {
                    bool lockTaken = false;
                    try
                    {
                        m_foreignLock.Enter(ref lockTaken);
                        return Math.Max(0, m_tailIndex - m_headIndex);
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }
            }
        }

#if CORECLR
        // This config var can be used to enable an experimental mode that may reduce the effects of some priority inversion
        // issues seen in cases involving a lot of sync-over-async. See EnqueueForPrioritizationExperiment() for more
        // information. The mode is experimental and may change in the future.
        internal static readonly bool s_prioritizationExperiment =
            AppContextConfigHelper.GetBooleanConfig(
                "System.Threading.ThreadPool.PrioritizationExperiment",
                "DOTNET_ThreadPool_PrioritizationExperiment",
                defaultValue: false);
#endif

        private const int ProcessorsPerAssignableWorkItemQueue = 16;
        private static readonly int s_assignableWorkItemQueueCount =
            Environment.ProcessorCount <= 32 ? 0 :
                (Environment.ProcessorCount + (ProcessorsPerAssignableWorkItemQueue - 1)) / ProcessorsPerAssignableWorkItemQueue;

        private bool _loggingEnabled;

        // SOS's ThreadPool command depends on the following names
        internal readonly WorkQueue workItems = new WorkQueue();
        internal readonly WorkQueue highPriorityWorkItems = new WorkQueue();

#if CORECLR
        internal readonly WorkQueue lowPriorityWorkItems =
            s_prioritizationExperiment ? new WorkQueue() : null!;
#endif

        // SOS's ThreadPool command depends on the following name. The global queue doesn't scale well beyond a point of
        // concurrency. Some additional queues may be added and assigned to a limited number of worker threads if necessary to
        // help with limiting the concurrency level.
        internal readonly WorkQueue[] _assignableWorkItemQueues =
            new WorkQueue[s_assignableWorkItemQueueCount];

        private readonly LowLevelLock _queueAssignmentLock = new();
        private readonly int[] _assignedWorkItemQueueThreadCounts =
            s_assignableWorkItemQueueCount > 0 ? new int[s_assignableWorkItemQueueCount] : [];

        public ThreadPoolWorkQueue()
        {
            for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
            {
                _assignableWorkItemQueues[i] = new WorkQueue();
            }

            RefreshLoggingEnabled();
        }

        private void AssignWorkItemQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            Debug.Assert(s_assignableWorkItemQueueCount > 0);

            _queueAssignmentLock.Acquire();

            // Determine the first queue that has not yet been assigned to the limit of worker threads
            int queueIndex = -1;
            int minCount = int.MaxValue;
            int minCountQueueIndex = 0;
            for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
            {
                int count = _assignedWorkItemQueueThreadCounts[i];
                Debug.Assert(count >= 0);
                if (count < ProcessorsPerAssignableWorkItemQueue)
                {
                    queueIndex = i;
                    _assignedWorkItemQueueThreadCounts[queueIndex] = count + 1;
                    break;
                }

                if (count < minCount)
                {
                    minCount = count;
                    minCountQueueIndex = i;
                }
            }

            if (queueIndex < 0)
            {
                // All queues have been fully assigned. Choose the queue that has been assigned to the least number of worker
                // threads.
                queueIndex = minCountQueueIndex;
                _assignedWorkItemQueueThreadCounts[queueIndex]++;
            }

            _queueAssignmentLock.Release();

            tl.queueIndex = queueIndex;
            tl.assignedGlobalWorkItemQueue = _assignableWorkItemQueues[queueIndex];
        }

        private void TryReassignWorkItemQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            Debug.Assert(s_assignableWorkItemQueueCount > 0);

            // Dispatch assigns a queue before dispatching any work, and only unassigns it on its way out,
            // so a queue is always assigned when this is called.
            int queueIndex = tl.queueIndex;
            Debug.Assert(queueIndex >= 0);
            if (queueIndex == 0)
            {
                return;
            }

            if (!_queueAssignmentLock.TryAcquire())
            {
                return;
            }

            // If the currently assigned queue is assigned to other worker threads, try to reassign an earlier queue to this
            // worker thread if the earlier queue is not assigned to the limit of worker threads
            Debug.Assert(_assignedWorkItemQueueThreadCounts[queueIndex] >= 0);
            if (_assignedWorkItemQueueThreadCounts[queueIndex] > 1)
            {
                for (int i = 0; i < queueIndex; i++)
                {
                    if (_assignedWorkItemQueueThreadCounts[i] < ProcessorsPerAssignableWorkItemQueue)
                    {
                        _assignedWorkItemQueueThreadCounts[queueIndex]--;
                        queueIndex = i;
                        _assignedWorkItemQueueThreadCounts[queueIndex]++;
                        break;
                    }
                }
            }

            _queueAssignmentLock.Release();

            tl.queueIndex = queueIndex;
            tl.assignedGlobalWorkItemQueue = _assignableWorkItemQueues[queueIndex];
        }

        private void UnassignWorkItemQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            Debug.Assert(s_assignableWorkItemQueueCount > 0);

            int queueIndex = tl.queueIndex;
            if (queueIndex == -1)
            {
                // a queue was never assigned
                return;
            }

            _queueAssignmentLock.Acquire();
            int newCount = --_assignedWorkItemQueueThreadCounts[queueIndex];
            _queueAssignmentLock.Release();

            Debug.Assert(newCount >= 0);
            if (newCount > 0)
            {
                return;
            }

            // This queue is not assigned to any other worker threads. Move its work items to the global queue to prevent them
            // from being starved for a long duration.
            bool movedWorkItem = false;
            WorkQueue queue = tl.assignedGlobalWorkItemQueue;
            while (_assignedWorkItemQueueThreadCounts[queueIndex] <= 0 && queue.TryDequeue(out object? workItem))
            {
                workItems.Enqueue(workItem);
                movedWorkItem = true;
            }

            if (movedWorkItem)
            {
                ThreadPool.EnsureWorkerRequested();
            }

            // unassigned state
            tl.queueIndex = -1;
            tl.assignedGlobalWorkItemQueue = workItems;
        }

        public ThreadPoolWorkQueueThreadLocals GetOrCreateThreadLocals() =>
            ThreadPoolWorkQueueThreadLocals.threadLocals ?? CreateThreadLocals();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ThreadPoolWorkQueueThreadLocals CreateThreadLocals()
        {
            Debug.Assert(ThreadPoolWorkQueueThreadLocals.threadLocals == null);

            return ThreadPoolWorkQueueThreadLocals.threadLocals = new ThreadPoolWorkQueueThreadLocals(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefreshLoggingEnabled()
        {
            if (!FrameworkEventSource.Log.IsEnabled())
            {
                if (_loggingEnabled)
                {
                    _loggingEnabled = false;
                }
                return;
            }

            RefreshLoggingEnabledFull();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RefreshLoggingEnabledFull()
        {
            _loggingEnabled = FrameworkEventSource.Log.IsEnabled(EventLevel.Verbose, FrameworkEventSource.Keywords.ThreadPool | FrameworkEventSource.Keywords.ThreadTransfer);
        }

        public void Enqueue(object callback, bool forceGlobal)
        {
            Debug.Assert((callback is IThreadPoolWorkItem) ^ (callback is Task));

            if (_loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(callback);

#if CORECLR
            if (s_prioritizationExperiment)
            {
                EnqueueForPrioritizationExperiment(callback, forceGlobal);
            }
            else
#endif
            {
                ThreadPoolWorkQueueThreadLocals? tl;
                if (!forceGlobal && (tl = ThreadPoolWorkQueueThreadLocals.threadLocals) != null)
                {
                    tl.workStealingQueue.LocalPush(callback);
                }
                else
                {
                    WorkQueue queue =
                        s_assignableWorkItemQueueCount > 0 && (tl = ThreadPoolWorkQueueThreadLocals.threadLocals) != null
                            ? tl.assignedGlobalWorkItemQueue
                            : workItems;
                    queue.Enqueue(callback);
                }
            }

            ThreadPool.EnsureWorkerRequested();
        }

#if CORECLR
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnqueueForPrioritizationExperiment(object callback, bool forceGlobal)
        {
            ThreadPoolWorkQueueThreadLocals? tl = ThreadPoolWorkQueueThreadLocals.threadLocals;
            if (!forceGlobal && tl != null)
            {
                tl.workStealingQueue.LocalPush(callback);
                return;
            }

            WorkQueue queue;

            // This is a rough and experimental attempt at identifying work items that should be lower priority than other
            // global work items (even ones that haven't been queued yet), and to queue them to a low-priority global queue that
            // is checked after all other global queues. In some cases, a work item may queue another work item that is part of
            // the same set of work. For global work items, the second work item would typically get queued behind other global
            // work items. In some cases involving a lot of sync-over-async, that can significantly delay worker threads from
            // getting unblocked.
            if (tl == null && callback is QueueUserWorkItemCallbackBase)
            {
                queue = lowPriorityWorkItems;
            }
            else if (s_assignableWorkItemQueueCount > 0 && tl != null)
            {
                queue = tl.assignedGlobalWorkItemQueue;
            }
            else
            {
                queue = workItems;
            }

            queue.Enqueue(callback);
        }
#endif

        public void EnqueueAtHighPriority(object workItem)
        {
            Debug.Assert((workItem is IThreadPoolWorkItem) ^ (workItem is Task));

            if (_loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(workItem);

            highPriorityWorkItems.Enqueue(workItem);

            ThreadPool.EnsureWorkerRequested();
        }

        internal static void TransferAllLocalWorkItemsToHighPriorityGlobalQueue()
        {
            // If there's no local queue, there's nothing to transfer.
            if (ThreadPoolWorkQueueThreadLocals.threadLocals is ThreadPoolWorkQueueThreadLocals tl)
            {
                TransferAllLocalWorkItemsToHighPriorityGlobalQueue(tl);
            }
        }

        internal static void TransferAllLocalWorkItemsToHighPriorityGlobalQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            // Pop each work item off the local queue and push it onto the global. This is a
            // bounded loop as no other thread is allowed to push into this thread's queue.
            ThreadPoolWorkQueue queue = tl.workQueue;
            bool ensureWorkerRequest = false;
            while (tl.workStealingQueue.LocalPop() is object workItem)
            {
                // A work item had been removed temporarily and other threads may have missed stealing it, so ensure that
                // there will be a thread request
                ensureWorkerRequest = true;

                // If there's an unexpected exception here that happens to get handled, the lost work item, or missing thread
                // request, etc., may lead to other issues. A fail-fast or try-finally here could reduce the effect of such
                // uncommon issues to various degrees, but it's also uncommon to check for unexpected exceptions.
                try
                {
                    queue.highPriorityWorkItems.Enqueue(workItem);
                }
                catch (OutOfMemoryException)
                {
                    // This is not expected to throw under normal circumstances
                    tl.workStealingQueue.LocalPush(workItem);

                    break;
                }
            }

            if (ensureWorkerRequest)
            {
                ThreadPool.EnsureWorkerRequested();
            }
        }

        public void TransferLocalWorkItemsBeforeBlocking() => TransferAllLocalWorkItemsToHighPriorityGlobalQueue();

        public bool TryRemove(Task workItem) => LocalFindAndPop(workItem);

        internal static bool LocalFindAndPop(object callback)
        {
            ThreadPoolWorkQueueThreadLocals? tl = ThreadPoolWorkQueueThreadLocals.threadLocals;
            return tl != null && tl.workStealingQueue.LocalFindAndPop(callback);
        }

        public object? Dequeue(ThreadPoolWorkQueueThreadLocals tl, ref bool missedSteal)
        {
            // Check for local work items
            object? workItem = tl.workStealingQueue.LocalPop();
            if (workItem != null)
            {
                return workItem;
            }

            // Check for high-priority work items
            if (tl.isProcessingHighPriorityWorkItems)
            {
                if (highPriorityWorkItems.TryDequeue(out workItem))
                {
                    return workItem;
                }

                tl.isProcessingHighPriorityWorkItems = false;
            }
#if FEATURE_MULTITHREADING
            else if (!highPriorityWorkItems.IsEmpty &&
#else
            else if (highPriorityWorkItems.Count == 0 &&
#endif
                TryStartProcessingHighPriorityWorkItemsAndDequeue(tl, out workItem))
            {
                return workItem;
            }

            // Check for work items from the assigned global queue
            if (s_assignableWorkItemQueueCount > 0 && tl.assignedGlobalWorkItemQueue.TryDequeue(out workItem))
            {
                return workItem;
            }

            // Check for work items from the global queue
            if (workItems.TryDequeue(out workItem))
            {
                return workItem;
            }

            // Check for work items in other assignable global queues
            uint randomValue = tl.random.NextUInt32();
            if (s_assignableWorkItemQueueCount > 0)
            {
                int queueIndex = tl.queueIndex;
                int c = s_assignableWorkItemQueueCount;
                int maxIndex = c - 1;
                for (int i = (int)(randomValue % (uint)c); c > 0; i = i < maxIndex ? i + 1 : 0, c--)
                {
                    if (i != queueIndex && _assignableWorkItemQueues[i].TryDequeue(out workItem))
                    {
                        return workItem;
                    }
                }
            }

#if CORECLR
            // Check for low-priority work items
            if (s_prioritizationExperiment && lowPriorityWorkItems.TryDequeue(out workItem))
            {
                return workItem;
            }
#endif

            // Try to steal from other threads' local work items
            {
                WorkStealingQueue localWsq = tl.workStealingQueue;
                WorkStealingQueue[] queues = WorkStealingQueueList.Queues;
                int c = queues.Length;
                Debug.Assert(c > 0, "There must at least be a queue for this thread.");
                int maxIndex = c - 1;
                for (int i = (int)(randomValue % (uint)c); c > 0; i = i < maxIndex ? i + 1 : 0, c--)
                {
                    WorkStealingQueue otherQueue = queues[i];
                    if (otherQueue != localWsq && otherQueue.CanSteal)
                    {
                        workItem = otherQueue.TrySteal(ref missedSteal);
                        if (workItem != null)
                        {
                            return workItem;
                        }
                    }
                }
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryStartProcessingHighPriorityWorkItemsAndDequeue(
            ThreadPoolWorkQueueThreadLocals tl,
            [MaybeNullWhen(false)] out object workItem)
        {
            Debug.Assert(!tl.isProcessingHighPriorityWorkItems);

            if (!highPriorityWorkItems.TryDequeue(out workItem))
            {
                return false;
            }

            tl.isProcessingHighPriorityWorkItems = true;
            return true;
        }

        public long LocalCount
        {
            get
            {
                long count = 0;
                foreach (WorkStealingQueue workStealingQueue in WorkStealingQueueList.Queues)
                {
                    count += workStealingQueue.Count;
                }
                return count;
            }
        }

        public long GlobalCount
        {
            get
            {
                long count = (long)highPriorityWorkItems.Count + workItems.Count;
#if CORECLR
                if (s_prioritizationExperiment)
                {
                    count += lowPriorityWorkItems.Count;
                }
#endif

                for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
                {
                    count += _assignableWorkItemQueues[i].Count;
                }

                return count;
            }
        }

        // Get all workitems.  Called by TaskScheduler in its debugger hooks.
        public IEnumerable<object> GetQueuedWorkItems()
        {
            // Enumerate high-priority queue
            foreach (object workItem in highPriorityWorkItems)
            {
                yield return workItem;
            }

            // Enumerate assignable global queues
            foreach (WorkQueue queue in _assignableWorkItemQueues)
            {
                foreach (object workItem in queue)
                {
                    yield return workItem;
                }
            }

            // Enumerate global queue
            foreach (object workItem in workItems)
            {
                yield return workItem;
            }

#if CORECLR
            if (ThreadPoolWorkQueue.s_prioritizationExperiment)
            {
                // Enumerate low-priority global queue
                foreach (object workItem in lowPriorityWorkItems)
                {
                    yield return workItem;
                }
            }
#endif

            // Enumerate each local queue
            foreach (ThreadPoolWorkQueue.WorkStealingQueue wsq in ThreadPoolWorkQueue.WorkStealingQueueList.Queues)
            {
                if (wsq != null && wsq.m_array != null)
                {
                    object?[] items = wsq.m_array;
                    for (int i = 0; i < items.Length; i++)
                    {
                        object? item = items[i];
                        if (item != null)
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        // Time in ms for which ThreadPoolWorkQueue.Dispatch keeps executing normal work items before either returning from
        // Dispatch (if YieldFromDispatchLoop is true), or performing periodic activities
        public const uint DispatchQuantumMs = 30;

        /// <summary>
        /// Dispatches work items to this thread.
        /// </summary>
        public DispatchResult Dispatch()
        {
            ThreadPoolWorkQueue workQueue = this;
            ThreadPoolWorkQueueThreadLocals tl = workQueue.GetOrCreateThreadLocals();

            if (s_assignableWorkItemQueueCount > 0)
            {
                workQueue.AssignWorkItemQueue(tl);
            }

            bool missedSteal = false;
            object? workItem = workQueue.Dequeue(tl, ref missedSteal);
            if (workItem == null)
            {
                if (s_assignableWorkItemQueueCount > 0)
                {
                    workQueue.UnassignWorkItemQueue(tl);
                }

                // Missing a steal means there may be an item that we were unable to get.
                // Effectively, we failed to fulfill our promise to check the queues for work.
                // We need to make sure someone will do another pass.
                if (missedSteal)
                {
                    ThreadPool.EnsureWorkerRequested();
                }

                // The thread found no work.
                return DispatchResult.Spurious;
            }


            // The workitems that are currently in the queues could have asked only for one worker.
            // We are going to process a workitem, which may take unknown time or even block.
            // In a worst case the current workitem will indirectly depend on progress of other
            // items and that would lead to a deadlock if no one else checks the queue.
            // We must ensure at least one more worker is coming if the queue is not empty.
            ThreadPool.EnsureWorkerRequested();

            //
            // After this point, this method is no longer responsible for ensuring thread requests except for missed steals
            //

            // Has the desire for logging changed since the last time we entered?
            workQueue.RefreshLoggingEnabled();

            ThreadInt64PersistentCounter.ThreadLocalNode? threadLocalCompletionCountNode = tl.threadLocalCompletionCountNode;
            Thread currentThread = tl.currentThread;

            // Start on clean ExecutionContext and SynchronizationContext
            currentThread._executionContext = null;
            currentThread._synchronizationContext = null;

            //
            // Save the start time
            //
            int startTickCount = Environment.TickCount;

            //
            // Loop until our quantum expires or there is no work.
            //
            while (true)
            {
                if (workItem == null)
                {
                    missedSteal = false;
                    workItem = workQueue.Dequeue(tl, ref missedSteal);

                    if (workItem == null)
                    {
                        if (s_assignableWorkItemQueueCount > 0)
                        {
                            workQueue.UnassignWorkItemQueue(tl);
                        }

                        //
                        // No work.
                        // If we missed a steal, though, there may be more work in the queue.
                        // Instead of looping around and trying again, we'll just request another thread.  Hopefully the thread
                        // that owns the contended work-stealing queue will pick up its own workitems in the meantime,
                        // which will be more efficient than this thread doing it anyway.
                        //
                        if (missedSteal)
                        {
                            ThreadPool.EnsureWorkerRequested();
                        }

                        return DispatchResult.Regular;
                    }
                }

                if (workQueue._loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                {
                    FrameworkEventSource.Log.ThreadPoolDequeueWorkObject(workItem);
                }

                //
                // Execute the workitem outside of any finally blocks, so that it can be aborted if needed.
                //
#if FEATURE_OBJCMARSHAL
                if (AutoreleasePool.EnableAutoreleasePool)
                {
                    ThreadPoolWorkItemDispatcher.DispatchItemWithAutoreleasePool(workItem, currentThread);
                }
                else
#endif
#pragma warning disable CS0162 // Unreachable code detected. EnableWorkerTracking may be a constant in some runtimes.
                if (ThreadPool.EnableWorkerTracking)
                {
                    ThreadPoolWorkItemDispatcher.DispatchWorkItemWithWorkerTracking(workItem, currentThread);
                }
                else
                {
                    ThreadPoolWorkItemDispatcher.DispatchWorkItem(workItem, currentThread);
                }
#pragma warning restore CS0162

                // Release refs
                workItem = null;

                // Return to clean ExecutionContext and SynchronizationContext. This may call user code (AsyncLocal value
                // change notifications).
                ExecutionContext.ResetThreadPoolThread(currentThread);

                // Reset thread state after all user code for the work item has completed
                currentThread.ResetThreadPoolThread();

                //
                // Notify the VM that we executed this workitem.  This is also our opportunity to ask whether Hill Climbing wants
                // us to return the thread to the pool or not.
                //
                int currentTickCount = Environment.TickCount;
                if (!ThreadPool.NotifyWorkItemComplete(threadLocalCompletionCountNode!, currentTickCount))
                {
                    // This thread is being parked and may remain inactive for a while. Transfer any thread-local work items
                    // to ensure that they would not be heavily delayed. Tell the caller that this thread was requested to stop
                    // processing work items.
                    ThreadPoolWorkQueue.TransferAllLocalWorkItemsToHighPriorityGlobalQueue(tl);
                    tl.isProcessingHighPriorityWorkItems = false;
                    if (s_assignableWorkItemQueueCount > 0)
                    {
                        workQueue.UnassignWorkItemQueue(tl);
                    }

                    return DispatchResult.ShouldStop;
                }

                // Check if the dispatch quantum has expired
                if ((uint)(currentTickCount - startTickCount) < DispatchQuantumMs)
                {
                    continue;
                }

                // The quantum expired, do any necessary periodic activities

                if (ThreadPool.YieldFromDispatchLoop(currentTickCount))
                {
                    // The runtime-specific thread pool implementation requires the Dispatch loop to return to the VM
                    // periodically to let it perform its own work
                    tl.isProcessingHighPriorityWorkItems = false;
                    if (s_assignableWorkItemQueueCount > 0)
                    {
                        workQueue.UnassignWorkItemQueue(tl);
                    }
                    return DispatchResult.Regular;
                }

                if (s_assignableWorkItemQueueCount > 0)
                {
                    // Due to hill climbing, over time arbitrary worker threads may stop working and eventually unbalance the
                    // queue assignments. Periodically try to reassign a queue to keep the assigned queues busy.
                    // The queue itself was already assigned when this Dispatch call started.
                    workQueue.TryReassignWorkItemQueue(tl);
                }

                // This method will continue to dispatch work items. Refresh the start tick count for the next dispatch
                // quantum and do some periodic activities.
                startTickCount = currentTickCount;

                // Periodically refresh whether logging is enabled
                workQueue.RefreshLoggingEnabled();
            }
        }
    }

    // Holds a WorkStealingQueue, and removes it from the list when this object is no longer referenced.
    internal sealed class ThreadPoolWorkQueueThreadLocals
    {
        [ThreadStatic]
        public static ThreadPoolWorkQueueThreadLocals? threadLocals;

        public bool isProcessingHighPriorityWorkItems;
        public int queueIndex;
        public WorkQueue assignedGlobalWorkItemQueue;
        public readonly ThreadPoolWorkQueue workQueue;
        public readonly ThreadPoolWorkQueue.WorkStealingQueue workStealingQueue;
        public readonly Thread currentThread;
        public readonly ThreadInt64PersistentCounter.ThreadLocalNode? threadLocalCompletionCountNode;
        public readonly Random.XoshiroImpl random = new Random.XoshiroImpl();

        public ThreadPoolWorkQueueThreadLocals(ThreadPoolWorkQueue tpq)
        {
            assignedGlobalWorkItemQueue = tpq.workItems;
            queueIndex = -1;
            workQueue = tpq;
            workStealingQueue = new ThreadPoolWorkQueue.WorkStealingQueue();
            ThreadPoolWorkQueue.WorkStealingQueueList.Add(workStealingQueue);
            currentThread = Thread.CurrentThread;
            threadLocalCompletionCountNode = ThreadPool.GetOrCreateThreadLocalCompletionCountNode();
        }


        ~ThreadPoolWorkQueueThreadLocals()
        {
            // Transfer any pending workitems into the global queue so that they will be executed by another thread
            if (null != workStealingQueue)
            {
                ThreadPoolWorkQueue.TransferAllLocalWorkItemsToHighPriorityGlobalQueue(this);
                ThreadPoolWorkQueue.WorkStealingQueueList.Remove(workStealingQueue);
            }
        }
    }
}

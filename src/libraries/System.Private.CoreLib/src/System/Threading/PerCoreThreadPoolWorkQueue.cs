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
using Internal;
using System.Numerics;

using DispatchResult = System.Threading.IThreadPoolWorkQueue.DispatchResult;
using WorkQueueUnused = System.Collections.Concurrent.ConcurrentQueue<object>;



namespace System.Threading
{
    /// <summary>
    /// A thread pool work queue implementation that uses per-core segmented FIFO and work-stealing queues.
    /// </summary>
    internal sealed class PerCoreThreadPoolWorkQueue : IThreadPoolWorkQueue
    {
        internal abstract class WorkQueueBase
        {
            // This implementation provides an unbounded, multi-producer multi-consumer queue
            // that supports the standard Enqueue/Dequeue operations.
            // It is composed of a linked list of bounded ring buffers, each of which has an enqueue
            // and a dequeue index, isolated from each other to minimize false sharing.  As long as
            // the number of elements in the queue remains less than the size of the current
            // buffer (Segment), no additional allocations are required for enqueued items.  When
            // the number of items exceeds the size of the current segment, the current segment is
            // "frozen" to prevent further enqueues, and a new segment is linked from it and set
            // as the new tail segment for subsequent enqueues.  As old segments are consumed by
            // dequeues, the dequeue reference is updated to point to the segment that dequeuers should
            // try next.

            /// <summary>
            /// Initial length of the segments used in the queue.
            /// </summary>
            internal const int InitialSegmentLength = 32;

            /// <summary>
            /// Maximum length of the segments used in the queue.  This is a somewhat arbitrary limit:
            /// larger means that as long as we don't exceed the size, we avoid allocating more segments,
            /// but if we do exceed it, then the segment becomes garbage.
            /// </summary>
            internal const int MaxSegmentLength = 1024 * 1024;

            /// <summary>
            /// Lock used to protect cross-segment operations"/>
            /// and any operations that need to get a consistent view of them.
            /// </summary>
            internal readonly object _addSegmentLock = new object();

            // The index of the current queue in a group of similar queues.
            internal readonly uint _queueIndex;

            [StructLayout(LayoutKind.Explicit, Size = Internal.PaddingHelpers.CACHE_LINE_SIZE * 3)]
            internal struct PaddedQueueEnds
            {
                [FieldOffset(Internal.PaddingHelpers.CACHE_LINE_SIZE * 1)]
                public int Dequeue;
                [FieldOffset(Internal.PaddingHelpers.CACHE_LINE_SIZE * 2)]
                public int Enqueue;
            }

            internal WorkQueueBase(int index)
            {
                _queueIndex = (uint)index;
            }

            internal class QueueSegmentBase
            {
                /// <summary>The array of items in this queue.  Each slot contains the item in that slot and its "sequence number".</summary>
                internal readonly Slot[] _slots;

                /// <summary>Mask for quickly accessing a position within the queue's array.</summary>
                internal readonly int _slotsMask;

                /// <summary>The queue end positions, with padding to help avoid false sharing contention.</summary>
                internal PaddedQueueEnds _queueEnds; // mutable struct: do not make this readonly

                /// <summary>Indicates whether the segment has been marked such that no additional items may be enqueued.</summary>
                internal bool _frozenForEnqueues;

                /// <summary>Creates the segment.</summary>
                /// <param name="length">
                /// The maximum number of elements the segment can contain.  Must be a power of 2.
                /// </param>
                internal QueueSegmentBase(int length)
                {
                    // Validate the length
                    Debug.Assert(length >= 2, $"Must be >= 2, got {length}");
                    Debug.Assert((length & (length - 1)) == 0, $"Must be a power of 2, got {length}");

                    // Initialize the slots and the mask.  The mask is used as a way of quickly doing "% _slots.Length",
                    // instead letting us do "& _slotsMask".
                    var slots = new Slot[length];
                    _slotsMask = length - 1;

                    // Initialize the sequence number for each slot.  The sequence number provides a ticket that
                    // allows dequeuers to know whether they can dequeue and enqueuers to know whether they can
                    // enqueue.  An enqueuer at position N can enqueue when the sequence number is N, and a dequeuer
                    // for position N can dequeue when the sequence number is N + 1.  When an enqueuer is done writing
                    // at position N, it sets the sequence number to N + 1 so that a dequeuer will be able to dequeue,
                    // and when a dequeuer is done dequeueing at position N, it sets the sequence number to N + _slots.Length,
                    // so that when an enqueuer loops around the slots, it'll find that the sequence number at
                    // position N is N.  This also means that when an enqueuer finds that at position N the sequence
                    // number is < N, there is still a value in that slot, i.e. the segment is full, and when a
                    // dequeuer finds that the value in a slot is < N + 1, there is nothing currently available to
                    // dequeue. (It is possible for multiple enqueuers to enqueue concurrently, writing into
                    // subsequent slots, and to have the first enqueuer take longer, so that the slots for 1, 2, 3, etc.
                    // may have values, but the 0th slot may still be filled... in that case, TryDequeue will
                    // return false.)
                    for (int i = 0; i < slots.Length; i++)
                    {
                        slots[i].SequenceNumber = i;
                    }

                    _slots = slots;
                }

                // The state of a slot as a difference between SequenceNumber of a slot and its index.
                //   0: - empty slot
                //   1: - full slot
                //   _slots.Length: - becomes Empty when enqueue position wraps around the segment.
                internal const int Empty = 0;
                internal const int Full = 1;

                /// <summary>Represents a slot in the queue.</summary>
                [DebuggerDisplay("Item = {Item}, SequenceNumber = {SequenceNumber}")]
                [StructLayout(LayoutKind.Auto)]
                internal struct Slot
                {
                    /// <summary>The item.</summary>
                    internal object? Item;
                    /// <summary>The sequence number for this slot, used to synchronize between enqueuers and dequeuers.</summary>
                    internal int SequenceNumber;
#if TARGET_64BIT
                    // Takes space that would otherwise be alignment padding, so the slot does not get bigger.
                    private uint _enqueueTimestamp;
#endif

                    /// <summary>
                    /// When the item was enqueued (see <see cref="WaitTimeTracking.GetTimestamp"/>), or 0 when wait time tracking
                    /// is disabled. Like <see cref="Item"/>, it must be written before the slot is published and read before
                    /// the slot is released.
                    /// </summary>
                    internal uint EnqueueTimestamp
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        readonly get
                        {
#if TARGET_64BIT
                            return WaitTimeTracking.IsEnabled ? _enqueueTimestamp : 0;
#else
                            return 0;
#endif
                        }
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        set
                        {
#if TARGET_64BIT
                            if (WaitTimeTracking.IsEnabled)
                            {
                                _enqueueTimestamp = value;
                            }
#endif
                        }
                    }
                }

                internal ref Slot this[int i]
                {
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    get
                    {
                        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_slots), i & _slotsMask);
                    }
                }

                /// <summary>Gets the "freeze offset" for this segment.</summary>
                internal int FreezeOffset => _slots.Length * 2;

                // typical use is for debugging
                // Also, EnqueueAtHighPriority uses this to find the shortest, approximately, fifo queue.
                internal int Count
                {
                    get
                    {
                        // If the queue is not quiescent, races are unavoidable and the result is approximation.
                        // Read the dequeue first, so that overestimating is more likely than underestimating.
                        int deq = Volatile.Read(ref _queueEnds.Dequeue);
                        int count = _queueEnds.Enqueue - deq;

                        // A frozen segment has its Enqueue inflated by FreezeOffset. We cannot just mask the count, since
                        // that would make a completely full segment appear empty.
                        if (count >= FreezeOffset)
                        {
                            count -= FreezeOffset;
                        }

                        return Math.Clamp(count, 0, _slots.Length);
                    }
                }

                // for debugging
                internal IEnumerable<object> GetQueuedWorkItems()
                {
                    Slot[] slots = _slots;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        object? item = slots[i].Item;
                        if (item != null)
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        /// This flavor of the queue only supports Enqueue and Dequeue and that allows for some simplifications.
        /// We use multiple queues like this to implement a "global queue" - so that multiple enqueuer threads
        /// would not compete for the same queue.
        ///
        /// For a single queue the fifo order mostly holds, but is not guaranteed as concurrent dequeue operations
        /// do not necessarily finish in the same order as they start. The combined "global" queue guarantees
        /// even less. However, the dequeuing strategy tries to be fair, thus no single item should be
        /// arbitrarily delayed while other items make progress.
        /// Basically the "global" queue provides overall fairness at some cost to the throughput.
        [DebuggerDisplay("Count = {Count}")]
        internal sealed class FifoWorkQueue : WorkQueueBase
        {
            /// <summary>The current enqueue segment.</summary>
            internal QueueSegment _enqSegment;
            /// <summary>The current dequeue segment.</summary>
            internal QueueSegment _deqSegment;

            /// <summary>
            /// Initializes a new instance of the <see cref="FifoWorkQueue"/> class.
            /// </summary>
            internal FifoWorkQueue(int index)
                : base(index)
            {
                _enqSegment = _deqSegment = new QueueSegment(InitialSegmentLength);
            }

            // for debugging
            internal int Count
            {
                get
                {
                    int count = 0;
                    for (QueueSegment? s = _deqSegment; s != null; s = s._nextSegment)
                    {
                        count += s.Count;
                    }
                    return count;
                }
            }

            // for debugging
            internal IEnumerable<object> GetQueuedWorkItems()
            {
                for (QueueSegment? s = _deqSegment; s != null; s = s._nextSegment)
                {
                    foreach (object item in s.GetQueuedWorkItems())
                    {
                        yield return item;
                    }
                }
            }

            /// <summary>
            /// Adds an object to the top of the queue
            /// </summary>
            internal void Enqueue(object item)
            {
                // try enqueuing. Should normally succeed unless we need a new segment.
                if (!_enqSegment.TryEnqueue(item))
                {
                    // If we're unable to enqueue, this segment will never take enqueues again.
                    // we need to take a slow path that will try adding a new segment.
                    EnqueueSlow(item);
                }
            }

            /// <summary>
            /// Slow path for enqueue, adding a new segment if necessary.
            /// </summary>
            private void EnqueueSlow(object item)
            {
                while (true)
                {
                    QueueSegment currentSegment = _enqSegment;
                    if (currentSegment.TryEnqueue(item))
                    {
                        return;
                    }

                    // take the lock to add a new segment
                    // we can make this optimistically lock free, but it is a rare code path
                    // and we do not want stampeding enqueuers allocating a lot of new segments when only one will win.
                    lock (_addSegmentLock)
                    {
                        if (currentSegment == _enqSegment)
                        {
                            // Make sure that no more items could be added to the current segment.
                            // NB: there may be some strugglers still finishing up out-of-order enqueues
                            //     TryDequeue knows how to deal with that.
                            currentSegment.EnsureFrozenForEnqueues();

                            // We determine the new segment's length based on the old length.
                            // In general, we double the size of the segment, to make it less likely
                            // that we'll need to grow again.
                            int nextSize = Math.Min(currentSegment._slots.Length * 2, MaxSegmentLength);
                            var newEnq = new QueueSegment(nextSize);

                            // Hook up the new enqueue segment.
                            currentSegment._nextSegment = newEnq;
                            _enqSegment = newEnq;
                        }
                    }
                }
            }

            /// <summary>
            /// Removes an object at the bottom of the queue
            /// Returns null if the queue is empty.
            /// </summary>
            internal object? TryDequeue(ref bool missedSteal)
            {
                var currentSegment = _deqSegment;

                // The caller's missedSteal may already be set - by an earlier queue in the same scan, or deliberately
                // by the dispatch loop. That must not prevent us from moving past a drained segment, so the decision
                // to take the slow path is based only on what happened with this queue.
                bool localMissedSteal = false;
                object? result = currentSegment.TryDequeue(ref localMissedSteal);

                if (result == null &&
                    !localMissedSteal &&
                    currentSegment._nextSegment != null)
                {
                    // slow path that fixes up segments
                    result = TryDequeueSlow(currentSegment, ref localMissedSteal);
                }

                missedSteal |= localMissedSteal;
                return result;
            }

            /// <summary>
            /// Slow path for Dequeue, removing frozen segments as needed.
            /// </summary>
            private object? TryDequeueSlow(QueueSegment currentSegment, ref bool missedSteal)
            {
                object? result;
                while (true)
                {
                    // At this point we know that this segment has been frozen for additional enqueues. But between
                    // the time that we ran TryDequeue and checked for a next segment,
                    // another item could have been added.  Try to dequeue one more time
                    // to confirm that the segment is indeed empty.
                    Debug.Assert(currentSegment._nextSegment != null);
                    result = currentSegment.TryDequeueThoroughly();
                    if (result != null)
                    {
                        return result;
                    }

                    // Current segment is frozen (nothing more can be added) and empty (nothing is in it).
                    // Update _deqSegment to point to the next segment in the list, assuming no one's beat us to it.
                    if (currentSegment == _deqSegment)
                    {
                        Interlocked.CompareExchange(ref _deqSegment, currentSegment._nextSegment, currentSegment);
                    }

                    currentSegment = _deqSegment;

                    // Try to take.  If we're successful, we're done.
                    result = currentSegment.TryDequeue(ref missedSteal);
                    if (result != null)
                    {
                        return result;
                    }

                    // Check to see whether this segment is the last. If it is, we can consider
                    // this to be a moment-in-time when the queue is empty.
                    if (currentSegment._nextSegment == null)
                    {
                        return null;
                    }
                }
            }

            /// <summary>
            /// Provides a multi-producer, multi-consumer thread-safe bounded segment.  When the queue is full,
            /// enqueues fail and return false.  When the queue is empty, dequeues fail and return null.
            /// These segments are linked together to form the unbounded queue.
            /// </summary>
            [DebuggerDisplay("Count = {Count}")]
            internal sealed class QueueSegment : QueueSegmentBase
            {
                /// <summary>The segment following this one in the queue, or null if this segment is the last in the queue.</summary>
                internal QueueSegment? _nextSegment;

                /// <summary>Creates the segment.</summary>
                /// <param name="length">
                /// The maximum number of elements the segment can contain.  Must be a power of 2.
                /// </param>
                internal QueueSegment(int length) : base(length) { }

                /// <summary>
                /// Tries to dequeue an element from the queue.
                /// Returns null if the segment is empty.
                /// </summary>
                internal object? TryDequeueThoroughly()
                {
                    // Loop in case of contention...
                    SpinWait sw = default;
                    while (true)
                    {
                        int position = _queueEnds.Dequeue;
                        ref Slot slot = ref this[position];

                        // Read the sequence number for the slot.
                        // Should read before reading Item, but we read Item after CAS, so ordinary read is ok.
                        int diff = slot.SequenceNumber - position;

                        // Check if the slot is considered Full in the current generation.
                        if (diff == Full)
                        {
                            // Attempt to acquire the slot for Dequeuing.
                            if (Interlocked.CompareExchange(ref _queueEnds.Dequeue, position + 1, position) == position)
                            {
                                var item = slot.Item;
                                uint enqueueTimestamp = slot.EnqueueTimestamp;
                                slot.Item = null;

                                // make the slot appear empty in the next generation
                                Volatile.Write(ref slot.SequenceNumber, position + 1 + _slotsMask);
                                WaitTimeTracking.RecordGlobal(enqueueTimestamp);
                                return item;
                            }

                            // lost a race to another dequeuer
                        }
                        else if (diff < Full)
                        {
                            // The sequence number was less than what we needed, which means we cannot return this item.
                            // Check if we have reached Enqueue and return null indicating the segment is in empty state.
                            // NB: reading stale _frozenForEnqueues is fine - we would just spin once more
                            var currentEnqueue = _queueEnds.Enqueue;
                            if (currentEnqueue == position || (_frozenForEnqueues && currentEnqueue == position + FreezeOffset))
                            {
                                return null;
                            }

                            // The enqueuer went ahead and took a slot, but it has not finished filling the value.
                            // We cannot return `null` since the segment is not empty, so we must retry.
                        }

                        // Or we have a stale dequeue value. Another dequeuer was quicker than us.
                        // We should retry with a new dequeue.
                        sw.SpinOnce(sleep1Threshold: -1);
                    }
                }

                /// <summary>
                /// Tries to dequeue an element from the queue in one shot (no retries).
                /// - Returns an item, if successful.
                /// - Returns null if dequeue slot is not marked as Full yet.
                ///   NB: Enqueue guarantees that the item that we are responsible for is in a Full slot,
                ///     so seeing dequeue advanced to a non-Full slot still counts as "mission accomplished".
                /// - Returns null + sets missedSteal if lost a race to another dequeuer.
                ///   Other dequeuers will likely take care of "our" item, but an eventual
                ///   revisiting of the queue by some thread is required - to be sure.
                /// </summary>
                internal object? TryDequeue(ref bool missedSteal)
                {
                    int position = _queueEnds.Dequeue;
                    ref Slot slot = ref this[position];

                    // Read the sequence number for the slot.
                    // Should read before reading Item, but we read Item after CAS, so ordinary read is ok.
                    int sequenceNumber = slot.SequenceNumber;

                    // Check if the slot is considered Full in the current generation.
                    if (sequenceNumber == position + Full)
                    {
                        // Attempt to acquire the slot for Dequeuing.
                        if (Interlocked.CompareExchange(ref _queueEnds.Dequeue, position + 1, position) == position)
                        {
                            var item = slot.Item;
                            uint enqueueTimestamp = slot.EnqueueTimestamp;
                            slot.Item = null;

                            // make the slot appear empty in the next generation
                            Volatile.Write(ref slot.SequenceNumber, position + 1 + _slotsMask);
                            WaitTimeTracking.RecordGlobal(enqueueTimestamp);
                            return item;
                        }

                        // lost a race to another dequeuer.
                    }
                    else if (sequenceNumber - position < Full)
                    {
                        // the item is not there yet
                        return null;
                    }
                    else
                    {
                        // lost a race to another dequeuer before even trying to advance the dequeue.
                    }

                    missedSteal = true;
                    return null;
                }

                /// <summary>
                /// Attempts to enqueue the item.
                /// If successful, the item will be stored in the queue and true will be returned;
                /// otherwise, the item won't be stored, the segment will be frozen and false will be returned.
                /// </summary>
                public bool TryEnqueue(object item)
                {
                    uint enqueueTimestamp = WaitTimeTracking.GetEnqueueTimestamp();
                    while (true)
                    {
                        int position = _queueEnds.Enqueue;
                        ref Slot slot = ref this[position];

                        // Read the sequence number for the enqueue position.
                        // Should read before writing Item, but our write is after CAS, so ordinary read is ok.
                        int sequenceNumber = slot.SequenceNumber;

                        // The slot is empty and ready for us to enqueue into it if its sequence number matches the slot.
                        if (sequenceNumber == position)
                        {
                            // Reserve the slot for Enqueuing.
                            if (Interlocked.CompareExchange(ref _queueEnds.Enqueue, position + 1, position) == position)
                            {
                                slot.Item = item;
                                slot.EnqueueTimestamp = enqueueTimestamp;
                                Volatile.Write(ref slot.SequenceNumber, position + Full);
                                // NB: Volatile.Write would be sufficient as the queue end update makes the queue not empty.
                                // But we would need to spin in Dequeue until item appears and current thread could be preempted,
                                // or implement some kind of "missed steal" scheme.
                                // With the memory barrier we wait for publishing of the item before checking if a thread is invited,
                                // thus dequeuer can treat seeing an old sequence number as "item is not there".
                                // However, another enqueuer would still need to wait for the sequence number to change.
                                Interlocked.MemoryBarrier();
                                return true;
                            }
                        }
                        else if (sequenceNumber - position < 0)
                        {
                            // The sequence number was less than what we needed, which means we have caught up with previous generation
                            // Technically it's possible that we have dequeuers in progress and spaces are or about to be available.
                            // We still would be better off with a new segment.
                            return false;
                        }

                        // Lost a race to another enqueue. Need to retry.
                        // No need to wait though. Either the current or the other thread use "wrong" queue.
                        // Waiting would not help with that.
                    }
                }

                internal void EnsureFrozenForEnqueues()
                {
                    // flag used to ensure we don't increase the enqueue more than once
                    if (!_frozenForEnqueues)
                    {
                        // Increase the enqueue by FreezeOffset atomically.
                        // enqueuing will be impossible after that
                        // dequeuers would need to dequeue 2 generations to catch up, and they can't
                        Interlocked.Add(ref _queueEnds.Enqueue, FreezeOffset);
                        _frozenForEnqueues = true;
                    }
                }
            }
        }

        /// <summary>
        /// A flavor of the queue that is similar to the fifo queue, but also supports Pop, Remove and Move operations.
        /// - Pop is used to implement Busy-Leaves scheduling strategy.
        /// - Remove is used when the caller finds it beneficial to execute a workitem "inline" after it has been scheduled.
        ///   (such as waiting on a task completion).
        /// - Move is used to rebalance queues if one is found to be "rich" by stealing 1/2 of its queue.
        ///   (a divide-and-conquer mitigation for contention among thieves if a few queues contain most of tasks)
        ///
        /// We create multiple such "local" queues and associate with CPU cores.
        /// </summary>
        [DebuggerDisplay("Count = {Count}")]
        internal sealed class WorkStealingQueue : WorkQueueBase
        {
            /// <summary>
            /// When a segment has more than this, we steal half of its slots.
            /// Intuitively, moving too few items is not profitable due to per-move overhead.
            /// The number is somewhat arbitrary chosen, considering extra complexity of the move vs. steal.
            /// The benchmarks show that it is not critical for this number to be precise.
            /// </summary>
            internal const int MoveThreshold = 32;

            /// <summary>The current enqueue segment.</summary>
            internal QueueSegment _enqSegment;
            /// <summary>The current dequeue segment.</summary>
            internal QueueSegment _deqSegment;

            /// <summary>
            /// Wait time statistics flushed by the threads that dispatch on this queue's core.
            /// Null when wait time tracking is disabled.
            /// </summary>
            internal readonly WaitTimeTracking.Accumulator? _waitTimes;

            /// <summary>
            /// Initializes a new instance of the <see cref="WorkStealingQueue"/> class.
            /// </summary>
            internal WorkStealingQueue(int index)
                  : base(index)
            {
                if (WaitTimeTracking.IsEnabled)
                {
                    _waitTimes = new WaitTimeTracking.Accumulator();
                }

                _enqSegment = _deqSegment = new QueueSegment(InitialSegmentLength);
            }

            // for debugging
            internal int Count
            {
                get
                {
                    int count = 0;
                    for (QueueSegment? s = _deqSegment; s != null; s = s._nextSegment)
                    {
                        count += s.Count;
                    }
                    return count;
                }
            }

            // for debugging
            internal IEnumerable<object> GetQueuedWorkItems()
            {
                for (QueueSegment? s = _deqSegment; s != null; s = s._nextSegment)
                {
                    foreach (object item in s.GetQueuedWorkItems())
                    {
                        yield return item;
                    }
                }
            }

            /// <summary>
            /// Adds an object to the top of the queue
            /// </summary>
            internal void Enqueue(object item)
            {
                // try enqueuing. Should normally succeed unless we need a new segment.
                if (!_enqSegment.TryEnqueue(item))
                {
                    // If we're unable to enqueue, this segment is full.
                    // we need to take a slow path that will try adding a new segment.
                    EnqueueSlow(item);
                }
            }

            /// <summary>
            /// Slow path for Enqueue, adding a new segment if necessary.
            /// </summary>
            private void EnqueueSlow(object item)
            {
                QueueSegment currentSegment = _enqSegment;
                while (true)
                {
                    if (currentSegment.TryEnqueue(item))
                    {
                        return;
                    }
                    currentSegment = EnsureNextSegment(currentSegment);
                }
            }

            private QueueSegment EnsureNextSegment(QueueSegment currentSegment)
            {
                var nextSegment = currentSegment._nextSegment;
                if (nextSegment != null)
                {
                    return nextSegment;
                }

                // take the lock to add a new segment
                // we can make this optimistically lock free, but it is a rare code path
                // and we do not want stampeding enqueuers allocating a lot of new segments when only one will win.
                lock (_addSegmentLock)
                {
                    if (currentSegment._nextSegment == null)
                    {
                        // We determine the new segment's length based on the old length.
                        // In general, we double the size of the segment, to make it less likely
                        // that we'll need to grow again.
                        int nextSize = Math.Min(currentSegment._slots.Length * 2, MaxSegmentLength);
                        var newEnq = new QueueSegment(nextSize);

                        // Hook up the new enqueue segment.
                        currentSegment._nextSegment = newEnq;
                        _enqSegment = newEnq;
                    }
                }

                return currentSegment._nextSegment;
            }

            /// <summary>
            /// Removes the oldest element from the queue
            /// Returns null if the queue is empty or if there is a contention.
            /// missedSteal is set to true if attempt failed due to contention while the queue may be not empty.
            /// </summary>
            internal object? TrySteal(WorkStealingQueue localWsQueue, ref bool missedSteal)
            {
                var currentSegment = _deqSegment;
                object? result = currentSegment.TrySteal(localWsQueue, ref missedSteal);

                // Note: we check the next segment even if we had a missed steal as that
                // helps with retiring the current segment.
                if (result == null && currentSegment._nextSegment != null)
                {
                    return TryStealSlow(localWsQueue, currentSegment, ref missedSteal);
                }

                return result;
            }

            /// <summary>
            /// Tries to dequeue an item, removing frozen segments as needed.
            /// </summary>
            private object? TryStealSlow(WorkStealingQueue localWsQueue, QueueSegment currentSegment, ref bool missedSteal)
            {
                object? result;
                while (true)
                {
                    // At this point we know that this segment has been frozen for additional enqueues.
                    // But between the time that we ran TrySteal and checked for a next segment,
                    // another item could have been added.  Try to dequeue one more time
                    // to confirm that the segment is indeed empty.
                    Debug.Assert(currentSegment._nextSegment != null);

                    // we must know for sure the segment is quiescent and empty before removing it
                    bool localMissedSteal = false;
                    result = currentSegment.TrySteal(localWsQueue, ref localMissedSteal);
                    if (result != null)
                    {
                        return result;
                    }

                    // Getting a missing steal makes us unsure if the segment has items or not.
                    // We cannot continue. We could either spin through steals,
                    // or just declare a missing steal to the caller. We do the latter.
                    if (localMissedSteal)
                    {
                        missedSteal = localMissedSteal;
                        return null;
                    }

                    // Current segment is frozen (nothing more can be added) and empty (nothing is in it).
                    // Update _deqSegment to point to the next segment in the list, assuming no one's beat us to it.
                    if (currentSegment == _deqSegment)
                    {
                        Interlocked.CompareExchange(ref _deqSegment, currentSegment._nextSegment, currentSegment);
                    }

                    currentSegment = _deqSegment;

                    // Try to dequeue.  If we're successful, we're done.
                    result = currentSegment.TrySteal(localWsQueue, ref missedSteal);
                    if (result != null)
                    {
                        return result;
                    }

                    // Check to see whether this segment is the last. If it is, we can consider
                    // this to be a moment-in-time when the queue is empty.
                    if (currentSegment._nextSegment == null)
                    {
                        return null;
                    }
                }
            }

            /// <summary>
            /// Pops the newest item from the queue.
            /// Returns null if there is nothing to pop.
            /// </summary>
            internal object? TryPop()
            {
                // we save current index + 1, because 0 means "uninitialized.
                t_localQueueIdx = this._queueIndex + 1;
                return _enqSegment.TryPop();
            }

            internal bool CanPop => _enqSegment.CanPop;

            /// <summary>
            /// Performs a search for the given item in the queue and removes the item if found.
            /// Returns true if item was indeed removed.
            /// Returns false if item was not found or was taken by another worker.
            /// </summary>
            internal bool TryRemove(Task callback)
            {
                var enqSegment = _enqSegment;
                if (enqSegment.TryRemove(callback))
                {
                    return true;
                }

                for (QueueSegment? segment = _deqSegment;
                   segment != null && segment != enqSegment;
                   segment = segment._nextSegment)
                {
                    if (segment.TryRemove(callback))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Provides a multi-producer, multi-consumer thread-safe bounded segment.  When the queue is full,
            /// enqueues fail and return false.  When the queue is empty, attempts to fetch an item fail and return null.
            /// These segments are linked together to form the unbounded queue.
            ///
            /// The main difference from the implementation in fifo queue is support for Pop so that a worker could select
            /// the most recent workitem. Among other benefits, like improving locality of workitem execution, the approach
            /// is critical to minimizing the number of workitems that exist at any given time (i.e. the max queue size),
            /// because a recently enqueued workitem is more likely to be a "leaf" workitem, vs. a workitem that might
            /// decompose itself into multiple workitems.
            ///
            /// Supporting Pop leads to additional complexity compared to fifo segments.
            /// In particular the enqueue index may move both forward and backward and therefore we cannot rely on atomic
            /// update of the enqueue index as a way of acquiring access to an Enqueue/Pop slot.
            /// Since all operation (Steal, Enqueue, Pop, etc.) we need coordination scheme that would work for all
            /// operations when the segment contains one element.
            ///
            /// We resolve these issues by observing the following rules:
            /// - In an empty segment dequeue and enqueue ends point to the same slot. This is also the initial state.
            /// - The enqueue end is never behind the dequeue end.
            /// - Slots in "Full" state form a contiguous range.
            /// - Pop and Enqueue operation must acquire the access to enqueue slot by atomically moving the previous slot to the "Change" state.
            ///   Setting the next state can be done via regular write after the Enqueue/Pop is complete and enqueue end is moved appropriately.
            /// - Steal operation must acquire access to the dequeue slot by atomically moving it to the "Dequeue" state.
            ///   Setting the slot to the next state can be done via regular write. After the Dequeue is complete and dequeue end is moved forward.
            ///
            ///   To summarize:
            ///     In a rare case of concurrent Pop and Enqueue, the threads claim the access by setting "Change" state in the same slot.
            ///     Similarly, concurrent Steals will coordinate the access on the other end of the segment.
            ///     When the segment shrinks to just 1 element, all Pop/Enqueue/Steal operations would end up using the same coordinating slot.
            ///     This indirectly guarantees that concurrent Steal and Pop cannot use the same slot and move enqueue/dequeue ends across each other.
            ///
            /// - Move/Remove operations get exclusive access to appropriate ranges of slots by setting "Change" on both sides of the range.
            /// - The enqueue and dequeue ends are updated only when corresponding slots are reserved for changing.
            /// - It is possible, in a case of a contention, to move a wrong slot to the "Change" state.
            ///   (Ex: the slot is no longer previous to an enqueue slot because enqueue index has moved forward)
            ///   When such situation is possible, it can be detected by re-examining the condition after the slot has been moved to "Change" state.
            ///   This kind of contentions are rare and handled by reverting the slot(s) to the original state and performing an appropriate backoff.
            ///   The reverting of failed enqueue slot is done via CAS - in case if the slot has changed the state due to Moving (the new state should win).
            ///   The reverting of failed dequeue slot is an ordinary write. Since dequeue moves monotonically, it cannot end up in a Moving range.
            ///
            /// </summary>
            [DebuggerDisplay("Count = {Count}")]
            internal sealed class QueueSegment : QueueSegmentBase
            {
                /// <summary>The segment following this one in the queue, or null if this segment is the last in the queue.</summary>
                internal QueueSegment? _nextSegment;

                /// <summary>
                /// Another state of the slot in addition to Empty and Full.
                /// "Change" means that the slot is reserved for possible modifications.
                /// The state is used for mutual communication between Enqueue/Pop/Remove.
                /// NB: Enqueue reserves the slot "to the left" of the slot that is targeted by Enqueue.
                ///     This ensures that "Full" slots occupy a contiguous range (not a requirement and is not true for the fifo flavor of the queue)
                /// "Dequeue" has roughly the same purpose as "Change", but only used on the dequeuing end of the queue.
                /// This allows us to detect cases where pop has reached the dequeuing end of the segment.
                /// </summary>
                private const int Change = 2;
                private const int Dequeue = 3;

                /// <summary>Creates the segment.</summary>
                /// <param name="length">
                /// The maximum number of elements the segment can contain.  Must be a power of 2.
                /// </param>
                internal QueueSegment(int length) : base(length) { }

                /// <summary>
                /// Attempts to enqueue the item.
                /// If successful, the item will be stored in the queue and true will be returned.
                /// Returns false if the segment has no space.
                /// </summary>
                internal bool TryEnqueue(object item)
                {
                    uint enqueueTimestamp = WaitTimeTracking.GetEnqueueTimestamp();

                    // Loop in a case if we need to try again.
                    // Contention is rare here, since this is "our" queue, but we may accidentally share
                    // or have interference from stealing.
                    SpinWait sw = default;
                    while (true)
                    {
                        int position = _queueEnds.Enqueue;
                        ref Slot prevSlot = ref this[position - 1];
                        int prevSequenceNumber = prevSlot.SequenceNumber;

                        // check if prev slot is full in the current generation or empty in the next
                        // otherwise retry - we have some kind of race, most likely the prev item is being stolen
                        if (prevSequenceNumber == position || prevSequenceNumber == position + _slotsMask)
                        {
                            // lock the previous slot (so no one could dequeue past us, pop the prev slot or enqueue into the same position)
                            // NB: once we lock the slot, the segment can not be considered empty by the TrySteal
                            if (Interlocked.CompareExchange(ref prevSlot.SequenceNumber, prevSequenceNumber + Change, prevSequenceNumber) == prevSequenceNumber)
                            {
                                // Confirm that enqueue did not change while we were locking the slot.
                                // It is not common, but we may see concurrent Enqueue on the same segment.
                                if (_queueEnds.Enqueue == position)
                                {
                                    // Successfully locked prev slot.
                                    // is the Enqueue slot empty?   (most common path)
                                    ref Slot slot = ref this[position];
                                    int sequenceNumber = slot.SequenceNumber;
                                    if (sequenceNumber == position)
                                    {
                                        // Noone can use the current slot right now, since its state is Empty and the prev slot is locked.
                                        // But once we advance Enqueue, the slot can be locked as a prev.
                                        // We do not want that until we mark the slot as Full. So mark the slot as Changing.
                                        // It does not need to be a CAS, but must happen before updating the Enqueue end.
                                        slot.SequenceNumber = position + Change;

                                        // Update Enqueue (must be done before marking the slot as full and after marking the slot as changing)
                                        Volatile.Write(ref _queueEnds.Enqueue, position + 1);

                                        // Fill the slot (must be done before the slot is marked as Full, ordering with above writes is unimportant)
                                        slot.Item = item;
                                        slot.EnqueueTimestamp = enqueueTimestamp;

                                        // Mark the slot as Full in the current generation.
                                        // the slot can be used immediately by other threads.
                                        Volatile.Write(ref slot.SequenceNumber, position + Full);

                                        // Unlock the prev slot
                                        // (must be after marking current as Full, can't allow both current and prev slots be Empty while write is in progress).
                                        Volatile.Write(ref prevSlot.SequenceNumber, prevSequenceNumber);
                                        return true;
                                    }

                                    // Not empty. Did we catch with the prev generation, meaning the segment is full?
                                    if (position - sequenceNumber > 0)
                                    {
                                        // Set Enqueue to throw off anyone else trying to enqueue or pop, unless we have already done that.
                                        // we need a fence between writing to Enqueue and unlocking, but we unlock with a CAS anyways
                                        _queueEnds.Enqueue = position + FreezeOffset;
                                        _frozenForEnqueues = true;
                                    }
                                    else
                                    {
                                        // A rare race. A slot is being popped, Enqueue has moved back already, but the slot is still not empty.
                                    }
                                }

                                // Enqueue changed and we locked a wrong slot.
                                // Unlock the slot through CAS in case slot was Moved, in such case the new state should win.
                                // We also use this code path for couple other rare cases (segment is full or being popped) - for simplicity.
                                Interlocked.CompareExchange(ref prevSlot.SequenceNumber, prevSequenceNumber, prevSequenceNumber + Change);
                            }
                        }

                        if (_frozenForEnqueues)
                        {
                            return false;
                        }

                        // Contention, we should try again after a delay
                        sw.SpinOnce(sleep1Threshold: -1);
                    }
                }

                internal bool CanPop
                {
                    get
                    {
                        int position = _queueEnds.Enqueue - 1;
                        ref Slot slot = ref this[position];

                        // Read the sequence number for the slot.
                        int sequenceNumber = slot.SequenceNumber;

                        // Check if the slot is considered Full in the current generation (other likely state - Empty).
                        return (sequenceNumber == position + Full);
                    }
                }

                // Returns the most recently added item, if there is one and we can pop it.
                // Returns null if we should start stealing instead.
                internal object? TryPop()
                {
                    // Retry in cases like contention or finding a removed item.
                    // Contention is rare here, since this is "our" queue, but we may accidentally share
                    // or have interference from stealing.
                    SpinWait sw = default;
                    while (true)
                    {
                        int position = _queueEnds.Enqueue - 1;
                        ref Slot slot = ref this[position];

                        // Read the sequence number for the slot.
                        int sequenceNumber = slot.SequenceNumber;

                        // Check if the slot is considered Full in the current generation
                        int diff = sequenceNumber - position;
                        if (diff == Full)
                        {
                            // lock the slot.
                            diff = Interlocked.CompareExchange(ref slot.SequenceNumber, position + Change, sequenceNumber) - position;
                            if (diff == Full)
                            {
                                // Confirm that enqueue did not change while we were locking the slot.
                                if (_queueEnds.Enqueue == sequenceNumber)
                                {
                                    // Update Enqueue before marking slot empty.
                                    // if enqueue update happens later than that, someone may enqueue into a wrong slot.
                                    // The order of updating Enqueue vs. taking the item is irrelevant.
                                    // We update the Enqueue first - with everything equal, there is a tiny chance there
                                    // is a concurrent Pop and it may be able to succeed as soon as Enqueue is updated.
                                    _queueEnds.Enqueue = position;

                                    var item = slot.Item;
                                    uint enqueueTimestamp = slot.EnqueueTimestamp;
                                    slot.Item = null;

                                    // make the slot appear empty in the current generation, this unlocks the slot
                                    Volatile.Write(ref slot.SequenceNumber, position);
                                    if (item == null)
                                    {
                                        // item was Removed
                                        // this is not a lost race though, so continue.
                                        continue;
                                    }

                                    WaitTimeTracking.RecordLocal(enqueueTimestamp);
                                    return item;
                                }

                                // enqueue changed, we locked a wrong slot
                                // Unlock the slot through CAS in case slot was Moved, in such case the new state should win.
                                Interlocked.CompareExchange(ref slot.SequenceNumber, sequenceNumber, position + Change);
                            }
                        }

                        // If the slot is empty (in the next generation) or in Dequeue state,
                        // then we have reached the dequeuing end of the segment.
                        // Note: "Move" may not take all the elements that it wanted, so segment is not definitely empty,
                        //       but it is still a good enough indication that we should start stealing.
                        if (diff == 1 + _slotsMask || diff == Dequeue || _frozenForEnqueues)
                        {
                            return null;
                        }

                        // Contention, we should try again after a delay
                        sw.SpinOnce(sleep1Threshold: -1);
                    }
                }

                /// <summary>
                /// Tries to dequeue an element from the queue.
                /// "missedSteal" is set to true when we find the segment in a state where we cannot take an element and
                /// cannot claim the segment is empty.
                /// That generally happens when another thread did or is doing modifications and we do not see all the changes.
                /// We could spin here until we see a consistent state, but it makes more sense to service other queues.
                /// </summary>
                internal object? TrySteal(WorkStealingQueue localWsQueue, ref bool missedSteal)
                {
                    while (true)
                    {
                        int position = _queueEnds.Dequeue;

                        // The emptiness criteria for steal is finding dequeue end pointing to a
                        // slot that is empty in current generation (thus all subsequent slots are empty), and
                        // prev is empty in the new generation. We need to check the prev slot before checking the current.
                        // NB: while slots that are prev to dequeue end cannot become Full,they can be locked if
                        //     the current slot is being filled up. In such case we cannot consider the current slot as empty.
                        if (!missedSteal)
                        {
                            missedSteal = Volatile.Read(ref this[position - 1].SequenceNumber) != (position + _slotsMask);
                        }

                        // Read the sequence number for the slot.
                        ref Slot slot = ref this[position];
                        int sequenceNumber = slot.SequenceNumber;

                        // Check if the slot is considered Full in the current generation.
                        int diff = sequenceNumber - position;
                        if (diff == Full)
                        {
                            // Reserve the slot for dequeuing.
                            diff = Interlocked.CompareExchange(ref slot.SequenceNumber, position + Dequeue, sequenceNumber) - position;
                            if (diff == Full)
                            {
                                // dequeue moves only forward. we cannot be ahead.
                                Debug.Assert(position == _queueEnds.Dequeue);

                                object? item;
                                uint enqueueTimestamp;

                                // if we have a local queue (it is likely that we have it and that it is empty),
                                // and if the queue we are stealing from is "rich", try stealing half its items.
                                var enqPos = _queueEnds.Enqueue;
                                if (enqPos - position < MoveThreshold ||
                                    // "this" is a sentinel for a failed Move attempt
                                    (item = TryMoveTo(localWsQueue._enqSegment, position, enqPos, out enqueueTimestamp)) == this)
                                {
                                    // Move did not work out, so just take the item that we have reserved.
                                    _queueEnds.Dequeue = position + 1;
                                    item = slot.Item;
                                    enqueueTimestamp = slot.EnqueueTimestamp;
                                    slot.Item = null;
                                }

                                // unlock the slot for enqueuing by making the slot empty in the next generation
                                Volatile.Write(ref slot.SequenceNumber, position + 1 + _slotsMask);

                                if (item == null)
                                {
                                    // the item was removed, so we have nothing to return. This is not a lost race though, so must try again.
                                    continue;
                                }

                                WaitTimeTracking.RecordLocal(enqueueTimestamp);
                                return item;
                            }
                        }

                        if (diff != Empty)
                        {
                            missedSteal = true;
                        }

                        return null;
                    }
                }

                /// <summary>
                /// Moves up to half of the items, starting at <paramref name="deqPosition"/>, to the <paramref name="other"/> segment,
                /// except for the last one, which is returned along with its <paramref name="enqueueTimestamp"/>.
                /// Returns "this" if the move did not happen.
                /// </summary>
                internal object? TryMoveTo(QueueSegment other, int deqPosition, int enqPosition, out uint enqueueTimestamp)
                {
                    enqueueTimestamp = 0;

                    // similar sequence as in TryEnqueue, since we will be adding items to the other queue.
                    int otherEnqPosition = other._queueEnds.Enqueue;
                    ref Slot enqPrevSlot = ref other[otherEnqPosition - 1];
                    int prevSequenceNumber = enqPrevSlot.SequenceNumber;

                    // Mask the count in case the segment is frozen and enqueue is inflated.
                    var count = (enqPosition - deqPosition) & _slotsMask;
                    // Recheck after masking. The outer check does not mask for simplicity.
                    if (count < MoveThreshold)
                    {
                        // fail
                        return this;
                    }

                    int halfPosition = deqPosition + count / 2;
                    ref Slot halfSlot = ref this[halfPosition];

                    // unlike Enqueue, we require prev slot be empty
                    // not just to prevent rich queue getting even richer
                    // we also do not want a possibility that the same segment is both Moved from and Moved to, which would be messy
                    if (prevSequenceNumber == otherEnqPosition + other._slotsMask)
                    {
                        // lock the other segment for enqueuing
                        if (Interlocked.CompareExchange(ref enqPrevSlot.SequenceNumber, prevSequenceNumber + Change, prevSequenceNumber) == prevSequenceNumber)
                        {
                            // confirm that enqueue did not change while we were locking the slot
                            // it is uncommon, but we may see another Pop or Enqueue on the same segment.
                            if (other._queueEnds.Enqueue == otherEnqPosition)
                            {
                                // Lock the halfSlot, it must be full
                                // We use Dequeue state here as indication that items to the left will likely be not available for popping.
                                if (Interlocked.CompareExchange(ref halfSlot.SequenceNumber, halfPosition + Dequeue, halfPosition + Full) == halfPosition + Full)
                                {
                                    // our enqueue could have changed before we locked half
                                    // make sure that half-way slot is still before enqueue
                                    // in fact give it more space - we do not want to Move all remaining items, especially if someone else popping them fast.
                                    var enq = deqPosition + ((_queueEnds.Enqueue - deqPosition) & _slotsMask);
                                    var nextGenEmpty = _slotsMask + 1;
                                    if (enq - halfPosition > (count / 4))
                                    {
                                        int fromIdx = deqPosition, toIdx = otherEnqPosition;
                                        // copy slots from "this" to "other", until the "other" is full or we reach the half-way point, whichever is first.
                                        // the last "from" slot is not copied and returned instead.
                                        ref Slot from = ref this[fromIdx++];
                                        while (true)
                                        {
                                            ref Slot next = ref this[fromIdx];
                                            ref Slot to = ref other[toIdx];

                                            // the "to" slot must be empty. (not empty means no more space)
                                            // the "next" slot must be full (any write must be completed, this also takes care of stopping at halfSlot)
                                            if (Volatile.Read(ref to.SequenceNumber) != toIdx ||
                                                Volatile.Read(ref next.SequenceNumber) != fromIdx + Full)
                                            {
                                                break;
                                            }

                                            to.Item = from.Item;
                                            // moved items keep their enqueue time
                                            to.EnqueueTimestamp = from.EnqueueTimestamp;
                                            // Note: the following enables "to" for dequeuing, which may immediately happen,
                                            // but not for popping, yet - since the other enq is locked.
                                            Volatile.Write(ref to.SequenceNumber, toIdx + Full);
                                            from.Item = null;

                                            // We are going to take from next, mark it empty.
                                            next.SequenceNumber = fromIdx + nextGenEmpty;
                                            from = ref next;

                                            fromIdx++;
                                            toIdx++;
                                        }

                                        // return the last slot value
                                        // (it should already be marked empty, or will be, if it is at deqPosition)
                                        var result = from.Item;
                                        enqueueTimestamp = from.EnqueueTimestamp;
                                        from.Item = null;

                                        // restore the half slot, must be after all the full->empty slot transitioning
                                        // to make sure that poppers cannot see Moved slots as still incorrectly full.
                                        Volatile.Write(ref halfSlot.SequenceNumber, halfPosition + Full);

                                        // advance the other enq, enables enq/pop
                                        // must be done before unlocking other prev slot, or someone could pop prev once unlocked.
                                        // must be done after the enq slot are full, or someone may try locking slots while/before we mark them full.
                                        Volatile.Write(ref other._queueEnds.Enqueue, toIdx);

                                        // advance Dequeue, must be after halfSlot is restored - someone could immediately start Moving.
                                        Volatile.Write(ref _queueEnds.Dequeue, fromIdx);

                                        // unlock other prev slot
                                        // must be after we moved other enq to the next slot, or someone may pop prev and break continuity of full slots.
                                        Volatile.Write(ref enqPrevSlot.SequenceNumber, prevSequenceNumber);

                                        if (toIdx != otherEnqPosition)
                                        {
                                            // The moved items are now in a queue that a worker may have already scanned and found empty,
                                            // and no thread request was made for them. Make sure some worker will check the queues again.
                                            // The fence is needed to publish the moved items before checking for an outstanding request,
                                            // for the same reason as in the fifo TryEnqueue.
                                            Interlocked.MemoryBarrier();
                                            ThreadPool.EnsureWorkerRequested();
                                        }

                                        return result;
                                    }

                                    // failed to lock the actual half-way slot.
                                    // restore via CAS, in case target slot has been Moved to
                                    Interlocked.CompareExchange(ref halfSlot.SequenceNumber, halfPosition + Full, halfPosition + Dequeue);
                                }
                            }

                            // failed to lock actual enqueue end, restore with CAS, in case target slot has been Moved to/from
                            Interlocked.CompareExchange(ref enqPrevSlot.SequenceNumber, prevSequenceNumber, prevSequenceNumber + Change);
                        }
                    }

                    // "this" is a sentinel value for a failed Moving attempt
                    return this;
                }

                /// <summary>
                /// Searches for the given callback and removes it.
                /// Returns "true" if actually removed the item.
                /// </summary>
                internal bool TryRemove(Task callback)
                {
                    for (int position = _queueEnds.Enqueue - 1; ; position--)
                    {
                        ref Slot slot = ref this[position];
                        if (slot.Item == callback)
                        {
                            // lock Dequeue (so that the slot would not be Moved while we are removing)
                            var deqPosition = _queueEnds.Dequeue;
                            ref var deqSlot = ref this[deqPosition];
                            if (Interlocked.CompareExchange(ref deqSlot.SequenceNumber, deqPosition + Change, deqPosition + Full) == deqPosition + Full)
                            {
                                // lock the slot,
                                // unless it is the same as Dequeue, in which case we have already locked it
                                if (position == deqPosition ||
                                    Interlocked.CompareExchange(ref slot.SequenceNumber, position + Change, position + Full) == position + Full)
                                {
                                    // Successfully locked the slot.
                                    // check if the item is still there
                                    if (slot.Item == callback)
                                    {
                                        slot.Item = null;
                                        // unlock the slot.
                                        // must happen after setting slot to null
                                        Volatile.Write(ref slot.SequenceNumber, position + Full);

                                        // unlock Dequeue (if different) and return success.
                                        if (position != deqPosition)
                                        {
                                            deqSlot.SequenceNumber = deqPosition + Full;
                                        }
                                        return true;
                                    }

                                    // unlock the slot and exit
                                    if (position != deqPosition)
                                    {
                                        slot.SequenceNumber = position + Full;
                                    }
                                }

                                // unlock Dequeue
                                deqSlot.SequenceNumber = deqPosition + Full;
                            }

                            // lost the item to someone else, will not see it again
                            break;
                        }
                        else if (slot.SequenceNumber - position > Change)
                        {
                            // reached the next gen
                            break;
                        }
                    }
                    return false;
                }
            }
        }

        private bool _loggingEnabled;

        // Same PRNG as t_rndState.
        // Used as a fallback when a stealing queue is not available.
        private uint _rndState;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint NextRnd()
        {
            return (_rndState = _rndState * 1664525u + 1013904223u) >> 16;
        }

        // SOS's ThreadPool command depends on the following names (these are dummies though)
        internal readonly WorkQueueUnused workItems = new WorkQueueUnused();
        internal readonly WorkQueueUnused highPriorityWorkItems = new WorkQueueUnused();
        internal readonly WorkQueueUnused[] _assignableWorkItemQueues = new WorkQueueUnused[1];

        // actual queues
        internal readonly WorkStealingQueue[] _WorkStealingQueues;
        internal readonly FifoWorkQueue[] _FifoQueues;

        // The purpose of this index is that if a currently executing task decomposes into
        // subtasks, they go into the original local queue.
        // We save the index with +1 offset so that 0 means "uninitialized", which
        // for most purposes is the same as "not a threadpool thread".
        [ThreadStatic]
        private static uint t_localQueueIdx;

        // Random number generator used to select stealing/dequeuing victims.
        // We use classic LCG PRNG here. (https://en.wikipedia.org/wiki/Linear_congruential_generator)
        // LCG with 32bit state is a good 16bit PRNG, as long as the upper bits are used.
        // LCG is very fast and 16bit is more than enough for our use. (bounded by the core count).
        // The state is per thread, so that updating it does not cause cache traffic between cores.
        [ThreadStatic]
        private static uint t_rndState;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint NextThreadRnd()
        {
            uint rndState = t_rndState;
            if (rndState == 0)
            {
                // Seed threads differently, so that they do not all traverse the queues in the same order.
                rndState = (uint)Environment.CurrentManagedThreadId;
            }

            t_rndState = rndState = rndState * 1664525u + 1013904223u;
            return rndState >> 16;
        }

        // Whether DequeueAll scans fifo queues starting from the queue the current thread would enqueue into (the default),
        // or from a random queue.
        private static readonly bool s_fifoScanFromCurrentQueue = AppContextConfigHelper.GetBooleanConfig(
            "System.Threading.ThreadPool.FifoScanFromCurrentQueue",
            "DOTNET_ThreadPool_FifoScanFromCurrentQueue",
            defaultValue: true);

        // For experiments: enqueue all work items into the fifo queues, even when the caller prefers the local queue.
        private static readonly bool s_forceGlobalEnqueue = AppContextConfigHelper.GetBooleanConfig(
            "System.Threading.ThreadPool.ForceGlobalEnqueue",
            "DOTNET_ThreadPool_ForceGlobalEnqueue",
            defaultValue: false);

        public PerCoreThreadPoolWorkQueue()
        {
            int processorCount = Environment.ProcessorCount;
            _WorkStealingQueues = new WorkStealingQueue[BitOperations.RoundUpToPowerOf2((uint)processorCount)];

            // The number of cores that share one fifo queue. Fewer fifo queues mean less to scan when looking for work,
            // but more contention between enqueuers.
            int fifoQueuesPerCores = AppContextConfigHelper.GetInt32Config(
                "System.Threading.ThreadPool.FifoQueuesPerCores",
                "DOTNET_ThreadPool_FifoQueuesPerCores",
                defaultValue: 1,
                allowNegative: false);
            fifoQueuesPerCores = Math.Clamp(fifoQueuesPerCores, 1, processorCount);
            int fifoQueueCount = (processorCount + fifoQueuesPerCores - 1) / fifoQueuesPerCores;
            _FifoQueues = new FifoWorkQueue[BitOperations.RoundUpToPowerOf2((uint)fifoQueueCount)];

            RefreshLoggingEnabled();
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

        /// <summary>
        /// Returns a work stealing queue softly associated with the current thread.
        /// </summary>
        internal WorkStealingQueue? GetPreferredWorkStealingQueue()
        {
            var queues = _WorkStealingQueues;
            return _WorkStealingQueues[GetPreferredIndexForQueues(queues)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal WorkStealingQueue GetOrAddWorkStealingQueue()
        {
            var queues = _WorkStealingQueues;
            var index = GetPreferredIndexForQueues(queues);
            var result = queues[index] ?? EnsureWorkStealingQueue(index);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private WorkStealingQueue EnsureWorkStealingQueue(int index)
        {
            var newQueue = new WorkStealingQueue(index);
            Interlocked.CompareExchange(ref _WorkStealingQueues[index], newQueue, null!);
            return _WorkStealingQueues[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal FifoWorkQueue GetOrAddFifoQueue()
        {
            var queues = _FifoQueues;
            var index = GetPreferredIndexForQueues(queues);
            var result = queues[index] ?? EnsureFifoQueue(index);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private FifoWorkQueue EnsureFifoQueue(int index)
        {
            var newQueue = new FifoWorkQueue(index);
            Interlocked.CompareExchange(ref _FifoQueues[index], newQueue, null!);
            return _FifoQueues[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int GetPreferredIndexForQueues(System.Array queues)
        {
            int id = Threading.Thread.GetCurrentProcessorNumber();
            // on windows GetCurrentProcessorNumber always works
#if !TARGET_WINDOWS
            if (id < 0)
                id = Environment.CurrentManagedThreadId;
#endif
            return id & (queues.Length - 1);
        }

        public void Enqueue(object callback, bool forceGlobal)
        {
            Debug.Assert((callback is IThreadPoolWorkItem) ^ (callback is Task));

            if (_loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(callback);

            uint localQueueIdx;
            if (forceGlobal || s_forceGlobalEnqueue || (localQueueIdx = t_localQueueIdx) == 0)
            {
                GetOrAddFifoQueue().Enqueue(callback);
            }
            else
            {
                WorkStealingQueue localQueue = _WorkStealingQueues[localQueueIdx - 1]!;
                localQueue.Enqueue(callback);
            }

            ThreadPool.EnsureWorkerRequested();
        }

        public void EnqueueAtHighPriority(object workItem)
        {
            // "high priority" here means - let's skip some waiting in the line, if possible.
            // We will try enqueueing into a queue with the shortest item count.
            FifoWorkQueue shortestQueue = GetOrAddFifoQueue();
            uint shortestLength = uint.MaxValue;
            uint startIndex = shortestQueue._queueIndex;

            FifoWorkQueue[] ffQueues = _FifoQueues;
            for (int i = 0; i < ffQueues.Length; i++)
            {
                FifoWorkQueue? ffQueue = ffQueues[startIndex ^ i];
                if (ffQueue != null)
                {
                    // Note: Count in nonquiescent state can be stale or even return negative values.
                    // For our purposes here getting something approximate is good enough.
                    // Typically not all queues are active at the same time and some may be empty.
                    // Since we are running on its core, the preferred queue is unlikely to be active.
                    // Thus we check the preferred queue first.
                    uint count = (uint)ffQueue._enqSegment.Count;
                    if (count == 0)
                    {
                        shortestQueue = ffQueue;
                        break;
                    }
                    else if (count < shortestLength)
                    {
                        shortestLength = count;
                        shortestQueue = ffQueue;
                    }
                }
            }

            shortestQueue.Enqueue(workItem);
            ThreadPool.EnsureWorkerRequested();
        }

        public bool TryRemove(Task callback)
        {
            uint localQueueIdx;
            if ((localQueueIdx = t_localQueueIdx) != 0)
            {
                WorkStealingQueue localQueue = _WorkStealingQueues[localQueueIdx - 1]!;
                if (localQueue.TryRemove(callback))
                {
                    return true;
                }
            }

            // We could also search through other local queues, but it seems to be an overkill.
            // If the workitem was stolen, chances of finding it unexecuted are not high.

            return false;
        }

        public object? Dequeue(ref bool missedSteal)
        {
            // Check for local work items
            WorkStealingQueue localWsQueue = GetOrAddWorkStealingQueue();
            object? workItem = localWsQueue.TryPop();
            if (workItem != null)
            {
                return workItem;
            }

            return DequeueAll(localWsQueue, ref missedSteal);
        }

        public object? DequeueAll(WorkStealingQueue localWsQueue, ref bool missedSteal)
        {
            object? workItem;
            FifoWorkQueue[] ffQueues = _FifoQueues;

            // For fairness we will traverse work-stealing queues starting from a random index.
            uint start = NextThreadRnd();

            // To decorrelate traversal patterns in different workers we will
            // do traversal with an odd stride derived from localWsq index.
            // For an array with a power of 2 length, an odd-stride traversal
            // will see every item and only once, since an odd number is a coprime
            // of the length.
            // Compute the stride by mixing up the start and the queue index and
            // force the result to be odd.
            uint stride = (localWsQueue._queueIndex + start) * 1664525u | 1;

            // By default fifo queues are traversed starting from the one that the current thread would enqueue into.
            // This favors latency over fairness - other fifo queues are only checked when that one is empty.
            // Otherwise the traversal starts from a random fifo queue, which is fairer.
            uint fifoStart = s_fifoScanFromCurrentQueue ? (uint)GetPreferredIndexForQueues(ffQueues) : start;

            uint n = (uint)ffQueues.Length;
            uint mask = n - 1;
            for (uint i = 0; i < n; i++)
            {
                uint idx = (fifoStart + i * stride) & mask;
                FifoWorkQueue? ffQueue = ffQueues[idx];
                workItem = ffQueue?.TryDequeue(ref missedSteal);
                if (workItem != null)
                {
                    return workItem;
                }
            }

            // Try stealing from all local queues.
            WorkStealingQueue[] wsQueues = _WorkStealingQueues;
            n = (uint)wsQueues.Length;
            mask = n - 1;
            for (uint i = 0; i < n; i++)
            {
                uint idx = (start + i * stride) & mask;
                WorkStealingQueue? wsQueue = wsQueues[idx];
                workItem = wsQueue?.TrySteal(localWsQueue, ref missedSteal);
                if (workItem != null)
                {
                    return workItem;
                }
            }

            return null;
        }

        public void TransferLocalWorkItemsBeforeBlocking()
        {
            // Local queues are shared with other threads, so there is nothing to transfer.
        }

        // Get all workitems.  Called by TaskScheduler in its debugger hooks.
        public IEnumerable<object> GetQueuedWorkItems()
        {
            // Enumerate each fifo queue
            foreach (FifoWorkQueue workQueue in _FifoQueues)
            {
                if (workQueue != null)
                {
                    foreach (object item in workQueue.GetQueuedWorkItems())
                    {
                        yield return item;
                    }
                }
            }

            // Enumerate each local queue
            foreach (WorkStealingQueue workStealingQueue in _WorkStealingQueues)
            {
                if (workStealingQueue != null)
                {
                    foreach (object item in workStealingQueue.GetQueuedWorkItems())
                    {
                        yield return item;
                    }
                }
            }
        }

        public long LocalCount
        {
            get
            {
                long count = 0;
                foreach (WorkStealingQueue workStealingQueue in _WorkStealingQueues)
                {
                    if (workStealingQueue != null)
                    {
                        count += workStealingQueue.Count;
                    }
                }
                return count;
            }
        }

        public long GlobalCount
        {
            get
            {
                long count = 0;
                foreach (FifoWorkQueue fifoQueue in _FifoQueues)
                {
                    if (fifoQueue != null)
                    {
                        count += fifoQueue.Count;
                    }
                }
                return count;
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
            PerCoreThreadPoolWorkQueue workQueue = this;
            bool missedSteal = false;
            object? workItem = workQueue.Dequeue(ref missedSteal);
            if (workItem == null)
            {
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

            // Has the desire for logging changed since the last time we entered?
            workQueue.RefreshLoggingEnabled();

            Thread currentThread = Thread.CurrentThread;
            ThreadInt64PersistentCounter.ThreadLocalNode threadLocalCompletionCountNode =
                ThreadPool.GetOrCreateThreadLocalCompletionCountNode();

            // Start on clean ExecutionContext and SynchronizationContext
            currentThread._executionContext = null;
            currentThread._synchronizationContext = null;

            //
            // Save the start time
            //
            int startTickCount = Environment.TickCount;

            // The workitems that are currently in the queues could have asked only for one worker.
            // We are going to process a workitem, which may take unknown time or even block.
            // In a worst case the current workitem will indirectly depend on progress of other
            // items and that would lead to a deadlock if no one else checks the queue.
            // We must ensure at least one more worker is coming if the queue is not empty.
            ThreadPool.EnsureWorkerRequested();

            //
            // After this point, this method is no longer responsible for ensuring thread requests
            //

            //
            // Loop until our quantum expires or there is no work.
            //
            while (true)
            {
                if (workItem == null)
                {
                    // Set missedSteal so that we do not compute/track it. It is not actionable here.
                    missedSteal = true;
                    workItem = workQueue.Dequeue(ref missedSteal);
                    if (workItem == null)
                    {
                        WaitTimeTracking.Flush(workQueue);
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
                    WaitTimeTracking.Flush(workQueue);
                    return DispatchResult.ShouldStop;
                }

                // Check if the dispatch quantum has expired
                if ((uint)(currentTickCount - startTickCount) < DispatchQuantumMs)
                {
                    continue;
                }

                // The quantum expired, do any necessary periodic activities

                WaitTimeTracking.Flush(workQueue);

                if (ThreadPool.YieldFromDispatchLoop(currentTickCount))
                {
                    return DispatchResult.Regular;
                }

                // This method will continue to dispatch work items. Refresh the start tick count for the next dispatch
                // quantum and do some periodic activities.
                startTickCount = currentTickCount;

                // Periodically refresh whether logging is enabled
                workQueue.RefreshLoggingEnabled();
            }
        }

        /// <summary>
        /// Opt-in tracking of how long work items wait in the queues, from enqueue to dequeue.
        /// </summary>
        /// <remarks>
        /// Enabled with DOTNET_ThreadPool_TrackWorkItemWaitTimes=1, or the System.Threading.ThreadPool.TrackWorkItemWaitTimes
        /// runtime configuration switch. It takes two timestamps per work item, so it is off by default. It is only available
        /// on 64-bit, where the enqueue time fits in the alignment padding of a queue slot.
        ///
        /// - Enqueue stores a timestamp in the item's slot. Items moved between local queues keep it.
        /// - A thread that dequeues an item adds the item's wait to thread-local statistics: count, total, min, max and a
        ///   power-of-two histogram. Local and global (fifo) queues are kept apart, since their latency and fairness
        ///   expectations differ.
        /// - At the end of each dispatch quantum and when leaving the dispatch loop, the thread adds its statistics to the
        ///   accumulator of its local queue, so the data does not stay with parked threads.
        /// - About once a second, the gate thread of the portable thread pool drains the accumulators and publishes, for each
        ///   kind of queue over the elapsed window, the count, the min, average and max wait, and the 50th, 90th, 99th and
        ///   99.9th percentiles estimated from the histogram: in <see cref="s_lastSample"/>, and in the FrameworkEventSource
        ///   ThreadPoolWorkItemWaitTimes event (ThreadPool keyword, Informational level).
        ///   The gate thread keeps running until the data of the last active window is published.
        /// </remarks>
        internal static class WaitTimeTracking
        {
            internal static readonly bool IsEnabled =
#if TARGET_64BIT
                AppContextConfigHelper.GetBooleanConfig(
                    "System.Threading.ThreadPool.TrackWorkItemWaitTimes",
                    "DOTNET_ThreadPool_TrackWorkItemWaitTimes",
                    defaultValue: false);
#else
                false;
#endif

            // Timestamps are Stopwatch ticks, scaled down by a power of 2 to units of at most a microsecond, and truncated to
            // 32 bits. The difference of two timestamps covers waits of up to 2^31 units: about 18 minutes with 1 GHz ticks.
            private static readonly int s_timestampShift = GetTimestampShift();
            private static readonly double s_microsecondsPerUnit = (double)(1L << s_timestampShift) * 1_000_000 / Stopwatch.Frequency;

            // The current thread's statistics that have not been added to an accumulator yet.
            [ThreadStatic]
            private static WaitStats t_localQueueWaits;
            [ThreadStatic]
            private static WaitStats t_globalQueueWaits;

            // Used only by the gate thread.
            private static long s_windowStart;
            private static bool s_keepGateThreadRunning;

            /// <summary>The last window published by the gate thread, for inspection in a debugger or a dump.</summary>
            internal static Sample s_lastSample;

            private static int GetTimestampShift()
            {
                int shift = 0;
                while ((Stopwatch.Frequency >> (shift + 1)) >= 1_000_000)
                {
                    shift++;
                }

                return shift;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static uint GetTimestamp() => (uint)(Stopwatch.GetTimestamp() >> s_timestampShift);

            /// <summary>Returns the timestamp to store with an item that is being enqueued, or 0 when tracking is disabled.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static uint GetEnqueueTimestamp() => IsEnabled ? GetTimestamp() : 0;

            /// <summary>Records the wait of an item dequeued from a local queue.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void RecordLocal(uint enqueueTimestamp)
            {
                if (IsEnabled)
                {
                    RecordLocalCore(enqueueTimestamp);
                }
            }

            /// <summary>Records the wait of an item dequeued from a global (fifo) queue.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void RecordGlobal(uint enqueueTimestamp)
            {
                if (IsEnabled)
                {
                    RecordGlobalCore(enqueueTimestamp);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void RecordLocalCore(uint enqueueTimestamp) => t_localQueueWaits.Add(GetWait(enqueueTimestamp));

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void RecordGlobalCore(uint enqueueTimestamp) => t_globalQueueWaits.Add(GetWait(enqueueTimestamp));

            private static uint GetWait(uint enqueueTimestamp)
            {
                uint wait = GetTimestamp() - enqueueTimestamp;

                // The clock is monotonic, so a wait that looks negative is one too long to represent.
                return (int)wait >= 0 ? wait : int.MaxValue;
            }

            /// <summary>
            /// Adds the current thread's statistics to the accumulator of the local queue it last dequeued from.
            /// Called at the end of each dispatch quantum and when the thread leaves the dispatch loop.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Flush(PerCoreThreadPoolWorkQueue workQueue)
            {
                if (IsEnabled)
                {
                    FlushCore(workQueue);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void FlushCore(PerCoreThreadPoolWorkQueue workQueue)
            {
                ref WaitStats local = ref t_localQueueWaits;
                ref WaitStats global = ref t_globalQueueWaits;
                uint localQueueIdx = t_localQueueIdx;
                if ((local.Count | global.Count) == 0 || localQueueIdx == 0)
                {
                    return;
                }

                // If the accumulator is busy, keep the data for the next flush rather than wait.
                Accumulator? accumulator = workQueue._WorkStealingQueues[localQueueIdx - 1]?._waitTimes;
                if (accumulator is not null && accumulator.TryAdd(in local, in global))
                {
                    local = default;
                    global = default;
                }
            }

            /// <summary>
            /// Called by the gate thread when it starts running after a period without activity,
            /// so that the idle time is not counted in the next window.
            /// </summary>
            internal static void OnGateThreadResumed()
            {
                s_windowStart = Stopwatch.GetTimestamp();
                s_keepGateThreadRunning = true;
            }

            /// <summary>
            /// Called by the gate thread each time it performs its periodic activities. About once a second, drains the
            /// accumulators and publishes the statistics of the elapsed window.
            /// Returns true while there may be data to publish, so that the gate thread keeps running until it is published.
            /// </summary>
            internal static bool PerformGateActivities()
            {
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - s_windowStart;

                // The gate activities run every 500 ms. Close a window every other time.
                if (elapsed < Stopwatch.Frequency * 3 / 4)
                {
                    return s_keepGateThreadRunning;
                }

                WaitStats local = default;
                WaitStats global = default;
                if (ThreadPool.s_workQueue is PerCoreThreadPoolWorkQueue workQueue)
                {
                    foreach (WorkStealingQueue? queue in workQueue._WorkStealingQueues)
                    {
                        queue?._waitTimes?.Drain(ref local, ref global);
                    }
                }

                s_windowStart = now;

                // After a window with data, there may be more to come. Keep running until a window is empty.
                s_keepGateThreadRunning = (local.Count | global.Count) != 0;
                if (s_keepGateThreadRunning)
                {
                    Publish(elapsed * 1000.0 / Stopwatch.Frequency, in local, in global);
                }

                return s_keepGateThreadRunning;
            }

            private static void Publish(double durationMs, in WaitStats local, in WaitStats global)
            {
                double us = s_microsecondsPerUnit;
                Sample sample = new Sample
                {
                    DurationMs = durationMs,
                    LocalCount = (long)local.Count,
                    LocalMinUs = local.Min * us,
                    LocalAvgUs = local.Count != 0 ? local.Total * us / local.Count : 0,
                    LocalMaxUs = local.Max * us,
                    LocalP50Us = local.Percentile(0.5) * us,
                    LocalP90Us = local.Percentile(0.9) * us,
                    LocalP99Us = local.Percentile(0.99) * us,
                    LocalP999Us = local.Percentile(0.999) * us,
                    GlobalCount = (long)global.Count,
                    GlobalMinUs = global.Min * us,
                    GlobalAvgUs = global.Count != 0 ? global.Total * us / global.Count : 0,
                    GlobalMaxUs = global.Max * us,
                    GlobalP50Us = global.Percentile(0.5) * us,
                    GlobalP90Us = global.Percentile(0.9) * us,
                    GlobalP99Us = global.Percentile(0.99) * us,
                    GlobalP999Us = global.Percentile(0.999) * us,
                    LocalHistogram = local.Buckets,
                    GlobalHistogram = global.Buckets,
                };

                s_lastSample = sample;

                FrameworkEventSource log = FrameworkEventSource.Log;
                if (log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.ThreadPool))
                {
                    log.ThreadPoolWorkItemWaitTimes(
                        sample.DurationMs,
                        sample.LocalCount, sample.LocalMinUs, sample.LocalAvgUs, sample.LocalMaxUs,
                        sample.GlobalCount, sample.GlobalMinUs, sample.GlobalAvgUs, sample.GlobalMaxUs,
                        sample.LocalP50Us, sample.LocalP90Us, sample.LocalP99Us, sample.LocalP999Us,
                        sample.GlobalP50Us, sample.GlobalP90Us, sample.GlobalP99Us, sample.GlobalP999Us);
                }
            }

            /// <summary>The number of work items and their wait statistics over one window.</summary>
            internal struct Sample
            {
                public double DurationMs;
                public long LocalCount;
                public double LocalMinUs;
                public double LocalAvgUs;
                public double LocalMaxUs;
                public double LocalP50Us;
                public double LocalP90Us;
                public double LocalP99Us;
                public double LocalP999Us;
                public long GlobalCount;
                public double GlobalMinUs;
                public double GlobalAvgUs;
                public double GlobalMaxUs;
                public double GlobalP50Us;
                public double GlobalP90Us;
                public double GlobalP99Us;
                public double GlobalP999Us;

                // In timestamp units (see s_microsecondsPerUnit). Bucket 0 counts waits of 0, bucket i counts waits in [2^(i-1), 2^i).
                public WaitHistogram LocalHistogram;
                public WaitHistogram GlobalHistogram;
            }

            /// <summary>Count, total, min, max and a power-of-two histogram of waits, in timestamp units.</summary>
            internal struct WaitStats
            {
                public ulong Total;
                public ulong Count;
                public uint Min;
                public uint Max;

                // Bucket 0 counts waits of 0, bucket i counts waits in [2^(i-1), 2^i).
                public WaitHistogram Buckets;

                public void Add(uint wait)
                {
                    if (Count == 0 || wait < Min)
                    {
                        Min = wait;
                    }

                    if (wait > Max)
                    {
                        Max = wait;
                    }

                    Total += wait;
                    Count++;

                    // Waits are below 2^31 units, so the bucket index is at most 31.
                    Buckets[32 - BitOperations.LeadingZeroCount(wait)]++;
                }

                public void Add(in WaitStats other)
                {
                    if (other.Count == 0)
                    {
                        return;
                    }

                    if (Count == 0 || other.Min < Min)
                    {
                        Min = other.Min;
                    }

                    if (other.Max > Max)
                    {
                        Max = other.Max;
                    }

                    Total += other.Total;
                    Count += other.Count;

                    for (int i = 0; i < HistogramLength; i++)
                    {
                        Buckets[i] += other.Buckets[i];
                    }
                }

                /// <summary>
                /// Estimates a percentile of the waits, in timestamp units, by interpolating within the histogram bucket
                /// that holds it.
                /// </summary>
                public readonly double Percentile(double fraction)
                {
                    if (Count == 0)
                    {
                        return 0;
                    }

                    ulong rank = (ulong)Math.Ceiling(Count * fraction);
                    ulong seen = 0;
                    for (int i = 0; i < HistogramLength; i++)
                    {
                        uint n = Buckets[i];
                        if (n != 0 && seen + n >= rank)
                        {
                            double low = i == 0 ? 0 : 1L << (i - 1);
                            double high = i == 0 ? 0 : 1L << i;
                            return Math.Clamp(low + (high - low) * (rank - seen) / n, Min, Max);
                        }

                        seen += n;
                    }

                    return Max;
                }
            }

            private const int HistogramLength = 32;

            [InlineArray(HistogramLength)]
            internal struct WaitHistogram
            {
                private uint _element0;
            }

            /// <summary>Wait time statistics added by the threads that dispatch on a core, and drained by the gate thread.</summary>
            internal sealed class Accumulator
            {
                private PaddedWaitStats _stats;

                /// <summary>Adds the statistics, unless another thread is using the accumulator.</summary>
                internal bool TryAdd(in WaitStats local, in WaitStats global)
                {
                    // Contention is rare: other threads add here only after dispatching on the same core,
                    // and the gate thread drains the accumulator once a second.
                    if (Interlocked.CompareExchange(ref _stats.Lock, 1, 0) != 0)
                    {
                        return false;
                    }

                    _stats.Local.Add(in local);
                    _stats.Global.Add(in global);
                    Volatile.Write(ref _stats.Lock, 0);
                    return true;
                }

                /// <summary>Moves the statistics into <paramref name="local"/> and <paramref name="global"/>.</summary>
                internal void Drain(ref WaitStats local, ref WaitStats global)
                {
                    SpinWait spinner = default;
                    while (Interlocked.CompareExchange(ref _stats.Lock, 1, 0) != 0)
                    {
                        spinner.SpinOnce();
                    }

                    local.Add(in _stats.Local);
                    global.Add(in _stats.Global);
                    _stats.Local = default;
                    _stats.Global = default;
                    Volatile.Write(ref _stats.Lock, 0);
                }
            }

            // Padded so that adding to an accumulator does not cause false sharing with other objects,
            // such as the fields of a local queue that stealing threads read.
            [StructLayout(LayoutKind.Sequential)]
            private struct PaddedWaitStats
            {
                private CacheLinePadding _paddingBefore;
                public int Lock;
                public WaitStats Local;
                public WaitStats Global;
                private CacheLinePadding _paddingAfter;
            }

            [StructLayout(LayoutKind.Explicit, Size = PaddingHelpers.CACHE_LINE_SIZE)]
            private struct CacheLinePadding
            {
            }
        }
    }
}

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

#if FEATURE_SINGLE_THREADED
using WorkQueue = System.Collections.Generic.Queue<object>;
#else
using WorkQueue = System.Collections.Concurrent.ConcurrentQueue<object>;
#endif
#if TARGET_WINDOWS
using IOCompletionPollerEvent = System.Threading.PortableThreadPool.IOCompletionPoller.Event;
#endif // TARGET_WINDOWS


namespace System.Threading
{
    /// <summary>
    /// Class for creating and managing a threadpool.
    /// </summary>
    internal sealed partial class ThreadPoolWorkQueue
    {
        internal class WorkQueueBase
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
            internal object _addSegmentLock = new object();

            internal struct PaddedQueueEnds
            {
                internal PaddingFor32 _pad0;
                public int Dequeue;
                internal PaddingFor32 _pad1;
                public int Enqueue;
                internal PaddingFor32 _pad2;
            }

            internal class QueueSegmentBase
            {
                // Segment design is inspired by the algorithm outlined at:
                // http://www.1024cores.net/home/lock-free-algorithms/queues/bounded-mpmc-queue

                /// <summary>The array of items in this queue.  Each slot contains the item in that slot and its "sequence number".</summary>
                internal readonly Slot[] _slots;

                /// <summary>Mask for quickly accessing a position within the queue's array.</summary>
                internal readonly int _slotsMask;

                /// <summary>The queue end positions, with padding to help avoid false sharing contention.</summary>
                internal PaddedQueueEnds _queueEnds; // mutable struct: do not make this readonly

                /// <summary>Indicates whether the segment has been marked such that no additional items may be enqueued.</summary>
                internal bool _frozenForEnqueues;

                internal const int Empty = 0;
                internal const int Full = 1;

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
                    // may have values, but the 0th slot may still be being filled... in that case, TryDequeue will
                    // return false.)
                    for (int i = 0; i < slots.Length; i++)
                    {
                        slots[i].SequenceNumber = i;
                    }

                    _slots = slots;
                }

                /// <summary>Represents a slot in the queue.</summary>
                [DebuggerDisplay("Item = {Item}, SequenceNumber = {SequenceNumber}")]
                [StructLayout(LayoutKind.Auto)]
                internal struct Slot
                {
                    /// <summary>The item.</summary>
                    internal object? Item;
                    /// <summary>The sequence number for this slot, used to synchronize between enqueuers and dequeuers.</summary>
                    internal int SequenceNumber;
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
            }
        }

        /// <summary>
        /// The "local" flavor of the queue is similar to the "global", but also supports Pop, Remove and Move operations.
        ///
        /// - Pop is used to implement Busy-Leaves scheduling strategy.
        /// - Remove is used when the caller finds it benefitial to execute a workitem "inline" after it has been scheduled.
        ///   (such as waiting on a task completion).
        /// - Move is used to rebalance queues if one is found to be "rich" by stealing 1/2 of its queue.
        ///   (this way we avoid having to continue stealing from the same single queue, Move may lead to 2 rich queues, then 4, then ...)
        ///
        /// We create multiple local queues and softly affinitize them with CPU cores.
        /// </summary>
        [DebuggerDisplay("Count = {Count}")]
        internal sealed class WorkStealingQueue : WorkQueueBase
        {
            /// <summary>
            /// When a segment has more than this, we steal half of its slots.
            /// </summary>
            internal const int MoveThreshold = 32;

            /// <summary>The current enqueue segment.</summary>
            internal WorkStealingQueueSegment _enqSegment;
            /// <summary>The current dequeue segment.</summary>
            internal WorkStealingQueueSegment _deqSegment;

            /// <summary>
            /// Initializes a new instance of the <see cref="WorkStealingQueue"/> class.
            /// </summary>
            internal WorkStealingQueue()
            {
                _addSegmentLock = new object();
                _enqSegment = _deqSegment = new WorkStealingQueueSegment(InitialSegmentLength);
            }

            // for debugging
            internal int Count
            {
                get
                {
                    int count = 0;
                    for (WorkStealingQueueSegment? s = _deqSegment; s != null; s = s._nextSegment)
                    {
                        count += s.Count;
                    }
                    return count;
                }
            }

            // for debugging
            internal IEnumerable<object> GetQueuedWorkItems()
            {
                for (WorkStealingQueueSegment? s = _deqSegment; s != null; s = s._nextSegment)
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
                    // If we're unable to enque, this segment is full.
                    // we need to take a slow path that will try adding a new segment.
                    EnqueueSlow(item);
                }
            }

            /// <summary>
            /// Slow path for Enqueue, adding a new segment if necessary.
            /// </summary>
            private void EnqueueSlow(object item)
            {
                WorkStealingQueueSegment currentSegment = _enqSegment;
                for (; ; )
                {
                    if (currentSegment.TryEnqueue(item))
                    {
                        return;
                    }
                    currentSegment = EnsureNextSegment(currentSegment);
                }
            }

            private WorkStealingQueueSegment EnsureNextSegment(WorkStealingQueueSegment currentSegment)
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
                        var newEnq = new WorkStealingQueueSegment(nextSize);

                        // Hook up the new enqueue segment.
                        currentSegment._nextSegment = newEnq;
                        _enqSegment = newEnq;
                    }
                }

                return currentSegment._nextSegment;
            }

            /// <summary>
            /// Removes an object at the bottom of the queue
            /// Returns null if the queue is empty or if there is a contention
            /// (no point to dwell on one local queue and make problem worse when there are other queues).
            /// </summary>
            internal object? TrySteal(ref bool missedSteal)
            {
                var currentSegment = _deqSegment;
                object? result = currentSegment.TrySteal(ref missedSteal);

                // if there is a new segment, we must help with retiring the current.
                if (result == null && currentSegment._nextSegment != null)
                {
                    return TryStealSlow(currentSegment, ref missedSteal);
                }

                return result;
            }

            /// <summary>
            /// Tries to dequeue an item, removing frozen segments as needed.
            /// </summary>
            private object? TryStealSlow(WorkStealingQueueSegment currentSegment, ref bool missedSteal)
            {
                object? result;
                for (; ; )
                {
                    // At this point we know that this segment has been frozen for additional enqueues. But between
                    // the time that we ran TryDequeue and checked for a next segment,
                    // another item could have been added.  Try to dequeue one more time
                    // to confirm that the segment is indeed empty.
                    Debug.Assert(currentSegment._nextSegment != null);

                    // we must know for sure the segment is quiescent and empty before removing it
                    bool localMissedSteal = false;
                    result = currentSegment.TrySteal(ref localMissedSteal);
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
                    result = currentSegment.TrySteal(ref missedSteal);
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
            /// Pops an item from the top of the queue.
            /// Returns null if there is nothing to pop or there is a contention.
            /// </summary>
            internal object? TryPop()
            {
                return _enqSegment.TryPop();
            }

            internal bool CanPop => _enqSegment.CanPop;

            /// <summary>
            /// Performs a search for the given item in the queue and removes the item if found.
            /// Returns true if item was indeed removed.
            /// Returns false if item was not found or is no longer inlineable.
            /// </summary>
            internal bool TryRemove(Task callback)
            {
                var enqSegment = _enqSegment;
                if (enqSegment.TryRemove(callback))
                {
                    return true;
                }

                for (WorkStealingQueueSegment? segment = _deqSegment;
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
            /// enqueues fail and return false.  When the queue is empty, dequeues fail and return null.
            /// These segments are linked together to form the unbounded queue.
            ///
            /// The main difference of "local" segment from "global" is support for Pop as needed by the Busy Leaves algorithm.
            /// For more details on Busy Leaves see:
            /// [Scheduling Multithreaded Computations by Work Stealing] (http://supertech.csail.mit.edu/papers/steal.pdf)
            ///
            /// Supporting Pop leads to some additional complexity compared to "global" segment.
            /// In particular enqueue index may move both forward and backward and therefore we cannot rely on atomic
            /// update of the enqueue index as a way to acquire access to an Enqueue/Pop slot.
            /// Another issue that complicates things is that all operation (Dequeue, Enqueue, Pop, etc.) may be concurrently
            /// performed and should coordinate access to the slots when necessary.
            ///
            /// We resolve these issues by observing the following rules:
            ///
            /// - In an empty segment dequeue and enqueue ends point to the same slot. This is also the initial state.
            ///
            /// - The enqueue end is never behind the dequeue end.
            ///
            /// - Slots in "Full" state form a contiguous range.
            ///
            /// - Dequeue operation must acquire access to the dequeue slot by atomically moving it to the "Change" state.
            ///   Setting the slot to the next state can be done via regular write. After the Dequeue is complete and dequeue end is moved forward.
            ///
            /// - Pop and Enqueue operation must acquire the access to enqueue slot by atomically moving the previous slot to the "Change" state.
            ///   Setting the next state can be done via regular write. After the Enqueue/Pop is complete and enqueue end is moved appropriately.
            ///
            ///   To summarise:
            ///     In a rare case of concurrent Pop and Enqueue, the threads coordinate the access by locking the same slot.
            ///     Similarly, concurrent Dequeues will coordinate the access on the other end of the segment.
            ///     When the segment shrinks to just 1 element, all Pop/Enqueue/Dequeue operations would end up using the same coordinating slot.
            ///     This indirectly guarantees that concurrent Dequeue and Pop cannot use the same slot and move enqueue/dequeue ends across each other.
            ///
            /// - Move/Remove operations get exclusive access to appropriate ranges of slots by setting "Change" on both sides of the range.
            ///
            /// - The enqueue and dequeue ends are updated only when corresponding slots are in the "Change" state.
            ///
            /// - It is possible, in a case of a contention, to move a wrong slot to the "Change" state.
            ///   (Ex: the slot is no longer previous to an enqueue slot because enqueue index has moved forward)
            ///   When such situation is possible, it can be detected by re-examining the condition after the slot has moved to "Change" state.
            ///   This kind of contentions are rare and handled by reverting the slot(s) to the original state and performing an appropriate backoff.
            ///   The reverting of failed enqueue slot is done via CAS - in case if the slot has changed the state due to Moving (that new state should win).
            ///   The reverting of failed dequeue slot is an ordinary write. Since dequeue moves monotonically, it cannot end up in a Moving range.
            ///
            /// </summary>
            [DebuggerDisplay("Count = {Count}")]
            internal sealed class WorkStealingQueueSegment : QueueSegmentBase
            {
                /// <summary>The segment following this one in the queue, or null if this segment is the last in the queue.</summary>
                internal WorkStealingQueueSegment? _nextSegment;

                /// <summary>
                /// Another state of the slot in addition to Empty and Full.
                /// "Change" means that the slot is reserved for possible modifications.
                /// The state is used for mutual communication between Enqueue/Dequeue/Pop/Remove.
                /// NB: Enqueue reserves the slot "to the left" of the slot that is targeted by Enqueue.
                ///     This ensures that "Full" slots occupy a contiguous range (not a requirement and is not true for the "global" flavor of the queue)
                /// </summary>
                private const int Change = 2;

                /// <summary>Creates the segment.</summary>
                /// <param name="length">
                /// The maximum number of elements the segment can contain.  Must be a power of 2.
                /// </param>
                internal WorkStealingQueueSegment(int length) : base(length) { }

                /// <summary>
                /// Attempts to enqueue the item.  If successful, the item will be stored
                /// in the queue and true will be returned; otherwise, the item won't be stored, and false
                /// will be returned.
                /// </summary>
                internal bool TryEnqueue(object item)
                {
                    // Loop in case of contention a few times.
                    // Contention is rare here, since this is "our" queue.
                    // When happens, is is mostly with stealing of the last item
                    // and cannot last long, unless stealing thread is preempted.
                    for (uint i = 0; i < 10; i++)
                    {
                        int position = _queueEnds.Enqueue;
                        ref Slot prevSlot = ref this[position - 1];
                        int prevSequenceNumber = prevSlot.SequenceNumber;
                        ref Slot slot = ref this[position];

                        // check if prev slot is full in the current generation or empty in the next
                        // otherwise retry - we have some kind of race, most likely the prev item is being stolen
                        if (prevSequenceNumber == position || prevSequenceNumber == position + _slotsMask)
                        {
                            // lock the previous slot (so noone could dequeue past us, pop the prev slot or enqueue into the same position)
                            // NB: once we lock, the segment can not be considered empty by the TrySteal
                            if (Interlocked.CompareExchange(ref prevSlot.SequenceNumber, prevSequenceNumber + Change, prevSequenceNumber) == prevSequenceNumber)
                            {
                                // confirm that enqueue did not change while we were locking the slot
                                // it is extremely rare, but we may see another Pop or Enqueue on the same segment.
                                if (_queueEnds.Enqueue == position)
                                {
                                    // Successfully locked prev slot.
                                    // is the slot empty?   (most common path)
                                    int sequenceNumber = slot.SequenceNumber;
                                    if (sequenceNumber == position)
                                    {
                                        // update Enqueue - must do before marking the slot full.
                                        // otherwise someone could lock the full slot while having stale Enqueue.
                                        _queueEnds.Enqueue = position + 1;
                                        slot.Item = item;

                                        // make the slot appear full in the current generation.
                                        // since the slot on the left is still locked, only poppers/enqueuers can use it, but can use immediately
                                        Volatile.Write(ref slot.SequenceNumber, position + Full);

                                        // unlock prev slot
                                        // must be after we moved enq to the next slot, or someone may pop prev and break continuity of full slots.
                                        prevSlot.SequenceNumber = prevSequenceNumber;
                                        return true;
                                    }

                                    // do we see the prev generation?
                                    if (position - sequenceNumber > 0)
                                    {
                                        // Set Enqueue to throw off anyone else trying to enqueue or pop, unless we have already done that.
                                        // we need a fence between writing to Enqueue and unlocking, but we unlock with a CAS anyways
                                        _queueEnds.Enqueue = position + FreezeOffset;
                                        _frozenForEnqueues = true;
                                    }
                                }

                                // enqueue changed or segment is full (rare cases)
                                // unlock the slot through CAS in case slot was Moved
                                Interlocked.CompareExchange(ref prevSlot.SequenceNumber, prevSequenceNumber, prevSequenceNumber + Change);
                            }
                        }

                        if (_frozenForEnqueues)
                        {
                            return false;
                        }

                        // Lost a race. Most likely to the dequeuer of the last remaining item, which will be gone shortly. Try again.
                        Backoff.Exponential(i);
                    }

                    ThreadPool.s_workQueue.EnqueueAtHighPriority(item);
                    return true;
                }

                internal int Count => _queueEnds.Enqueue - _queueEnds.Dequeue;

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

                internal bool IsEmpty
                {
                    get
                    {
                        int position = _queueEnds.Dequeue;
                        int sequenceNumber = this[position].SequenceNumber;

                        // "position == sequenceNumber" means that we have reached an empty slot.
                        // since full slots are contiguous, finding an empty slot means that
                        // for our purposes and for the moment in time the segment is empty
                        return position == sequenceNumber;
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

                internal object? TryPop()
                {
                    // retry in rare cases like finding a removed item or enqueue is changed.
                    // in a case of collision, we just leave and try doing somewthing else.
                    // we will eventually try stealing from the same queue and will have a chance to check/report a missed steal.
                    for (; ; )
                    {
                        int position = _queueEnds.Enqueue - 1;
                        ref Slot slot = ref this[position];

                        // Read the sequence number for the slot.
                        int sequenceNumber = slot.SequenceNumber;

                        // Check if the slot is considered Full in the current generation (other likely state - Empty).
                        if (sequenceNumber == position + Full)
                        {
                            // lock the slot.
                            if (Interlocked.CompareExchange(ref slot.SequenceNumber, position + Change, sequenceNumber) == sequenceNumber)
                            {
                                // confirm that enqueue did not change while we were locking the slot
                                // it is extremely rare, but we may see another Pop or Enqueue on the same segment.
                                // this is the same as "if (_queueEnds.Enqueue == position + 1)"
                                if (_queueEnds.Enqueue == sequenceNumber)
                                {
                                    var item = slot.Item;
                                    slot.Item = null;

                                    // update Enqueue before marking slot empty. - if enqueue update is later than that it may happen after the slot is enqueued.
                                    _queueEnds.Enqueue = position;

                                    // make the slot appear empty in the current generation and update enqueue
                                    // that unlocks the slot
                                    Volatile.Write(ref slot.SequenceNumber, position);

                                    if (item == null)
                                    {
                                        // item was removed
                                        // this is not a lost race though, so continue.
                                        continue;
                                    }

                                    return item;
                                }
                                else
                                {
                                    // enqueue changed, in this rare case we just retry.
                                    // unlock the slot through CAS in case the slot was Moved
                                    Interlocked.CompareExchange(ref slot.SequenceNumber, sequenceNumber, position + Change);
                                    continue;
                                }
                            }
                        }

                        // found no items or encountered a contention (most likely with a dequeuer)
                        return null;
                    }
                }

                /// <summary>
                /// Tries to dequeue an element from the queue.
                ///
                /// "missedSteal" is set to true when we find the segment in a state where we cannot take an element and
                /// cannot claim the segment is empty.
                /// That generally happens when another thread did or is doing modifications and we do not see all the changes.
                /// We could spin here until we see a consistent state, but it makes more sense to look in other queues.
                /// </summary>
                internal object? TrySteal(ref bool missedSteal)
                {
                    for (; ; )
                    {
                        int position = _queueEnds.Dequeue;

                        // if prev is not empty (in next generation), there might be more work in the segment.
                        // NB: enqueues are initiated by CAS-locking the prev slot.
                        //     it is unlikely, but theoretically possible that we will arrive here and see only that,
                        //     while all other changes are still write-buffered in the other core.
                        //     We cannot claim that the queue is empty, and should treat this as a missed steal.
                        //     Also make sure we read the prev slot before reading the actual slot, reading after is pointless.
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
                            // Reserve the slot for Dequeuing.
                            if (Interlocked.CompareExchange(ref slot.SequenceNumber, position + Change, sequenceNumber) == sequenceNumber)
                            {
                                object? item;
                                var enqPos = _queueEnds.Enqueue;
                                if (enqPos - position < MoveThreshold ||
                                    // "this" is a sentinel for a failed Moving attempt
                                    (item = TryMoveCore(ThreadPool.s_workQueue.GetOrAddWorkStealingQueue()._enqSegment, position, enqPos)) == this)
                                {
                                    _queueEnds.Dequeue = position + 1;
                                    item = slot.Item;
                                    slot.Item = null;
                                }

                                // unlock the slot for enqueuing by making the slot empty in the next generation
                                Volatile.Write(ref slot.SequenceNumber, position + 1 + _slotsMask);

                                if (item == null)
                                {
                                    // the item was removed, so we have nothing to return.
                                    // this is not a lost race though, so must try again.
                                    continue;
                                }

                                return item;
                            }
                        }
                        else if (diff == 0)
                        {
                            // reached an empty slot
                            // since full slots are contiguous, finding an empty slot means that
                            // for our purposes and for the moment in time the segment is empty
                            return null;
                        }

                        // contention with other thread
                        // must check this segment again later
                        missedSteal = true;
                        return null;
                    }
                }

                internal object? TryMoveTo(WorkStealingQueueSegment other)
                {
                    int deqPos = _queueEnds.Dequeue;
                    ref Slot slot = ref this[deqPos];
                    int sequenceNumber = slot.SequenceNumber;

                    // Check if the slot is considered Full in the current generation.
                    int diff = sequenceNumber - deqPos;
                    if (diff == Full)
                    {
                        // Reserve the slot for Dequeuing.
                        if (Interlocked.CompareExchange(ref slot.SequenceNumber, deqPos + Change, sequenceNumber) == sequenceNumber)
                        {
                            var enqPos = _queueEnds.Enqueue;
                            object? item = TryMoveCore(other, deqPos, enqPos);

                            // "this" is a sentinel for a failed Moving attempt
                            if (item == this)
                            {
                                // steal one item anyways then, since we can.
                                _queueEnds.Dequeue = deqPos + 1;
                                item = slot.Item;
                                slot.Item = null;
                            }

                            // unlock the slot for enqueuing by making the slot empty in the next generation
                            Volatile.Write(ref slot.SequenceNumber, deqPos + 1 + _slotsMask);
                            return item;
                        }
                    }

                    return null;
                }

                internal object? TryMoveCore(WorkStealingQueueSegment other, int deqPosition, int enqPosition)
                {
                    // same as in TryEnqueue
                    int otherEnqPosition = other._queueEnds.Enqueue;
                    ref Slot enqPrevSlot = ref other[otherEnqPosition - 1];
                    int prevSequenceNumber = enqPrevSlot.SequenceNumber;

                    var srcSlotsMask = _slotsMask;
                    // mask in case the segment is frozen and enqueue is inflated
                    var count = (enqPosition - deqPosition) & srcSlotsMask;
                    int halfPosition = deqPosition + count / 2;
                    ref Slot halfSlot = ref this[halfPosition];

                    // unlike Enqueue, we require prev slot be empty
                    // not just to prevent rich getting richer
                    // we also do not want a possibility that the same segment is both Moved from and Moved to, which would be messy
                    if (prevSequenceNumber == otherEnqPosition + other._slotsMask)
                    {
                        // lock the other segment for enqueuing
                        if (Interlocked.CompareExchange(ref enqPrevSlot.SequenceNumber, prevSequenceNumber + Change, prevSequenceNumber) == prevSequenceNumber)
                        {
                            // confirm that enqueue did not change while we were locking the slot
                            // it is extremely rare, but we may see another Pop or Enqueue on the same segment.
                            if (other._queueEnds.Enqueue == otherEnqPosition)
                            {
                                // lock halfslot, it must be full
                                if (Interlocked.CompareExchange(ref halfSlot.SequenceNumber, halfPosition + Change, halfPosition + Full) == halfPosition + Full)
                                {
                                    // our enqueue could have changed before we locked half
                                    // make sure that half-way slot is still before enqueue
                                    // in fact give it more space - we do not want to Move all the items, especially if someone else popping them fast.
                                    var enq = deqPosition + ((_queueEnds.Enqueue - deqPosition) & _slotsMask);
                                    if (enq - halfPosition > (MoveThreshold / 4))
                                    {
                                        int i = deqPosition, j = otherEnqPosition;
                                        ref Slot last = ref this[i++];

                                        while (true)
                                        {
                                            ref Slot next = ref this[i];
                                            ref Slot to = ref other[j];

                                            // the other slot must be empty
                                            // next slot must be full
                                            if (to.SequenceNumber != j | next.SequenceNumber != i + Full)
                                            {
                                                break;
                                            }

                                            to.Item = last.Item;
                                            // NB: enables "to" for dequeuing, which may immediately happen,
                                            // but not for popping, yet - since the other enq is locked
                                            Volatile.Write(ref to.SequenceNumber, j + Full);

                                            last.Item = null;

                                            // we are going to take from next, mark it empty already
                                            next.SequenceNumber = i + 1 + srcSlotsMask;
                                            last = ref next;

                                            i++;
                                            j++;
                                        }

                                        // return the last slot value
                                        // (it should already be marked empty, or will be, if it is at deqPosition)
                                        var result = last.Item;
                                        last.Item = null;

                                        // restore the half slot, must be after all the full->empty slot transitioning
                                        // to make sure that poppers cannot see Moved slots as still incorrectly full when moving to the left of half.
                                        Volatile.Write(ref halfSlot.SequenceNumber, halfPosition + Full);

                                        // advance the other enq, must be done before unlocking other prev slot, or someone could pop prev once unlocked.
                                        // enables enq/pop
                                        other._queueEnds.Enqueue = j;

                                        // advance Dequeue, must be after halfSlot is restored - someone could immediately start Moving.
                                        Volatile.Write(ref _queueEnds.Dequeue, i);

                                        // unlock other prev slot
                                        // must be after we moved other enq to the next slot, or someone may pop prev and break continuity of full slots.
                                        enqPrevSlot.SequenceNumber = prevSequenceNumber;
                                        return result;
                                    }

                                    // failed to lock the half-way slot.
                                    // restore via CAS, in case target slot has been Moved to
                                    Interlocked.CompareExchange(ref halfSlot.SequenceNumber, halfPosition + Full, halfPosition + Change);
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

        private const int ProcessorsPerAssignableWorkItemQueue = 16;
        private static readonly int s_assignableWorkItemQueueCount =
            Environment.ProcessorCount <= 32 ? 0 :
                (Environment.ProcessorCount + (ProcessorsPerAssignableWorkItemQueue - 1)) / ProcessorsPerAssignableWorkItemQueue;

        private bool _loggingEnabled;
        private bool _dispatchNormalPriorityWorkFirst;

        // SOS's ThreadPool command depends on the following names
        internal readonly WorkQueue workItems = new WorkQueue();
        internal readonly WorkQueue highPriorityWorkItems = new WorkQueue();

        internal readonly WorkStealingQueue[] _WorkStealingQueues;

        // SOS's ThreadPool command depends on the following name. The global queue doesn't scale well beyond a point of
        // concurrency. Some additional queues may be added and assigned to a limited number of worker threads if necessary to
        // help with limiting the concurrency level.
        internal readonly WorkQueue[] _assignableWorkItemQueues =
            new WorkQueue[s_assignableWorkItemQueueCount];

        private readonly LowLevelLock _queueAssignmentLock = new();
        private readonly int[] _assignedWorkItemQueueThreadCounts =
            s_assignableWorkItemQueueCount > 0 ? new int[s_assignableWorkItemQueueCount] : Array.Empty<int>();

        public ThreadPoolWorkQueue()
        {
            _WorkStealingQueues = new WorkStealingQueue[BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount)];

            for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
            {
                _assignableWorkItemQueues[i] = new WorkQueue();
            }

            RefreshLoggingEnabled();
        }

        private void TryReassignWorkItemQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            Debug.Assert(s_assignableWorkItemQueueCount > 0);

            int queueIndex = tl.queueIndex;
            if (queueIndex == 0)
            {
                return;
            }

            if (!_queueAssignmentLock.TryAcquire())
            {
                return;
            }

            // if not assigned yet, assume temporarily that the last queue is assigned
            if (queueIndex == -1)
            {
                queueIndex = _assignedWorkItemQueueThreadCounts.Length - 1;
                _assignedWorkItemQueueThreadCounts[queueIndex]++;
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

        /// <summary>
        /// Returns a local queue softly affinitized with the current thread.
        /// </summary>
        internal WorkStealingQueue? GetWorkStealingQueue()
        {
            return _WorkStealingQueues[GetWorkStealingQueueIndex()];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal WorkStealingQueue GetOrAddWorkStealingQueue()
        {
            var index = GetWorkStealingQueueIndex();
            var result = _WorkStealingQueues[index] ?? EnsureWorkStealingQueue(index);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private WorkStealingQueue EnsureWorkStealingQueue(int index)
        {
            var newQueue = new WorkStealingQueue();
            Interlocked.CompareExchange(ref _WorkStealingQueues[index], newQueue, null!);
            return _WorkStealingQueues[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int GetWorkStealingQueueIndex()
        {
            int id = Threading.Thread.GetCurrentProcessorNumber();
            // on windows GetCurrentProcessorNumber always works
#if !TARGET_WINDOWS
            if (id < 0)
                id = Environment.CurrentManagedThreadId;
#endif
            return id & (_WorkStealingQueues.Length - 1);
        }

        public void Enqueue(object callback, bool forceGlobal)
        {
            Debug.Assert((callback is IThreadPoolWorkItem) ^ (callback is Task));

            if (_loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(callback);

            // TODO: VS now we can enqueue localy from non-pool thread, but should we?
            //       noone works with the local queue right now, possibly the queue is empty
            //       or will be stolen away soon. So this is similar to high-pri enqueue.
            //       In fact high-pri enq is probably just a local enqueue with global fallback.
            //
            // if (forceGlobal || !Thread.CurrentThread.IsThreadPoolThread)
            if (forceGlobal)
            {
                ThreadPoolWorkQueueThreadLocals? tl;
                WorkQueue queue =
                s_assignableWorkItemQueueCount > 0 && (tl = ThreadPoolWorkQueueThreadLocals.threadLocals) != null
                    ? tl.assignedGlobalWorkItemQueue
                    : workItems;

                queue.Enqueue(callback);
            }
            else
            {
                GetOrAddWorkStealingQueue().Enqueue(callback);
            }

            ThreadPool.EnsureWorkerRequested();
        }

        public void EnqueueAtHighPriority(object workItem)
        {
            Debug.Assert((workItem is IThreadPoolWorkItem) ^ (workItem is Task));

            if (_loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(workItem);

            highPriorityWorkItems.Enqueue(workItem);

            ThreadPool.EnsureWorkerRequested();
        }

        internal bool TryRemove(Task callback)
        {
            int WorkStealingQueueIndex = GetWorkStealingQueueIndex();
            WorkStealingQueue? WorkStealingQueue = _WorkStealingQueues[WorkStealingQueueIndex];
            if (WorkStealingQueue != null && WorkStealingQueue.TryRemove(callback))
            {
                return true;
            }

            // We could also search through other local queues, but it seems to be an overkill.
            // If the workitem was stolen, chances of finding it unexecuted are not high.

            return false;
        }

        public object? Dequeue(ref bool missedSteal)
        {
            // Check for local work items
            WorkStealingQueue? localWsq = GetWorkStealingQueue();
            object? workItem = localWsq?.TryPop();
            if (workItem != null)
            {
                return workItem;
            }

            return DequeueAll(ref missedSteal);
        }

        public object? DequeueAll(ref bool missedSteal)
        {
            ThreadPoolWorkQueueThreadLocals tl = ThreadPoolWorkQueueThreadLocals.threadLocals!;

            // Check for high-priority work items
            object? workItem;
            if (tl.isProcessingHighPriorityWorkItems)
            {
                if (highPriorityWorkItems.TryDequeue(out workItem))
                {
                    return workItem;
                }

                tl.isProcessingHighPriorityWorkItems = false;
            }
#if FEATURE_SINGLE_THREADED
            else if (highPriorityWorkItems.Count == 0 &&
#else
            else if (!highPriorityWorkItems.IsEmpty &&
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
            if (s_assignableWorkItemQueueCount > 0)
            {
                uint randomValue = tl.NextRnd();
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

            // Try to steal from other threads' local work items
            WorkStealingQueue[] queues = _WorkStealingQueues;
            int startIndex = GetWorkStealingQueueIndex();

            // do a sweep of all local queues.
            for (int i = 0; i < queues.Length; i++)
            {
                WorkStealingQueue? localWsq = queues[startIndex ^ i];
                workItem = localWsq?.TrySteal(ref missedSteal);
                if (workItem != null)
                {
                    return workItem;
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
                long count = (long)highPriorityWorkItems.Count + workItems.Count;

                for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
                {
                    count += _assignableWorkItemQueues[i].Count;
                }

                return count;
            }
        }

        // Time in ms for which ThreadPoolWorkQueue.Dispatch keeps executing normal work items before either returning from
        // Dispatch (if YieldFromDispatchLoop is true), or performing periodic activities
        public const uint DispatchQuantumMs = 30;

        private static object? DequeueWithPriorityAlternation(ThreadPoolWorkQueue workQueue, ThreadPoolWorkQueueThreadLocals tl, out bool missedSteal)
        {
            object? workItem = null;

            // Alternate between checking for high-prioriy and normal-priority work first, that way both sets of work
            // items get a chance to run in situations where worker threads are starved and work items that run also
            // take over the thread, sustaining starvation. For example, when worker threads are continually starved,
            // high-priority work items may always be queued and normal-priority work items may not get a chance to run.
            bool dispatchNormalPriorityWorkFirst = workQueue._dispatchNormalPriorityWorkFirst;
            var workStealingQueue = workQueue.GetWorkStealingQueue();
            if (dispatchNormalPriorityWorkFirst && workStealingQueue?.CanPop != true)
            {
                workQueue._dispatchNormalPriorityWorkFirst = !dispatchNormalPriorityWorkFirst;
                WorkQueue queue =
                    s_assignableWorkItemQueueCount > 0 ? tl.assignedGlobalWorkItemQueue : workQueue.workItems;
                if (!queue.TryDequeue(out workItem) && s_assignableWorkItemQueueCount > 0)
                {
                    workQueue.workItems.TryDequeue(out workItem);
                }
            }

            missedSteal = false;
            workItem ??= workQueue.Dequeue(ref missedSteal);

            return workItem;
        }

        /// <summary>
        /// Dispatches work items to this thread.
        /// </summary>
        /// <returns>
        /// <c>true</c> if this thread did as much work as was available or its quantum expired.
        /// <c>false</c> if this thread stopped working early.
        /// </returns>
        internal static bool Dispatch()
        {
            ThreadPoolWorkQueue workQueue = ThreadPool.s_workQueue;
            ThreadPoolWorkQueueThreadLocals tl = workQueue.GetOrCreateThreadLocals();
            object? workItem = DequeueWithPriorityAlternation(workQueue, tl, out bool missedSteal);
            if (workItem == null)
            {
                // Missing a steal means there may be an item that we were unable to get.
                // Effectively, we failed to fulfill our promise to check the queues for work.
                // We need to make sure someone will do another pass.
                if (missedSteal)
                {
                    ThreadPool.EnsureWorkerRequested();
                }

                // Tell the VM we're returning normally, not because Hill Climbing asked us to return.
                return true;
            }

            // The workitems that are currently in the queues could have asked only for one worker.
            // We are going to process a workitem, which may take unknown time or even block.
            // In a worst case the current workitem will indirectly depend on progress of other
            // items and that would lead to a deadlock if no one else checks the queue.
            // We must ensure at least one more worker is coming if the queue is not empty.
            ThreadPool.EnsureWorkerRequested();

            //
            // After this point, this method is no longer responsible for ensuring thread requests
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
                    workItem = workQueue.Dequeue(ref missedSteal);
                    if (workItem == null)
                    {
                        if (s_assignableWorkItemQueueCount > 0)
                        {
                            workQueue.UnassignWorkItemQueue(tl);
                        }

                        return true;
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
                    DispatchItemWithAutoreleasePool(workItem, currentThread);
                }
                else
#endif
#pragma warning disable CS0162 // Unreachable code detected. EnableWorkerTracking may be a constant in some runtimes.
                if (ThreadPool.EnableWorkerTracking)
                {
                    DispatchWorkItemWithWorkerTracking(workItem, currentThread);
                }
                else
                {
                    DispatchWorkItem(workItem, currentThread);
                }
#pragma warning restore CS0162

                // Release refs
                workItem = null;

                // Return to clean ExecutionContext and SynchronizationContext. This may call user code (AsyncLocal value
                // change notifications).
                ExecutionContext.ResetThreadPoolThread(currentThread);

                // Reset thread state after all user code for the work item has completed
                currentThread.ResetThreadPoolThread();

                threadLocalCompletionCountNode!.Increment();
                int currentTickCount = Environment.TickCount;

                // Check if the dispatch quantum has expired
                if ((uint)(currentTickCount - startTickCount) < DispatchQuantumMs)
                {
                    continue;
                }

                // The quantum expired, do any necessary periodic activities

                // Notify the threadpool that we executed through a quantum.
                // This is also our opportunity to ask whether Hill Climbing wants
                // us to return the thread to the pool or not.
                if (ThreadPool.YieldFromDispatchLoop(currentTickCount))
                {
                    tl.isProcessingHighPriorityWorkItems = false;
                    if (s_assignableWorkItemQueueCount > 0)
                    {
                        workQueue.UnassignWorkItemQueue(tl);
                    }

                    // the thread should stop running.
                    return false;
                }

                if (s_assignableWorkItemQueueCount > 0)
                {
                    // Due to hill climbing, over time arbitrary worker threads may stop working and eventually unbalance the
                    // queue assignments. Periodically try to reassign a queue to keep the assigned queues busy.
                    //
                    // This can also be the first time the queue is assigned.
                    // We do not assign eagerly at the beginning of Dispatch as we would need to take _queueAssignmentLock
                    // and that lock may cause massive contentions if many threads start dispatching.
                    workQueue.TryReassignWorkItemQueue(tl);
                }

                // This method will continue to dispatch work items. Refresh the start tick count for the next dispatch
                // quantum and do some periodic activities.
                startTickCount = currentTickCount;

                // Periodically refresh whether logging is enabled
                workQueue.RefreshLoggingEnabled();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DispatchWorkItemWithWorkerTracking(object workItem, Thread currentThread)
        {
            Debug.Assert(ThreadPool.EnableWorkerTracking);
            Debug.Assert(currentThread == Thread.CurrentThread);

            bool reportedStatus = false;
            try
            {
                ThreadPool.ReportThreadStatus(isWorking: true);
                reportedStatus = true;
                DispatchWorkItem(workItem, currentThread);
            }
            finally
            {
                if (reportedStatus)
                    ThreadPool.ReportThreadStatus(isWorking: false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchWorkItem(object workItem, Thread currentThread)
        {
            if (workItem is Task task)
            {
                // Task workitems catch their exceptions for later observation
                // We do not need to pass unhandled ones to ExceptionHandling.s_handler
                task.ExecuteFromThreadPool(currentThread);
            }
            else
            {
                Debug.Assert(workItem is IThreadPoolWorkItem);
                try
                {
                    Unsafe.As<IThreadPoolWorkItem>(workItem).Execute();
                }
                catch (Exception ex) when (ExceptionHandling.IsHandledByGlobalHandler(ex))
                {
                    // the handler returned "true" means the exception is now "handled" and we should continue.
                }
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
        public readonly Thread currentThread;
        public readonly ThreadInt64PersistentCounter.ThreadLocalNode? threadLocalCompletionCountNode;

        // Very cheap random sequence generator. We keep one per-local queue.
        // We do not need a lot of randomness, I think even _rnd++ would be fairly good here.
        // Sequences attached to different queues go out of sync quickly and that could be sufficient.
        // However we can make this sequence is a bit more random at a very modest additional cost.
        // [Fast High Quality Parallel Random Number] (http://www.drdobbs.com/tools/fast-high-quality-parallel-random-number/231000484)
        private uint rndVal = 6247;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint NextRnd()
        {
            var r = rndVal;
            r -= Numerics.BitOperations.RotateRight(r, 11);
            return (rndVal = r);
        }

        public ThreadPoolWorkQueueThreadLocals(ThreadPoolWorkQueue tpq)
        {
            assignedGlobalWorkItemQueue = tpq.workItems;
            queueIndex = -1;
            workQueue = tpq;
            currentThread = Thread.CurrentThread;
            threadLocalCompletionCountNode = ThreadPool.GetOrCreateThreadLocalCompletionCountNode();
        }
    }

#if TARGET_WINDOWS

    internal sealed class ThreadPoolTypedWorkItemQueue : IThreadPoolWorkItem
    {
        // This flag is used for communication between item enqueuing and workers that process the items.
        // There are two states of this flag:
        // 0: has no guarantees
        // 1: means a worker will check work queues and ensure that
        //    any work items inserted in work queue before setting the flag
        //    are picked up.
        //    Note: The state must be cleared by the worker thread _before_
        //       checking. Otherwise there is a window between finding no work
        //       and resetting the flag, when the flag is in a wrong state.
        //       A new work item may be added right before the flag is reset
        //       without asking for a worker, while the last worker is quitting.
        private int _hasOutstandingThreadRequest;
        private readonly ConcurrentQueue<IOCompletionPollerEvent> _workItems = new();

        public int Count => _workItems.Count;

        public void Enqueue(IOCompletionPollerEvent workItem)
        {
            BatchEnqueue(workItem);
            CompleteBatchEnqueue();
        }

        public void BatchEnqueue(IOCompletionPollerEvent workItem) => _workItems.Enqueue(workItem);
        public void CompleteBatchEnqueue() => EnsureWorkerScheduled();

        private void EnsureWorkerScheduled()
        {
            // Only one worker is requested at a time to mitigate Thundering Herd problem.
            if (Interlocked.Exchange(ref _hasOutstandingThreadRequest, 1) == 0)
            {
                // Currently where this type is used, queued work is expected to be processed
                // at high priority. The implementation could be modified to support different
                // priorities if necessary.
                ThreadPool.UnsafeQueueHighPriorityWorkItemInternal(this);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            // We are asking for one worker at a time, thus the state should be 1.
            Debug.Assert(_hasOutstandingThreadRequest == 1);
            _hasOutstandingThreadRequest = 0;

            // Checking for items must happen after resetting the processing state.
            Interlocked.MemoryBarrier();

            if (!_workItems.TryDequeue(out var workItem))
            {
                // Discount a work item here to avoid counting this queue processing work item
                ThreadPoolWorkQueueThreadLocals.threadLocals!.threadLocalCompletionCountNode!.Decrement();
                return;
            }

            // The batch that is currently in the queue could have asked only for one worker.
            // We are going to process a workitem, which may take unknown time or even block.
            // In a worst case the current workitem will indirectly depend on progress of other
            // items and that would lead to a deadlock if no one else checks the queue.
            // We must ensure at least one more worker is coming if the queue is not empty.
            if (!_workItems.IsEmpty)
            {
                EnsureWorkerScheduled();
            }

            ThreadPoolWorkQueueThreadLocals tl = ThreadPoolWorkQueueThreadLocals.threadLocals!;
            Debug.Assert(tl != null);
            Thread currentThread = tl.currentThread;
            Debug.Assert(currentThread == Thread.CurrentThread);
            uint completedCount = 0;
            int startTimeMs = Environment.TickCount;
            var wsq = tl.workQueue.GetWorkStealingQueue();
            while (true)
            {
                workItem.Invoke();

                // This work item processes queued work items until certain conditions are met, and tracks some things:
                // - Keep track of the number of work items processed, it will be added to the counter later
                // - Local work items take precedence over all other types of work items, process them first
                // - This work item should not run for too long. It is processing a specific type of work in batch, but should
                //   not starve other thread pool work items. Check how long it has been since this work item has started, and
                //   yield to the thread pool after some time. The threshold used is half of the thread pool's dispatch quantum,
                //   which the thread pool uses for doing periodic work.
                if (++completedCount == uint.MaxValue ||
                    wsq?.CanPop == true ||
                    (uint)(Environment.TickCount - startTimeMs) >= ThreadPoolWorkQueue.DispatchQuantumMs / 2 ||
                    !_workItems.TryDequeue(out workItem))
                {
                    break;
                }

                // Return to clean ExecutionContext and SynchronizationContext. This may call user code (AsyncLocal value
                // change notifications).
                ExecutionContext.ResetThreadPoolThread(currentThread);

                // Reset thread state after all user code for the work item has completed
                currentThread.ResetThreadPoolThread();
            }

            // Discount a work item here to avoid counting this queue processing work item
            if (completedCount > 1)
            {
                tl.threadLocalCompletionCountNode!.Add(completedCount - 1);
            }
        }
    }

#endif

    public delegate void WaitCallback(object? state);

    public delegate void WaitOrTimerCallback(object? state, bool timedOut);  // signaled or timed out

    internal abstract class QueueUserWorkItemCallbackBase : IThreadPoolWorkItem
    {
#if DEBUG
        private bool _executed;

        ~QueueUserWorkItemCallbackBase()
        {
            Interlocked.MemoryBarrier(); // ensure that an old cached value is not read below
            Debug.Assert(_executed, "A QueueUserWorkItemCallback was never called!");
        }
#endif

        public virtual void Execute()
        {
#if DEBUG
            GC.SuppressFinalize(this);
            Debug.Assert(!Interlocked.Exchange(ref _executed, true), "A QueueUserWorkItemCallback was called twice!");
#endif
        }
    }

    internal sealed class QueueUserWorkItemCallback : QueueUserWorkItemCallbackBase
    {
        private WaitCallback? _callback; // SOS's ThreadPool command depends on this name
        private readonly object? _state;
        private readonly ExecutionContext _context;

        private static readonly Action<QueueUserWorkItemCallback> s_executionContextShim = quwi =>
        {
            Debug.Assert(quwi._callback != null);
            WaitCallback callback = quwi._callback;
            quwi._callback = null;

            callback(quwi._state);
        };

        internal QueueUserWorkItemCallback(WaitCallback callback, object? state, ExecutionContext context)
        {
            Debug.Assert(context != null);

            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void Execute()
        {
            base.Execute();

            ExecutionContext.RunForThreadPoolUnsafe(_context, s_executionContextShim, this);
        }
    }

    internal sealed class QueueUserWorkItemCallback<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState>? _callback; // SOS's ThreadPool command depends on this name
        private readonly TState _state;
        private readonly ExecutionContext _context;

        internal QueueUserWorkItemCallback(Action<TState> callback, TState state, ExecutionContext context)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void Execute()
        {
            base.Execute();

            Debug.Assert(_callback != null);
            Action<TState> callback = _callback;
            _callback = null;

            ExecutionContext.RunForThreadPoolUnsafe(_context, callback, in _state);
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext : QueueUserWorkItemCallbackBase
    {
        private WaitCallback? _callback; // SOS's ThreadPool command depends on this name
        private readonly object? _state;

        internal QueueUserWorkItemCallbackDefaultContext(WaitCallback callback, object? state)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
        }

        public override void Execute()
        {
            ExecutionContext.CheckThreadPoolAndContextsAreDefault();
            base.Execute();

            Debug.Assert(_callback != null);
            WaitCallback callback = _callback;
            _callback = null;

            callback(_state);

            // ThreadPoolWorkQueue.Dispatch will handle notifications and reset EC and SyncCtx back to default
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState>? _callback; // SOS's ThreadPool command depends on this name
        private readonly TState _state;

        internal QueueUserWorkItemCallbackDefaultContext(Action<TState> callback, TState state)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
        }

        public override void Execute()
        {
            ExecutionContext.CheckThreadPoolAndContextsAreDefault();
            base.Execute();

            Debug.Assert(_callback != null);
            Action<TState> callback = _callback;
            _callback = null;

            callback(_state);

            // ThreadPoolWorkQueue.Dispatch will handle notifications and reset EC and SyncCtx back to default
        }
    }

    internal sealed class _ThreadPoolWaitOrTimerCallback
    {
        private readonly WaitOrTimerCallback _waitOrTimerCallback;
        private readonly ExecutionContext? _executionContext;
        private readonly object? _state;
        private static readonly ContextCallback _ccbt = new ContextCallback(WaitOrTimerCallback_Context_t);
        private static readonly ContextCallback _ccbf = new ContextCallback(WaitOrTimerCallback_Context_f);

        internal _ThreadPoolWaitOrTimerCallback(WaitOrTimerCallback waitOrTimerCallback, object? state, bool flowExecutionContext)
        {
            _waitOrTimerCallback = waitOrTimerCallback;
            _state = state;

            if (flowExecutionContext)
            {
                // capture the exection context
                _executionContext = ExecutionContext.Capture();
            }
        }

        private static void WaitOrTimerCallback_Context_t(object? state) =>
            WaitOrTimerCallback_Context(state, timedOut: true);

        private static void WaitOrTimerCallback_Context_f(object? state) =>
            WaitOrTimerCallback_Context(state, timedOut: false);

        private static void WaitOrTimerCallback_Context(object? state, bool timedOut)
        {
            _ThreadPoolWaitOrTimerCallback helper = (_ThreadPoolWaitOrTimerCallback)state!;
            helper._waitOrTimerCallback(helper._state, timedOut);
        }

        // call back helper
        internal static void PerformWaitOrTimerCallback(_ThreadPoolWaitOrTimerCallback helper, bool timedOut)
        {
            Debug.Assert(helper != null, "Null state passed to PerformWaitOrTimerCallback!");
            // call directly if it is an unsafe call OR EC flow is suppressed
            ExecutionContext? context = helper._executionContext;
            if (context == null)
            {
                WaitOrTimerCallback callback = helper._waitOrTimerCallback;
                callback(helper._state, timedOut);
            }
            else
            {
                ExecutionContext.Run(context, timedOut ? _ccbt : _ccbf, helper);
            }
        }
    }

    public static partial class ThreadPool
    {
        internal const string WorkerThreadName = ".NET TP Worker";

        internal static readonly ThreadPoolWorkQueue s_workQueue = new ThreadPoolWorkQueue();

        /// <summary>Shim used to invoke <see cref="IAsyncStateMachineBox.MoveNext"/> of the supplied <see cref="IAsyncStateMachineBox"/>.</summary>
        internal static readonly Action<object?> s_invokeAsyncStateMachineBox = static state =>
        {
            if (state is IAsyncStateMachineBox box)
            {
                box.MoveNext();
            }
            else
            {
                ThrowHelper.ThrowUnexpectedStateForKnownCallback(state);
            }
        };

        internal static bool EnableWorkerTracking => IsWorkerTrackingEnabledInConfig && EventSource.IsSupported;

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        [CLSCompliant(false)]
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
#if TARGET_WASI
            if (OperatingSystem.IsWasi()) throw new PlatformNotSupportedException(); // TODO remove with https://github.com/dotnet/runtime/pull/107185
#endif
            if (millisecondsTimeOutInterval > (uint)int.MaxValue && millisecondsTimeOutInterval != uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        [CLSCompliant(false)]
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
#if TARGET_WASI
            if (OperatingSystem.IsWasi()) throw new PlatformNotSupportedException(); // TODO remove with https://github.com/dotnet/runtime/pull/107185
#endif
            if (millisecondsTimeOutInterval > (uint)int.MaxValue && millisecondsTimeOutInterval != uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeOutInterval, -1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
#if TARGET_WASI
            if (OperatingSystem.IsWasi()) throw new PlatformNotSupportedException(); // TODO remove with https://github.com/dotnet/runtime/pull/107185
#endif
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeOutInterval, -1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            object? state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
#if TARGET_WASI
            if (OperatingSystem.IsWasi()) throw new PlatformNotSupportedException(); // TODO remove with https://github.com/dotnet/runtime/pull/107185
#endif
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeOutInterval, -1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(millisecondsTimeOutInterval, int.MaxValue);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            object? state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
#if TARGET_WASI
            if (OperatingSystem.IsWasi()) throw new PlatformNotSupportedException(); // TODO remove with https://github.com/dotnet/runtime/pull/107185
#endif
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeOutInterval, -1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(millisecondsTimeOutInterval, int.MaxValue);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          object? state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
#if TARGET_WASI
            if (OperatingSystem.IsWasi()) throw new PlatformNotSupportedException(); // TODO remove with https://github.com/dotnet/runtime/pull/107185
#endif
            long tm = (long)timeout.TotalMilliseconds;

            ArgumentOutOfRangeException.ThrowIfLessThan(tm, -1, nameof(timeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(tm, int.MaxValue, nameof(timeout));

            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)tm, executeOnlyOnce, true);
        }

#if !FEATURE_WASM_MANAGED_THREADS
        [UnsupportedOSPlatform("browser")]
#endif
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          object? state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
#if TARGET_WASI
            if (OperatingSystem.IsWasi()) throw new PlatformNotSupportedException(); // TODO remove with https://github.com/dotnet/runtime/pull/107185
#endif
            long tm = (long)timeout.TotalMilliseconds;

            ArgumentOutOfRangeException.ThrowIfLessThan(tm, -1, nameof(timeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(tm, int.MaxValue, nameof(timeout));

            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)tm, executeOnlyOnce, false);
        }

        public static bool QueueUserWorkItem(WaitCallback callBack) =>
            QueueUserWorkItem(callBack, null);

        public static bool QueueUserWorkItem(WaitCallback callBack, object? state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            ExecutionContext? context = ExecutionContext.Capture();

            object tpcallBack = (context == null || context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext(callBack, state) :
                (object)new QueueUserWorkItemCallback(callBack, state, context);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        public static bool QueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            ExecutionContext? context = ExecutionContext.Capture();

            object tpcallBack = (context == null || context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext<TState>(callBack, state) :
                (object)new QueueUserWorkItemCallback<TState>(callBack, state, context);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: !preferLocal);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            // If the callback is the runtime-provided invocation of an IAsyncStateMachineBox,
            // then we can queue the Task state directly to the ThreadPool instead of
            // wrapping it in a QueueUserWorkItemCallback.
            //
            // This occurs when user code queues its provided continuation to the ThreadPool;
            // internally we call UnsafeQueueUserWorkItemInternal directly for Tasks.
            if (ReferenceEquals(callBack, s_invokeAsyncStateMachineBox))
            {
                if (state is not IAsyncStateMachineBox)
                {
                    // The provided state must be the internal IAsyncStateMachineBox (Task) type
                    ThrowHelper.ThrowUnexpectedStateForKnownCallback(state);
                }

                UnsafeQueueUserWorkItemInternal((object)state, preferLocal);
                return true;
            }

            s_workQueue.Enqueue(
                new QueueUserWorkItemCallbackDefaultContext<TState>(callBack, state), forceGlobal: !preferLocal);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem(WaitCallback callBack, object? state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            object tpcallBack = new QueueUserWorkItemCallbackDefaultContext(callBack, state);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem(IThreadPoolWorkItem callBack, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }
            if (callBack is Task)
            {
                // Prevent code from queueing a derived Task that also implements the interface,
                // as that would bypass Task.Start and its safety checks.
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.callBack);
            }

            UnsafeQueueUserWorkItemInternal(callBack, preferLocal);
            return true;
        }

        internal static void UnsafeQueueUserWorkItemInternal(object callBack, bool preferLocal) =>
            s_workQueue.Enqueue(callBack, forceGlobal: !preferLocal);
        internal static void UnsafeQueueHighPriorityWorkItemInternal(IThreadPoolWorkItem callBack) =>
            s_workQueue.EnqueueAtHighPriority(callBack);

        // This method tries to take the target callback out of the current thread's queue.
        internal static bool TryPopCustomWorkItem(Task workItem)
        {
            Debug.Assert(null != workItem);
            return s_workQueue.TryRemove(workItem);
        }

        // Get all workitems.  Called by TaskScheduler in its debugger hooks.
        internal static IEnumerable<object> GetQueuedWorkItems()
        {
            // Enumerate high-priority queue
            foreach (object workItem in s_workQueue.highPriorityWorkItems)
            {
                yield return workItem;
            }

            // Enumerate assignable global queues
            foreach (WorkQueue queue in s_workQueue._assignableWorkItemQueues)
            {
                foreach (object workItem in queue)
                {
                    yield return workItem;
                }
            }

            // Enumerate global queue
            foreach (object workItem in s_workQueue.workItems)
            {
                yield return workItem;
            }

            // Enumerate each local queue
            foreach (ThreadPoolWorkQueue.WorkStealingQueue workStealingQueue in s_workQueue._WorkStealingQueues)
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

        /// <summary>
        /// Gets the number of work items that are currently queued to be processed.
        /// </summary>
        /// <remarks>
        /// For a thread pool implementation that may have different types of work items, the count includes all types that can
        /// be tracked, which may only be the user work items including tasks. Some implementations may also include queued
        /// timer and wait callbacks in the count. On Windows, the count is unlikely to include the number of pending IO
        /// completions, as they get posted directly to an IO completion port.
        /// </remarks>
        public static long PendingWorkItemCount
        {
            get
            {
                ThreadPoolWorkQueue workQueue = s_workQueue;
                return workQueue.LocalCount + workQueue.GlobalCount;
            }
        }
    }
}

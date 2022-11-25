// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Threading
{
    /// <summary>
    /// Manipulates the object header located 4 bytes before each object's MethodTable pointer
    /// in the managed heap.
    /// </summary>
    /// <remarks>
    /// Do not store managed pointers (ref int) to the object header in locals or parameters
    /// as they may be incorrectly updated during garbage collection.
    /// </remarks>
    internal static class ObjectHeader
    {
        // The following three header bits are reserved for the GC engine:
        //   BIT_SBLK_UNUSED        = 0x80000000
        //   BIT_SBLK_FINALIZER_RUN = 0x40000000
        //   BIT_SBLK_GC_RESERVE    = 0x20000000
        //
        // All other bits may be used to store runtime data: hash code, sync entry index, etc.
        // Here we use the same bit layout as in CLR: if bit 26 (BIT_SBLK_IS_HASHCODE) is set,
        // all the lower bits 0..25 store the hash code, otherwise they store either the sync
        // entry index (indicated by BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX) or thin lock data.
        private const int IS_HASHCODE_BIT_NUMBER = 26;
        private const int IS_HASH_OR_SYNCBLKINDEX_BIT_NUMBER = 27;
        private const int BIT_SBLK_IS_HASHCODE = 1 << IS_HASHCODE_BIT_NUMBER;
        internal const int MASK_HASHCODE_INDEX = BIT_SBLK_IS_HASHCODE - 1;
        private const int BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX = 1 << IS_HASH_OR_SYNCBLKINDEX_BIT_NUMBER;


        // if BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX is clear, the rest of the header dword is laid out as follows:
        // - lower ten bits (bits 0 thru 9) is thread id used for the thin locks
        //   value is zero if no thread is holding the lock
        // - following six bits (bits 10 thru 15) is recursion level used for the thin locks
        //   value is zero if lock is not taken or only taken once by the same thread
        private const int SBLK_MASK_LOCK_THREADID = 0x000003FF;   // special value of 0 + 1023 thread ids
        private const int SBLK_MASK_LOCK_RECLEVEL = 0x0000FC00;   // 64 recursion levels
        private const int SBLK_LOCK_RECLEVEL_INC =  0x00000400;   // each level is this much higher than the previous one
        private const int SBLK_RECLEVEL_SHIFT =     10;           // shift right this much to get recursion level

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int* GetHeaderPtr(byte* pRawData)
        {
            // The header is 4 bytes before m_pEEType field on all architectures
            return (int*)(pRawData - sizeof(IntPtr) - sizeof(int));
        }

        /// <summary>
        /// Returns the hash code assigned to the object.  If no hash code has yet been assigned,
        /// it assigns one in a thread-safe way.
        /// </summary>
        public static unsafe int GetHashCode(object o)
        {
            if (o == null)
                return 0;

            fixed (byte* pRawData = &o.GetRawData())
            {
                int* pHeader = GetHeaderPtr(pRawData);
                int bits = *pHeader;
                int hashOrIndex = bits & MASK_HASHCODE_INDEX;
                if ((bits & BIT_SBLK_IS_HASHCODE) != 0)
                {
                    // Found the hash code in the header
                    Debug.Assert(hashOrIndex != 0);
                    return hashOrIndex;
                }

                if ((bits & BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX) != 0)
                {
                    // Look up the hash code in the SyncTable
                    int hashCode = SyncTable.GetHashCode(hashOrIndex);
                    if (hashCode != 0)
                    {
                        return hashCode;
                    }
                }

                // The hash code has not yet been set.  Assign some value.
                return AssignHashCode(o, pHeader);
            }
        }

        /// <summary>
        /// Assigns a hash code to the object in a thread-safe way.
        /// </summary>
        private static unsafe int AssignHashCode(object o, int* pHeader)
        {
            int newHash = RuntimeHelpers.GetNewHashCode() & MASK_HASHCODE_INDEX;
            // Never use the zero hash code.  SyncTable treats the zero value as "not assigned".
            if (newHash == 0)
            {
                newHash = 1;
            }

            while (true)
            {
                int oldBits = *pHeader;

                // if have hashcode, just return it
                if ((oldBits & BIT_SBLK_IS_HASHCODE) != 0)
                {
                    // Found the hash code in the header
                    int h = oldBits & MASK_HASHCODE_INDEX;
                    Debug.Assert(h != 0);
                    return h;
                }

                // if have something else, break, we need a syncblock.
                if ((oldBits & MASK_HASHCODE_INDEX) != 0)
                {
                    break;
                }

                // there is nothing - try set hashcode inline
                Debug.Assert((oldBits & BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX) == 0);
                int newBits = BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX | BIT_SBLK_IS_HASHCODE | oldBits | newHash;
                if (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) == oldBits)
                {
                    return newHash;
                }

                // contention, try again
            }

            if (!GetSyncEntryIndex(*pHeader, out int syncIndex))
            {
                // Assign a new sync entry
                syncIndex = SyncTable.AssignEntry(o, pHeader);
            }

            // Set the hash code in SyncTable. This call will resolve the potential race.
            return SyncTable.SetHashCode(syncIndex, newHash);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasSyncEntryIndex(int header)
        {
            return (header & (BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX | BIT_SBLK_IS_HASHCODE)) == BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX;
        }

        /// <summary>
        /// Extracts the sync entry index or the hash code from the header value.  Returns true
        /// if the header value stores the sync entry index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetSyncEntryIndex(int header, out int index)
        {
            index = header & MASK_HASHCODE_INDEX;
            return HasSyncEntryIndex(header);
        }

        /// <summary>
        /// Returns the Monitor synchronization object assigned to this object.  If no synchronization
        /// object has yet been assigned, it assigns one in a thread-safe way.
        /// </summary>
        public static unsafe Lock GetLockObject(object o)
        {
            return SyncTable.GetLockObject(GetSyncIndex(o));
        }

        private static unsafe int GetSyncIndex(object o)
        {
            fixed (byte* pRawData = &o.GetRawData())
            {
                int* pHeader = GetHeaderPtr(pRawData);
                if (GetSyncEntryIndex(*pHeader, out int syncIndex))
                {
                    return syncIndex;
                }

                // Assign a new sync entry
                return SyncTable.AssignEntry(o, pHeader);
            }
        }

        /// <summary>
        /// Sets the sync entry index in a thread-safe way.
        /// </summary>
        public static unsafe void SetSyncEntryIndex(int* pHeader, int syncIndex)
        {
            // Holding this lock implies there is at most one thread setting the sync entry index at
            // any given time.  We also require that the sync entry index has not been already set.
            Debug.Assert(SyncTable.s_lock.IsAcquired);
            int oldBits, newBits;

            do
            {
                oldBits = *pHeader;
                // we should not have a sync index yet.
                Debug.Assert(!HasSyncEntryIndex(oldBits));

                if ((oldBits & BIT_SBLK_IS_HASHCODE) != 0)
                {
                    // set the hash code in the sync entry
                    SyncTable.MoveHashCodeToNewEntry(syncIndex, oldBits & MASK_HASHCODE_INDEX);
                    // reset the lock info, in case we have set it in the previous iteration
                    SyncTable.MoveThinLockToNewEntry(syncIndex, 0, 0);
                }
                else
                {
                    // set the lock info
                    SyncTable.MoveThinLockToNewEntry(
                        syncIndex,
                        oldBits & SBLK_MASK_LOCK_THREADID,
                        (oldBits & SBLK_MASK_LOCK_RECLEVEL) >> SBLK_RECLEVEL_SHIFT);
                }

                // Store the sync entry index
                newBits = oldBits & ~(BIT_SBLK_IS_HASHCODE | MASK_HASHCODE_INDEX);
                newBits |= syncIndex | BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX;
            }
            while (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) != oldBits);
        }

        // only acquire if unowned.
        // 1 - success
        // 0 - failed
        // syncIndex - retry with the Lock
        public static unsafe int TryAcquire(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Debug.Assert(!(obj is Lock),
                "Do not use Monitor.Enter or TryEnter on a Lock instance; use Lock methods directly instead.");

            int currentThreadID = Environment.CurrentManagedThreadId;
            // does thread ID fit?
            if (currentThreadID > SBLK_MASK_LOCK_THREADID)
                return GetSyncIndex(obj);

            fixed (byte* pRawData = &obj.GetRawData())
            {
                int* pHeader = GetHeaderPtr(pRawData);

                while (true)
                {
                    int oldBits = *pHeader;

                    // if noone owns, try setting our thread id
                    if ((oldBits & MASK_HASHCODE_INDEX) == 0)
                    {
                        int newBits = oldBits | currentThreadID;
                        if (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) == oldBits)
                        {
                            return 1;
                        }

                        // contention, but we do not know if there is owner, must try again
                        continue;
                    }

                    // has sync entry
                    if (GetSyncEntryIndex(oldBits, out int syncIndex))
                    {
                        return syncIndex;
                    }

                    // has hashcode
                    if ((oldBits & BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX) != 0)
                    {
                        return SyncTable.AssignEntry(obj, pHeader);
                    }

                    // we own the lock already
                    if ((oldBits & SBLK_MASK_LOCK_THREADID) == currentThreadID)
                    {
                        // try incrementing recursion level, check for overflow
                        int newBits = oldBits + SBLK_LOCK_RECLEVEL_INC;
                        if ((newBits & SBLK_MASK_LOCK_RECLEVEL) != 0)
                        {
                            if (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) == oldBits)
                            {
                                return 1;
                            }

                            // contention, but we still own the lock and may be able to increment, try again
                            continue;
                        }
                        else
                        {
                            // overflow, transition to a Lock
                            return SyncTable.AssignEntry(obj, pHeader);
                        }
                    }

                    // someone else owns.
                    return 0;
                }
            }
        }

        // Same as TryAcquire, but with some retrying when owned by someone else.
        // 1 - success
        // 0 - failed
        // syncIndex - retry with the Lock
        public static unsafe int Acquire(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Debug.Assert(!(obj is Lock),
                "Do not use Monitor.Enter or TryEnter on a Lock instance; use Lock methods directly instead.");

            // TODO: VS spin
            return TryAcquire(obj);
        }

        // 1 - success
        // 0 - failed
        // syncIndex - retry with the Lock
        public static unsafe int Release(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Debug.Assert(!(obj is Lock),
                "Do not use Monitor.Enter or TryEnter on a Lock instance; use Lock methods directly instead.");

            int currentThreadID = Environment.CurrentManagedThreadId;

            fixed (byte* pRawData = &obj.GetRawData())
            {
                int* pHeader = GetHeaderPtr(pRawData);

                while (true)
                {
                    int oldBits = *pHeader;

                    // if we own the lock
                    if ((oldBits & SBLK_MASK_LOCK_THREADID) == currentThreadID &&
                        (oldBits & BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX) == 0)
                    {
                        // decrement count or release entirely.
                        int newBits = (oldBits & SBLK_MASK_LOCK_RECLEVEL) != 0 ?
                            oldBits - SBLK_LOCK_RECLEVEL_INC :
                            oldBits & ~SBLK_MASK_LOCK_THREADID;

                        if (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) == oldBits)
                        {
                            return 1;
                        }

                        // contention, we still own the lock, try again
                        continue;
                    }

                    if (GetSyncEntryIndex(oldBits, out int syncIndex))
                    {
                        return syncIndex;
                    }

                    // someone else owns or noone.
                    return 0;
                }
            }
        }

        // 1 - yes
        // 0 - no
        // syncIndex - retry with the Lock
        public static unsafe int IsAcquired(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Debug.Assert(!(obj is Lock),
                "Do not use Monitor.Enter or TryEnter on a Lock instance; use Lock methods directly instead.");

            int currentThreadID = Environment.CurrentManagedThreadId;
            fixed (byte* pRawData = &obj.GetRawData())
            {
                int* pHeader = GetHeaderPtr(pRawData);
                int oldBits = *pHeader;

                // if we own the lock
                if ((oldBits & SBLK_MASK_LOCK_THREADID) == currentThreadID &&
                   (oldBits & BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX) == 0)
                {
                    return 1;
                }

                if (GetSyncEntryIndex(oldBits, out int syncIndex))
                {
                    return syncIndex;
                }

                // someone else owns or noone.
                return 0;
            }
        }
    }
}

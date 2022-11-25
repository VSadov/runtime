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
        // The following two header bits are used by the GC engine:
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
        internal const int BIT_SBLK_IS_HASHCODE = 1 << IS_HASHCODE_BIT_NUMBER;
        internal const int MASK_HASHCODE_INDEX = BIT_SBLK_IS_HASHCODE - 1;
        internal const int BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX = 1 << IS_HASH_OR_SYNCBLKINDEX_BIT_NUMBER;


        // if BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX is clear, the rest of the header dword is laid out as follows:
        // - lower ten bits (bits 0 thru 9) is thread id used for the thin locks
        //   value is zero if no thread is holding the lock
        // - following six bits (bits 10 thru 15) is recursion level used for the thin locks
        //   value is zero if lock is not taken or only taken once by the same thread
        private const int SBLK_MASK_LOCK_THREADID = 0x000003FF;   // special value of 0 + 1023 thread ids
        private const int SBLK_MASK_LOCK_RECLEVEL = 0x0000FC00;   // 64 recursion levels
        private const int SBLK_LOCK_RECLEVEL_INC =  0x00000400;   // each level is this much higher than the previous one
        private const int SBLK_RECLEVEL_SHIFT =     10;           // shift right this much to get recursion level

        /// <summary>
        /// Returns the hash code assigned to the object.  If no hash code has yet been assigned,
        /// it assigns one in a thread-safe way.
        /// </summary>
        public static unsafe int GetHashCode(object o)
        {
            if (o == null)
            {
                return 0;
            }

            fixed (byte* pRawData = &o.GetRawData())
            {
                // The header is 4 bytes before m_pEEType field on all architectures
                int* pHeader = (int*)(pRawData - sizeof(IntPtr) - sizeof(int));

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
        public static bool HasSyncEntryIndex(int header)
        {
            return (header & (BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX | BIT_SBLK_IS_HASHCODE)) == BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX;
        }

        /// <summary>
        /// Extracts the sync entry index or the hash code from the header value.  Returns true
        /// if the header value stores the sync entry index.
        /// </summary>
        // Inlining is important for lock performance
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
        // Called from Monitor.Enter only; inlining is important for lock performance
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Lock GetLockObject(object o)
        {
            fixed (byte* pRawData = &o.GetRawData())
            {
                // The header is 4 bytes before m_pEEType field on all architectures
                int* pHeader = (int*)(pRawData - sizeof(IntPtr) - sizeof(int));

                if (GetSyncEntryIndex(*pHeader, out int syncIndex))
                {
                    // Already have a sync entry for this object, return the synchronization object
                    // stored in the entry.
                    return SyncTable.GetLockObject(syncIndex);
                }

                // Assign a new sync entry
                syncIndex = SyncTable.AssignEntry(o, pHeader);
                return SyncTable.GetLockObject(syncIndex);
            }
        }

        public static unsafe int GetSyncIndex(object o)
        {
            fixed (byte* pRawData = &o.GetRawData())
            {
                // The header is 4 bytes before m_pEEType field on all architectures
                int* pHeader = (int*)(pRawData - sizeof(IntPtr) - sizeof(int));

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

        // does not fail
        // 1 - success
        // syncIndex - retry with the Lock
        public static unsafe int Lock(object o)
        {
            // TODO: VS spin

            int result = TryLock(o);
            if (result == 0)
                result = GetSyncIndex(o);

            return result;
        }

        // one-shot version
        // 1 - success
        // 0 - failed
        // syncIndex - retry with the Lock
        public static unsafe int TryLock(object o)
        {
            Debug.Assert(o != null);

            int currentThreadID = Environment.CurrentManagedThreadId;
            // does thread ID fit?
            if ((currentThreadID & SBLK_MASK_LOCK_THREADID) != currentThreadID)
                return GetSyncIndex(o);

            fixed (byte* pRawData = &o.GetRawData())
            {
                // The header is 4 bytes before m_pEEType field on all architectures
                int* pHeader = (int*)(pRawData - sizeof(IntPtr) - sizeof(int));

            tryAgain:
                int oldBits = *pHeader;

                // if noone owns, try setting our thread id
                if ((oldBits & MASK_HASHCODE_INDEX) == 0)
                {
                    int newBits = oldBits | currentThreadID;
                    if (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) == oldBits)
                    {
                        return 1;
                    }

                    goto tryAgain;
                }

                // if we own the lock
                if ((oldBits & SBLK_MASK_LOCK_THREADID) == currentThreadID &&
                    (oldBits & BIT_SBLK_IS_HASH_OR_SYNCBLKINDEX) == 0)
                {
                    // try incrementing recursion level
                    if ((oldBits & SBLK_MASK_LOCK_RECLEVEL) != SBLK_MASK_LOCK_RECLEVEL)
                    {
                        // if recursion count is not full, increment by one
                        int newBits = oldBits + SBLK_LOCK_RECLEVEL_INC;
                        if (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) == oldBits)
                        {
                            return 1;
                        }

                        goto tryAgain;
                    }
                    else
                    {
                        return SyncTable.AssignEntry(o, pHeader);
                    }
                }

                if (GetSyncEntryIndex(oldBits, out int syncIndex))
                {
                    return syncIndex;
                }
            }

            // someone else owns.
            return 0;
        }

        // 1 - success
        // 0 - failed
        // syncIndex - retry with the Lock
        public static unsafe int Unlock(object o)
        {
            Debug.Assert(o != null);
            int currentThreadID = Environment.CurrentManagedThreadId;

            fixed (byte* pRawData = &o.GetRawData())
            {
                // The header is 4 bytes before m_pEEType field on all architectures
                int* pHeader = (int*)(pRawData - sizeof(IntPtr) - sizeof(int));

            tryAgain:
                int oldBits = *pHeader;

                // if we own the lock
                if ((oldBits & SBLK_MASK_LOCK_THREADID) == currentThreadID  &&
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

                    goto tryAgain;
                }

                if (GetSyncEntryIndex(oldBits, out int syncIndex))
                {
                    return syncIndex;
                }
            }

            // someone else owns or noone.
            return 0;
        }

        // 1 - yes
        // 0 - no
        // syncIndex - retry with the Lock
        public static unsafe int IsAcquired(object o)
        {
            Debug.Assert(o != null);
            int currentThreadID = Environment.CurrentManagedThreadId;
            fixed (byte* pRawData = &o.GetRawData())
            {
                // The header is 4 bytes before m_pEEType field on all architectures
                int* pHeader = (int*)(pRawData - sizeof(IntPtr) - sizeof(int));
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

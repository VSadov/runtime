// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*============================================================
**
** Header:  LoaderAllocator.hpp
**

**
** Purpose: Implements collection of loader heaps
**
**
===========================================================*/

#ifndef __LoaderAllocator_h__
#define __LoaderAllocator_h__

class FuncPtrStubs;
#include "qcall.h"
#include "ilstubcache.h"

#include "callcounting.h"
#include "methoddescbackpatchinfo.h"
#include "crossloaderallocatorhash.h"
#include "onstackreplacement.h"
#include "lockedrangelist.h"
#include "pgo.h"

#define VPTRU_LoaderAllocator 0x3200

enum LoaderAllocatorType
{
    LAT_Invalid,
    LAT_Global,
    LAT_Assembly
};

typedef SHash<PtrSetSHashTraits<LoaderAllocator *>> LoaderAllocatorSet;

class CustomAssemblyBinder;


// This implements the Add/Remove rangelist api on top of the CodeRangeMap in the code manager
class CodeRangeMapRangeList : public RangeList
{
public:
    VPTR_VTABLE_CLASS(CodeRangeMapRangeList, RangeList)

#if defined(DACCESS_COMPILE) || !defined(TARGET_WINDOWS)
    CodeRangeMapRangeList() :
        _RangeListRWLock(COOPERATIVE_OR_PREEMPTIVE, LOCK_TYPE_DEFAULT),
        _rangeListType(STUB_CODE_BLOCK_UNKNOWN),
        _id(NULL),
        _collectible(true)
    {}
#endif

    CodeRangeMapRangeList(StubCodeBlockKind rangeListType, bool collectible) :
        _RangeListRWLock(COOPERATIVE_OR_PREEMPTIVE, LOCK_TYPE_DEFAULT),
        _rangeListType(rangeListType),
        _id(NULL),
        _collectible(collectible)
    {
        LIMITED_METHOD_CONTRACT;
    }

    ~CodeRangeMapRangeList()
    {
        LIMITED_METHOD_CONTRACT;
        RemoveRangesWorker(_id);
    }

    StubCodeBlockKind GetCodeBlockKind()
    {
        LIMITED_METHOD_CONTRACT;
        return _rangeListType;
    }

private:
#ifndef DACCESS_COMPILE
    void AddRangeWorkerHelper(TADDR start, TADDR end, void* id)
    {
        ReportStubBlock((void*)start, (size_t)(end - start), _rangeListType);
        SimpleWriteLockHolder lh(&_RangeListRWLock);

        _ASSERTE(id == _id || _id == NULL);
        _id = id;
        // Grow the array first, so that a failure cannot break the

        RangeSection::RangeSectionFlags flags = RangeSection::RANGE_SECTION_RANGELIST;
        if (_collectible)
        {
            _starts.Preallocate(_starts.GetCount() + 1);
            flags = (RangeSection::RangeSectionFlags)(flags | RangeSection::RANGE_SECTION_COLLECTIBLE);
        }

        ExecutionManager::AddCodeRange(start, end, ExecutionManager::GetEEJitManager(), flags, this);

        if (_collectible)
        {
            // This cannot fail as the array was Preallocated above.
            _starts.Append(start);
        }
    }
#endif

protected:
    virtual BOOL AddRangeWorker(const BYTE *start, const BYTE *end, void *id)
    {
        CONTRACTL
        {
            NOTHROW;
            GC_NOTRIGGER;
        }
        CONTRACTL_END;

#ifndef DACCESS_COMPILE
        BOOL result = FALSE;

        EX_TRY
        {
            AddRangeWorkerHelper((TADDR)start, (TADDR)end, id);
            result = TRUE;
        }
        EX_CATCH
        {
        }
        EX_END_CATCH

        return result;
#else
        return FALSE;
#endif // DACCESS_COMPILE
    }

    virtual void RemoveRangesWorker(void *id)
    {
        CONTRACTL
        {
            NOTHROW;
            GC_NOTRIGGER;
        }
        CONTRACTL_END;

#ifndef DACCESS_COMPILE
        SimpleWriteLockHolder lh(&_RangeListRWLock);
        _ASSERTE(id == _id || (_id == NULL && _starts.IsEmpty()));

        // Iterate backwards to improve efficiency of removals
        // as any linked lists in the RangeSectionMap code are in reverse order of insertion.
        for (auto i = _starts.GetCount(); i > 0;)
        {
            --i;
            if (_starts[i] != 0)
            {
                ExecutionManager::DeleteRange(_starts[i]);
                _starts[i] = 0;
            }
        }
#endif // DACCESS_COMPILE
    }

    virtual BOOL IsInRangeWorker(TADDR address)
    {
        WRAPPER_NO_CONTRACT;
        RangeSection *pRS = ExecutionManager::FindCodeRange(address, ExecutionManager::ScanReaderLock);
        if (pRS == NULL)
            return FALSE;
        if ((pRS->_flags & RangeSection::RANGE_SECTION_RANGELIST) == 0)
            return FALSE;

        return (pRS->_pRangeList == this);
    }

private:
    SimpleRWLock _RangeListRWLock;
    StubCodeBlockKind _rangeListType;
    SArray<TADDR> _starts;
    void* _id;
    bool _collectible;
};

// Iterator over a DomainAssembly in the same ALC
class DomainAssemblyIterator
{
    DomainAssembly* pCurrentAssembly;
    DomainAssembly* pNextAssembly;

public:
    DomainAssemblyIterator(DomainAssembly* pFirstAssembly);

    bool end() const
    {
        return pCurrentAssembly == NULL;
    }

    operator DomainAssembly*() const
    {
        return pCurrentAssembly;
    }

    DomainAssembly* operator ->() const
    {
        return pCurrentAssembly;
    }

    void operator++();

    void operator++(int dummy)
    {
        this->operator++();
    }
};

class LoaderAllocatorID
{

protected:
    LoaderAllocatorType m_type;
    union
    {
        DomainAssembly* m_pDomainAssembly;
        void* m_pValue;
    };

    VOID * GetValue();

public:
    LoaderAllocatorID(LoaderAllocatorType laType=LAT_Invalid, VOID* value = 0)
    {
        m_type = laType;
        m_pValue = value;
    };
    VOID Init();
    LoaderAllocatorType GetType();
    VOID AddDomainAssembly(DomainAssembly* pDomainAssembly);
    DomainAssemblyIterator GetDomainAssemblyIterator();
    BOOL Equals(LoaderAllocatorID* pId);
    COUNT_T Hash();
};

// Segmented stack to store freed handle indices
class SegmentedHandleIndexStack
{
    // Segment of the stack
    struct Segment
    {
        static const int Size = 64;

        Segment* m_prev;
        DWORD    m_data[Size];
    };

    // Segment containing the TOS
    Segment * m_TOSSegment = NULL;
    // One free segment to prevent rapid delete / new if pop / push happens rapidly
    // at the boundary of two segments.
    Segment * m_freeSegment = NULL;
    // Index of the top of stack in the TOS segment
    int       m_TOSIndex = Segment::Size;

public:

    ~SegmentedHandleIndexStack();

    // Push the value to the stack. If the push cannot be done due to OOM, return false;
    inline bool Push(DWORD value);

    // Pop the value from the stack
    inline DWORD Pop();

    // Check if the stack is empty.
    inline bool IsEmpty();
};

class StringLiteralMap;
class VirtualCallStubManager;
template <typename ELEMENT>
class ListLockEntryBase;
typedef ListLockEntryBase<void*> ListLockEntry;
class UMEntryThunkCache;

#ifdef FEATURE_COMINTEROP
class ComCallWrapperCache;
#endif // FEATURE_COMINTEROP
class EEMarshalingData;

class LoaderAllocator
{
    VPTR_BASE_VTABLE_CLASS(LoaderAllocator)
    VPTR_UNIQUE(VPTRU_LoaderAllocator)
protected:

    //****************************************************************************************
    // #LoaderAllocator Heaps
    // Heaps for allocating data that persists for the life of the AppDomain
    // Objects that are allocated frequently should be allocated into the HighFreq heap for
    // better page management

    DAC_ALIGNAS(UINT64) // Align the first member to alignof(m_nLoaderAllocator). Windows does this by default, force Linux to match.
    BYTE *              m_InitialReservedMemForLoaderHeaps;
    BYTE                m_LowFreqHeapInstance[sizeof(LoaderHeap)];
    BYTE                m_HighFreqHeapInstance[sizeof(LoaderHeap)];
    BYTE                m_StubHeapInstance[sizeof(LoaderHeap)];
    BYTE                m_FixupPrecodeHeapInstance[sizeof(InterleavedLoaderHeap)];
    BYTE                m_NewStubPrecodeHeapInstance[sizeof(InterleavedLoaderHeap)];
    BYTE                m_StaticsHeapInstance[sizeof(LoaderHeap)];
#ifdef FEATURE_READYTORUN
#ifdef FEATURE_STUBPRECODE_DYNAMIC_HELPERS
    BYTE                m_DynamicHelpersHeapInstance[sizeof(InterleavedLoaderHeap)];
#endif // !FEATURE_STUBPRECODE_DYNAMIC_HELPERS
#endif // FEATURE_READYTORUN
    PTR_LoaderHeap      m_pLowFrequencyHeap;
    PTR_LoaderHeap      m_pHighFrequencyHeap;
    PTR_LoaderHeap      m_pStaticsHeap;
    PTR_LoaderHeap      m_pStubHeap; // stubs for PInvoke, remoting, etc
    PTR_LoaderHeap      m_pExecutableHeap;
#ifdef FEATURE_READYTORUN
#ifdef FEATURE_STUBPRECODE_DYNAMIC_HELPERS
    PTR_InterleavedLoaderHeap      m_pDynamicHelpersStubHeap; // R2R Stubs for dynamic helpers. Separate from m_pNewStubPrecodeHeap to avoid allowing these stubs to take up cache space once the process is fully hot.
#else
    PTR_CodeFragmentHeap m_pDynamicHelpersHeap;
#endif // !FEATURE_STUBPRECODE_DYNAMIC_HELPERS
#endif // FEATURE_READYTORUN
    PTR_InterleavedLoaderHeap      m_pFixupPrecodeHeap;
    PTR_InterleavedLoaderHeap      m_pNewStubPrecodeHeap;
    //****************************************************************************************
    OBJECTHANDLE        m_hLoaderAllocatorObjectHandle;
    FuncPtrStubs *      m_pFuncPtrStubs; // for GetMultiCallableAddrOfCode()
    // The LoaderAllocator specific string literal map.
    StringLiteralMap   *m_pStringLiteralMap;
    CrstExplicitInit    m_crstLoaderAllocator;

    // Protect the handle table data structures, seperated from m_crstLoaderAllocator to allow thread cleanup to use the lock
    CrstExplicitInit    m_crstLoaderAllocatorHandleTable;
    bool                m_fGCPressure;
    bool                m_fUnloaded;
    bool                m_fTerminated;
    bool                m_fMarked;
    int                 m_nGCCount;
    bool                m_IsCollectible;

    // Pre-allocated blocks of heap for collectible assemblies. Will be set to NULL as soon as it is
    // used. See code in GetVSDHeapInitialBlock and GetCodeHeapInitialBlock
    BYTE *              m_pVSDHeapInitialAlloc;
    BYTE *              m_pCodeHeapInitialAlloc;

    // U->M thunks that are not associated with a delegate.
    // The cache is keyed by MethodDesc pointers.
    UMEntryThunkCache * m_pUMEntryThunkCache;

    // IL stub cache with fabricated MethodTable parented by a random module in this LoaderAllocator.
    ILStubCache         m_ILStubCache;

#if defined(FEATURE_READYTORUN) && defined(FEATURE_STUBPRECODE_DYNAMIC_HELPERS)
    CodeRangeMapRangeList m_dynamicHelpersRangeList;
#endif // defined(FEATURE_READYTORUN) && defined(FEATURE_STUBPRECODE_DYNAMIC_HELPERS)
    CodeRangeMapRangeList m_stubPrecodeRangeList;
    CodeRangeMapRangeList m_fixupPrecodeRangeList;

#ifdef FEATURE_PGO
    // PgoManager to hold pgo data associated with this LoaderAllocator
    Volatile<PgoManager *> m_pgoManager;
#endif // FEATURE_PGO

    SArray<TLSIndex> m_tlsIndices;

public:
    BYTE *GetVSDHeapInitialBlock(DWORD *pSize);
    BYTE *GetCodeHeapInitialBlock(const BYTE * loAddr, const BYTE * hiAddr, DWORD minimumSize, DWORD *pSize);

    // ExecutionManager caches
    void * m_pLastUsedCodeHeap;
    void * m_pLastUsedDynamicCodeHeap;
#ifdef FEATURE_INTERPRETER
    void * m_pLastUsedInterpreterCodeHeap;
    void * m_pLastUsedInterpreterDynamicCodeHeap;
#endif // FEATURE_INTERPRETER
    void * m_pJumpStubCache;

    // LoaderAllocator GC Structures
    PTR_LoaderAllocator m_pLoaderAllocatorDestroyNext; // Used in LoaderAllocator GC process (during sweeping)
protected:
    void ClearMark();
    void Mark();
    bool Marked();

#ifdef FAT_DISPATCH_TOKENS
    struct DispatchTokenFatSHashTraits : public DefaultSHashTraits<DispatchTokenFat*>
    {
        typedef DispatchTokenFat* key_t;

        static key_t GetKey(element_t e)
            { return e; }

        static BOOL Equals(key_t k1, key_t k2)
            { return *k1 == *k2; }

        static count_t Hash(key_t k)
            { return (count_t)(size_t)*k; }
    };

    typedef SHash<DispatchTokenFatSHashTraits> FatTokenSet;
    SimpleRWLock *m_pFatTokenSetLock;
    FatTokenSet *m_pFatTokenSet;
#endif

    PTR_VirtualCallStubManager m_pVirtualCallStubManager;

public:
    SArray<TLSIndex>& GetTLSIndexList()
    {
        return m_tlsIndices;
    }

private:
    LoaderAllocatorSet m_LoaderAllocatorReferences;
    Volatile<UINT32>   m_cReferences;
    // This will be set by code:LoaderAllocator::Destroy (from managed scout finalizer) and signalizes that
    // the assembly was collected
    DomainAssembly * m_pFirstDomainAssemblyFromSameALCToDelete;

    BOOL CheckAddReference_Unlocked(LoaderAllocator *pOtherLA);

    static UINT64 cLoaderAllocatorsCreated;
    UINT64 m_nLoaderAllocator;

    struct FailedTypeInitCleanupListItem
    {
        SLink m_Link;
        ListLockEntry *m_pListLockEntry;
        explicit FailedTypeInitCleanupListItem(ListLockEntry *pListLockEntry)
                :
            m_pListLockEntry(pListLockEntry)
        {
        }
    };

    SList<FailedTypeInitCleanupListItem> m_failedTypeInitCleanupList;

    SegmentedHandleIndexStack m_freeHandleIndexesStack;
#ifdef FEATURE_COMINTEROP
    // The wrapper cache for this loader allocator - it has its own CCacheLineAllocator on a per loader allocator basis
    // to allow the loader allocator to go away and eventually kill the memory when all refs are gone

    VolatilePtr<ComCallWrapperCache> m_pComCallWrapperCache;
    // Used for synchronizing creation of the m_pComCallWrapperCache
    CrstExplicitInit m_ComCallWrapperCrst;
    // Hash table that maps a MethodTable to COM Interop compatibility data.
    PtrHashMap m_interopDataHash;

#endif

    // Used for synchronizing access to the m_interopDataHash and m_pMarshalingData
    CrstExplicitInit m_InteropDataCrst;
    EEMarshalingData* m_pMarshalingData;

#ifdef FEATURE_TIERED_COMPILATION
    PTR_CallCountingManager m_callCountingManager;
#endif

    MethodDescBackpatchInfoTracker m_methodDescBackpatchInfoTracker;

#ifdef FEATURE_ON_STACK_REPLACEMENT
    PTR_OnStackReplacementManager m_onStackReplacementManager;
#endif

#ifndef DACCESS_COMPILE

public:
    // CleanupFailedTypeInit is called from AppDomain
    // This method accesses loader allocator state in a thread unsafe manner.
    // It expects to be called only from Terminate.
    void CleanupFailedTypeInit();
#endif //!DACCESS_COMPILE

    // Collect unreferenced assemblies, remove them from the assembly list and return their loader allocator
    // list.
    static LoaderAllocator * GCLoaderAllocators_RemoveAssemblies(AppDomain * pAppDomain);

public:

    //
    // The scheme for ensuring that LoaderAllocators are destructed correctly is substantially
    // complicated by the requirement that LoaderAllocators that are eligible for destruction
    // must be destroyed as a group due to issues where there may be ordering issues in destruction
    // of LoaderAllocators.
    // Thus, while there must be a complete web of references keeping the LoaderAllocator alive in
    // managed memory, we must also have an analogous web in native memory to manage the specific
    // ordering requirements.
    //
    // Thus we have an extra garbage collector here to manage the native web of LoaderAllocator references
    // Also, we have a reference count scheme so that LCG methods keep their associated LoaderAllocator
    // alive. LCG methods cannot be referenced by LoaderAllocators, so they do not need to participate
    // in the garbage collection scheme except by using AddRef/Release to adjust the root set of this
    // garbage collector.
    //

    //#AssemblyPhases
    // The phases of unloadable assembly are:
    //
    // 1. Managed LoaderAllocator is alive.
    //    - Assembly is visible to managed world, the managed scout is alive and was not finalized yet.
    //      Note that the fact that the managed scout is in the finalizer queue is not important as it can
    //      (and in certain cases has to) ressurect itself.
    //    Detection:
    //        code:IsAlive ... TRUE
    //        code:IsManagedScoutAlive ... TRUE
    //        code:DomainAssembly::GetExposedAssemblyObject ... non-NULL (may need to allocate GC object)
    //
    //        code:AddReferenceIfAlive ... TRUE (+ adds reference)
    //
    // 2. Managed scout is alive, managed LoaderAllocator is collected.
    //    - All managed object related to this assembly (types, their instances, Assembly/AssemblyBuilder)
    //      are dead and/or about to disappear and cannot be recreated anymore. We are just waiting for the
    //      managed scout to run its finalizer.
    //    Detection:
    //        code:IsAlive ... TRUE
    //        code:IsManagedScoutAlive ... TRUE
    //        code:DomainAssembly::GetExposedAssemblyObject ... NULL (change from phase #1)
    //
    //        code:AddReferenceIfAlive ... TRUE (+ adds reference)
    //
    // 3. Native LoaderAllocator is alive, managed scout is collected.
    //    - The native LoaderAllocator can be kept alive via native reference with code:AddRef call, e.g.:
    //        * Reference from LCG method,
    //        * Reference received from assembly iterator code:AppDomain::AssemblyIterator::Next and/or
    //          held by code:CollectibleAssemblyHolder.
    //    - Other LoaderAllocator can have this LoaderAllocator in its reference list
    //      (code:m_LoaderAllocatorReferences), but without call to code:AddRef.
    //    - LoaderAllocator cannot ever go back to phase #1 or #2, but it can skip this phase if there are
    //      no LCG method references keeping it alive at the time of managed scout finalization.
    //    Detection:
    //        code:IsAlive ... TRUE
    //        code:IsManagedScoutAlive ... FALSE (change from phase #2)
    //        code:DomainAssembly::GetExposedAssemblyObject ... NULL
    //
    //        code:AddReferenceIfAlive ... TRUE (+ adds reference)
    //
    // 4. LoaderAllocator is dead.
    //    - The managed scout was collected. No one holds a native reference with code:AddRef to this
    //      LoaderAllocator.
    //    - Other LoaderAllocator can have this LoaderAllocator in its reference list
    //      (code:m_LoaderAllocatorReferences), but without call to code:AddRef.
    //    - LoaderAllocator cannot ever become alive again (i.e. go back to phase #3, #2 or #1).
    //    Detection:
    //        code:IsAlive ... FALSE (change from phase #3, #2 and #1)
    //
    //        code:AddReferenceIfAlive ... FALSE (change from phase #3, #2 and #1)
    //

    void AddReference();
    // Adds reference if the native object is alive  - code:#AssemblyPhases.
    // Returns TRUE if the reference was added.
    BOOL AddReferenceIfAlive();
    BOOL Release();
    // Checks if the native object is alive - see code:#AssemblyPhases.
    BOOL IsAlive() { LIMITED_METHOD_DAC_CONTRACT; return (m_cReferences != (UINT32)0); }
    // Checks if managed scout is alive - see code:#AssemblyPhases.
    BOOL IsManagedScoutAlive()
    {
        return (m_pFirstDomainAssemblyFromSameALCToDelete == NULL);
    }

    // Collect unreferenced assemblies, delete all their remaining resources.
    static void GCLoaderAllocators(LoaderAllocator* firstLoaderAllocator);

    UINT64 GetCreationNumber() { LIMITED_METHOD_DAC_CONTRACT; return m_nLoaderAllocator; }

    // Ensure this LoaderAllocator has a reference to another LoaderAllocator
    BOOL EnsureReference(LoaderAllocator *pOtherLA);

    // Ensure this LoaderAllocator has a reference to every LoaderAllocator of the types
    // in an instantiation
    BOOL EnsureInstantiation(Module *pDefiningModule, Instantiation inst);

    // Given typeId and slotNumber, GetDispatchToken will return a DispatchToken
    // representing <typeId, slotNumber>. If the typeId is big enough, this
    // method will automatically allocate a DispatchTokenFat and encapsulate it
    // in the return value.
    DispatchToken GetDispatchToken(UINT32 typeId, UINT32 slotNumber);

    virtual LoaderAllocatorID* Id() =0;
    BOOL IsCollectible() { WRAPPER_NO_CONTRACT; return m_IsCollectible; }

    // This function may only be called while the runtime is suspended
    // As it does not lock around access to a RangeList
    static void GcReportAssociatedLoaderAllocators_Unsafe(TADDR ptr, promote_func* fn, ScanContext* sc);

    static void AssociateMemoryWithLoaderAllocator(BYTE *start, const BYTE *end, LoaderAllocator* pLoaderAllocator);
    static void RemoveMemoryToLoaderAllocatorAssociation(LoaderAllocator* pLoaderAllocator);

#ifdef DACCESS_COMPILE
    void EnumMemoryRegions(CLRDataEnumMemoryFlags flags);
#endif

    PTR_LoaderHeap GetLowFrequencyHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pLowFrequencyHeap;
    }

    PTR_LoaderHeap GetHighFrequencyHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pHighFrequencyHeap;
    }

    PTR_LoaderHeap GetStaticsHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pStaticsHeap;
    }

    PTR_LoaderHeap GetStubHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pStubHeap;
    }

    PTR_InterleavedLoaderHeap GetNewStubPrecodeHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pNewStubPrecodeHeap;
    }

#if defined(FEATURE_READYTORUN) && defined(FEATURE_STUBPRECODE_DYNAMIC_HELPERS)
    PTR_InterleavedLoaderHeap GetDynamicHelpersStubHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pDynamicHelpersStubHeap;
    }
#endif // defined(FEATURE_READYTORUN) && defined(FEATURE_STUBPRECODE_DYNAMIC_HELPERS)

    // The executable heap is intended to only be used by the global loader allocator.
    // It refers to executable memory that is not associated with a rangelist.
    PTR_LoaderHeap GetExecutableHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pExecutableHeap;
    }

    PTR_InterleavedLoaderHeap GetFixupPrecodeHeap()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pFixupPrecodeHeap;
    }

    PTR_CodeFragmentHeap GetDynamicHelpersHeap();

    FuncPtrStubs * GetFuncPtrStubs();

    FuncPtrStubs * GetFuncPtrStubsNoCreate()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pFuncPtrStubs;
    }

    OBJECTHANDLE GetLoaderAllocatorObjectHandle()
    {
        LIMITED_METHOD_CONTRACT;
        return m_hLoaderAllocatorObjectHandle;
    }

    LOADERALLOCATORREF GetExposedObject();
    bool IsExposedObjectLive();

#ifdef _DEBUG
    bool HasHandleTableLock()
    {
        WRAPPER_NO_CONTRACT;
        if (this == NULL) return true; // During initialization of the LoaderAllocator object, callers may call this with a null this pointer.
        return m_crstLoaderAllocatorHandleTable.OwnedByCurrentThread();
    }
#endif

#ifndef DACCESS_COMPILE
    bool InsertObjectIntoFieldWithLifetimeOfCollectibleLoaderAllocator(OBJECTREF value, Object** pField);
    LOADERHANDLE AllocateHandle(OBJECTREF value);

    void SetHandleValue(LOADERHANDLE handle, OBJECTREF value);
    OBJECTREF CompareExchangeValueInHandle(LOADERHANDLE handle, OBJECTREF value, OBJECTREF compare);
    void FreeHandle(LOADERHANDLE handle);

    // The default implementation is a no-op. Only collectible loader allocators implement this method.
    virtual void RegisterHandleForCleanup(OBJECTHANDLE /* objHandle */) { }
    virtual void RegisterHandleForCleanupLocked(OBJECTHANDLE /* objHandle */) { }
    virtual void UnregisterHandleFromCleanup(OBJECTHANDLE /* objHandle */) { }
    virtual void CleanupHandles() { }

    void RegisterFailedTypeInitForCleanup(ListLockEntry *pListLockEntry);

#ifdef FEATURE_PGO
    PgoManager *GetPgoManager()
    {
        return m_pgoManager;
    }

    PgoManager *GetOrCreatePgoManager()
    {
        auto currentValue = GetPgoManager();
        if (currentValue != NULL)
        {
            return currentValue;
        }
        PgoManager::CreatePgoManager(&m_pgoManager, true);
        return GetPgoManager();
    }
#endif // FEATURE_PGO
#else
#ifdef FEATURE_PGO
    PgoManager *GetPgoManager()
    {
        return NULL;
    }

    PgoManager *GetOrCreatePgoManager()
    {
        return NULL;
    }
#endif // FEATURE_PGO
#endif // !defined(DACCESS_COMPILE)


    // This function is only safe to call if the handle is known to be a handle in a collectible
    // LoaderAllocator, and the handle is allocated, and the LoaderAllocator is also not collected.
    FORCEINLINE OBJECTREF GetHandleValueFastCannotFailType2(LOADERHANDLE handle);

    // These functions are designed to be used for maximum performance to access handle values
    // The GetHandleValueFast will handle the scenario where a loader allocator pointer does not
    // need to be acquired to do the handle lookup, and the GetHandleValueFastPhase2 handles
    // the scenario where the LoaderAllocator pointer is required.
    // Do not use these functions directly - use GET_LOADERHANDLE_VALUE_FAST macro instead.
    FORCEINLINE static BOOL GetHandleValueFast(LOADERHANDLE handle, OBJECTREF *pValue);
    FORCEINLINE BOOL GetHandleValueFastPhase2(LOADERHANDLE handle, OBJECTREF *pValue);

#define GET_LOADERHANDLE_VALUE_FAST(pLoaderAllocator, handle, pRetVal)              \
    do {                                                                            \
        LOADERHANDLE __handle__ = handle;                                           \
        if (!LoaderAllocator::GetHandleValueFast(__handle__, pRetVal) &&            \
            !pLoaderAllocator->GetHandleValueFastPhase2(__handle__, pRetVal))       \
        {                                                                           \
            *(pRetVal) = NULL;                                                      \
        }                                                                           \
    } while (0)

    OBJECTREF GetHandleValue(LOADERHANDLE handle);

    LoaderAllocator(bool collectible);
    virtual ~LoaderAllocator();
    virtual BOOL CanUnload() = 0;
    void Init(BYTE *pExecutableHeapMemory);
    void Terminate();
    virtual void ReleaseAssemblyLoadContext() {}

    SIZE_T EstimateSize();

    void SetupManagedTracking(LOADERALLOCATORREF *pLoaderAllocatorKeepAlive);
    void ActivateManagedTracking();

    // Unloaded in this context means that there is no managed code running against this loader allocator.
    // This flag is used by debugger to filter out methods in modules that are being destructed.
    bool IsUnloaded() { LIMITED_METHOD_CONTRACT; return m_fUnloaded; }
    void SetIsUnloaded() { LIMITED_METHOD_CONTRACT; m_fUnloaded = true; }

    void SetGCRefPoint(int gccounter)
    {
        LIMITED_METHOD_CONTRACT;
        m_nGCCount=gccounter;
    }
    int GetGCRefPoint()
    {
        LIMITED_METHOD_CONTRACT;
        return m_nGCCount;
    }
    void AllocateBytesForStaticVariables(DynamicStaticsInfo* pStaticsInfo, uint32_t cbMem, bool isClassInitedByUpdatingStaticPointer);
    void AllocateGCHandlesBytesForStaticVariables(DynamicStaticsInfo* pStaticsInfo, uint32_t cSlots, MethodTable* pMTWithStaticBoxes, bool isClassInitedByUpdatingStaticPointer);

    static BOOL Destroy(QCall::LoaderAllocatorHandle pLoaderAllocator);

    //****************************************************************************************
    // Methods to retrieve a pointer to the CLR string STRINGREF for a string constant.
    // If the string is not currently in the hash table it will be added and if the
    // copy string flag is set then the string will be copied before it is inserted.
    STRINGREF *GetStringObjRefPtrFromUnicodeString(EEStringData *pStringData, void** ppPinnedString = nullptr);
    void LazyInitStringLiteralMap();
    STRINGREF *IsStringInterned(STRINGREF *pString);
    STRINGREF *GetOrInternString(STRINGREF *pString);
    void CleanupStringLiteralMap();

    void InitVirtualCallStubManager();
    void UninitVirtualCallStubManager();

    inline PTR_VirtualCallStubManager GetVirtualCallStubManager()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pVirtualCallStubManager;
    }

    UMEntryThunkCache *GetUMEntryThunkCache();


    static LoaderAllocator* GetLoaderAllocator(ILStubCache* pILStubCache)
    {
         return CONTAINING_RECORD(pILStubCache, LoaderAllocator, m_ILStubCache);
    }

    ILStubCache* GetILStubCache()
    {
        LIMITED_METHOD_CONTRACT;
        return &m_ILStubCache;
    }

    //****************************************************************************************
    // This method returns marshaling data that the EE uses that is stored on a per LoaderAllocator
    // basis.
    EEMarshalingData *GetMarshalingData();

    EEMarshalingData* GetMarshalingDataIfAvailable()
    {
        LIMITED_METHOD_CONTRACT;
        return m_pMarshalingData;
    }

private:
    // Deletes marshaling data at shutdown (which contains cached factories that needs to be released)
    void DeleteMarshalingData();

public:

#ifdef FEATURE_COMINTEROP

    ComCallWrapperCache * GetComCallWrapperCache();

    void ResetComCallWrapperCache()
    {
        LIMITED_METHOD_CONTRACT;
        m_pComCallWrapperCache = NULL;
    }

#ifndef DACCESS_COMPILE

    // Look up interop data for a method table
    // Returns the data pointer if present, NULL otherwise
    InteropMethodTableData *LookupComInteropData(MethodTable *pMT);

    // Returns TRUE if successfully inserted, FALSE if this would be a duplicate entry
    BOOL InsertComInteropData(MethodTable* pMT, InteropMethodTableData *pData);

#endif // DACCESS_COMPILE

#endif // FEATURE_COMINTEROP

#ifdef FEATURE_TIERED_COMPILATION
public:
    PTR_CallCountingManager GetCallCountingManager()
    {
        LIMITED_METHOD_CONTRACT;
        return m_callCountingManager;
    }
#endif // FEATURE_TIERED_COMPILATION

    MethodDescBackpatchInfoTracker *GetMethodDescBackpatchInfoTracker()
    {
        LIMITED_METHOD_CONTRACT;
        return &m_methodDescBackpatchInfoTracker;
    }

#ifdef FEATURE_ON_STACK_REPLACEMENT
public:
    PTR_OnStackReplacementManager GetOnStackReplacementManager();
#endif // FEATURE_ON_STACK_REPLACEMENT

#ifndef DACCESS_COMPILE
public:
    virtual void RegisterDependentHandleToNativeObjectForCleanup(LADependentHandleToNativeObject *dependentHandle) {};
    virtual void UnregisterDependentHandleToNativeObjectFromCleanup(LADependentHandleToNativeObject *dependentHandle) {};
    virtual void CleanupDependentHandlesToNativeObjects() {};
#endif

    friend struct ::cdac_data<LoaderAllocator>;
};  // class LoaderAllocator

template<>
struct cdac_data<LoaderAllocator>
{
    static constexpr size_t ReferenceCount = offsetof(LoaderAllocator, m_cReferences);
    static constexpr size_t HighFrequencyHeap = offsetof(LoaderAllocator, m_pHighFrequencyHeap);
    static constexpr size_t LowFrequencyHeap = offsetof(LoaderAllocator, m_pLowFrequencyHeap);
    static constexpr size_t StubHeap = offsetof(LoaderAllocator, m_pStubHeap);
};

typedef VPTR(LoaderAllocator) PTR_LoaderAllocator;

extern "C" BOOL QCALLTYPE LoaderAllocator_Destroy(QCall::LoaderAllocatorHandle pLoaderAllocator);

class GlobalLoaderAllocator : public LoaderAllocator
{
    friend class LoaderAllocator;
    VPTR_VTABLE_CLASS(GlobalLoaderAllocator, LoaderAllocator)
    VPTR_UNIQUE(VPTRU_LoaderAllocator+1)

    DAC_ALIGNAS(LoaderAllocator) // Align the first member to the alignment of the base class
    BYTE                m_ExecutableHeapInstance[sizeof(LoaderHeap)];

    // Associate memory regions with loader allocator objects
    LockedRangeList     m_memoryAssociations;

protected:
    LoaderAllocatorID m_Id;

public:
    void Init();
    GlobalLoaderAllocator() : LoaderAllocator(false), m_Id(LAT_Global, (void*)1) { LIMITED_METHOD_CONTRACT;};
    virtual LoaderAllocatorID* Id();
    virtual BOOL CanUnload();
};

typedef VPTR(GlobalLoaderAllocator) PTR_GlobalLoaderAllocator;

class ShuffleThunkCache;

class AssemblyLoaderAllocator : public LoaderAllocator
{
    VPTR_VTABLE_CLASS(AssemblyLoaderAllocator, LoaderAllocator)
    VPTR_UNIQUE(VPTRU_LoaderAllocator+3)

protected:
    DAC_ALIGNAS(LoaderAllocator) // Align the first member to the alignment of the base class
    LoaderAllocatorID  m_Id;
    ShuffleThunkCache* m_pShuffleThunkCache;
public:
    virtual LoaderAllocatorID* Id();
    AssemblyLoaderAllocator() : LoaderAllocator(true), m_Id(LAT_Assembly), m_pShuffleThunkCache(NULL)
#if !defined(DACCESS_COMPILE)
        , m_binderToRelease(NULL)
#endif
    { LIMITED_METHOD_CONTRACT; }
    void Init();
    virtual BOOL CanUnload();

    void AddDomainAssembly(DomainAssembly *pDomainAssembly)
    {
        WRAPPER_NO_CONTRACT;
        m_Id.AddDomainAssembly(pDomainAssembly);
    }

    ShuffleThunkCache* GetShuffleThunkCache()
    {
        return m_pShuffleThunkCache;
    }

#if !defined(DACCESS_COMPILE)
    virtual void RegisterHandleForCleanup(OBJECTHANDLE objHandle);
    virtual void RegisterHandleForCleanupLocked(OBJECTHANDLE objHandle);
    virtual void UnregisterHandleFromCleanup(OBJECTHANDLE objHandle);
    virtual void CleanupHandles();
    CustomAssemblyBinder* GetBinder()
    {
        return m_binderToRelease;
    }
    virtual ~AssemblyLoaderAllocator();
    void RegisterBinder(CustomAssemblyBinder* binderToRelease);
    virtual void ReleaseAssemblyLoadContext();
#endif // !defined(DACCESS_COMPILE)

private:
    struct HandleCleanupListItem
    {
        SLink m_Link;
        OBJECTHANDLE m_handle;
        explicit HandleCleanupListItem(OBJECTHANDLE handle)
                :
            m_handle(handle)
        {
        }
    };

    SList<HandleCleanupListItem> m_handleCleanupList;
#if !defined(DACCESS_COMPILE)
    CustomAssemblyBinder* m_binderToRelease;
#endif

private:
    class DependentHandleToNativeObjectHashTraits : public PtrSetSHashTraits<LADependentHandleToNativeObject *> {};
    typedef SHash<DependentHandleToNativeObjectHashTraits> DependentHandleToNativeObjectSet;

    CrstExplicitInit m_dependentHandleToNativeObjectSetCrst;
    DependentHandleToNativeObjectSet m_dependentHandleToNativeObjectSet;

#ifndef DACCESS_COMPILE
public:
    virtual void RegisterDependentHandleToNativeObjectForCleanup(LADependentHandleToNativeObject *dependentHandle);
    virtual void UnregisterDependentHandleToNativeObjectFromCleanup(LADependentHandleToNativeObject *dependentHandle);
    virtual void CleanupDependentHandlesToNativeObjects();
#endif
};

typedef VPTR(AssemblyLoaderAllocator) PTR_AssemblyLoaderAllocator;

#ifndef DACCESS_COMPILE
class LOADERHANDLEHolder
{
    LOADERHANDLE _handle;
    LoaderAllocator* _pLoaderAllocator;

public:

    LOADERHANDLEHolder(LOADERHANDLE handle, LoaderAllocator* pLoaderAllocator)
    {
        _handle = handle;
        _pLoaderAllocator = pLoaderAllocator;
        _ASSERTE(_pLoaderAllocator != NULL);
    }

    LOADERHANDLEHolder(const LOADERHANDLEHolder&) = delete;

    LOADERHANDLE GetValue() const
    {
        return _handle;
    }

    void SuppressRelease()
    {
        _pLoaderAllocator = NULL;
    }

    ~LOADERHANDLEHolder()
    {
        if (_pLoaderAllocator != NULL)
            _pLoaderAllocator->FreeHandle(_handle);
    }
};
#endif

#include "loaderallocator.inl"

#endif //  __LoaderAllocator_h__


// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "Crst.h"
#include "cdacdata.h"

class Thread;
class CLREventStatic;
class RuntimeInstance;
class Array;
typedef DPTR(RuntimeInstance) PTR_RuntimeInstance;

enum class TrapThreadsFlags
{
    None = 0,
    TrapThreads = 1,
};

// The GC polls in managed code read the trap flags, and they can be very frequent on all the threads, in particular in
// signalless mode. The flags have a cache line of their own, so that writes to other data do not keep evicting them
// from the caches of the polling threads. 128 bytes cover the cache lines of Apple Silicon and the pairs of lines that
// the spatial prefetchers of x64 CPUs fetch together. Assembly helpers and compiled code use the address of
// RhpTrapThreads as the address of the flags.
struct alignas(128) TrapThreadsFlagsCacheLine
{
    volatile uint32_t Value;
};

class ThreadStore
{
    friend class RuntimeInstance;
    friend struct ::cdac_data<ThreadStore>;

    SList<Thread>       m_ThreadList;
    PTR_RuntimeInstance m_pRuntimeInstance;
    Crst                m_Lock;

private:
    ThreadStore();

public:
    SPTR_DECL(ThreadStore, s_pThreadStore);
    void                    LockThreadStore();
    void                    UnlockThreadStore();

public:
    class Iterator
    {
        PTR_Thread                      m_pCurrentPosition;
    public:
        Iterator();
        ~Iterator();
        PTR_Thread GetNext();
    };

    ~ThreadStore();
    static ThreadStore *    Create(RuntimeInstance * pRuntimeInstance);
    static Thread *         RawGetCurrentThread();
    static Thread *         GetCurrentThread();
    static Thread *         GetCurrentThreadIfAvailable();
#if defined(TARGET_UNIX) && !defined(TARGET_WASM)
    static Thread *         GetCurrentThreadIfAvailableAsyncSafe();
#endif
    static PTR_Thread       GetSuspendingThread();
    static void             AttachCurrentThread();
    static void             AttachCurrentThread(bool fAcquireThreadStoreLock);
    static void             DetachCurrentThread();
#ifndef DACCESS_COMPILE
    static void             SaveCurrentThreadOffsetForDAC();
#endif

    void        Destroy();
    void        SuspendAllThreads(bool waitForGCEvent);
    void        ResumeAllThreads(bool waitForGCEvent);

    static bool IsTrapThreadsRequested();

    // Signalless mode: the runtime never interrupts a thread asynchronously to suspend it. It neither injects
    // activations (signals or APCs) nor hijacks return addresses, so threads running managed code stop only at GC
    // polls. The compiler inserts them into the code that it compiles for this mode (ILC --signalless), which it
    // records in the configuration embedded in the binary.
    static bool IsSignalless();
    static void InitializeSignalless();

private:
    static bool s_isSignalless;
};
typedef DPTR(ThreadStore) PTR_ThreadStore;

template<> struct cdac_data<ThreadStore>
{
    static constexpr size_t FirstThreadLink = offsetof(ThreadStore, m_ThreadList);
};

ThreadStore * GetThreadStore();

#define FOREACH_THREAD(p_thread_name)                       \
{                                                           \
    ThreadStore::Iterator __threads;                        \
    Thread * p_thread_name;                                 \
    while ((p_thread_name = __threads.GetNext()) != NULL)   \
    {                                                       \

#define END_FOREACH_THREAD  \
    }                       \
}

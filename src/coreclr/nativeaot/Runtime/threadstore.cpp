// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "common.h"
#include "gcenv.h"
#include "gcheaputilities.h"
#include "CommonTypes.h"
#include "CommonMacros.h"
#include "daccess.h"
#include "PalLimitedContext.h"
#include "Pal.h"
#include "rhassert.h"
#include "slist.h"
#include "regdisplay.h"
#include "StackFrameIterator.h"
#include "thread.h"
#include "holder.h"
#include "rhbinder.h"
#include "threadstore.h"
#include "threadstore.inl"
#include "thread.inl"
#include "RuntimeInstance.h"
#include "TargetPtrs.h"
#include "yieldprocessornormalized.h"
#include <minipal/time.h>
#include <minipal/thread.h>
#include "SignalSafeThreadMap.h"

#if defined(__KOS__)
// TODO-KOS(11): no API to interrupt or inspect another thread, so an opt-in watchdog reports what holds up a GC
// A GC waits until every thread reaches a safe point by itself, and a program has no API to read the registers of its
// other threads. With DOTNET_KOS_WATCHDOG=1 the runtime reports on stderr: from the suspending thread, a suspension
// still waiting after 10 s (the threads not yet suspended; a count again at 70 s and 130 s); from a watchdog thread
// outside the runtime, the thread store lock found busy at every 15 s sample for 120 s (the suspending thread's and
// the GC threads' stacks), 120 s without an allocation by any thread (every thread's state), free memory crossing
// 512, 256, 128 and 64 MiB, and at exit. A stack is the frame-record chains found in the top 128 KB of its memory; the
// lowest may be stale. Every report gives the address of minipal_lowres_ticks, which places the binary for
// symbolizing. Unset, it costs a pthread_once check per thread attach (the first reads the
// variable) and a flag test per suspension. Distilled from the trace that found the SDK 2.0 GC stalls' causes
// (2026-10-21).
#include <coresrv/stat/stat_api.h>
#include <errno.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>

static bool s_kosWatchdog = false; // set during the first thread's attach, never changed after
static pthread_once_t s_kosWatchdogOnce = PTHREAD_ONCE_INIT;

static void KosWatchdogWrite(const char* text)
{
    size_t length = strlen(text);
    while (length > 0)
    {
        ssize_t written = write(2, text, length);
        if (written <= 0)
            return;
        text += written;
        length -= (size_t)written;
    }
}

static void* KosWatchdogAnchor()
{
    return (void*)&minipal_lowres_ticks;
}

static int64_t KosWatchdogFreeMiB()
{
    rtl_int64_t pages = -1;
    if (KnGroupStatGetParam(GROUP_PARAM_MEM_FREE, &pages) != 0 || pages < 0)
        return -1;
    return (int64_t)(pages * (rtl_int64_t)sysconf(_SC_PAGESIZE) / (1024 * 1024));
}

static volatile int64_t s_kosWatchdogFreeMiB = -1;
static volatile int64_t s_kosWatchdogMinFreeMiB = -1;
static volatile int64_t s_kosWatchdogMinFreeAtS = 0;

static void KosWatchdogWriteMemory(const char* when)
{
    char line[200];
    snprintf(line, sizeof(line), "KOS-WATCHDOG memory (%s): free %lld MiB, lowest %lld MiB at %lld s; anchor %p\n", when,
             (long long)s_kosWatchdogFreeMiB, (long long)s_kosWatchdogMinFreeMiB, (long long)s_kosWatchdogMinFreeAtS,
             KosWatchdogAnchor());
    KosWatchdogWrite(line);
}

static void KosWatchdogAtExit()
{
    s_kosWatchdogFreeMiB = KosWatchdogFreeMiB();
    KosWatchdogWriteMemory("exit");
}

// The frame-record chains in the top 128 KB of another thread's stack, read from this thread (the same address space).
// A record is a 16-byte-aligned slot holding a higher frame pointer within the stack and, beside it, an address within
// 256 MB of this binary's code. Prints, from the lowest address up, the first five chains of at least four records,
// a record already printed as part of a chain starting none.
static void KosWatchdogScanStack(unsigned long long tid, uintptr_t low, uintptr_t high)
{
    const uintptr_t ScanBytes = 128 * 1024;
    uintptr_t code = (uintptr_t)KosWatchdogAnchor();
    uintptr_t codeLow = code - 0x10000000;
    uintptr_t codeHigh = code + 0x10000000;
    uintptr_t start = high - low > ScanBytes ? high - ScanBytes : low;
    start = (start + 15) & ~(uintptr_t)15;

    auto isRecord = [&](uintptr_t f) -> bool
    {
        if (f < start || f + 16 > high || (f & 15) != 0)
            return false;
        uintptr_t next = ((uintptr_t*)f)[0];
        uintptr_t ra = ((uintptr_t*)f)[1];
        return next > f && next < high && (next & 15) == 0 && ra >= codeLow && ra < codeHigh;
    };

    char line[1024];
    uintptr_t members[5 * 32];
    int memberCount = 0;
    int printed = 0;
    for (uintptr_t f = start; f + 16 <= high && printed < 5; f += 16)
    {
        if (!isRecord(f))
            continue;
        bool seen = false;
        for (int i = 0; i < memberCount; i++)
        {
            if (members[i] == f)
            {
                seen = true;
                break;
            }
        }
        if (seen)
            continue;
        int length = 0;
        for (uintptr_t g = f; isRecord(g) && length < 64; g = ((uintptr_t*)g)[0])
            length++;
        if (length < 4)
            continue;
        int used = snprintf(line, sizeof(line), "KOS-WATCHDOG   stack of tid %llu, record %p (%d frames):", tid, (void*)f,
                            length);
        int frames = 0;
        for (uintptr_t g = f; isRecord(g) && frames < 32 && used > 0 && used < (int)sizeof(line) - 24;
             g = ((uintptr_t*)g)[0])
        {
            used += snprintf(line + used, sizeof(line) - used, " %p", (void*)((uintptr_t*)g)[1]);
            if (memberCount < (int)(sizeof(members) / sizeof(members[0])))
                members[memberCount++] = g;
            frames++;
        }
        snprintf(line + used, sizeof(line) - used, "\n");
        KosWatchdogWrite(line);
        printed++;
    }
    if (printed == 0)
    {
        snprintf(line, sizeof(line), "KOS-WATCHDOG   stack of tid %llu: no chain in [%p,%p)\n", tid, (void*)start,
                 (void*)high);
        KosWatchdogWrite(line);
    }
}

static void KosWatchdogStart()
{
    const char* setting = getenv("DOTNET_KOS_WATCHDOG");
    if (setting == NULL || strcmp(setting, "1") != 0)
        return;
    s_kosWatchdog = true;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_t thread;
    if (pthread_create(&thread, &attr, ThreadStore::KosWatchdog, NULL) != 0)
    {
        KosWatchdogWrite("KOS-WATCHDOG: pthread_create failed\n");
    }
    pthread_attr_destroy(&attr);
}
#endif

EXTERN_C volatile uint32_t RhpTrapThreads;
volatile uint32_t RhpTrapThreads = (uint32_t)TrapThreadsFlags::None;

GVAL_IMPL_INIT(PTR_Thread, RhpSuspendingThread, 0);

SPTR_IMPL(ThreadStore, ThreadStore, s_pThreadStore);

ThreadStore * GetThreadStore()
{
    return GetRuntimeInstance()->GetThreadStore();
}

ThreadStore::Iterator::Iterator() :
    m_pCurrentPosition(GetThreadStore()->m_ThreadList.GetHead())
{
    // GC threads may access threadstore without locking as
    // the lock taken during suspension effectively held by the entire GC.
    // Others must take a lock.
    ASSERT(GetThreadStore()->m_Lock.OwnedByCurrentThread() ||
        (ThreadStore::GetCurrentThread()->IsGCSpecial() && GCHeapUtilities::IsGCInProgress()));
}

ThreadStore::Iterator::~Iterator()
{
}

PTR_Thread ThreadStore::Iterator::GetNext()
{
    PTR_Thread pResult = m_pCurrentPosition;
    if (NULL != pResult)
        m_pCurrentPosition = pResult->m_pNext;
    return pResult;
}

//static
PTR_Thread ThreadStore::GetSuspendingThread()
{
    return (RhpSuspendingThread);
}

#ifndef DACCESS_COMPILE


ThreadStore::ThreadStore() :
    m_ThreadList(),
    m_Lock(CrstThreadStore)
{
    SaveCurrentThreadOffsetForDAC();
}

ThreadStore::~ThreadStore()
{
    m_Lock.Destroy();
}

// static
ThreadStore * ThreadStore::Create(RuntimeInstance * pRuntimeInstance)
{
    NewHolder<ThreadStore> pNewThreadStore = new (nothrow) ThreadStore();
    if (NULL == pNewThreadStore)
        return NULL;

    pNewThreadStore->m_pRuntimeInstance = pRuntimeInstance;

    pNewThreadStore.SuppressRelease();
    return pNewThreadStore;
}

void ThreadStore::Destroy()
{
    delete this;
}

// static
void ThreadStore::AttachCurrentThread(bool fAcquireThreadStoreLock)
{
    //
    // step 1: ThreadStore::InitCurrentThread
    // step 2: add this thread to the ThreadStore
    //

    // The thread has been constructed, during which some data is initialized (like which RuntimeInstance the
    // thread belongs to), but it hasn't been added to the thread store because doing so takes a lock, which
    // we want to avoid at construction time because the loader lock is held then.
    Thread * pAttachingThread = RawGetCurrentThread();

#if defined(__KOS__)
    pthread_once(&s_kosWatchdogOnce, KosWatchdogStart); // TODO-KOS(11): starts the opt-in GC watchdog once
#endif

    if (pAttachingThread->IsDetached())
    {
        ASSERT_UNCONDITIONALLY("Attempt to execute managed code after the .NET runtime thread state has been destroyed.");
        RhFailFast();
    }

    // The thread was already initialized, so it is already attached.
    if (pAttachingThread->IsInitialized())
    {
        ASSERT((pAttachingThread->m_ThreadStateFlags & Thread::TSF_Attached) != 0);
        return;
    }

    PalAttachThread(pAttachingThread);

    //
    // Init the thread buffer
    //
    pAttachingThread->Construct();
    ASSERT(pAttachingThread->m_ThreadStateFlags == Thread::TSF_Unknown);

    // fAcquireThreadStoreLock is false when threads are created/attached for GC purpose
    // in such case the lock is already held and GC takes care to ensure safe access to the threadstore

    ThreadStore* pTS = GetThreadStore();
    CrstHolderWithState threadStoreLock(&pTS->m_Lock, fAcquireThreadStoreLock);

    //
    // Set thread state to be attached
    //
    ASSERT(pAttachingThread->m_ThreadStateFlags == Thread::TSF_Unknown);
    pAttachingThread->m_ThreadStateFlags = Thread::TSF_Attached;

    pTS->m_ThreadList.InsertHead(pAttachingThread);

#if defined(TARGET_UNIX) && !defined(TARGET_WASM)
    if (!InsertThreadIntoSignalSafeMap(pAttachingThread->m_threadId, pAttachingThread))
    {
        PalPrintFatalError("\nFailed to insert thread into signal-safe map due to out of memory.\n");
        RhFailFast();
    }
#endif // TARGET_UNIX && !TARGET_WASM
}

// static
void ThreadStore::AttachCurrentThread()
{
    AttachCurrentThread(true);
}

void ThreadStore::DetachCurrentThread()
{
    // The thread may not have been initialized because it may never have run managed code before.
    Thread * pDetachingThread = RawGetCurrentThread();

    // The thread was not initialized yet, so it was not attached
    if (!pDetachingThread->IsInitialized())
    {
        return;
    }

    // detach callback should not call us twice
    ASSERT(!pDetachingThread->IsDetached());

    // Run pre-mortem callbacks while we still can run managed code and not holding locks.
    // NOTE: background GC threads are attached/suspendable threads, but should not run ordinary
    // managed code. Make sure that does not happen here.
    if (g_threadExitCallback != NULL && !pDetachingThread->IsGCSpecial())
    {
        g_threadExitCallback();
    }

    // we will be taking the threadstore lock and need to be in preemptive mode.
    ASSERT(!pDetachingThread->IsCurrentThreadInCooperativeMode());

    // The following makes the thread no longer able to run managed code or participate in GC.
    // We need to hold threadstore lock while doing that.
    {
        ThreadStore* pTS = GetThreadStore();
        // Note that when process is shutting down, the threads may be rudely terminated,
        // possibly while holding the threadstore lock. That is ok, since the process is being torn down.
        CrstHolder threadStoreLock(&pTS->m_Lock);
        // remove the thread from the list of managed threads.
        bool removed = pTS->m_ThreadList.RemoveFirst(pDetachingThread);
        ASSERT(removed);
        // tidy up GC related stuff (release allocation context, etc..)
        pDetachingThread->Detach();
#if defined(TARGET_UNIX) && !defined(TARGET_WASM)
        RemoveThreadFromSignalSafeMap(pDetachingThread->m_threadId, pDetachingThread);
#endif
    }

    // post-mortem clean up.
    pDetachingThread->Destroy();
}

// Used by GC to prevent new threads during a GC and
// to ensure that only one thread performs suspension.
void ThreadStore::LockThreadStore()
{
    // the thread should not be in coop mode when taking the threadstore lock.
    // this is required to avoid deadlocks if suspension is in progress.
    bool wasCooperative = false;
    Thread* pThisThread = GetCurrentThreadIfAvailable();
    if (pThisThread && pThisThread->IsCurrentThreadInCooperativeMode())
    {
        wasCooperative = true;
        pThisThread->EnablePreemptiveMode();
    }

    m_Lock.Enter();

    if (wasCooperative)
    {
        // we just got the lock thus EE can't be suspending, so no waiting here
        pThisThread->DisablePreemptiveMode();
    }
}

void ThreadStore::UnlockThreadStore()
{
    m_Lock.Leave();
}

#if defined(__KOS__)
// TODO-KOS(11): the watchdog thread, never attached to the runtime, so no GC stops it
// Every 15 s it samples free memory, then try-locks the thread store. Found busy at every sample for 120 s, it reports
// the suspending thread and the GC threads without holding the lock: the holder can release it meanwhile, a thread
// can then exit and free its Thread (thread-local) and stack, and GC threads attach while the GC holds the lock, so
// the walk can read freed memory and a fault there ends the process (a risk taken only in a stall). If it gets the
// lock, it fingerprints every thread's allocation context, and after 120 s without a change (and again at 300 s)
// reports every thread, writing only after releasing the lock.
void* ThreadStore::KosWatchdog(void* unused)
{
    (void)unused;
    const int PeriodS = 15;
    const int64_t FirstReportMs = 120000;
    const int64_t SecondDumpMs = 300000;
    const int64_t Thresholds[] = { 512, 256, 128, 64 };
    static char text[32768];
    ThreadStore* pTS = GetThreadStore();
    // CrstStatic's only member in a release build is a minipal_mutex, whose only member is a pthread_mutex_t.
    pthread_mutex_t* lock = (pthread_mutex_t*)&pTS->m_Lock;
    uint64_t lastSignature = 0;
    int64_t started = minipal_lowres_ticks();
    int64_t unchangedSince = started;
    int64_t busySince = 0;
    int64_t busyReportedAt = 0;
    unsigned busySinceGcCount = 0; // a count unchanged at the report: one GC held the lock throughout
    int busyReports = 0;
    int dumps = 0;
    int thresholdsCrossed = 0;
    atexit(KosWatchdogAtExit);

    for (;;)
    {
        struct timespec pause = { PeriodS, 0 };
        while (nanosleep(&pause, &pause) < 0 && errno == EINTR)
        {
        }
        int64_t now = minipal_lowres_ticks();

        int64_t freeMiB = KosWatchdogFreeMiB();
        s_kosWatchdogFreeMiB = freeMiB;
        if (freeMiB >= 0 && (s_kosWatchdogMinFreeMiB < 0 || freeMiB < s_kosWatchdogMinFreeMiB))
        {
            s_kosWatchdogMinFreeMiB = freeMiB;
            s_kosWatchdogMinFreeAtS = (now - started) / 1000;
        }
        if (thresholdsCrossed < 4 && freeMiB >= 0 && freeMiB < Thresholds[thresholdsCrossed])
        {
            while (thresholdsCrossed < 4 && freeMiB < Thresholds[thresholdsCrossed])
                thresholdsCrossed++;
            KosWatchdogWriteMemory("below a threshold");
        }

        if (pthread_mutex_trylock(lock) != 0)
        {
            if (busySince == 0)
            {
                busySince = now;
                busySinceGcCount = (unsigned)GCHeapUtilities::GetGCHeap()->GetGcCount();
            }
            else if (now - busySince >= FirstReportMs && (busyReports == 0 || now - busyReportedAt >= FirstReportMs) &&
                     busyReports < 6)
            {
                char line[300];
                IGCHeap* heap = GCHeapUtilities::GetGCHeap();
                snprintf(line, sizeof(line),
                         "KOS-WATCHDOG: thread store lock busy at every sample for %lld s; trap 0x%x, suspending thread "
                         "%p; gc count %u (%u at the first busy sample), in progress %d, concurrent in progress %d; "
                         "anchor %p\n",
                         (long long)((now - busySince) / 1000), (unsigned)RhpTrapThreads, (void*)RhpSuspendingThread,
                         (unsigned)heap->GetGcCount(), busySinceGcCount, GCHeapUtilities::IsGCInProgress() ? 1 : 0,
                         heap->IsConcurrentGCInProgress() ? 1 : 0, KosWatchdogAnchor());
                KosWatchdogWrite(line);
                KosWatchdogWriteMemory("lock busy");
                FOREACH_THREAD(pThread)
                {
                    if (pThread == RhpSuspendingThread || pThread->IsGCSpecial())
                    {
                        snprintf(line, sizeof(line), "KOS-WATCHDOG   thread %p tid %llu%s%s flags 0x%x frame %p\n",
                                 (void*)pThread, (unsigned long long)pThread->m_threadId,
                                 pThread == RhpSuspendingThread ? " suspending" : "",
                                 pThread->IsGCSpecial() ? " gcspecial" : "", (unsigned)pThread->m_ThreadStateFlags,
                                 (void*)VolatileLoadWithoutBarrier(&pThread->m_pTransitionFrame));
                        KosWatchdogWrite(line);
                        KosWatchdogScanStack((unsigned long long)pThread->m_threadId,
                                             (uintptr_t)pThread->m_pStackLow, (uintptr_t)pThread->m_pStackHigh);
                    }
                }
                END_FOREACH_THREAD
                busyReportedAt = now;
                busyReports++;
            }
            continue;
        }
        busySince = 0;
        busyReports = 0;

        uint64_t signature = 0;
        int count = 0;
        FOREACH_THREAD(pThread)
        {
            gc_alloc_context* ac = pThread->m_eeAllocContext.GetGCAllocContext();
            signature = signature * 31 + (uint64_t)(uintptr_t)ac->alloc_ptr + (uint64_t)ac->alloc_bytes;
            count++;
        }
        END_FOREACH_THREAD

        if (signature != lastSignature)
        {
            lastSignature = signature;
            unchangedSince = now;
            dumps = 0;
            pthread_mutex_unlock(lock);
            continue;
        }

        int64_t unchangedMs = now - unchangedSince;
        if (!((dumps == 0 && unchangedMs >= FirstReportMs) || (dumps == 1 && unchangedMs >= SecondDumpMs)))
        {
            pthread_mutex_unlock(lock);
            continue;
        }
        dumps++;

        struct { unsigned long long tid; uintptr_t low; uintptr_t high; } coop[16];
        int coopCount = 0;
        size_t used = 0;
#define KOS_WATCHDOG_APPEND(...)                                                                         \
        do                                                                                               \
        {                                                                                                \
            if (used < sizeof(text))                                                                     \
            {                                                                                            \
                int n = snprintf(text + used, sizeof(text) - used, __VA_ARGS__);                         \
                if (n > 0)                                                                               \
                    used = used + (size_t)n < sizeof(text) ? used + (size_t)n : sizeof(text) - 1;        \
            }                                                                                            \
        } while (0)

        KOS_WATCHDOG_APPEND("KOS-WATCHDOG: dump %d: no allocation for %lld s, %d threads, trap 0x%x, suspending "
                            "thread %p; anchor %p\n",
                            dumps, (long long)(unchangedMs / 1000), count, (unsigned)RhpTrapThreads,
                            (void*)RhpSuspendingThread, KosWatchdogAnchor());
        FOREACH_THREAD(pThread)
        {
            PInvokeTransitionFrame* frame = VolatileLoadWithoutBarrier(&pThread->m_pTransitionFrame);
            uintptr_t low = (uintptr_t)pThread->m_pStackLow;
            uintptr_t high = (uintptr_t)pThread->m_pStackHigh;
            KOS_WATCHDOG_APPEND("KOS-WATCHDOG   thread %p tid %llu flags 0x%x stack [%p,%p) gcspecial %d", (void*)pThread,
                                (unsigned long long)pThread->m_threadId, (unsigned)pThread->m_ThreadStateFlags,
                                (void*)low, (void*)high, pThread->IsGCSpecial() ? 1 : 0);
            if (frame == NULL)
            {
                KOS_WATCHDOG_APPEND(" cooperative (no transition frame)\n");
                if (coopCount < (int)(sizeof(coop) / sizeof(coop[0])))
                {
                    coop[coopCount].tid = (unsigned long long)pThread->m_threadId;
                    coop[coopCount].low = low;
                    coop[coopCount].high = high;
                    coopCount++;
                }
                continue;
            }
            if (frame == TOP_OF_STACK_MARKER)
            {
                KOS_WATCHDOG_APPEND(" top-of-stack marker\n");
                continue;
            }
            KOS_WATCHDOG_APPEND(" frame %p ip %p fp %p chain", (void*)frame, (void*)frame->m_RIP,
                                (void*)frame->m_FramePointer);
            uintptr_t fp = (uintptr_t)frame->m_FramePointer;
            for (int i = 0; i < 16; i++)
            {
                if (fp < low || fp + 2 * sizeof(uintptr_t) > high || (fp & 7) != 0)
                    break;
                uintptr_t* record = (uintptr_t*)fp;
                KOS_WATCHDOG_APPEND(" %p", (void*)record[1]);
                if (record[0] <= fp)
                    break;
                fp = record[0];
            }
            KOS_WATCHDOG_APPEND("\n");
        }
        END_FOREACH_THREAD
#undef KOS_WATCHDOG_APPEND
        pthread_mutex_unlock(lock);

        KosWatchdogWrite(text);
        KosWatchdogWriteMemory("no allocation");
        // After the unlock: a thread that exits meanwhile has its stack freed (a risk taken only in a stall).
        for (int i = 0; i < coopCount; i++)
        {
            KosWatchdogScanStack(coop[i].tid, coop[i].low, coop[i].high);
        }
        char line[80];
        snprintf(line, sizeof(line), "KOS-WATCHDOG: dump %d end\n", dumps);
        KosWatchdogWrite(line);
    }
    return NULL;
}
#endif

void ThreadStore::SuspendAllThreads(bool waitForGCEvent)
{
    Thread * pThisThread = GetCurrentThreadIfAvailable();
#if defined(__KOS__)
    // TODO-KOS(11): with DOTNET_KOS_WATCHDOG=1, a suspension that waits 10 s names the threads it waits for (from this,
    // the suspending thread), gives a count again at 70 s and 130 s, and its length when it ends. Each write is an IPC
    // call to the VFS program serving stderr, so a report adds its own time to the suspension it reports.
    int64_t kosWatchdogStart = s_kosWatchdog ? minipal_lowres_ticks() : 0;
    int64_t kosWatchdogNext = kosWatchdogStart + 10000;
    int kosWatchdogReports = 0;
#endif
    RhpSuspendingThread = pThisThread;

    if (waitForGCEvent)
    {
        GCHeapUtilities::GetGCHeap()->ResetWaitForGCEvent();
    }

    // set the global trap for pinvoke leave and return
    GCHeapUtilities::GetGCHeap()->SetSuspensionPending(true);
    RhpTrapThreads |= (uint32_t)TrapThreadsFlags::TrapThreads;

    // Our lock-free algorithm depends on flushing write buffers of all processors running RH code.  The
    // reason for this is that we essentially implement Dekker's algorithm, which requires write ordering.
    minipal_memory_barrier_process_wide();

    int prevRemaining = INT32_MAX;
    bool observeOnly = true;
    uint32_t rehijackDelay = 8;
    uint32_t usecsSinceYield = 0;

    while(true)
    {
        int remaining = 0;
        FOREACH_THREAD(pTargetThread)
        {
            if (pTargetThread == pThisThread)
                continue;

            if (!pTargetThread->CacheTransitionFrameForSuspend())
            {
                remaining++;
#ifdef FEATURE_HIJACK
                if (!observeOnly)
                {
                    pTargetThread->Hijack();
                }
#endif // FEATURE_HIJACK
            }
        }
        END_FOREACH_THREAD

#if defined(__KOS__)
        if (s_kosWatchdog)
        {
            int64_t kosWatchdogNow = minipal_lowres_ticks();
            char line[400];
            if (remaining == 0)
            {
                if (kosWatchdogReports > 0)
                {
                    snprintf(line, sizeof(line), "KOS-WATCHDOG: suspension done after %lld ms; anchor %p\n",
                             (long long)(kosWatchdogNow - kosWatchdogStart), KosWatchdogAnchor());
                    KosWatchdogWrite(line);
                }
            }
            else if (kosWatchdogNow >= kosWatchdogNext && kosWatchdogReports < 3)
            {
                snprintf(line, sizeof(line),
                         "KOS-WATCHDOG: suspension waiting %lld ms for %d thread(s); suspender tid %llu; anchor %p\n",
                         (long long)(kosWatchdogNow - kosWatchdogStart), remaining,
                         pThisThread != NULL ? (unsigned long long)pThisThread->m_threadId : 0ULL, KosWatchdogAnchor());
                KosWatchdogWrite(line);
                if (kosWatchdogReports == 0)
                {
                    FOREACH_THREAD(pWaitedFor)
                    {
                        if (pWaitedFor == pThisThread || pWaitedFor->m_pCachedTransitionFrame != NULL)
                            continue;
                        snprintf(line, sizeof(line),
                                 "KOS-WATCHDOG   not suspended: tid %llu flags 0x%x gcspecial %d stack [%p,%p)\n",
                                 (unsigned long long)pWaitedFor->m_threadId, (unsigned)pWaitedFor->m_ThreadStateFlags,
                                 pWaitedFor->IsGCSpecial() ? 1 : 0, pWaitedFor->m_pStackLow, pWaitedFor->m_pStackHigh);
                        KosWatchdogWrite(line);
                        KosWatchdogScanStack((unsigned long long)pWaitedFor->m_threadId,
                                             (uintptr_t)pWaitedFor->m_pStackLow, (uintptr_t)pWaitedFor->m_pStackHigh);
                    }
                    END_FOREACH_THREAD
                }
                kosWatchdogReports++;
                kosWatchdogNext = kosWatchdogNow + 60000;
            }
        }
#endif
        if (remaining == 0)
            break;

        // if we see progress or have just done a hijacking pass
        // do not hijack in the next iteration
        if (remaining < prevRemaining || !observeOnly)
        {
            // 5 usec delay, then check for more progress
            minipal_microdelay(5, &usecsSinceYield);
            observeOnly = true;
        }
        else
        {
            minipal_microdelay(rehijackDelay, &usecsSinceYield);
            observeOnly = false;

            // double up rehijack delay in case we are rehjacking too often
            // up to 100 usec, as that should be enough to make progress.
            if (rehijackDelay < 100)
            {
                rehijackDelay *= 2;
            }
        }

        prevRemaining = remaining;

        // If we see 1 msec of uninterrupted wait, it is a concern.
        // Since we are stopping threads, there should be free cores to run on. Perhaps
        // some thread that we need to stop needs to run on the same core as ours.
        // Let's yield the timeslice to make sure such threads can run.
        // We will not do this often though, since this can introduce arbitrary delays.
        if (usecsSinceYield > 1000)
        {
            PalSwitchToThread();
            usecsSinceYield = 0;
        }
    }

#if defined(TARGET_ARM) || defined(TARGET_ARM64) || defined(TARGET_LOONGARCH64)
    // Flush the store buffers on all CPUs, to ensure that all changes made so far are seen
    // by the GC threads. This only matters on weak memory ordered processors as
    // the strong memory ordered processors wouldn't have reordered the relevant writes.
    // This is needed to synchronize threads that were running in preemptive mode thus were
    // left alone by suspension to flush their writes that they made before they switched to
    // preemptive mode.
    minipal_memory_barrier_process_wide();
#endif //TARGET_ARM || TARGET_ARM64 || TARGET_LOONGARCH64
}

void ThreadStore::ResumeAllThreads(bool waitForGCEvent)
{
    FOREACH_THREAD(pTargetThread)
    {
        pTargetThread->ResetCachedTransitionFrame();
    }
    END_FOREACH_THREAD

#if defined(TARGET_ARM) || defined(TARGET_ARM64) || defined(TARGET_LOONGARCH64)
        // Flush the store buffers on all CPUs, to ensure that they all see changes made
        // by the GC threads. This only matters on weak memory ordered processors as
        // the strong memory ordered processors wouldn't have reordered the relevant reads.
        // This is needed to synchronize threads that were running in preemptive mode while
        // the runtime was suspended and that will return to cooperative mode after the runtime
        // is restarted.
        minipal_memory_barrier_process_wide();
#endif //TARGET_ARM || TARGET_ARM64 || TARGET_LOONGARCH64

    RhpTrapThreads &= ~(uint32_t)TrapThreadsFlags::TrapThreads;
    GCHeapUtilities::GetGCHeap()->SetSuspensionPending(false);

    RhpSuspendingThread = NULL;
    if (waitForGCEvent)
    {
        GCHeapUtilities::GetGCHeap()->SetWaitForGCEvent();
    }
} // ResumeAllThreads

static_assert(sizeof(Thread) == sizeof(RuntimeThreadLocals));

#ifndef _MSC_VER
PLATFORM_THREAD_LOCAL RuntimeThreadLocals tls_CurrentThread;
#endif

EXTERN_C RuntimeThreadLocals* RhpGetThread()
{
    return &tls_CurrentThread;
}

#if defined(TARGET_UNIX) && !defined(TARGET_WASM)
Thread * ThreadStore::GetCurrentThreadIfAvailableAsyncSafe()
{
    return (Thread*)FindThreadInSignalSafeMap(minipal_get_current_thread_id_no_cache());
}
#endif // TARGET_UNIX && !TARGET_WASM

#endif // !DACCESS_COMPILE

#ifdef _WIN32

#ifndef DACCESS_COMPILE

// Keep a global variable in the target process which contains
// the address of _tls_index.  This is the breadcrumb needed
// by DAC to read _tls_index since we don't control the
// declaration of _tls_index directly.

// volatile to prevent the compiler from removing the unused global variable
volatile uint32_t * p_tls_index;
volatile uint32_t SECTIONREL__tls_CurrentThread;

EXTERN_C uint32_t _tls_index;
#if defined(TARGET_ARM64)
// ARM64TODO: Re-enable optimization
#pragma optimize("", off)
#endif
void ThreadStore::SaveCurrentThreadOffsetForDAC()
{
    p_tls_index = &_tls_index;

    uint8_t * pTls = *(uint8_t **)(PalNtCurrentTeb() + OFFSETOF__TEB__ThreadLocalStoragePointer);

    uint8_t * pOurTls = *(uint8_t **)(pTls + (_tls_index * sizeof(void*)));

    SECTIONREL__tls_CurrentThread = (uint32_t)((uint8_t *)&tls_CurrentThread - pOurTls);
}
#if defined(TARGET_ARM64)
#pragma optimize("", on)
#endif
#else // DACCESS_COMPILE

GPTR_IMPL(uint32_t, p_tls_index);
GVAL_IMPL(uint32_t, SECTIONREL__tls_CurrentThread);

#endif // DACCESS_COMPILE

#else // _WIN32

void ThreadStore::SaveCurrentThreadOffsetForDAC()
{
}

#endif // _WIN32

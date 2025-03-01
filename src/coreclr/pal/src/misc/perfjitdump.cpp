// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ===========================================================================

#if defined(__linux__)
#define JITDUMP_SUPPORTED
#endif

#include "pal/palinternal.h"
#include "pal/dbgmsg.h"

#include <cstddef>

#ifdef JITDUMP_SUPPORTED

#include <fcntl.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/types.h>
#include <sys/stat.h>
#if HAVE_SYS_SYSCALL_H
#include <sys/syscall.h>
#endif
#include <sys/uio.h>
#include <time.h>
#include <unistd.h>
#include <limits.h>

#include "../inc/llvm/ELF.h"

SET_DEFAULT_DEBUG_CHANNEL(MISC);

namespace
{
    enum
    {
        JIT_DUMP_MAGIC = 0x4A695444,
        JIT_DUMP_VERSION = 1,

#if defined(HOST_X86)
        ELF_MACHINE = EM_386,
#elif defined(HOST_ARM)
        ELF_MACHINE = EM_ARM,
#elif defined(HOST_AMD64)
        ELF_MACHINE = EM_X86_64,
#elif defined(HOST_ARM64)
        ELF_MACHINE = EM_AARCH64,
#elif defined(HOST_LOONGARCH64)
        ELF_MACHINE = EM_LOONGARCH,
#elif defined(HOST_RISCV64)
        ELF_MACHINE = EM_RISCV,
#elif defined(HOST_S390X)
        ELF_MACHINE = EM_S390,
#elif defined(HOST_POWERPC64)
	ELF_MACHINE = EM_PPC64,
#else
#error ELF_MACHINE unsupported for target
#endif

        JIT_CODE_LOAD = 0,
    };

    uint64_t GetTimeStampNS()
    {
#if HAVE_CLOCK_MONOTONIC
        struct timespec ts;
        int result = clock_gettime(CLOCK_MONOTONIC, &ts);

        if (result != 0)
        {
            ASSERT("clock_gettime(CLOCK_MONOTONIC) failed: %d\n", result);
            return 0;
        }
        else
        {
            return  ts.tv_sec * 1000000000ULL + ts.tv_nsec;
        }
#else
    #error "The PAL jitdump requires clock_gettime(CLOCK_MONOTONIC) to be supported."
#endif
    }

    struct FileHeader
    {
        FileHeader() :
            magic(JIT_DUMP_MAGIC),
            version(JIT_DUMP_VERSION),
            total_size(sizeof(FileHeader)),
            elf_mach(ELF_MACHINE),
            pad1(0),
            pid(getpid()),
            timestamp(GetTimeStampNS()),
            flags(0)
        {}

        uint32_t magic;
        uint32_t version;
        uint32_t total_size;
        uint32_t elf_mach;
        uint32_t pad1;
        uint32_t pid;
        uint64_t timestamp;
        uint64_t flags;
    };

    struct RecordHeader
    {
        uint32_t id;
        uint32_t total_size;
        uint64_t timestamp;
    };

    struct JitCodeLoadRecord
    {
        JitCodeLoadRecord() :
            pid(getpid()),
#if defined(__KOS__)
            tid(gettid())
#else
            tid(syscall(SYS_gettid))
#endif
        {
            header.id = JIT_CODE_LOAD;
            header.timestamp = GetTimeStampNS();
        }

        RecordHeader header;
        uint32_t pid;
        uint32_t tid;
        uint64_t vma;
        uint64_t code_addr;
        uint64_t code_size;
        uint64_t code_index;
        // Null terminated name
        // Optional native code
    };
};

struct PerfJitDumpState
{
    PerfJitDumpState() :
        enabled(false),
        fd(-1),
        mmapAddr(MAP_FAILED),
        codeIndex(0)
    {}

    volatile bool enabled;
    int fd;
    void *mmapAddr;
    volatile uint64_t codeIndex;

    int FatalError()
    {
        enabled = false;

        if (mmapAddr != MAP_FAILED)
        {
            munmap(mmapAddr, sizeof(FileHeader));
            mmapAddr = MAP_FAILED;
        }

        if (fd != -1)
        {
            close(fd);
            fd = -1;
        }

        return -1;
    }

    int Start(const char* path)
    {
        int result = 0;

        // Write file header
        FileHeader header;

        if (result != 0)
            return FatalError();

        if (enabled)
            goto exit;

        char jitdumpPath[PATH_MAX];

        result = snprintf(jitdumpPath, sizeof(jitdumpPath), "%s/jit-%i.dump", path, getpid());

        if (result >= PATH_MAX)
            return FatalError();

        result = open(jitdumpPath, O_CREAT|O_TRUNC|O_RDWR|O_CLOEXEC, S_IRUSR|S_IWUSR );

        if (result == -1)
            return FatalError();

        fd = result;

        result = write(fd, &header, sizeof(FileHeader));

        if (result == -1)
            return FatalError();

        result = fsync(fd);

        if (result == -1)
            return FatalError();

        // mmap jitdump file
        // this is a marker for perf inject to find the jitdumpfile
        mmapAddr = mmap(nullptr, sizeof(FileHeader), PROT_READ | PROT_EXEC, MAP_PRIVATE, fd, 0);

        if (mmapAddr == MAP_FAILED)
            return FatalError();

        enabled = true;

exit:
        return 0;
    }

    int LogMethod(void* pCode, size_t codeSize, const char* symbol, void* debugInfo, void* unwindInfo)
    {
        int result = 0;

        if (enabled)
        {
            size_t symbolLen = strlen(symbol);

            JitCodeLoadRecord record;

            size_t bytesRemaining = sizeof(JitCodeLoadRecord) + symbolLen + 1 + codeSize;

            record.header.timestamp = GetTimeStampNS();
            record.vma = (uint64_t) pCode;
            record.code_addr = (uint64_t) pCode;
            record.code_size = codeSize;
            record.header.total_size = bytesRemaining;

            iovec items[] = {
                // ToDo insert debugInfo and unwindInfo record items immediately before the JitCodeLoadRecord.
                { &record, sizeof(JitCodeLoadRecord) },
                { (void *)symbol, symbolLen + 1 },
                { pCode, codeSize },
            };
            size_t itemsCount = sizeof(items) / sizeof(items[0]);

            size_t itemsWritten = 0;

            if (result != 0)
                return FatalError();

            if (!enabled)
                goto exit;

            // Increment codeIndex while locked
            record.code_index = ++codeIndex;

            do
            {
                result = writev(fd, items + itemsWritten, itemsCount - itemsWritten);

                if ((size_t)result == bytesRemaining)
                    break;

                if (result == -1)
                {
                    if (errno == EINTR)
                        continue;

                    return FatalError();
                }

                // Detect unexpected failure cases.
                _ASSERTE(bytesRemaining > (size_t)result);
                _ASSERTE(result > 0);

                // Handle partial write case

                bytesRemaining -= result;

                do
                {
                    if ((size_t)result < items[itemsWritten].iov_len)
                    {
                        items[itemsWritten].iov_len -= result;
                        items[itemsWritten].iov_base = (void*)((size_t) items[itemsWritten].iov_base + result);
                        break;
                    }
                    else
                    {
                        result -= items[itemsWritten].iov_len;
                        itemsWritten++;

                        // Detect unexpected failure case.
                        _ASSERTE(itemsWritten < itemsCount);
                    }
                } while (result > 0);
            } while (true);

        }
exit:
        return 0;
    }

    int Finish()
    {
        int result = 0;

        if (enabled)
        {
            enabled = false;

            if (result != 0)
                return FatalError();

            if (!enabled)
                goto exit;

            result = munmap(mmapAddr, sizeof(FileHeader));

            if (result == -1)
                return FatalError();

            mmapAddr = MAP_FAILED;

            result = fsync(fd);

            if (result == -1)
                return FatalError();

            result = close(fd);

            if (result == -1)
                return FatalError();

            fd = -1;
        }
exit:
        return 0;
    }
};


PerfJitDumpState& GetState()
{
    static PerfJitDumpState s;

    return s;
}

int
PALAPI
PAL_PerfJitDump_Start(const char* path)
{
    return GetState().Start(path);
}

bool
PALAPI
PAL_PerfJitDump_IsStarted()
{
    return GetState().enabled;
}

int
PALAPI
PAL_PerfJitDump_LogMethod(void* pCode, size_t codeSize, const char* symbol, void* debugInfo, void* unwindInfo)
{
    return GetState().LogMethod(pCode, codeSize, symbol, debugInfo, unwindInfo);
}

int
PALAPI
PAL_PerfJitDump_Finish()
{
    return GetState().Finish();
}

#else // JITDUMP_SUPPORTED

int
PALAPI
PAL_PerfJitDump_Start(const char* path)
{
    return 0;
}

bool
PALAPI
PAL_PerfJitDump_IsStarted()
{
    return false;
}

int
PALAPI
PAL_PerfJitDump_LogMethod(void* pCode, size_t codeSize, const char* symbol, void* debugInfo, void* unwindInfo)
{
    return 0;
}

int
PALAPI
PAL_PerfJitDump_Finish()
{
    return 0;
}

#endif // JITDUMP_SUPPORTED

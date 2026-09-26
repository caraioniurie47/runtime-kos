// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "pal_config.h"
#include "pal_memory.h"

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#if defined(__KOS__)
    // KOS-NOT-LINUX: no malloc size query (malloc_usable_size)
    // KOS libc has no malloc size query. Only AlignedRealloc needs one, and the Aligned* exports are
    // only used with each other, so they keep the base pointer and the size in a header in front of
    // the aligned block.
    typedef struct
    {
        void* base;
        uintptr_t size;
    } KosAlignedHeader;
    #define KOS_ALIGNED_HEADER(p) ((KosAlignedHeader*)((uintptr_t)(p) - sizeof(KosAlignedHeader)))
    #define MALLOC_SIZE(s) ((s) == NULL ? 0 : KOS_ALIGNED_HEADER(s)->size)
#elif HAVE_MALLOC_SIZE
    #include <malloc/malloc.h>
    #define MALLOC_SIZE(s) malloc_size(s)
#elif HAVE_MALLOC_USABLE_SIZE
    #include <malloc.h>
    #define MALLOC_SIZE(s) malloc_usable_size(s)
#elif HAVE_MALLOC_USABLE_SIZE_NP
    #include <malloc_np.h>
    #define MALLOC_SIZE(s) malloc_usable_size(s)
#endif

// These functions look like simple wrappers around the standard C library functions, but they are actually
// exports to managed code and not allocation functions for unmanaged code. Therefore we suppress the warnings.
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wallocator-wrappers"
#endif // __clang__

void* SystemNative_AlignedAlloc(uintptr_t alignment, uintptr_t size)
{
#if defined(__KOS__)
    // alignment is a power of two (checked by NativeMemory.AlignedAlloc).
    if (size > UINTPTR_MAX - alignment - sizeof(KosAlignedHeader))
    {
        return NULL;
    }
    void* base = malloc(size + alignment + sizeof(KosAlignedHeader));
    if (base == NULL)
    {
        return NULL;
    }
    // At least sizeof(KosAlignedHeader) past base, so the header fits; the header is itself
    // pointer-aligned because malloc's result and (for alignments of 8 and up) the block are.
    uintptr_t aligned = ((uintptr_t)base + sizeof(KosAlignedHeader) + alignment - 1) & ~(alignment - 1);
    KOS_ALIGNED_HEADER(aligned)->base = base;
    KOS_ALIGNED_HEADER(aligned)->size = size;
    return (void*)aligned;
#elif HAVE_ALIGNED_ALLOC
    // We want to prefer the standardized aligned_alloc function.
    return aligned_alloc(alignment, size);
#elif HAVE_POSIX_MEMALIGN
    void* result = NULL;
    posix_memalign(&result, alignment, size);
    return result;
#else
    #error "Platform doesn't support aligned_alloc or posix_memalign"
#endif
}

void SystemNative_AlignedFree(void* ptr)
{
#if defined(__KOS__)
    if (ptr != NULL)
    {
        free(KOS_ALIGNED_HEADER(ptr)->base);
    }
#else
    free(ptr);
#endif
}

void* SystemNative_AlignedRealloc(void* ptr, uintptr_t alignment, uintptr_t new_size)
{
    void* result = SystemNative_AlignedAlloc(alignment, new_size);

    if (result != NULL)
    {
#ifdef MALLOC_SIZE
        uintptr_t old_size = MALLOC_SIZE(ptr);
        assert((ptr != NULL) || (old_size == 0));

        uintptr_t size_to_copy = (new_size < old_size) ? new_size : old_size;
#else
        // Less efficient implementation for platforms that do not provide MALLOC_SIZE.
        ptr = realloc(ptr, new_size);
        if (ptr == NULL)
        {
            SystemNative_AlignedFree(result);
            return NULL;
        }
        uintptr_t size_to_copy = new_size;
#endif

        memcpy(result, ptr, size_to_copy);
        SystemNative_AlignedFree(ptr);
    }

    return result;
}

void* SystemNative_Calloc(uintptr_t num, uintptr_t size)
{
    return calloc(num, size);
}

void SystemNative_Free(void* ptr)
{
    free(ptr);
}

void* SystemNative_Malloc(uintptr_t size)
{
    return malloc(size);
}

void* SystemNative_Realloc(void* ptr, uintptr_t new_size)
{
    return realloc(ptr, new_size);
}

#if defined(__clang__)
#pragma clang diagnostic pop
#endif // __clang__

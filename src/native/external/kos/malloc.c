#include <malloc.h>
#include <string.h>
#include <kos/alloc.h>

void *malloc(size_t n)
{
    return KosMemAlloc(n);
}

void *calloc(size_t m, size_t n)
{
    return KosMemZalloc(m * n);
}

void *realloc(void *p, size_t n)
{
    size_t psize = malloc_usable_size(p);
    if (n <= psize)
        return p;

    void *newptr = malloc(n);
    memcpy(newptr, p, psize);
    free(p);
    return newptr;
}

void free(void *p)
{
    KosMemFree(p);
}

size_t malloc_usable_size(void *p)
{
    return KosMemGetSize(p);
}

void *aligned_alloc(size_t align, size_t len)
{
    return KosMemAllocEx(len, align, 0);
}
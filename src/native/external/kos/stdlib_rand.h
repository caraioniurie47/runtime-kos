#ifndef _STDLIB_RAND_H
#define _STDLIB_RAND_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdlib.h>

#if defined(_XOPEN_SOURCE) || defined(_GNU_SOURCE) \
 || defined(_BSD_SOURCE)
long int lrand48 (void);
long mrand48 (void);
void srand48 (long);
#endif

#ifdef __cplusplus
}
#endif

#endif

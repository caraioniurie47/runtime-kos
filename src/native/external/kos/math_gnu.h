#ifndef _MATH_GNU_H
#define _MATH_GNU_H

#ifdef __cplusplus
extern "C" {
#endif

#include <math.h>

#ifdef _GNU_SOURCE

void sincos(double, double*, double*);
void sincosf(float, float*, float*);

#endif

#ifdef __cplusplus
}
#endif

#endif

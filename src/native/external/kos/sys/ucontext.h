#ifndef _UCONTEXTSYS_H
#define _UCONTEXTSYS_H
#ifdef __cplusplus
extern "C" {
#endif

#include <machine/mcontext.h>
#include <sys/sigtypes.h>

// copied from musl

#ifdef _GNU_SOURCE

typedef struct sigaltstack stack_t;

struct _aarch64_ctx {
	unsigned int magic;
	unsigned int size;
};
struct fpsimd_context {
	struct _aarch64_ctx head;
	unsigned int fpsr;
	unsigned int fpcr;
	__uint128_t vregs[32];
};

typedef struct __ucontext {
	unsigned long uc_flags;
	struct __ucontext *uc_link;
	stack_t uc_stack;
	sigset_t uc_sigmask;
	mcontext_t uc_mcontext;
} ucontext_t;

int  getcontext(struct __ucontext *);
void makecontext(struct __ucontext *, void (*)(), int, ...);
int  setcontext(const struct __ucontext *);
int  swapcontext(struct __ucontext *, const struct __ucontext *);

#endif

#ifdef __cplusplus
}
#endif
#endif

#pragma once

#include <sys/exec_elf.h>

// NetBSD name for the glibc one used by nativeaot/Runtime/unix/PalUnix.cpp.
#ifndef NT_GNU_BUILD_ID
#define NT_GNU_BUILD_ID ELF_NOTE_TYPE_GNU_BUILD_ID
#endif
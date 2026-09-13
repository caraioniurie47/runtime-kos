#pragma once

// brotli's CMakeLists.txt defines OS_LINUX from CMAKE_SYSTEM_NAME, so c/common/platform.h includes
// <endian.h>; KOS has the NetBSD <sys/endian.h>. brotli takes the byte order from GCC's __BYTE_ORDER__.
#include <sys/endian.h>

# KasperskyOS image for the NativeAOT-published helloworldapp-kos, for KOS CE SDK 1.4 and later, which
# build images through SDK CMake instead of the einit/makeimg tools that dist/kos-image.sh calls.
# Modeled on the SDK's examples/hello. Build (QEMU) with the SDK's own cmake, which carries the
# `platform` modules:
#   $SDK/toolchain/bin/cmake -B build -D CMAKE_TOOLCHAIN_FILE=$SDK/toolchain/share/toolchain-aarch64-kos.cmake \
#         -D HELLO_BINARY=<publish dir>/helloworldapp-kos
#   $SDK/toolchain/bin/cmake --build build --target kos-qemu-image   # or: sim, which also starts QEMU

cmake_minimum_required (VERSION 3.25)

project (helloworldapp_kos_image)

include (platform)
initialize_platform (FORCE_STATIC)
include (platform/nk)
include (platform/image)

set (HELLO_BINARY "" CACHE FILEPATH "The NativeAOT-published helloworldapp-kos executable")
if (NOT EXISTS "${HELLO_BINARY}")
    message (FATAL_ERROR "Set HELLO_BINARY to the published helloworldapp-kos executable (got '${HELLO_BINARY}')")
endif ()
# The ROMFS entry keeps the file name; init.yaml.in's `path:` points Einit at it.
get_filename_component (HELLO_BINARY_NAME "${HELLO_BINARY}" NAME)

# The SDK's prebuilt VfsRamFs program serves files and stdout to the program over IPC (as in the SDK's
# examples/secure_logger): a RAM file system at /tmp, devices at /dev. It brings the entropy program with it.
# init.yaml.in points the program at it with VFS_FILESYSTEM_BACKEND; the program links the SDK's
# libvfs_remote.a, the client side, which dotnet publish adds.
find_package (precompiled_vfs REQUIRED)

set (NK_RESOURCES ${CMAKE_SOURCE_DIR}/resources/xdl)
nk_include_directories (${NK_RESOURCES})

# Copies Hello.edl into build/_headers_/hello/, where Einit generation looks for it.
nk_build_edl_files (hello_edl_files
                    NK_MODULE "hello"
                    EDL       "${NK_RESOURCES}/hello/Hello.edl")

# The program is built by dotnet publish, not by this project.
add_executable (Hello IMPORTED)
set_target_properties (Hello PROPERTIES IMPORTED_LOCATION "${HELLO_BINARY}")

build_kos_qemu_image (kos-qemu-image
                      EINIT_ENTITY EinitQemu
                      CONNECTIONS_CFG "src/init.yaml.in"
                      SECURITY_PSL "src/security.psl"
                      IMAGE_FILES Hello precompiled_vfs::VfsRamFs)
# An IMPORTED target cannot carry the dependency, so attach it to the image targets.
add_dependencies (kos-qemu-image hello_edl_files)
add_dependencies (EinitQemu hello_edl_files)

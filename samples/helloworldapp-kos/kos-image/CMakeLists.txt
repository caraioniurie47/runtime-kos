# KasperskyOS image for the NativeAOT-published helloworldapp-kos, for KOS CE SDK 1.4 and later, which
# build images through SDK CMake instead of the einit/makeimg tools that dist/kos-image.sh calls.
# Modeled on the SDK's examples/hello. Build (QEMU) with the SDK's own cmake, which carries the
# `platform` modules:
#   $SDK/toolchain/bin/cmake -B build -D CMAKE_TOOLCHAIN_FILE=$SDK/toolchain/share/toolchain-aarch64-kos.cmake \
#         -D HELLO_BINARY=<publish dir>/helloworldapp-kos [-D HOST_TCP_PORT=5047]
#   $SDK/toolchain/bin/cmake --build build --target kos-qemu-image   # or: sim, which also starts QEMU

cmake_minimum_required (VERSION 3.25)

project (helloworldapp_kos_image)

include (platform)
initialize_platform (FORCE_STATIC)
project_header_default ("STANDARD_GNU_17:YES" "EXTRA_STRICT_WARNINGS:YES")
include (platform/nk)
include (platform/image)

set (HELLO_BINARY "" CACHE FILEPATH "The NativeAOT-published helloworldapp-kos executable")
if (NOT EXISTS "${HELLO_BINARY}")
    message (FATAL_ERROR "Set HELLO_BINARY to the published helloworldapp-kos executable (got '${HELLO_BINARY}')")
endif ()
# The ROMFS entry keeps the file name; init.yaml.in's `path:` points Einit at it.
get_filename_component (HELLO_BINARY_NAME "${HELLO_BINARY}" NAME)

# Optional: forward this TCP port from the host (QEMU user networking) to the same port of the program, and
# tell the program in HOST_TCP_PORT. The showcase then waits for one client from the host.
set (HOST_TCP_PORT "" CACHE STRING "TCP port forwarded from the host to the program; empty: none")
set (HOST_TCP_PORT_ENV "")
set (HOST_TCP_PORT_QEMU "")
if (HOST_TCP_PORT)
    if (NOT HOST_TCP_PORT MATCHES "^[0-9]+$")
        message (FATAL_ERROR "HOST_TCP_PORT must be a port number (got '${HOST_TCP_PORT}')")
    endif ()
    set (HOST_TCP_PORT_ENV "HOST_TCP_PORT: \"${HOST_TCP_PORT}\"")
    # Appended to the SDK's default `-nic user`.
    set (HOST_TCP_PORT_QEMU QEMU_USER_NETWORK_FLAGS "hostfwd=tcp::${HOST_TCP_PORT}-:${HOST_TCP_PORT}")
endif ()

# The SDK's prebuilt VFS programs serve files, stdout and sockets to the program over IPC; the program links the
# SDK's libvfs_remote.a, the client side, which dotnet publish adds, and init.yaml.in names the servers.
# VfsRamFs (as in the SDK's examples/secure_logger): a RAM file system at /tmp, devices at /dev, for
# VFS_FILESYSTEM_BACKEND. VfsNet (as in examples/multi_vfs_ntpd): the network stack, for VFS_NETWORK_BACKEND,
# with the SDK's network driver program. Both bring the entropy program.
find_package (precompiled_vfs REQUIRED)
find_package (vfs REQUIRED)

set (NK_RESOURCES ${CMAKE_SOURCE_DIR}/resources/xdl)
nk_include_directories (${NK_RESOURCES})

# Copies Hello.edl into build/_headers_/hello/, where Einit generation looks for it.
nk_build_edl_files (hello_edl_files
                    NK_MODULE "hello"
                    EDL       "${NK_RESOURCES}/hello/Hello.edl")

# The program is built by dotnet publish, not by this project.
add_executable (Hello IMPORTED)
set_target_properties (Hello PROPERTIES IMPORTED_LOCATION "${HELLO_BINARY}")

# VfsNet does not address the network interface. NetInit gives en0 the address QEMU user networking expects
# (as the SDK's network examples do in their own programs), then exits.
nk_build_edl_files (netinit_edl_files
                    NK_MODULE "hello"
                    EDL       "${NK_RESOURCES}/hello/NetInit.edl")
add_executable (NetInit "src/netinit.c")
add_dependencies (NetInit netinit_edl_files)
target_link_libraries (NetInit vfs::client)
# vfs::client would add the SDK's default VFS program; VfsNet serves NetInit instead.
set_target_properties (NetInit PROPERTIES vfs::entity_REPLACEMENT "")

build_kos_qemu_image (kos-qemu-image
                      EINIT_ENTITY EinitQemu
                      CONNECTIONS_CFG "src/init.yaml.in"
                      SECURITY_PSL "src/security.psl"
                      ${HOST_TCP_PORT_QEMU}
                      IMAGE_FILES Hello NetInit precompiled_vfs::VfsRamFs precompiled_vfs::VfsNet)
# An IMPORTED target cannot carry the dependency, so attach it to the image targets.
add_dependencies (kos-qemu-image hello_edl_files)
add_dependencies (EinitQemu hello_edl_files)

# KasperskyOS image for the NativeAOT-published helloworldapp-kos, for KOS CE SDK 1.4 and later, which
# build images through SDK CMake instead of the einit/makeimg tools that dist/kos-image.sh calls.
# Modeled on the SDK's examples/hello. Build (QEMU) with the SDK's own cmake, which carries the
# `platform` modules:
#   $SDK/toolchain/bin/cmake -B build -D CMAKE_TOOLCHAIN_FILE=$SDK/toolchain/share/toolchain-aarch64-kos.cmake \
#         -D HELLO_BINARY=<publish dir>/helloworldapp-kos [-D HOST_TCP_PORT=5047] [-D HOST_HTTP_PORT=8080]
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

# Optional: forward a TCP port from the host (QEMU user networking) to the same port of the program, and tell
# the program in an environment variable of the option's name. HOST_TCP_PORT: the showcase waits for one client
# from the host. HOST_HTTP_PORT: webserver-kos serves HTTP on it.
set (HOST_TCP_PORT "" CACHE STRING "TCP port forwarded from the host to the program for the showcase; empty: none")
set (HOST_HTTP_PORT "" CACHE STRING "TCP port forwarded from the host to the program for webserver-kos; empty: none")
set (HOST_PORTS_ENV "")
set (HOST_PORTS_FWD "")
foreach (option HOST_TCP_PORT HOST_HTTP_PORT)
    if (${option})
        if (NOT ${option} MATCHES "^[0-9]+$")
            message (FATAL_ERROR "${option} must be a port number (got '${${option}}')")
        endif ()
        string (APPEND HOST_PORTS_ENV "\n    ${option}: \"${${option}}\"")
        list (APPEND HOST_PORTS_FWD "hostfwd=tcp::${${option}}-:${${option}}")
    endif ()
endforeach ()
set (HOST_PORTS_QEMU "")
if (HOST_PORTS_FWD)
    # The SDK inserts this after its default `-nic user,`; several hostfwd rules are separated by commas.
    list (JOIN HOST_PORTS_FWD "," HOST_PORTS_FWD)
    set (HOST_PORTS_QEMU QEMU_USER_NETWORK_FLAGS "${HOST_PORTS_FWD}")
endif ()

# The SDK's prebuilt VFS programs serve files, stdout and sockets to the program over IPC; the program links the
# SDK's libvfs_remote.a, the client side, which dotnet publish adds, and init.yaml.in names the servers.
# VfsRamFs (as in the SDK's examples/secure_logger): a RAM file system at /tmp, devices at /dev, for
# VFS_FILESYSTEM_BACKEND. VfsNet (as in examples/multi_vfs_ntpd): the network stack, for VFS_NETWORK_BACKEND,
# with the SDK's network driver program. Both bring the entropy program.
find_package (precompiled_vfs REQUIRED)
find_package (vfs REQUIRED)

# KOS-DOC(limitations_and_known_problems): a VFS program serves each client with at most 5 threads
# A socket call that blocks (a synchronous Receive, Accept, a Send to a full socket) holds one of VfsNet's threads for
# this program until it returns; with 5 of them held, every other socket call of the program waits, poll() included,
# and the program stalls for good. VFS_SERVER_MAX_THREADS_PER_CLIENT raises the limit. The rest is VfsNet's default
# environment (the SDK's precompiled_vfsConfig.cmake), which this property replaces.
set (VFSNET_MAX_THREADS_PER_CLIENT "64" CACHE STRING "Threads VfsNet serves this image's programs with, each")
set_target_properties (precompiled_vfs::VfsNet PROPERTIES EXTRA_ENV "
    VFS_NETWORK_BACKEND: server:kl.VfsNet
    VFS_FILESYSTEM_BACKEND: server:kl.VfsNet
    DEFAULT_DNS_SERVER: 8.8.8.8
    VFS_SERVER_MAX_THREADS_PER_CLIENT: \"${VFSNET_MAX_THREADS_PER_CLIENT}\"")

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

# VfsNet resolves host names for its clients, from /etc/hosts or DNS; without a hosts file not even `localhost`
# resolves. As in the SDK's examples/net_with_separate_vfs, the image's ROMFS carries one and VfsNet mounts it at /etc
# (besides the devfs the SDK gives it by default).
set_target_properties (precompiled_vfs::VfsNet PROPERTIES EXTRA_ARGS "
    - -l
    - devfs /dev devfs 0
    - -l
    - romfs /etc romfs ro")

build_kos_qemu_image (kos-qemu-image
                      EINIT_ENTITY EinitQemu
                      CONNECTIONS_CFG "src/init.yaml.in"
                      SECURITY_PSL "src/security.psl"
                      ${HOST_PORTS_QEMU}
                      IMAGE_FILES Hello NetInit precompiled_vfs::VfsRamFs precompiled_vfs::VfsNet
                                  ${CMAKE_SOURCE_DIR}/resources/romfs/etc/hosts)
# An IMPORTED target cannot carry the dependency, so attach it to the image targets.
add_dependencies (kos-qemu-image hello_edl_files)
add_dependencies (EinitQemu hello_edl_files)

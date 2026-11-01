# KasperskyOS image for the NativeAOT-published helloworldapp-kos, for KOS CE SDK 2.0 and later. SDK 2.0 builds no
# Einit-only image (kos-image/ beside this one is for SDK 1.4): a prebuilt MinimalKOS boots, programs are KPA packages
# that ExecutionManager installs and starts, and the system programs a solution adds run from a product ROMFS started
# by ProductEinit. Modeled on the SDK's examples/net_with_separate_vfs. Build (QEMU) with the SDK's own cmake:
#   $SDK/toolchain/bin/cmake -B build -D CMAKE_TOOLCHAIN_FILE=$SDK/toolchain/share/toolchain-aarch64-kos.cmake \
#         -D HELLO_BINARY=<publish dir>/helloworldapp-kos [-D HOST_TCP_PORT=5047] [-D HOST_HTTP_PORT=8080]
#   $SDK/toolchain/bin/cmake --build build --target qemu   # or: sim, which also starts QEMU

cmake_minimum_required (VERSION 3.29)

project (helloworldapp_kos_image)

include (platform)
initialize_platform (FORCE_STATIC)
project_header_default ("STANDARD_GNU_17:YES" "EXTRA_STRICT_WARNINGS:YES")
include (platform/nk)
include (platform/options)
include (platform/kpa)

find_package (minimal_kos REQUIRED)
find_package (vfs REQUIRED)
find_package (precompiled_vfs REQUIRED)
find_package (dnet REQUIRED)
include (${KL_SDK_ROOT_PATH}/share/vendor_rom/build_vendor_rom.cmake)

# The EDL files, NetInit's source and the hosts file are the 1.4 image's.
set (IMAGE14 ${CMAKE_SOURCE_DIR}/../kos-image)

set (HELLO_BINARY "" CACHE FILEPATH "The NativeAOT-published helloworldapp-kos executable")
if (NOT EXISTS "${HELLO_BINARY}")
    message (FATAL_ERROR "Set HELLO_BINARY to the published helloworldapp-kos executable (got '${HELLO_BINARY}')")
endif ()

# Optional: arguments and more environment variables for the program, as CMake lists (e.g. for a test runner).
set (HELLO_ARGS "" CACHE STRING "Command-line arguments for the program, a CMake list")
set (HELLO_ENV "" CACHE STRING "More NAME=value environment variables for the program, a CMake list")

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
        list (APPEND HOST_PORTS_ENV "${option}=${${option}}")
        list (APPEND HOST_PORTS_FWD "hostfwd=tcp::${${option}}-:${${option}}")
    endif ()
endforeach ()
set (HOST_PORTS_QEMU "")
if (HOST_PORTS_FWD)
    # The SDK appends this to its default `-nic user,`; several hostfwd rules are separated by commas.
    list (JOIN HOST_PORTS_FWD "," HOST_PORTS_FWD)
    set (HOST_PORTS_QEMU QEMU_USER_NETWORK_FLAGS "${HOST_PORTS_FWD}")
endif ()

# Kernel startup parameters, e.g. vmm.overcommit=exception (manual, "Kernel startup parameters"); empty by default.
set (KOS_KERNEL_CMDLINE "" CACHE STRING "Kernel startup parameters passed with QEMU's -append")
set (KERNEL_CMDLINE_QEMU "")
if (KOS_KERNEL_CMDLINE)
    set (KERNEL_CMDLINE_QEMU KERNEL_CMDLINE ${KOS_KERNEL_CMDLINE})
endif ()

# KOS-DOC(limitations_and_known_problems): a VFS program serves each client with at most 5 threads
# A socket call that blocks (a synchronous Receive, Accept, a Send to a full socket) holds one of VfsNet's threads for
# this program until it returns; with 5 of them held, every other socket call of the program waits, poll() included,
# and the program stalls for good. VFS_SERVER_MAX_THREADS_PER_CLIENT raises the limit (product_init.yaml.in).
set (VFSNET_MAX_THREADS_PER_CLIENT "64" CACHE STRING "Threads VfsNet serves this image's programs with, each")

# TODO-KOS(6f): VfsRamFs faults (NULL dereference) under concurrent file calls from one client
# The KasperskyOS team's workaround for SDK 1.4: VfsRamFs serves one call at a time, per client and per process.
# Kept on SDK 2.0 until the .NET tests show it can go (the C reproducer no longer faults on 2.0.0.179); empty: the
# SDK's default.
set (VFSRAMFS_MAX_THREADS "1" CACHE STRING "Threads VfsRamFs serves with, per client and per process; empty: default")
set (VFSRAMFS_THREADS_ENV "")
if (VFSRAMFS_MAX_THREADS)
    set (VFSRAMFS_THREADS_ENV "\n    VFS_SERVER_MAX_THREADS_PER_CLIENT: \"${VFSRAMFS_MAX_THREADS}\"\n    VFS_SERVER_MAX_THREADS_PER_PROCESS: \"${VFSRAMFS_MAX_THREADS}\"")
endif ()

set (NK_RESOURCES ${IMAGE14}/resources/xdl)
nk_include_directories (${NK_RESOURCES})
nk_build_edl_files (hello_edl_files
                    NK_MODULE "hello"
                    EDL       "${NK_RESOURCES}/hello/Hello.edl")
nk_build_edl_files (netinit_edl_files
                    NK_MODULE "hello"
                    EDL       "${NK_RESOURCES}/hello/NetInit.edl")

# The program, built by dotnet publish, as the KPA package Hello. The client form of the backends that waits for the
# server, as the SDK examples use for programs started at boot.
add_kpa_package (Hello.kpa ID Hello MANIFEST_V "2")
add_kpa_component (Hello.kpa RESOURCE "${HELLO_BINARY}" TYPE "bin" NAME "Hello")
add_kpa_component (Hello.kpa
                   RUN_CONFIGURATION "default"
                   TYPE              "application"
                   NAME              "Hello"
                   EIID              "hello.Hello"
                   PATH              "bin/Hello"
                   ENV               "VFS_FILESYSTEM_BACKEND=client:task:kl.VfsRamFs:timeout_dcm_ms:600000"
                                     "VFS_NETWORK_BACKEND=client:task:kl.VfsNet:timeout_dcm_ms:600000"
                                     ${HOST_PORTS_ENV} ${HELLO_ENV}
                   ARGS              ${HELLO_ARGS}
                   NO_AUTO_VFS_CONNECTION)

# VfsNet does not address the network interface. NetInit gives en0 the address QEMU user networking expects (as the
# SDK's network examples did in their own programs on 1.4), then exits.
add_executable (NetInit "${IMAGE14}/src/netinit.c")
add_dependencies (NetInit netinit_edl_files)
target_link_libraries (NetInit vfs::client)
set_target_properties (NetInit PROPERTIES vfs::entity_REPLACEMENT "")
add_kpa_package (NetInit.kpa ID NetInit MANIFEST_V "2")
add_kpa_component (NetInit.kpa RESOURCE NetInit TYPE "bin")
add_kpa_component (NetInit.kpa
                   RUN_CONFIGURATION "default"
                   TYPE              "application"
                   NAME              "NetInit"
                   EIID              "hello.NetInit"
                   PATH              "bin/NetInit"
                   ENV               "VFS_NETWORK_BACKEND=client:task:kl.VfsNet:timeout_dcm_ms:600000"
                   NO_AUTO_VFS_CONNECTION)

# VfsRamFs, VfsNet and the dnet driver VfsNet needs run from the product ROMFS. VfsNet resolves host names for its
# clients from /etc/hosts or DNS; without a hosts file not even `localhost` resolves. As in the SDK's
# examples/net_with_separate_vfs, VfsNet reads /etc/hosts from the root file system (MinimalKOS's kl.VfsEntity).
set (EINIT_SRC ${CMAKE_SOURCE_DIR}/einit)
set (ROOTFS_DIR ${CMAKE_BINARY_DIR}/rootfs_img)
set (PRODUCT_ROM_PROGRAMS dnet_executables::whole precompiled_vfs::VfsNet precompiled_vfs::VfsRamFs)
set (PRODUCT_INIT_YAML "${CMAKE_BINARY_DIR}/product_init.yaml")
configure_file (${EINIT_SRC}/product_init.yaml.in "${PRODUCT_INIT_YAML}")

build_vendor_romfs (vendor_romfs
                    WITH_PRODUCT_ROMFS
                    SECURITY_PSL_FILE ${EINIT_SRC}/security.psl
                    EDL_TARGETS       hello_edl_files netinit_edl_files)
build_einit (product_einit ${PRODUCT_INIT_YAML} DEPENDS ${PRODUCT_ROM_PROGRAMS})
build_romfs (product_romfs product_einit FILES ${PRODUCT_ROM_PROGRAMS})

find_program (TAR_COMMAND tar REQUIRED)
add_custom_target (rootfs_part_src
                   COMMAND ${CMAKE_COMMAND} -E make_directory ${ROOTFS_DIR}
                   COMMAND ${TAR_COMMAND} --extract --keep-directory-symlink
                                          --file ${MINIMAL_KOS_ROOTFS_ARCHIVE} --directory ${ROOTFS_DIR}
                   COMMAND ${CMAKE_COMMAND} -E copy $<TARGET_PROPERTY:product_romfs,ROMFS_LOCATION>
                                                    ${ROOTFS_DIR}/product_romfs
                   COMMAND ${CMAKE_COMMAND} -E copy ${EINIT_SRC}/initial_install.json
                                                    ${EINIT_SRC}/system_services.json
                                                    ${ROOTFS_DIR}${MINIMAL_KOS_INITIAL_SETTINGS_DIR}/
                   COMMAND ${CMAKE_COMMAND} -E make_directory ${ROOTFS_DIR}/data/etc
                   COMMAND ${CMAKE_COMMAND} -E copy ${IMAGE14}/resources/romfs/etc/hosts ${ROOTFS_DIR}/data/etc/
                   COMMAND ${CMAKE_COMMAND} -E copy $<TARGET_PROPERTY:Hello.kpa,KPA_OUTPUT_DIR>/Hello.kpa
                                                    $<TARGET_PROPERTY:NetInit.kpa,KPA_OUTPUT_DIR>/NetInit.kpa
                                                    ${ROOTFS_DIR}${MINIMAL_KOS_INITIAL_PACKAGES_DIR}
                   DEPENDS product_romfs Hello.kpa NetInit.kpa)
build_rootfs (rootfs DIR ${ROOTFS_DIR} DEPENDS rootfs_part_src)

generate_qemu_target (qemu
                      KERNEL_IMAGE ${MINIMAL_KOS_IMAGE}
                      VENDOR_IMAGE vendor_romfs
                      ROOTFS_IMAGE rootfs
                      ${HOST_PORTS_QEMU}
                      ${KERNEL_CMDLINE_QEMU})

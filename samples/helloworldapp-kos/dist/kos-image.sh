#!/bin/bash

__dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# The SDK variable should specify the path to the KasperskyOS Community Edition installation directory.
SDK="/opt/KasperskyOS-Community-Edition-1.1.1.13"
TOOLCHAIN=$SDK/toolchain
SYSROOT=$SDK/sysroot-aarch64-kos
PATH=$TOOLCHAIN/bin:$PATH

cd ${__dir}
if [ -f "helloworldapp-kos" ]; then
    mv helloworldapp-kos Hello
fi

# Create the Hello.edl.h file from Hello.edl
# (The Hello program does not implement any endpoints, so there are no CDL or IDL files.)
nk-gen-c -I $SYSROOT/include Hello.edl

# Create the Kaspersky Security Module (ksm.module)
makekss --target=aarch64-kos                             \
        --module=-lksm_kss                                           \
        --with-nkflags="-I . -I $SYSROOT/include -I $TOOLCHAIN/include" \
        --with-nktype="psl" \
        --output=ksm.module \
        security.psl

# Create code of the Einit initializing program
einit -I $SYSROOT/include -I . init.yaml -o einit.c

# Compile and build the Einit program
aarch64-kos-gcc -I . -o einit einit.c

# Create loadable solution image (kos-qemu-image)
makeimg --target=aarch64-kos                              \
        --sys-root=$SYSROOT                                 \
        --with-toolchain=$TOOLCHAIN                         \
        --ldscript=$SDK/libexec/aarch64-kos/kos-qemu.ld   \
        --img-src=$SDK/libexec/aarch64-kos/kos-qemu       \
        --img-dst=kos-qemu-image                            \
       einit Hello ksm.module

# Run solution under QEMU
qemu-system-aarch64 -m 2048 -machine vexpress-a15,secure=on -cpu cortex-a72 -nographic -monitor none -smp 4 -nic user -serial stdio -kernel kos-qemu-image
# qemu-system-aarch64 -s -S -m 2048 -machine vexpress-a15,secure=on -cpu cortex-a72 -nographic -monitor none -smp 4 -nic user -serial stdio -kernel kos-qemu-image

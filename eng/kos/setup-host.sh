#!/bin/bash
# Sets up an Ubuntu 24.04 x64 host for the KasperskyOS port, as HOWTO-KOS.md steps 2-4 do: packages, CMake, the
# KasperskyOS CE SDK and the environment file /etc/profile.d/kos-net.sh. With --source it also builds ICU and
# prepares the OpenSSL headers (step 5), which only a build of the runtime from source needs.
#
# Usage, as root: eng/kos/setup-host.sh --accept-eula [--source]
#
# The SDK is Kaspersky's, under its own licence agreement; the script downloads it only once you have accepted that
# agreement: read KOS_EULA_URL below, then pass --accept-eula (or set ACCEPT_KASPERSKYOS_EULA=yes). A step whose
# result is already there is skipped, so the script can be run again.

set -euo pipefail

KOS_SDK_NAME=KasperskyOS-Community-Edition-Qemu-arm64-2.0.0.179
KOS_SDK_URL=https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/2.0.0.179/multilanguage-INT-2.0.0.179/8c9dc725f0bb4efe9c4932f205e01374/${KOS_SDK_NAME}_en.deb
KOS_SDK_SHA256=78faab9ab8e06a4312386f6d9d23be0d98ceb376e07380f916784a6f42098434
KOS_EULA_URL=https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/2.0.0.179/multilanguage-INT-2.0.0.179/english-INT/8c26d1dfbb0c450ea9284875e2c87018/EULA.en.txt
CMAKE_NAME=cmake-3.31.12-linux-x86_64
OPENSSL_NAME=openssl-3.5.7
OPENSSL_SHA256=a8c0d28a529ca480f9f36cf5792e2cd21984552a3c8e4aa11a24aa31aeac98e8
ICU_COMMIT=c6131fd7b7cebcef44c17f381b2fe325cb331964
KOS_SDK=/opt/$KOS_SDK_NAME
REPO=$(cd "$(dirname "$0")/../.." && pwd)

source_build=0
accept=${ACCEPT_KASPERSKYOS_EULA:-no}
for arg in "$@"; do
    case $arg in
        --source) source_build=1 ;;
        --accept-eula) accept=yes ;;
        *) echo "usage: $0 --accept-eula [--source]" >&2; exit 2 ;;
    esac
done

[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }
grep -q '^VERSION_ID="24.04"' /etc/os-release || echo "warning: SDK 2.0 requires Ubuntu 24.04; this host is not" >&2
if [ ! -d "$KOS_SDK" ] && [ "$accept" != yes ]; then
    echo "The KasperskyOS CE SDK is used under Kaspersky's licence agreement: $KOS_EULA_URL" >&2
    echo "Read it; if you accept it, run again with --accept-eula." >&2
    exit 1
fi

step() { echo "=== $*"; }

step "Step 2: packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get upgrade -y
apt-get install -y --no-install-recommends build-essential clang llvm lld lldb python-is-python3 curl wget git unzip file ca-certificates libicu-dev liblttng-ust-dev libssl-dev libkrb5-dev zlib1g-dev ninja-build cpio pigz

step "Step 2: CMake 3.31.12 in /opt"
if [ ! -x "/opt/$CMAKE_NAME/bin/cmake" ]; then
    cd /home
    wget -nc "https://github.com/Kitware/CMake/releases/download/v3.31.12/$CMAKE_NAME.tar.gz"
    tar -xzf "$CMAKE_NAME.tar.gz" -C /opt
fi
for t in cmake ctest cpack; do ln -sf "/opt/$CMAKE_NAME/bin/$t" "/usr/local/bin/$t"; done
hash -r
cmake --version

step "Step 3: KasperskyOS CE SDK in $KOS_SDK"
if [ ! -d "$KOS_SDK" ]; then
    cd /home
    wget -nc "$KOS_SDK_URL"
    echo "$KOS_SDK_SHA256  ${KOS_SDK_NAME}_en.deb" | sha256sum -c
    apt-get install -y "./${KOS_SDK_NAME}_en.deb"
fi

step "Steps 3-4: environment in /etc/profile.d/kos-net.sh"
# Not the SDK's toolchain/bin: it goes on PATH only after step 6 (HOWTO "Environment"); run-sample.sh adds it itself.
cat > /etc/profile.d/kos-net.sh <<EOF
export KOS_SDK=$KOS_SDK
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NuGetAudit=false
EOF
. /etc/profile.d/kos-net.sh

if [ "$source_build" = 1 ]; then
    [ -f "$REPO/eng/kos/icu/kos-icu.patch" ] || { echo "--source needs the runtime-kos clone; $REPO is not one" >&2; exit 1; }

    step "Step 5: ICU in /opt/icu4c/kos"
    if [ ! -f /opt/icu4c/kos/lib/libicuuc.a ]; then
        rm -rf /home/dotnet-icu /home/icu4c-build-x64 /home/icu4c-build-kos
        cd /home
        git init -q dotnet-icu && cd dotnet-icu
        git fetch --depth 1 https://github.com/dotnet/icu.git "$ICU_COMMIT"
        git checkout -q FETCH_HEAD
        patch -p1 -d icu < "$REPO/eng/kos/icu/kos-icu.patch"
        export ICU_DATA_FILTER_FILE=$REPO/eng/kos/icu/icudt_kos.json
        mkdir -p /home/icu4c-build-x64 && cd /home/icu4c-build-x64
        sh /home/dotnet-icu/icu/icu4c/source/runConfigureICU Linux/gcc --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static
        make -j"$(nproc)"
        mkdir -p /home/icu4c-build-kos && cd /home/icu4c-build-kos
        (
            export PATH=$PATH:$KOS_SDK/toolchain/bin
            CC=aarch64-kos-clang CXX=aarch64-kos-clang++ AR=llvm-ar RANLIB=llvm-ranlib \
                sh /home/dotnet-icu/icu/icu4c/source/configure --host=aarch64-kos --with-cross-build=/home/icu4c-build-x64 --prefix=/opt/icu4c/kos --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static
            make -j"$(nproc)"
            make install
        )
        unset ICU_DATA_FILTER_FILE
    fi

    step "Step 5: OpenSSL 3.5.7 headers in /opt/openssl-kos/include"
    if [ ! -f /opt/openssl-kos/include/openssl/opensslv.h ]; then
        cd /home
        wget -nc "https://github.com/openssl/openssl/releases/download/$OPENSSL_NAME/$OPENSSL_NAME.tar.gz"
        echo "$OPENSSL_SHA256  $OPENSSL_NAME.tar.gz" | sha256sum -c
        rm -rf "/home/$OPENSSL_NAME"
        tar -xzf "$OPENSSL_NAME.tar.gz"
        cd "/home/$OPENSSL_NAME"
        ./Configure linux-aarch64 no-shared no-quic no-cmp
        make build_generated
        mkdir -p /opt/openssl-kos/include
        cp -a include/openssl /opt/openssl-kos/include/
    fi
fi

step "Done. In a new shell: . /etc/profile.d/kos-net.sh"

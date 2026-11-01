#!/bin/bash
# Sets up an Ubuntu 24.04 x64 host for the KasperskyOS port, as HOWTO-KOS.md steps 2-4 do: packages, CMake, the
# KasperskyOS CE SDK and the environment file /etc/profile.d/kos-net.sh. With --source it also builds ICU and
# prepares the OpenSSL headers (step 5), which only a build of the runtime from source needs.
#
# Usage, as root: eng/kos/setup-host.sh --accept-eula [--source]
#
# The SDK is Kaspersky's, under its own licence agreement; the script downloads it only once you have accepted that
# agreement: read KOS_EULA_URL below, then pass --accept-eula (or set ACCEPT_KASPERSKYOS_EULA=yes). The script can be
# run again after a failure: an interrupted download resumes, one that fails its SHA-256 is deleted, and a step is
# skipped only when it finished before (the SDK: dpkg's state of its package and version; CMake, ICU and OpenSSL: the
# marker file each writes last). Otherwise the step runs again: ICU and the OpenSSL headers are written over what is
# there (a release asset's ICU too), and CMake is unpacked beside its folder, then takes its place, so an interrupted
# unpack never looks finished.

set -euo pipefail

KOS_SDK_VERSION=2.0.0.179
KOS_SDK_NAME=KasperskyOS-Community-Edition-Qemu-arm64-$KOS_SDK_VERSION
KOS_SDK_URL=https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/2.0.0.179/multilanguage-INT-2.0.0.179/8c9dc725f0bb4efe9c4932f205e01374/${KOS_SDK_NAME}_en.deb
KOS_SDK_SHA256=78faab9ab8e06a4312386f6d9d23be0d98ceb376e07380f916784a6f42098434
KOS_EULA_URL=https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/2.0.0.179/multilanguage-INT-2.0.0.179/english-INT/8c26d1dfbb0c450ea9284875e2c87018/EULA.en.txt
CMAKE_NAME=cmake-3.31.12-linux-x86_64
CMAKE_SHA256=0dc2e9a6860f06bf10bd8fadc03e35d9eeb4df46e33763a7e480e987758f385c
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
sdk_installed() {
    [ "$(dpkg-query -W -f='${Status} ${Version}' kasperskyos-community-edition-qemu-arm64 2>/dev/null)" = \
      "install ok installed $KOS_SDK_VERSION" ]
}
if ! sdk_installed && [ "$accept" != yes ]; then
    echo "The KasperskyOS CE SDK is used under Kaspersky's licence agreement: $KOS_EULA_URL" >&2
    echo "Read it; if you accept it, run again with --accept-eula." >&2
    exit 1
fi

step() { echo "=== $*"; }

# Downloads into the current directory, resuming a partial file; with a SHA-256, a file that fails it is deleted.
fetch() {  # url [sha256]
    local file
    file=$(basename "$1")
    wget -c "$1"
    if [ -n "${2:-}" ] && ! echo "$2  $file" | sha256sum -c; then
        rm -f "$file"
        echo "$file failed its checksum and was deleted; run the script again" >&2
        exit 1
    fi
}

step "Step 2: packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get upgrade -y
apt-get install -y --no-install-recommends build-essential clang llvm lld lldb python-is-python3 curl wget git unzip file ca-certificates libicu-dev liblttng-ust-dev libssl-dev libkrb5-dev zlib1g-dev ninja-build cpio pigz

step "Step 2: CMake 3.31.12 in /opt"
if [ ! -f "/opt/$CMAKE_NAME/.kos-setup-done" ]; then
    cd /home
    fetch "https://github.com/Kitware/CMake/releases/download/v3.31.12/$CMAKE_NAME.tar.gz" "$CMAKE_SHA256"
    rm -rf "/opt/.kos-unpack"
    mkdir -p "/opt/.kos-unpack"
    tar -xzf "$CMAKE_NAME.tar.gz" -C /opt/.kos-unpack
    touch "/opt/.kos-unpack/$CMAKE_NAME/.kos-setup-done"
    rm -rf "/opt/$CMAKE_NAME"
    mv "/opt/.kos-unpack/$CMAKE_NAME" "/opt/$CMAKE_NAME"
    rmdir /opt/.kos-unpack
fi
for t in cmake ctest cpack; do ln -sf "/opt/$CMAKE_NAME/bin/$t" "/usr/local/bin/$t"; done
hash -r
cmake --version

step "Step 3: KasperskyOS CE SDK in $KOS_SDK"
# The package's own state, not the directory: an install that failed after unpacking leaves the directory behind.
if ! sdk_installed; then
    cd /home
    fetch "$KOS_SDK_URL" "$KOS_SDK_SHA256"
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
    if [ ! -f /opt/icu4c/kos/.kos-setup-done ]; then
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
        touch /opt/icu4c/kos/.kos-setup-done
    fi

    step "Step 5: OpenSSL 3.5.7 headers in /opt/openssl-kos/include"
    if [ ! -f /opt/openssl-kos/.kos-setup-done ]; then
        cd /home
        fetch "https://github.com/openssl/openssl/releases/download/$OPENSSL_NAME/$OPENSSL_NAME.tar.gz" "$OPENSSL_SHA256"
        rm -rf "/home/$OPENSSL_NAME"
        tar -xzf "$OPENSSL_NAME.tar.gz"
        cd "/home/$OPENSSL_NAME"
        ./Configure linux-aarch64 no-shared no-quic no-cmp
        make build_generated
        mkdir -p /opt/openssl-kos/include
        cp -a include/openssl /opt/openssl-kos/include/
        touch /opt/openssl-kos/.kos-setup-done
    fi
fi

step "Done. In a new shell: . /etc/profile.d/kos-net.sh"

# .NET NativeAOT on KasperskyOS: build and run HelloWorld

This branch ports the .NET 10 NativeAOT runtime (based on `release/10.0`) to KasperskyOS Community
Edition on arm64, and boots the C# sample `samples/helloworldapp-kos` under QEMU. Two SDK versions
are supported. Each needs its own Linux host:

| KasperskyOS CE SDK | Host | KasperskyOS compiler | Image built by |
| --- | --- | --- | --- |
| 1.1.1.40 | Debian 11 (bullseye) | GCC 9.2.1 | `dist/kos-image.sh` (`einit`, `makeimg`) |
| 1.4.0.102 | Ubuntu 22.04 | clang 17.0.6 | the `kos-image/` CMake project |

**This branch, `kos-sdk-1.1.1.40`, is frozen:** it stays on .NET 10 and receives no further changes.
It is the last state verified on SDK 1.1.1.40. Work for SDK 1.4 and later continues on `kos_changes`.

Every step below applies to both SDKs unless its heading names one. Commands run as `root` inside the
WSL distro: `wsl --import` creates no other user. The environment variables set along the way are
used by later steps, so run everything in one shell, or set them again in a new one.

The prebuilt assets of release `release_v01` were built from .NET 8 sources and do not fit this
branch; only its `icu4c-kos.zip` is still used.

## 1. Create the WSL distro (Windows)

Any directory works in place of `T:\KasperskyOS`. The builds were run with `memory=16GB` and
`processors=12` in `%UserProfile%\.wslconfig`.

### SDK 1.1.1.40: Debian bullseye

```powershell
mkdir T:\KasperskyOS
curl.exe -L -o T:\KasperskyOS\install.tar.gz https://salsa.debian.org/debian/WSL/-/raw/v1.14.0.0/x64/install.tar.gz
wsl --import DebianKOS T:\KasperskyOS\wsl T:\KasperskyOS\install.tar.gz
wsl -d DebianKOS -u root
```

### SDK 1.4.0.102: Ubuntu 22.04

```powershell
mkdir T:\KasperskyOS
curl.exe -L -o T:\KasperskyOS\ubuntu-22.04.5-wsl-amd64.wsl https://releases.ubuntu.com/22.04/ubuntu-22.04.5-wsl-amd64.wsl
wsl --import UbuntuKOS T:\KasperskyOS\wsl-ubuntu T:\KasperskyOS\ubuntu-22.04.5-wsl-amd64.wsl --version 2
wsl -d UbuntuKOS -u root
```

## 2. Install prerequisites

### SDK 1.1.1.40: Debian packages

Bullseye has moved to `archive.debian.org`; the image's original sources no longer resolve.

```sh
echo 'deb [check-valid-until=no] http://archive.debian.org/debian bullseye main' > /etc/apt/sources.list
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends build-essential clang llvm lld lldb python-is-python3 curl wget git gdebi-core unzip file ca-certificates libicu-dev liblttng-ust-dev libssl-dev libkrb5-dev zlib1g-dev ninja-build cpio pigz
```

No separate CMake is needed: the SDK's CMake 3.22 comes first on `PATH` (step 3), and `release/10.0`
requires 3.20.

### SDK 1.4.0.102: Ubuntu packages

```sh
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get upgrade -y
apt-get install -y --no-install-recommends build-essential clang llvm lld lldb cmake python-is-python3 curl wget git gdebi-core unzip file ca-certificates libicu-dev liblttng-ust-dev libssl-dev libkrb5-dev zlib1g-dev ninja-build cpio pigz
```

## 3. Install the KasperskyOS CE SDK

### SDK 1.1.1.40: install

```sh
cd /home
wget -nc https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/1.1.1.40/multilanguage-1.1.1.40/3737323236397c44454c7c31/KasperskyOS-Community-Edition_1.1.1.40_en.deb
gdebi -n KasperskyOS-Community-Edition_1.1.1.40_en.deb
```

### SDK 1.1.1.40: environment

The SDK's `toolchain/bin` goes **first** on `PATH`, for `aarch64-kos-gcc` and its CMake.

```sh
export KOS_SDK=/opt/KasperskyOS-Community-Edition-1.1.1.40
export PATH=$KOS_SDK/toolchain/bin:$PATH
```

### SDK 1.4.0.102: install

```sh
cd /home
wget -nc https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/1.4.0.102/multilanguage-INT-1.4.0.102/fdbf0e5762204c3180f0eabca0423c55/KasperskyOS-Community-Edition-Qemu-1.4.0.102_en.deb
gdebi -n KasperskyOS-Community-Edition-Qemu-1.4.0.102_en.deb
```

### SDK 1.4.0.102: environment

The SDK's `toolchain/bin` goes on `PATH` only after the host-side build in step 6: it holds `clang`
and `clang-17` targeting KasperskyOS, and the .NET build takes the highest-versioned `clang-<N>` it
finds on `PATH`, wherever that entry is.

```sh
export KOS_SDK=/opt/KasperskyOS-Community-Edition-Qemu-1.4.0.102
```

## 4. Build environment

`NuGetAudit=false`: `release/10.0` pins `Microsoft.DiaSymReader.Native` 17.12.0-beta1.24603.5, which
NuGet's vulnerability audit now fails as an error during restore. The package only carries Windows
DLLs.

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NuGetAudit=false
```

## 5. Get ICU and the sources

ICU 73.1 from [unicode-org-icu, branch `kos_changes`](https://github.com/caraioniurie47/unicode-org-icu/tree/kos_changes)
goes to `/opt/icu4c/kos`. The build always needs it (`System.Globalization.Native` compiles against
its headers) and packs its libraries; they are linked only when `InvariantGlobalization` is false.

### SDK 1.1.1.40: prebuilt ICU

`icu4c-kos.zip` is that ICU built with the 1.1.1.40 GCC.

```sh
mkdir -p /opt/icu4c
cd /opt/icu4c
wget -nc https://github.com/caraioniurie47/runtime-kos/releases/download/release_v01/icu4c-kos.zip
unzip -o -q icu4c-kos.zip
```

### SDK 1.4.0.102: build ICU with clang

The prebuilt GCC libraries need libstdc++, which the 1.4 sysroot does not have. A host build comes
first; the cross build uses its tools.

```sh
cd /home
git clone --depth 1 --branch kos_changes https://github.com/caraioniurie47/unicode-org-icu.git
cd /home/unicode-org-icu/icu4c/source/data/in
wget -nc https://github.com/unicode-org/icu/releases/download/release-73-1/icu4c-73_1-data-bin-l.zip
unzip -j -o icu4c-73_1-data-bin-l.zip
mkdir -p /home/icu4c-build-x64 && cd /home/icu4c-build-x64
sh /home/unicode-org-icu/icu4c/source/runConfigureICU Linux/gcc --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static
make -j$(nproc)
mkdir -p /home/icu4c-build-kos && cd /home/icu4c-build-kos
(
    export PATH=$PATH:$KOS_SDK/toolchain/bin
    CC=aarch64-kos-clang CXX=aarch64-kos-clang++ AR=llvm-ar RANLIB=llvm-ranlib \
        sh /home/unicode-org-icu/icu4c/source/configure --host=aarch64-kos --with-cross-build=/home/icu4c-build-x64 --prefix=/opt/icu4c/kos --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static
    make -j$(nproc)
    make install
)
```

### Clone the runtime

```sh
cd /home
git clone --depth 1 --branch kos-sdk-1.1.1.40 https://github.com/caraioniurie47/runtime-kos.git
find /home/runtime-kos -name "*.sh" -exec chmod +x {} +
```

## 6. Build ilc-tools (host compiler)

The sample's `IlcToolsPath` is `/home/ilc-tools-10`.

```sh
cd /home/runtime-kos
./build.sh -s clr.alljits+clr.tools -c Release
mkdir -p /home/ilc-tools-10
cp -a artifacts/bin/coreclr/linux.x64.Release/ilc-published/. /home/ilc-tools-10/
git clean -ffdx
```

## 7. Cross-build the runtime for KasperskyOS

### SDK 1.4.0.102: toolchain on PATH

Last on `PATH`, so the host's own `clang` and `cmake` keep precedence.

```sh
export PATH=$PATH:$KOS_SDK/toolchain/bin
```

### SDK 1.1.1.40: cross builds with GCC

```sh
cd /home/runtime-kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.nativeaotruntime+clr.nativeaotlibs -c release --cross --gcc --kos --arch arm64 --icudir /opt/icu4c/kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s libs -c release --cross --gcc --kos --arch arm64 --icudir /opt/icu4c/kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.aottools+packs.aot -c release --cross --gcc --kos --arch arm64 --icudir /opt/icu4c/kos
```

### SDK 1.4.0.102: cross builds with clang

Without `--gcc`, the cross toolchain file picks `aarch64-kos-clang` when the SDK has it.

```sh
cd /home/runtime-kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.nativeaotruntime+clr.nativeaotlibs -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s libs -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.aottools+packs.aot -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos
```

### Copy the packages

The packages go to the feed that the sample's `nuget.config` names:

```sh
mkdir -p /home/kos-net-packages-10
cp -a /home/runtime-kos/artifacts/packages/Release/Shipping/. /home/kos-net-packages-10/
```

## 8. Publish HelloWorld

The sample links with `aarch64-kos-clang++` when the SDK next to `SysRoot` has it, otherwise with
`aarch64-kos-g++`.

```sh
cp -a /home/runtime-kos/samples/helloworldapp-kos/. /home/helloworldapp-kos/
cd /home
/home/runtime-kos/.dotnet/dotnet publish helloworldapp-kos -o helloworldapp-kos/dist -c Release -r linux-arm64 --self-contained \
    -p:PublishAot=true -p:StaticExecutable=true -p:StaticallyLinked=true -p:TargetsKOS=true \
    -p:SysRoot=$KOS_SDK/sysroot-aarch64-kos --packages helloworldapp-pkg-kos
```

## 9. Build the image and run it

QEMU runs in the foreground and does not exit by itself; stop it with Ctrl+C.

### Run on SDK 1.1.1.40

`kos-image.sh` builds the image with the SDK's `einit` and `makeimg`, then starts QEMU.

```sh
bash /home/helloworldapp-kos/dist/kos-image.sh
```

### SDK 1.4.0.102: image

SDK 1.4 has no `einit` or `makeimg`; the image is a CMake project, built with the SDK's own CMake,
which carries the `platform` modules.

```sh
$KOS_SDK/toolchain/bin/cmake -S /home/helloworldapp-kos/kos-image -B /home/helloworldapp-kos-image \
    -D CMAKE_TOOLCHAIN_FILE=$KOS_SDK/toolchain/share/toolchain-aarch64-kos.cmake \
    -D HELLO_BINARY=/home/helloworldapp-kos/dist/helloworldapp-kos
$KOS_SDK/toolchain/bin/cmake --build /home/helloworldapp-kos-image --target kos-qemu-image
```

### Run on SDK 1.4.0.102

```sh
$KOS_SDK/toolchain/bin/cmake --build /home/helloworldapp-kos-image --target sim
```

## Output

After the KasperskyOS boot log, the program prints:

```text
Hello from .NET! Math.Min(4, 7)=4
```

On SDK 1.4 it is preceded by `[hello.Hello][14:14][CRT0] Initing main app: statically-linked, PIE.`

## Limitations

- **No hardware exceptions.** KasperskyOS delivers only `SIGTERM`, so the runtime registers no
  `SIGSEGV` or `SIGFPE` handler there, and a fault such as a null dereference does not become a
  managed exception.
- **ICU adds about 37 MB to the unstripped binary.** The sample sets `InvariantGlobalization=true`; publish with
  `-p:InvariantGlobalization=false` to link ICU and its data for culture-aware formatting.
- **No cryptography or `System.Net.Security` native libraries** are built or linked.

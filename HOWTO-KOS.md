# .NET NativeAOT on KasperskyOS: build and run the samples

This branch ports the NativeAOT runtime of dotnet/runtime `main` to KasperskyOS Community Edition
1.4.0.102 on arm64, and boots two C# samples under QEMU: `samples/helloworldapp-kos` and
`samples/showcase-kos`. The build produces packages versioned `12.0.0-dev`; the samples target
`net11.0`. The .NET 10 port (based on `release/10.0`) is on the branch `kos_changes`.

| KasperskyOS CE SDK | Host | KasperskyOS compiler | Image built by |
| --- | --- | --- | --- |
| 1.4.0.102 | Ubuntu 22.04 | clang 17.0.6 | the `kos-image/` CMake project |

SDK 1.1.1.40 (GCC) is supported on the frozen branch `kos-sdk-1.1.1.40`, whose `HOWTO-KOS.md` covers it.

Commands run as `root` inside the WSL distro: `wsl --import` creates no other user. The environment
variables set along the way are used by later steps, so run everything in one shell, or set them again
in a new one.

## 1. Create the WSL distro (Windows)

Any directory works in place of `T:\KasperskyOS`. The builds were run with `memory=16GB` and
`processors=12` in `%UserProfile%\.wslconfig`.

```powershell
mkdir T:\KasperskyOS
curl.exe -L -o T:\KasperskyOS\ubuntu-22.04.5-wsl-amd64.wsl https://releases.ubuntu.com/22.04/ubuntu-22.04.5-wsl-amd64.wsl
wsl --import UbuntuKOS T:\KasperskyOS\wsl-ubuntu T:\KasperskyOS\ubuntu-22.04.5-wsl-amd64.wsl --version 2
wsl -d UbuntuKOS -u root
```

## 2. Install prerequisites

```sh
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get upgrade -y
apt-get install -y --no-install-recommends build-essential clang llvm lld lldb python-is-python3 curl wget git gdebi-core unzip file ca-certificates libicu-dev liblttng-ust-dev libssl-dev libkrb5-dev zlib1g-dev ninja-build cpio pigz
```

### CMake

`main` needs CMake 3.26 or later; Ubuntu 22.04's `apt` has 3.22. Kitware's portable build goes to
`/opt`, linked from `/usr/local/bin`, which comes before `/usr/bin` on `PATH`.

```sh
cd /home
wget -nc https://github.com/Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-linux-x86_64.tar.gz
tar -xzf cmake-3.31.12-linux-x86_64.tar.gz -C /opt
for t in cmake ctest cpack; do ln -sf /opt/cmake-3.31.12-linux-x86_64/bin/$t /usr/local/bin/$t; done
hash -r
cmake --version
```

## 3. Install the KasperskyOS CE SDK

```sh
cd /home
wget -nc https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/1.4.0.102/multilanguage-INT-1.4.0.102/fdbf0e5762204c3180f0eabca0423c55/KasperskyOS-Community-Edition-Qemu-1.4.0.102_en.deb
gdebi -n KasperskyOS-Community-Edition-Qemu-1.4.0.102_en.deb
```

### Environment

The SDK's `toolchain/bin` goes on `PATH` only after the host-side build in step 6: it holds `clang`
and `clang-17` targeting KasperskyOS, and the .NET build takes the highest-versioned `clang-<N>` it
finds on `PATH`, wherever that entry is.

```sh
export KOS_SDK=/opt/KasperskyOS-Community-Edition-Qemu-1.4.0.102
```

## 4. Build environment

`NuGetAudit=false`: on `release/10.0`, NuGet's vulnerability audit failed restore with an error on the
pinned `Microsoft.DiaSymReader.Native` beta, a package carrying only Windows DLLs. The `main` builds
kept the setting and were not tried without it.

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NuGetAudit=false
```

## 5. Get ICU and the sources

ICU 73.1 from [unicode-org-icu, branch `kos_changes`](https://github.com/caraioniurie47/unicode-org-icu/tree/kos_changes)
goes to `/opt/icu4c/kos`. The build always needs it (`System.Globalization.Native` compiles against
its headers) and packs its libraries; they are linked only when `InvariantGlobalization` is false.

### Build ICU with clang

A host build comes first; the cross build uses its tools.

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
git clone --depth 1 --branch kos-main https://github.com/caraioniurie47/runtime-kos.git
find /home/runtime-kos -name "*.sh" -exec chmod +x {} +
```

## 6. Build ilc-tools (host compiler)

The samples' `IlcToolsPath` is `/home/ilc-tools-main`.

```sh
cd /home/runtime-kos
./build.sh -s clr.alljits+clr.tools -c Release
mkdir -p /home/ilc-tools-main
cp -a artifacts/bin/coreclr/linux.x64.Release/ilc-published/. /home/ilc-tools-main/
git clean -ffdx
```

## 7. Cross-build the runtime for KasperskyOS

### Toolchain on PATH

Last on `PATH`, so the host's own `clang` and `cmake` keep precedence.

```sh
export PATH=$PATH:$KOS_SDK/toolchain/bin
```

### Cross builds with clang

The cross toolchain file picks `aarch64-kos-clang` from the SDK.

```sh
cd /home/runtime-kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.nativeaotruntime+clr.nativeaotlibs -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s libs -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.aottools+packs.aot -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos
```

### Copy the packages

The packages go to the feed that the samples' `nuget.config` names:

```sh
mkdir -p /home/kos-net-packages-main
cp -a /home/runtime-kos/artifacts/packages/Release/Shipping/. /home/kos-net-packages-main/
```

## 8. Publish HelloWorld

The sample links with `aarch64-kos-clang++` from the SDK next to `SysRoot`. Besides the local feed and
nuget.org, its `nuget.config` lists the `dotnet11` feed: the repo's SDK restores runtime and
ILCompiler packs of its own build, which nuget.org does not carry.

```sh
cp -a /home/runtime-kos/samples/helloworldapp-kos/. /home/helloworldapp-kos/
cd /home
/home/runtime-kos/.dotnet/dotnet publish helloworldapp-kos -o helloworldapp-kos/dist -c Release -r linux-arm64 --self-contained \
    -p:PublishAot=true -p:StaticExecutable=true -p:StaticallyLinked=true -p:TargetsKOS=true \
    -p:SysRoot=$KOS_SDK/sysroot-aarch64-kos --packages helloworldapp-pkg-kos
```

## 9. Build the image and run it

The image is a CMake project, built with the SDK's own CMake, which carries the `platform` modules.
QEMU runs in the foreground and does not exit by itself; stop it with Ctrl+C.

### Image

```sh
$KOS_SDK/toolchain/bin/cmake -S /home/helloworldapp-kos/kos-image -B /home/helloworldapp-kos-image \
    -D CMAKE_TOOLCHAIN_FILE=$KOS_SDK/toolchain/share/toolchain-aarch64-kos.cmake \
    -D HELLO_BINARY=/home/helloworldapp-kos/dist/helloworldapp-kos
$KOS_SDK/toolchain/bin/cmake --build /home/helloworldapp-kos-image --target kos-qemu-image
```

### Run HelloWorld

```sh
$KOS_SDK/toolchain/bin/cmake --build /home/helloworldapp-kos-image --target sim
```

After the KasperskyOS boot log, the program prints:

```text
[hello.Hello][14:14][CRT0] Initing main app: statically-linked, PIE.
...
Hello from .NET! Math.Min(4, 7)=4
```

## 10. The showcase sample

`samples/showcase-kos` runs a set of sections, each ending in `PASS`, `SKIP` or `FAIL`: runtime
information, culture-aware formatting and sorting, a `Parallel.For` Mandelbrot, async/await with
channels and timers, source-generated `System.Text.Json` and `Regex`, LINQ and generic math, the GC
under allocation load, and exceptions with stack traces. It uses the HelloWorld image project.

`InvariantGlobalization=false` links ICU, so the globalization section runs; the binary grows from
about 15 MB to about 52 MB. Without it the section reports `SKIP`.

### Publish the showcase

```sh
cp -a /home/runtime-kos/samples/showcase-kos/. /home/showcase-kos/
cd /home
/home/runtime-kos/.dotnet/dotnet publish showcase-kos -o showcase-kos/dist -c Release -r linux-arm64 --self-contained \
    -p:PublishAot=true -p:StaticExecutable=true -p:StaticallyLinked=true -p:TargetsKOS=true \
    -p:SysRoot=$KOS_SDK/sysroot-aarch64-kos -p:InvariantGlobalization=false --packages showcase-pkg-kos
$KOS_SDK/toolchain/bin/cmake -S /home/runtime-kos/samples/helloworldapp-kos/kos-image -B /home/showcase-kos-image \
    -D CMAKE_TOOLCHAIN_FILE=$KOS_SDK/toolchain/share/toolchain-aarch64-kos.cmake \
    -D HELLO_BINARY=/home/showcase-kos/dist/showcase-kos
$KOS_SDK/toolchain/bin/cmake --build /home/showcase-kos-image --target kos-qemu-image
```

### Run the showcase

```sh
$KOS_SDK/toolchain/bin/cmake --build /home/showcase-kos-image --target sim
```

The last line it prints is the summary, for example:

```text
SHOWCASE DONE: 9 passed, 0 skipped, 0 failed, 9261 ms
```

## Limitations

- **No hardware exceptions.** KasperskyOS delivers only `SIGTERM`, so the runtime registers no
  `SIGSEGV` or `SIGFPE` handler there, and a fault such as a null dereference does not become a
  managed exception.
- **No file system, network or stdout in these images.** The program starts with "VFS filesystem and
  network backends initialized with stub (related calls will return EIO)": only stderr reaches the
  console, and writing to `Console.Out` throws `IOException`. A VFS component in the image is needed
  for files and sockets; none was tried.
- **ICU adds about 37 MB to the unstripped binary**; see step 10.
- **No cryptography or `System.Net.Security` native libraries** are built or linked.

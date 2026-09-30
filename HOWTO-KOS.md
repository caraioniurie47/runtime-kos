# .NET NativeAOT on KasperskyOS: build and run the samples

This branch ports the NativeAOT runtime of dotnet/runtime `main` to KasperskyOS Community Edition
1.4.0.102 on arm64, and boots three C# samples under QEMU: `samples/helloworldapp-kos`,
`samples/showcase-kos` and `samples/webserver-kos`. The build produces packages versioned `12.0.0-dev`; the samples target
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

## 5. Get the sources and ICU

### Clone the runtime

```sh
cd /home
git clone --depth 1 --branch kos-main https://github.com/caraioniurie47/runtime-kos.git
find /home/runtime-kos -name "*.sh" -exec chmod +x {} +
```

### Build ICU with clang

ICU is .NET's own, [dotnet/icu](https://github.com/dotnet/icu) (ICU 72.1.0.4 with Microsoft's patches, listed in its `icu-patches` folder), at the
commit this branch pins for `Microsoft.NETCore.Runtime.ICU.Transport` in `eng/Version.Details.xml`, patched for
KasperskyOS (`eng/kos/icu/kos-icu.patch`). It goes to `/opt/icu4c/kos`. Its data leaves out the ICU features .NET
never calls (`eng/kos/icu/icudt_kos.json`: code page tables, transliteration, spoofing checks, character names, units,
spelled-out numbers, stringprep profiles, and break rules and dictionaries other than character breaks) and keeps
every locale, calendar, collation and display name. The build always needs ICU (`System.Globalization.Native`
compiles against its headers) and packs its libraries; they are linked only when `InvariantGlobalization` is false.

A host build comes first; the cross build uses its tools. Both read the data filter.

```sh
cd /home
git init -q dotnet-icu && cd dotnet-icu
git fetch --depth 1 https://github.com/dotnet/icu.git c6131fd7b7cebcef44c17f381b2fe325cb331964
git checkout -q FETCH_HEAD
patch -p1 -d icu < /home/runtime-kos/eng/kos/icu/kos-icu.patch
export ICU_DATA_FILTER_FILE=/home/runtime-kos/eng/kos/icu/icudt_kos.json
mkdir -p /home/icu4c-build-x64 && cd /home/icu4c-build-x64
sh /home/dotnet-icu/icu/icu4c/source/runConfigureICU Linux/gcc --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static
make -j$(nproc)
mkdir -p /home/icu4c-build-kos && cd /home/icu4c-build-kos
(
    export PATH=$PATH:$KOS_SDK/toolchain/bin
    CC=aarch64-kos-clang CXX=aarch64-kos-clang++ AR=llvm-ar RANLIB=llvm-ranlib \
        sh /home/dotnet-icu/icu/icu4c/source/configure --host=aarch64-kos --with-cross-build=/home/icu4c-build-x64 --prefix=/opt/icu4c/kos --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static
    make -j$(nproc)
    make install
)
unset ICU_DATA_FILTER_FILE
```

### OpenSSL headers

The SDK has OpenSSL 1.1.1t as static libraries (`sysroot-aarch64-kos/lib/libcrypto.a`, `libssl.a`) but no
headers. `System.Security.Cryptography.Native` compiles against the headers of that release, configured with
its defaults, which the SDK's libraries match; nothing of OpenSSL is compiled here.

```sh
cd /home
wget -nc https://www.openssl.org/source/old/1.1.1/openssl-1.1.1t.tar.gz
echo "8dee9b24bdb1dcbf0c3d1e9b02fb8f6bf22165e807f45adeb7c9677536859d3b  openssl-1.1.1t.tar.gz" | sha256sum -c
tar -xzf openssl-1.1.1t.tar.gz
cd /home/openssl-1.1.1t
./Configure linux-aarch64 no-shared
make include/openssl/opensslconf.h
mkdir -p /opt/openssl-kos/include
cp -a include/openssl /opt/openssl-kos/include/
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
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.nativeaotruntime+clr.nativeaotlibs -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos --opensslincludedir /opt/openssl-kos/include
ROOTFS_DIR=$KOS_SDK ./build.sh -s libs -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos --opensslincludedir /opt/openssl-kos/include
ROOTFS_DIR=$KOS_SDK ./build.sh -s clr.aottools+packs.aot -c release --cross --kos --arch arm64 --icudir /opt/icu4c/kos --opensslincludedir /opt/openssl-kos/include
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

Besides the program, the image holds three programs from the SDK and one of its own. The SDK's prebuilt
`VfsRamFs` serves files and stdout to the program over IPC: a RAM file system at `/tmp` and devices at
`/dev`. The SDK's prebuilt `VfsNet` serves sockets, with the SDK's network driver program; both come with
the SDK's entropy program. `NetInit`, built from `kos-image/src/netinit.c`, gives the network interface
`en0` the address QEMU user networking expects (`10.0.2.15/24`, gateway `10.0.2.2`) and exits, as the
SDK's network examples do in their own programs. `kos-image/src/init.yaml.in` sets `VFS_FILESYSTEM_BACKEND: client:kl.VfsRamFs`
and `VFS_NETWORK_BACKEND: client:kl.VfsNet` for the program and connects it to both. The program has to link the client
side, `libvfs_remote.a`, which each sample's `.csproj` adds as a `LinkerArg`. A program of your own needs
both that item and this image project.

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
[hello.Hello][19:19][CRT0] Initing main app: statically-linked, PIE.
...
[hello.Hello][19:19][CRT0] VFS filesystem backend initialized with env(client:kl.VfsRamFs)
[hello.Hello][19:19][CRT0] VFS network backend initialized with env(client:kl.VfsNet)
...
[NetInit] en0 is 10.0.2.15/255.255.255.0, gateway 10.0.2.2
Hello from .NET! Math.Min(4, 7)=4
```

## 10. The showcase sample

`samples/showcase-kos` runs a set of sections, each ending in `PASS`, `SKIP` or `FAIL`: runtime
information, files under `/tmp` and a line on `Console.Out`, TCP sockets, cryptography with OpenSSL, HTTPS
over loopback, culture-aware formatting and
sorting, a `Parallel.For` Mandelbrot, async/await with
channels and timers, source-generated `System.Text.Json` and `Regex`, LINQ and generic math, the GC
under allocation load, and exceptions with stack traces. It uses the HelloWorld image project.

The sockets section echoes 256 KiB over loopback with the `*Async` socket methods, then receives a
100,000-byte message with blocking `Socket.Receive` calls into an 81920-byte buffer. Given `-D HOST_TCP_PORT=<port>` when the image is
configured, the image project also forwards that TCP port from the host (QEMU `hostfwd`) and the section
waits up to 120 seconds for a client from the host: it sends a greeting line, reads a line and answers
`KOS echo: <line>`. Without the option it does not wait.

The cryptography section checks SHA-256 and HMAC against published test vectors, AES-GCM, RSA-2048 PSS
signatures, ECDH and an NTLM negotiate message. The HTTPS section makes a test CA and a certificate for
`127.0.0.1` signed by it, serves one request with `SslStream`, and fetches it with `HttpClient` trusting only
that CA; a second `HttpClient` with the default trust must refuse the same server.

`InvariantGlobalization=false` links ICU, so the globalization section runs; the binary grows from
about 31 MB to about 53 MB. Without it the section reports `SKIP`.

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

To try the host client, configure the image with `-D HOST_TCP_PORT=5047` added to the first `cmake`
command above, rebuild it, and once the showcase prints `listening on 0.0.0.0:5047`, run `nc localhost
5047` in a second WSL terminal and type a line.

The last line it prints is the summary, for example:

```text
SHOWCASE DONE: 15 passed, 0 skipped, 0 failed, 23826 ms
```

## 11. The web server sample

`samples/webserver-kos` serves a status page at `/` and the same figures as JSON at `/api/status`, with
`System.Net.HttpListener`; the page refreshes its figures from the JSON every two seconds. It runs until QEMU is
stopped. It uses the HelloWorld image project with `-D HOST_HTTP_PORT=8080`, which forwards that TCP port from
the host (QEMU `hostfwd`) and tells the program to listen on it.

### Publish the web server

```sh
cp -a /home/runtime-kos/samples/webserver-kos/. /home/webserver-kos/
cd /home
/home/runtime-kos/.dotnet/dotnet publish webserver-kos -o webserver-kos/dist -c Release -r linux-arm64 --self-contained \
    -p:PublishAot=true -p:StaticExecutable=true -p:StaticallyLinked=true -p:TargetsKOS=true \
    -p:SysRoot=$KOS_SDK/sysroot-aarch64-kos --packages webserver-pkg-kos
$KOS_SDK/toolchain/bin/cmake -S /home/runtime-kos/samples/helloworldapp-kos/kos-image -B /home/webserver-kos-image \
    -D CMAKE_TOOLCHAIN_FILE=$KOS_SDK/toolchain/share/toolchain-aarch64-kos.cmake \
    -D HELLO_BINARY=/home/webserver-kos/dist/webserver-kos -D HOST_HTTP_PORT=8080
$KOS_SDK/toolchain/bin/cmake --build /home/webserver-kos-image --target kos-qemu-image
```

### Run the web server

```sh
$KOS_SDK/toolchain/bin/cmake --build /home/webserver-kos-image --target sim
```

Once it prints

```text
webserver-kos: ready, open http://localhost:8080/ on the host
```

open `http://localhost:8080/` in a browser on Windows: WSL forwards the port to Windows' `localhost`. From a
second WSL terminal, `curl http://localhost:8080/api/status` prints the JSON. Each request is logged on the
KasperskyOS console.

## Limitations

What .NET does not do, or does differently, on KasperskyOS, and the test suites run there:
[LIMITATIONS-KOS.md](LIMITATIONS-KOS.md).

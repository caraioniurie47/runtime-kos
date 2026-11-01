#!/bin/bash
# Publishes a C# program for KasperskyOS, builds the image around it and boots it under QEMU, as HOWTO-KOS.md
# steps 8-11 do. Needs eng/kos/setup-host.sh (or HOWTO steps 2-4) and the packages in /home/kos-net-packages-main
# and /home/ilc-tools-main, from a release's assets or HOWTO steps 6-7.
#
# Usage, as root: eng/kos/run-sample.sh <sample> [--icu] [--tcp-port N] [--http-port N] [--no-run]
#   <sample>      helloworldapp-kos, showcase-kos or webserver-kos (from samples/), or a path with a / to a directory
#                 holding one .csproj set up as samples/helloworldapp-kos is (HOWTO "Your own program")
#   --icu         publish with InvariantGlobalization=false, linking ICU
#   --tcp-port N  forward TCP port N from the host (the showcase waits for a client on it)
#   --http-port N forward TCP port N from the host and serve HTTP on it (webserver-kos; it defaults to 8080)
#   --no-run      build the image, don't boot it
# The program is copied to /home/<name>, published to /home/<name>/dist, and imaged in /home/<name>-image. QEMU runs
# in the foreground until stopped with Ctrl+C.

set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
usage() { sed -n '6,12p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

[ $# -ge 1 ] || usage
sample=$1
shift
icu=0
tcp_port=
http_port=
run=1
while [ $# -gt 0 ]; do
    case $1 in
        --icu) icu=1 ;;
        --tcp-port) tcp_port=${2:?--tcp-port needs a port}; shift ;;
        --http-port) http_port=${2:?--http-port needs a port}; shift ;;
        --no-run) run=0 ;;
        *) usage ;;
    esac
    shift
done

[ -f /etc/profile.d/kos-net.sh ] && . /etc/profile.d/kos-net.sh
[ -n "${KOS_SDK:-}" ] && [ -d "$KOS_SDK" ] || { echo "KOS_SDK is not set or missing; run eng/kos/setup-host.sh first" >&2; exit 1; }
[ -d /home/kos-net-packages-main ] || { echo "/home/kos-net-packages-main is missing: unpack a release's packages or build them (HOWTO step 7)" >&2; exit 1; }
[ -d /home/ilc-tools-main ] || { echo "/home/ilc-tools-main is missing: unpack a release's ilc-tools or build them (HOWTO step 6)" >&2; exit 1; }

# A bare name is always the repository's sample: /home/<name>, an earlier run's copy, must not shadow it.
case $sample in
    */*) [ -d "$sample" ] || { echo "no directory $sample" >&2; exit 1; }; src=$(cd "$sample" && pwd) ;;
    *) src=$REPO/samples/$sample ;;
esac
[ -d "$src" ] || { echo "no sample $sample in $REPO/samples (a directory of your own needs a / in its path)" >&2; exit 1; }
projects=("$src"/*.csproj)
[ ${#projects[@]} = 1 ] && [ -f "${projects[0]}" ] || { echo "$src must hold exactly one .csproj" >&2; exit 1; }
name=$(basename "${projects[0]}" .csproj)
if [ "$name" = webserver-kos ] && [ -z "$http_port" ]; then
    http_port=8080
fi

export PATH=$PATH:$KOS_SDK/toolchain/bin
[ -x "$REPO/.dotnet/dotnet" ] || "$REPO/dotnet.sh" --version

echo "=== Publish $name"
if [ "$src" != "/home/$name" ]; then
    mkdir -p "/home/$name"
    cp -a "$src/." "/home/$name/"
fi
publish_args=(-p:PublishAot=true -p:StaticExecutable=true -p:StaticallyLinked=true -p:TargetsKOS=true
              -p:SysRoot="$KOS_SDK/sysroot-aarch64-kos")
if [ "$icu" = 1 ]; then
    publish_args+=(-p:InvariantGlobalization=false)
fi
cd /home
"$REPO/.dotnet/dotnet" publish "$name" -o "$name/dist" -c Release -r linux-arm64 --self-contained \
    "${publish_args[@]}" --packages "${name%-kos}-pkg-kos"

echo "=== Image /home/$name-image"
# The ports are passed even when empty, so a port from an earlier configure does not stay in the CMake cache.
"$KOS_SDK/toolchain/bin/cmake" -S "$REPO/samples/helloworldapp-kos/kos-image" -B "/home/$name-image" \
    -D CMAKE_TOOLCHAIN_FILE="$KOS_SDK/toolchain/share/toolchain-aarch64-kos.cmake" \
    -D HELLO_BINARY="/home/$name/dist/$name" -D HOST_TCP_PORT="$tcp_port" -D HOST_HTTP_PORT="$http_port"
"$KOS_SDK/toolchain/bin/cmake" --build "/home/$name-image" --target qemu

if [ "$run" = 1 ]; then
    echo "=== Boot under QEMU (stop with Ctrl+C)"
    exec "$KOS_SDK/toolchain/bin/cmake" --build "/home/$name-image" --target sim
fi

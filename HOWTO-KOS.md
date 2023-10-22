_Tested with Debian bullseye and KOS 1.1.1.40_ 

### WSL image initialization:

Download: https://salsa.debian.org/debian/WSL/-/raw/v1.14.0.0/x64/install.tar.gz to T:\KasperskyOS
wsl --import DebianKOS T:\KasperskyOS\wsl "T:\KasperskyOS\install.tar.gz"
wsl -d DebianKOS

### Install prerequisites:

cd /home
sudo apt update && sudo apt upgrade

sudo apt install -y git wget gdebi
**OR if you want to build .NET:** 
sudo apt install -y cmake llvm lld clang build-essential python-is-python3 curl git lldb libicu-dev liblttng-ust-dev libssl-dev libkrb5-dev zlib1g-dev wget gdebi

### Install KasperskyOS CE SDK:

cd /home
wget https://products.s.kaspersky-labs.com/special/KasperskyOSCommunityEdition/1.1.1.40/multilanguage-1.1.1.40/3737323236397c44454c7c31/KasperskyOS-Community-Edition_1.1.1.40_en.deb

sudo gdebi KasperskyOS-Community-Edition_1.1.1.40_en.deb
export PATH=/opt/KasperskyOS-Community-Edition-1.1.1.40/toolchain/bin:$PATH

### Download icu4c: 

mkdir -p "/opt/icu4c"
cd /opt/icu4c

wget https://github.com/caraioniurie47/runtime-kos/releases/download/release_v01/icu4c-kos.zip
unzip icu4c-kos.zip

**OR Build icu4c:**

cd /home
git clone --branch kos_changes https://github.com/caraioniurie47/unicode-org-icu.git
cd /home/unicode-org-icu/icu4c/source/data/in
wget https://github.com/unicode-org/icu/releases/download/release-73-1/icu4c-73_1-data-bin-l.zip
unzip -j icu4c-73_1-data-bin-l.zip
cd /home
mkdir icu4c-build-x64 && cd icu4c-build-x64

sh /home/unicode-org-icu/icu4c/source/runConfigureICU Linux/gcc --prefix=/opt/icu4c/x64 --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static

make && sudo make install

cd /home
mkdir icu4c-build-kos && cd icu4c-build-kos

sh /home/unicode-org-icu/icu4c/source/configure --host=aarch64-kos --with-cross-build=/home/icu4c-build-x64 --prefix=/opt/icu4c/kos --enable-static --disable-shared --disable-samples --disable-tests --disable-extras --disable-draft --disable-dyload --disable-icuio --with-data-packaging=static

make && sudo make install

### Clone KOS .NET repository:

cd /home
git clone --branch release_v01 https://github.com/caraioniurie47/runtime-kos.git
sudo find /home/runtime-kos -name "*.sh" -exec chmod +x {} +
export DOTNET_CLI_TELEMETRY_OPTOUT=1

### Download ilc-tools:

cd /home
wget https://github.com/caraioniurie47/runtime-kos/releases/download/release_v01/ilc-tools.zip
unzip ilc-tools.zip

**OR Build ilc-tools:**

cd /home/runtime-kos
git clean -ffdx
sudo env "PATH=$PATH" ./build.sh -s clr.alljits+clr.tools -c Release

mkdir -p "/home/ilc-tools" && cp artifacts/bin/coreclr/linux.x64.Release/ilc-published/* /home/ilc-tools/

### Download .NET NativeAOT package:

cd /home
wget https://github.com/caraioniurie47/runtime-kos/releases/download/release_v01/kos-net-packages.zip
unzip kos-net-packages.zip

**OR Build .NET NativeAOT package:**

cd /home/runtime-kos
git clean -ffdx

sudo env "PATH=$PATH" ROOTFS_DIR="/opt/KasperskyOS-Community-Edition-1.1.1.40" ./build.sh -s clr.nativeaotruntime+clr.nativeaotlibs -c release --cross --gcc --kos --arch arm64 --icudir "/opt/icu4c/kos"

sudo env "PATH=$PATH" ROOTFS_DIR="/opt/KasperskyOS-Community-Edition-1.1.1.40" ./build.sh -s libs -c release --cross --gcc --kos --arch arm64 --icudir "/opt/icu4c/kos"

sudo env "PATH=$PATH" ROOTFS_DIR="/opt/KasperskyOS-Community-Edition-1.1.1.40" ./build.sh -s clr.aottools+packs.aot -c release --cross --gcc --kos --arch arm64 --icudir "/opt/icu4c/kos"

cp -a artifacts/packages/Release/Shipping/. /home/kos-net-packages/

### Install .NET (if build was not run):

cd /home/runtime-kos
sudo ./dotnet.sh

### Build and Run HelloWorld sample for KOS:

if not run -> export PATH=/opt/KasperskyOS-Community-Edition-1.1.1.40/toolchain/bin:$PATH

cd /home/runtime-kos
cp -a samples/helloworldapp-kos/. /home/helloworldapp-kos/
cd /home
/home/runtime-kos/.dotnet/dotnet publish helloworldapp-kos -o helloworldapp-kos/dist -p:PublishAot=true -c Release -r "linux-arm64" -p:StaticExecutable=true -p:StaticallyLinked=true -p:TargetsKOS=true -p:SysRoot="/opt/KasperskyOS-Community-Edition-1.1.1.40/sysroot-aarch64-kos" --packages "helloworldapp-pkg-kos" --self-contained

sudo chmod +x /home/helloworldapp-kos/dist/kos-image.sh
/home/helloworldapp-kos/dist/kos-image.sh

### Output:

![hello_from_net](https://github.com/caraioniurie47/runtime-kos/assets/444025/418f62d9-013b-48b2-8d2d-864a9230f636)

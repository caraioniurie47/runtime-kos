# A build host for the KasperskyOS port in a container, in place of a WSL distro (HOWTO-KOS.md "In a container").
#
#   docker build --build-arg ACCEPT_KASPERSKYOS_EULA=yes -t kos-net-host eng/kos
#   docker run -it -v kos-net-home:/home -p 8080:8080 kos-net-host
#
# ACCEPT_KASPERSKYOS_EULA=yes states that you have read and accepted the KasperskyOS CE SDK's licence agreement
# (setup-host.sh names its URL). The image then holds the SDK, which that agreement lets you use but not give to
# others (clause 4.2): build the image yourself and keep it to yourself; don't push it to a registry.

FROM ubuntu:24.04
ARG ACCEPT_KASPERSKYOS_EULA=no
COPY setup-host.sh /usr/local/bin/kos-setup-host.sh
RUN ACCEPT_KASPERSKYOS_EULA=$ACCEPT_KASPERSKYOS_EULA bash /usr/local/bin/kos-setup-host.sh \
    && rm -f /home/*.deb /home/*.tar.gz
ENV KOS_SDK=/opt/KasperskyOS-Community-Edition-Qemu-arm64-2.0.0.179 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NuGetAudit=false
WORKDIR /home
CMD ["bash", "-l"]

/* Gives the network interface en0 an IPv4 address, netmask and default gateway through VfsNet, then exits.
 * kos_net.h's defaults match QEMU user networking: 10.0.2.15/24, gateway 10.0.2.2, which is also the guest
 * address a QEMU hostfwd rule forwards to. */

#include <stdio.h>
#include <stdlib.h>

#include <kos_net.h>

int main(void)
{
    if (!wait_for_iface(DEFAULT_INTERFACE, IWF_EXISTS, DEFAULT_TIMEOUT))
    {
        fprintf(stderr, "[NetInit] %s did not appear\n", DEFAULT_INTERFACE);
        return EXIT_FAILURE;
    }

    if (!configure_net_iface(DEFAULT_INTERFACE, DEFAULT_ADDR, DEFAULT_MASK, DEFAULT_GATEWAY, DEFAULT_MTU))
    {
        fprintf(stderr, "[NetInit] configuring %s failed\n", DEFAULT_INTERFACE);
        return EXIT_FAILURE;
    }

    fprintf(stderr, "[NetInit] %s is %s/%s, gateway %s\n", DEFAULT_INTERFACE, DEFAULT_ADDR, DEFAULT_MASK,
            DEFAULT_GATEWAY);
    return EXIT_SUCCESS;
}

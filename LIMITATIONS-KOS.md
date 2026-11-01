# .NET on KasperskyOS: limitations

What .NET does not do, or does differently, when a program built with this port runs on KasperskyOS Community Edition.
It covers the branch `kos-main`, KasperskyOS CE SDK **2.0.0.179**, arm64, under QEMU; nothing was run on hardware.
SDK 1.4.0.102 is covered by the frozen branch `kos-sdk-1.4.0.102`.
[HOWTO-KOS.md](HOWTO-KOS.md) builds the port and the samples.

Each entry says where the difference comes from:

- **KasperskyOS**: documented in the [KasperskyOS CE 2.0 documentation](https://support.kaspersky.com/kos-community-edition/2.0/)
  or a design choice of the system (a Linux interface it does not have). Expect it to stay.
- **SDK**: undocumented, or unlike POSIX or the documentation; it may change with an SDK release, and the port keeps a
  list of these for the KasperskyOS team. The public repository [kos-sdk-checks](https://github.com/caraioniurie47/kos-sdk-checks) holds C programs
  that show many of these without .NET, with their KasperskyOS and Linux output.
- **Image**: missing from the samples' image, not from KasperskyOS; an image of your own can add it.
- **Port**: a choice of this port.
- **.NET**: a .NET defect that KasperskyOS happens to expose.

In the sources, every KasperskyOS workaround, and every test skipped or failing for a KasperskyOS cause, carries a
marker naming its cause:
`KOS-DOC(<documentation page>)`, `TODO-KOS(<id>)` (an SDK finding; the ids are listed in
[SDK differences the port absorbs](#sdk-differences-the-port-absorbs)), `KOS-NOT-LINUX`, `TODO-KOS-IMAGE` or
`KOS-PERF`. `git grep -n -E "KOS-DOC|TODO-KOS|KOS-NOT-LINUX|KOS-PERF"` finds them all.

## Programs

- **NativeAOT only: one statically linked executable per program.** There is no JIT and no `dotnet` host. Programs are
  published for `linux-arm64` with `TargetsKOS=true` (HOWTO step 8). To .NET the system is Linux:
  `OperatingSystem.IsLinux()` is true, and `RuntimeInformation.OSDescription` is built from the SDK's product name and
  version (`KasperskyOS-Community-Edition-Qemu-arm64 2.0.0.179`), since KasperskyOS's `uname()` returns constants. *Port.*
- **`[DllImport]` needs a link-time binding.** The executable is static and nothing resolves a library name at run
  time, so a call into a library that was not bound when linking throws `EntryPointNotFoundException`, `"libc"`
  included. Bind each entry point with `<DirectPInvoke Include="libc!geteuid" />` (one item per function: binding all of
  `libc` fails the link when the code also imports functions KasperskyOS's libc lacks). The runtime's own native
  libraries are bound this way already. *Port.*
- **No program path.** The executable is loaded from the image, and there is no procfs, so `AppContext.BaseDirectory` is
  empty (by the source, `Environment.ProcessPath` is null too). A file looked up "beside the app" resolves against the
  working directory, `/`. Write such files to `/tmp` at run time, or ship them in the image where a VFS program
  serves them to the program (done on SDK 1.4 with VfsRamFs mounting the ROMFS; not tried on SDK 2.0). *KasperskyOS.*
- **No child processes.** KasperskyOS has no `fork` or `exec` (`posix_uns_ifaces`): `Process.Start` throws
  `Win32Exception` ("Not supported"). *KasperskyOS.*
- **Other processes cannot be inspected.** `Process.GetProcesses`, `GetProcessesByName`, and a process's modules,
  threads, handle count and start time read procfs, which KasperskyOS does not have, and fail. `kill()` sends only
  `SIGTERM` and ignores its pid (`posix_uns_ifaces`), so .NET cannot tell whether another process is alive and treats
  every other pid as exited; the current process is always reported alive. *KasperskyOS.*
- **No crash dumps.** `createdump` runs as a child process, so the runtime does not launch it. *KasperskyOS.*

## Runtime

- **Hardware exceptions: null dereferences only.** KasperskyOS delivers no `SIGSEGV`, so the runtime registers a
  process-wide fault handler with `KnTaskSetExceptionHandler`. A null dereference in managed code becomes a
  `NullReferenceException`, on any thread. A fault the runtime does not claim, such as one in native code, goes to the
  handler registered before it and otherwise ends the task. A stack overflow ends the task, because the handler runs on
  the stack that overflowed. Integer division by zero needs no signal on arm64 (the compiler emits a check), so
  `DivideByZeroException` is thrown as elsewhere. The handler depends on undocumented kernel structures (SDK finding
  12). *KasperskyOS, SDK.*
- **A garbage collection waits for GC polls.** Without signals the runtime cannot interrupt a thread running managed
  code, so a collection waits until each such thread checks for a pending suspension: at a GC poll or on return from a
  P/Invoke. The KasperskyOS build puts a GC poll in every loop that has no call (`--codegenopt:JitGCPollLoops=1`): a
  `GC.Collect` that took 19.5 s while another thread spun on a flag took 13 ms with the polls. Loops the compiler does
  not poll still hold up a collection until they end: loops closed by a tail call or by exception-handling flow, and
  methods compiled fully interruptible (debuggable code). *KasperskyOS, SDK (finding 11).*
- **Memory the GC gives back stays with the process.** `madvise(MADV_DONTNEED)` frees nothing and `MAP_FIXED` over a
  mapping is not supported (`posix_ifaces_impl_features`), so the GC's decommit zeroes the pages and makes them
  inaccessible; they are not returned to the system. Reservations use `MAP_NORESERVE`, because a plain `PROT_NONE`
  reservation commits its memory at once (512 MiB took tens of seconds; `mem-check.c` in kos-sdk-checks). *SDK
  (finding 8).*
- **Running out of memory ends the process in the samples' image.** The GC reserves with `MAP_NORESERVE` (above), and
  KasperskyOS takes such memory only when it is first written: making it read-write succeeds even when too little is
  free, so in the runs made no allocation failed for .NET to report as `OutOfMemoryException`. What happens at the
  write is the kernel's overcommit mode. SDK 2.0's default makes the writing thread wait for free pages, so a
  program that exhausts memory on its own waits for good (a program writing 64 MiB arrays stopped after 1152 MiB with
  `[OCMT] ... is waiting for free pages`). The
  samples' image boots with `vmm.overcommit=exception` instead (`KOS_KERNEL_CMDLINE`, HOWTO step 9): the kernel ends
  the process at that write (`Unhandled Overcommit` on the console; the same program ended after 1408 MiB).
  `vmm.overcommit=disabled` commits every reservation when it is made, which the GC's reservations do not fit. An
  image of your own chooses the mode. *KasperskyOS (documented), SDK (findings 8, 8a), Port.*
- **Signals: registrations succeed and never fire.** The kernel delivers no signal but `SIGTERM`, the only one `kill()`
  can send (`posix_uns_ifaces`). The runtime therefore installs no signal handler: `Console.CancelKeyPress` and
  `PosixSignalRegistration.Create` succeed, for any signal (even `SIGKILL`, which Linux refuses), and their handlers
  never run; `SIGTERM` keeps its default action. *KasperskyOS, Port.*
- **At most 512 descriptors per process** (`OPEN_MAX`, `sysconf(_SC_OPEN_MAX)`); the next one fails with `EMFILE`,
  which .NET reports as "Too many open files in system". `setrlimit` cannot raise it (not implemented), so the runtime's
  startup raise of the limit does nothing. System.Net.Sockets.Tests reaches it now and then when its parallel test
  classes overlap; which tests fail varies, so none is marked. *SDK (finding 6x).*
- **No diagnostics tracing through LTTng.** The Linux EventSource-to-LTTng bridge is not built. *KasperskyOS.*

## Files

The samples' image gives the program one file system, the SDK's VfsRamFs (HOWTO step 9): RAM, `/` and `/tmp`
included, with devices at `/dev`. What follows was measured there; other VFS programs were not
tried.

- **VfsRamFs runs single-threaded in the samples' image.** On SDK 1.4, under concurrent file calls from one program
  VfsRamFs could fault or hang, and every client then lost its file system. The KasperskyOS team's workaround was one
  server thread per client and per process (`VFS_SERVER_MAX_THREADS_PER_CLIENT` and `..._PER_PROCESS` 1 in its
  environment); the samples' image keeps both (`VFSRAMFS_MAX_THREADS` in `kos-image/CMakeLists.txt`). On SDK 2.0 the
  C reproducer no longer faults and one full System.IO.FileSystem.Tests run without the limit passed, in about half
  the time; the limit stays until more runs agree. Report:
  [forum topic 59792](https://forum.kaspersky.com/topic/vfsramfs-crashes-null-dereference-in-inode_destructor-under-concurrent-statreaddirunlink-kasperskyos-ce-140102-59792/),
  reproducer [kos-vfsramfs-repro](https://github.com/caraioniurie47/kos-vfsramfs-repro). *SDK (finding 6f).*
- **`FileShare` is not enforced.** There is no `flock` (no `sys/file.h`), so opening a file another handle holds with
  `FileShare.None` succeeds. *KasperskyOS.*
- **`FileSystemWatcher` does not work.** There is no inotify, and the call that starts watching fails with `ENOTSUP`.
  Not run on KasperskyOS. *KasperskyOS.*
- **uid 0 is not a superuser.** The process reports uid and gid 0, yet file permissions apply to it: writing a
  read-only file throws `UnauthorizedAccessException`, where root on Linux may write it. *SDK (finding 6d).*
- **`UnixFileMode.SetUser` and `SetGroup` are dropped on directories** (kept on files). *SDK (finding 6d).*
- **`File.Delete` on an empty directory deletes it**, where Linux throws. *SDK (finding 6e).*
- **Setting the times of a read-only file throws `UnauthorizedAccessException`**, even for the process that created it. *SDK (finding 6e).*
- **Moving to an over-long path throws `IOException` ("Invalid argument")**, not `PathTooLongException`. *SDK (finding 6e).*
- **`preallocationSize` is ignored.** `fallocate()` has no `FALLOC_FL_*` flags, so the port does not preallocate:
  opening a file with a preallocation larger than the free space succeeds, and the
  `WhenDiskIsFullTheErrorMessageContainsAllDetails` tests fail. *SDK (finding 5).*
- **Enumerating a directory that is removed meanwhile throws**, where Linux ends the enumeration: `readdir` fails with
  `ENOENT`, which POSIX allows. *SDK (finding 6l).*
- **`CreationTime` cannot be set.** KasperskyOS keeps a birth time, which setting the file's times does not change.
  *KasperskyOS.*
- **A file name can be 511 characters long** (`NAME_MAX`), not 255. *KasperskyOS.*
- **No hard links** (`link()` fails with `ENOSYS`, though the documentation lists it as implemented); symbolic links
  work. **No FIFOs**: `mkfifo()` is a stub (`posix_uns_ifaces`). *SDK (finding 6b), KasperskyOS.*
- **`DriveInfo` sees a RAM drive with no free space.** `DriveType` is `Ram`; `AvailableFreeSpace` and `TotalFreeSpace`
  are 0 and `TotalSize` is the space in use, because VfsRamFs's `statvfs` reports so (POSIX leaves the values
  unspecified). *SDK (finding 6s); `DriveType`: Image.*
- **No procfs.** Anything that reads `/proc` fails; see [Programs](#programs) and [Networking](#networking).
  *KasperskyOS.*
- **Files and stdout need a VFS program.** The program reaches them through the VFS client, configured by
  `VFS_FILESYSTEM_BACKEND` (HOWTO step 9). Every SDK 2.0 image runs MinimalKOS's own VFS program, so an image without
  one was not tried on 2.0; on SDK 1.4, file calls and `Console.Out` then threw `IOException` after an 11 s wait.
  *KasperskyOS; the wait: SDK (finding 14).*

## Networking

Sockets are served by the SDK's VfsNet over IPC; the samples' image connects the program to it (HOWTO step 9).
`Socket`, `TcpListener`, `TcpClient`, `UdpClient`, `NetworkStream`, `HttpListener`, `HttpClient`, `SslStream` and
named pipes (Unix domain sockets) work; see [What was tested](#what-was-tested).

- **No IPv6.** The documentation describes IPv6 functions, but the SDK's network stack is built without IPv6: creating
  an `AF_INET6` socket fails with `EAFNOSUPPORT`, and `Socket.OSSupportsIPv6` is false. Use IPv4 addresses
  (`IPAddress.Loopback`, not `IPv6Loopback` or dual-mode sockets). *SDK (finding 6g).*
- **VfsNet serves each program with 5 threads by default.** A socket call that blocks (a synchronous `Receive`,
  `Accept`, a `Send` to a full socket) holds one of them until it returns; with all of them held, every other socket
  call of the program waits, and the program stalls for good. The documentation states the limit
  (`limitations_and_known_problems`); the samples' image raises it to 64 (`VFS_SERVER_MAX_THREADS_PER_CLIENT` in
  VfsNet's environment, `kos-image/einit/product_init.yaml.in`, from the cache variable
  `VFSNET_MAX_THREADS_PER_CLIENT`), and an image of your own needs the same. *KasperskyOS.*
- **Host names resolve through VfsNet**, from its own `/etc/hosts` or its DNS server (8.8.8.8 in the samples' image).
  Without a hosts file not even `localhost` resolves, so the samples' image copies one
  (`kos-image/resources/romfs/etc/hosts`) to `/etc/hosts` in the root file system, where VfsNet reads it, as the SDK's
  `net_with_separate_vfs` example does; an `/etc/hosts` the
  program writes is not VfsNet's and changes nothing.
  *SDK (finding 6k).*
- **Socket events are polled.** KasperskyOS has neither epoll nor kqueue, so the port's socket event loop polls with
  `poll()`: a socket registered while the loop waits is polled up to 10 ms later. Every socket call is an IPC call to
  VfsNet: under QEMU a loopback connect took a median 12 ms, and calls ran several times slower while many sockets were
  busy. *KasperskyOS, Port.*
- **UDP on loopback can reorder datagrams under load** (7 of 150 pairs in one measurement; UDP does not promise order,
  Linux loopback keeps it). *KasperskyOS.*
- **A datagram can be sent or received from at most 10 buffers** (`IOV_MAX` 10); more fail with "Invalid argument",
  not `MessageSize`. Stream sockets are not affected. *SDK (finding 6v).*
- **A connected UDP socket cannot be disconnected.** A second `Connect` to port 0 fails ("Can't assign requested
  address"), and `connect()` cannot dissolve a UDP association. *SDK (finding 6m).*
- **`Socket.Available` on a UDP socket counts 16 bytes more than the queued datagram** (the sender's address, as in
  NetBSD). *SDK (finding 6o).*
- **`Socket.Disconnect` on an unconnected TCP socket does not throw**, because `shutdown()` succeeds. *SDK (finding 6i).*
- **`SendBufferSize` or `ReceiveBufferSize` of 0 throws** ("Invalid argument") on a TCP socket, and
  `SendBufferSize` 0 on a UDP one. *SDK (finding 6q).*
- **Socket options from BSD, not Linux.** The stack is NetBSD's: no TCP Fast Open; no `DontFragment` (no
  `IP_MTU_DISCOVER` or `IP_DONTFRAG`); `ReceiveBufferSize` reads back as set, not doubled; a raw socket's protocol
  reads `Unknown` when a `Socket` is made from its handle (no `SO_PROTOCOL`); no netlink. The port maps
  `SO_REUSEPORT` and `IP_MULTICAST_IF` to their NetBSD forms, so `ReuseAddress`, `ExclusiveAddressUse` and
  `MulticastInterface` work. *KasperskyOS.*
- **Unix domain sockets have no abstract addresses**, and their socket files live in VfsNet's own file system, not
  VfsRamFs's, so `stat()` in the program's file system does not find them (`posix_ifaces_impl_features`). Named pipes
  work. *KasperskyOS.*
- **`NetworkInformation`: interfaces, not statistics.** `NetworkInterface.GetAllNetworkInterfaces()` works; the
  loopback interface is `lo0` (its BSD name), the QEMU one `en0`. What Linux reads from procfs is unavailable: TCP and
  UDP statistics and connection lists from `IPGlobalProperties`, and interface statistics. There is no
  `/etc/resolv.conf` in a program's file system, so `DnsSuffix` throws `PlatformNotSupportedException`, and
  `IPGlobalProperties.DomainName` is empty. *KasperskyOS.*
- **Without VfsNet, no sockets.** In an image with VfsRamFs but no VfsNet, creating a socket throws `SocketException`
  ("Unknown socket error"). *KasperskyOS.*

## Security

- **Cryptography is the SDK's OpenSSL 3.5.7**, linked statically (SDK 1.4 had 1.1.1t). The SDK ships no OpenSSL
  headers; the port compiles against the 3.5.7 release's, configured without QUIC and CMP as the SDK's libraries are.
  The showcase's TLS 1.3 handshake, AES-GCM, RSA, ECDH and HMAC work. *SDK (finding 1).*
- **No RC2.** The SDK's OpenSSL has no legacy provider (its `libcrypto.a` holds no RC2, RC4 or MD4 implementation,
  and there are no provider modules to load), so `RC2` encryption and decryption throw `CryptographicException`
  ("digital envelope routines::unsupported"), not `PlatformNotSupportedException`: .NET checks only that OpenSSL
  names the cipher, which OpenSSL 3 does without the legacy provider on Linux too. SDK 1.4's 1.1.1t had RC2.
  *SDK, .NET.*
- **No CA certificates in the image.** The default trust refuses every server, public ones included, and the
  `LocalMachine` `Root` store is empty. Either trust CAs in code (`X509ChainPolicy.CustomTrustStore`, as the showcase
  does), or set `SSL_CERT_FILE` in the program's environment (the image's `HELLO_ENV`) to a PEM bundle the program
  can read: on SDK 1.4 a probe that wrote a CA bundle to `/tmp` before its first request fetched
  `https://example.com/` with the default trust. `SSL_CERT_DIR` was not tried. *Image.*
- **No Kerberos.** The SDK has no GSSAPI library, so there is no `System.Net.Security.Native`; NTLM and Negotiate use
  .NET's managed implementation, which does NTLM only. *SDK (finding 3).*

## Globalization, time, console

- **ICU adds about 22 MB.** ICU is linked unless the program sets `InvariantGlobalization=true`, as the HelloWorld and
  web server samples do; the showcase grows from about 35 MB to about 57 MB with it (unstripped). Its data leaves out
  only ICU features .NET never calls (HOWTO step 5); System.Globalization's test suites give the same results with and
  without that filter. *Port.*
- **No time zone database.** .NET reads tzdata files under `/usr/share/zoneinfo`, which the image lacks, so looking up
  a zone such as `Europe/London` fails. A program that needs zones ships the files and sets `TZDIR` to their directory
  (the image's `HELLO_ENV`); not tried on KasperskyOS. *Image.*
- **No terminfo database in the image.** What .NET's console does without one was not examined on KasperskyOS.
  *Image.*
- **Serial ports: no hardware flow control.** KasperskyOS's `termios.h` has no `CRTSCTS`, so configuring a port with
  `Handshake.RequestToSend` fails, and `RequestToSendXOnXOff` gets XON/XOFF only. No serial port was tried.
  *KasperskyOS.*

## What was tested

The dotnet/runtime library test suites below, built as NativeAOT for KasperskyOS and run in the samples' image under
QEMU, with the CI's test filters. "Failed" counts are those of the run named; the causes are above.

SDK 2.0.0.179, 2026-10-20, the samples' image at its defaults (VfsRamFs one thread, `vmm.overcommit=exception`), ICU
linked. A suite marked *stalled* stopped printing before its end in some runs, and those runs have no result; the
cause is not known yet.

| Suite | Result | Failures |
| --- | --- | --- |
| System.Runtime.Tests | 77325 run, 0 failed, 193 skipped | |
| System.Collections.Tests | 33679 run, 0 failed, 134 skipped | |
| System.Linq.Tests | 52250 run, 0 failed, 24 skipped | |
| System.Globalization.Tests (ICU) | 61639 run, 0 failed, 11 skipped | |
| System.Globalization.Extensions.Tests, .Calendars.Tests | 689 and 1477 run, 0 failed | |
| System.Threading.Channels.Tests | 1589 run, 0 failed, 14 skipped | |
| System.Console.Tests | 4287 run, 0 failed, 22 skipped | |
| System.IO.FileSystem.Tests | 8468 run, 0 failed, 261 skipped | named-pipe class run separately: 128 run, 0 failed; with VfsRamFs's default threads, two full runs of 8596 passed and one stalled |
| System.IO.FileSystem.DriveInfo.Tests | 6 run, 0 failed | |
| System.Net.Sockets.Tests | 2124 run, 1 failed, 378 skipped | a timeout under the suite's load (`ReceiveAsync_ConcurrentDispose_*`), as on 1.4 (which failed `ConnectAsync_WithLargeBuffer_*` so); *stalled* in one of four runs, slow in another |
| System.Net.NetworkInformation.Functional.Tests | 248 run, 0 failed, 13 skipped | |
| System.Net.Security.Tests | *stalled* | before the stall, only `SslStream_RandomSizeWrites_OK` (3), which emulation makes too slow for its fixed 60 s, as on 1.4 |
| System.Security.Cryptography.Tests | *stalled* | before the stall, 184 failed, all from the missing RC2 ([Security](#security)): the tests' RC2 check expects `PlatformNotSupportedException` |
| System.Diagnostics.Process.Tests | 454 run, 0 failed, 256 skipped | |
| System.Runtime.InteropServices.Tests | 2461 run, 0 failed, 164 skipped | |
| System.IO.Ports.Tests | 919 run, 0 failed, 797 skipped | no serial ports, as on Linux without them |
| System.Text.RegularExpressions.Tests | 23404 run, 8 failed, 15 skipped | the same 8 fail as NativeAOT on linux-x64 |
| System.Text.Json.Tests (`System.Text.Json.Tests` namespace, reflection on) | 17916 run, 71 failed | NativeAOT's: every failure also fails as NativeAOT on linux-x64 |

On SDK 1.4.0.102 the same suites ran without a stall (results on the branch `kos-sdk-1.4.0.102`).

Each KasperskyOS-specific skip carries its marker in its skip reason or beside its condition in the source.

## SDK differences the port absorbs

These need nothing from a program; they are listed so that the markers in the sources can be looked up. The ids are
those of `TODO-KOS(<id>)`; 8c is marked `KOS-DOC(posix_uns_ifaces)`, which documents the refusal but not its error.
Each was found on SDK 1.4.0.102 and is unchanged on 2.0.0.179 unless its row says otherwise.

| Id | KasperskyOS CE | What the port does |
| --- | --- | --- |
| 2 | `libcrypto.a` references a `getentropy()` no library defines | defines it on the kernel's random generator |
| 4 | headers declare XSI and optional functions (`getgrent`, `shm_open`, robust mutexes) no library defines | stays off those paths; a user's groups are their primary group only |
| 4a | `pthread_condattr_init` fails on memory that looks initialized | zeroes the attribute first |
| 4b | `dlerror()` is declared `const char *` | casts |
| 4c | no `SA_RESETHAND`, `SA_NODEFER` | compiles them out |
| 5 | `fallocate()` without `FALLOC_FL_*` flags | does not use it |
| 6 | headers portable code probes are absent (`sys/statfs.h`, `mntent.h`, `cpu_set_t`, ...) | NetBSD equivalents |
| 6a | `mkstemps()` fails with `EINVAL` | creates temporary files itself (`Path.GetTempFileName` works) |
| 6c | `pread`, `pwrite`, `ftruncate`, `fsync` fail with `ENOSYS` on `/dev/null` and pipes (2.0: `pread` and `pwrite` on `/dev/null` work, `pread` on a pipe fails with `ESPIPE`; `pwrite` on a pipe not checked on either SDK) | maps to the errors .NET handles (`/dev/null` and pipe I/O work) |
| 6h | `sendfile()` to a socket fails with `EINVAL` | read/write loop (`Socket.SendFile` works) |
| 6j | an accepted socket inherits the listener's `O_NONBLOCK` | clears it |
| 6t | the stack honours `SO_REUSEPORT`, the headers lack it | defines NetBSD's value |
| 6u | `getsockopt()` rejects a NULL buffer of length 0 | passes a one-byte buffer |
| 7 | `uname()` returns constants | reports the SDK's product and version |
| 8 | `PROT_NONE` reservations commit memory; `MADV_DONTNEED` frees nothing | `MAP_NORESERVE`; decommit keeps pages ([Runtime](#runtime)) |
| 8b | `sysconf(_SC_PHYS_PAGES)` fails | reads the kernel's memory statistic |
| 8c | write+execute memory refused (`posix_uns_ifaces`), with `ENOMEM` rather than the documented `ENOTSUP` | delegate thunks are written read-write, then made read-execute (`Marshal.GetFunctionPointerForDelegate` works) |
| 8d | a read-only image segment cannot be made writable | read-only GS cookie off, as on Apple platforms and OpenBSD |
| 9 | `poll()` fails the whole call on a closed descriptor | retries per descriptor; polls 512 at a time |
| 9a | (2.0) a `close()` while another thread's `poll()` holds the descriptor ends the process: the kernel's invalid-handle detection, on by default | a close waits for the socket event loop's poll that holds the descriptor (at most one 10 ms round plus the poll call) |
| 10, 10a | a read over 64 KiB once failed; `sendmsg()` over 64 KiB fails on TCP | reads at most 64 KiB at a time; retries such a `sendmsg()` with 64 KiB |
| 10b | `setsockopt()` fails after the peer's reset | treats it as done, so the socket is closed |
| 10c | `accept()` fails for an address buffer over 128 bytes | passes 128 |
| 11 | no API to interrupt another thread | GC polls in loops ([Runtime](#runtime)) |
| 12 | a fault handler cannot redirect the faulting thread | restores the trap frame itself ([Runtime](#runtime)) |
| 13 | no way to query CPU features | skips .NET's startup check for the arm64 baseline |
| 15 | `aarch64-kos-clang` ignores `-static-pie` | links with `-static` |
| 16 | (SDK 1.1.1.40) an interpreter entry in a static executable breaks thread-local storage | links with `--no-dynamic-linker` |

The SDK findings 1, 3, 6b, 6d, 6e, 6f, 6g, 6i, 6k, 6l, 6m, 6o, 6q, 6s, 6v, 8a and 14 are described above. Linux
interfaces KasperskyOS does not have, handled inside the port with no effect found on programs (`KOS-NOT-LINUX`): cgroups, NUMA,
`/proc/meminfo`, `memfd_create`, `membarrier`, `mlock`, the futex system call, `malloc_usable_size`, `AF_PACKET`,
`CLOCK_BOOTTIME`, `SO_DOMAIN`, `strcasecmp` in `<string.h>` (POSIX puts it in `<strings.h>` only), Linux's
`pthread_setname_np` signature, and `POLLERR` (NetBSD's `poll()` never reports it for a socket error; one test that
expects it is skipped). `KOS-PERF` marks one choice kept because it measured faster under QEMU: the GC without regions.

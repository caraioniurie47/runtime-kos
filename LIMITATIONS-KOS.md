# .NET on KasperskyOS: limitations

What .NET does not do, or does differently, when a program built with this port runs on KasperskyOS Community Edition.
It covers the branch `kos-main`, KasperskyOS CE SDK **1.4.0.102**, arm64, under QEMU; nothing was run on hardware.
[HOWTO-KOS.md](HOWTO-KOS.md) builds the port and the samples.

Each entry says where the difference comes from:

- **KasperskyOS**: documented in the [KasperskyOS CE 1.4 documentation](https://support.kaspersky.com/kos-community-edition/1.4/)
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
  version (`KasperskyOS-Community-Edition-Qemu 1.4.0.102`), since KasperskyOS's `uname()` returns constants. *Port.*
- **`[DllImport]` needs a link-time binding.** The executable is static and nothing resolves a library name at run
  time, so a call into a library that was not bound when linking throws `EntryPointNotFoundException`, `"libc"`
  included. Bind each entry point with `<DirectPInvoke Include="libc!geteuid" />` (one item per function: binding all of
  `libc` fails the link when the code also imports functions KasperskyOS's libc lacks). The runtime's own native
  libraries are bound this way already. *Port.*
- **No program path.** The executable is loaded from the image, and there is no procfs, so `AppContext.BaseDirectory` is
  empty (by the source, `Environment.ProcessPath` is null too). A file looked up "beside the app" resolves against the
  working directory, `/`. Ship files in the image's ROMFS, which keeps file names only and which a program sees only
  where a VFS program mounts it (`-l` followed by `romfs /romfs romfs ro` among VfsRamFs's `EXTRA_ARGS`), or write
  them to `/tmp` at run time. *KasperskyOS.*
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
- **Running out of memory ends the process.** The GC reserves with `MAP_NORESERVE` (above), and KasperskyOS takes
  such memory only when it is first written: making it read-write succeeds even when too little is free, and the
  kernel then ends the process during the write (`Unhandled Overcommit` on the console) instead of .NET
  throwing `OutOfMemoryException`. Seen with a 512 MiB array; `oom-check.c` in kos-sdk-checks shows it without .NET.
  *SDK (findings 8, 8a), Port.*
- **Signals: registrations succeed and never fire.** The kernel delivers no signal but `SIGTERM`, the only one `kill()`
  can send (`posix_uns_ifaces`). The runtime therefore installs no signal handler: `Console.CancelKeyPress` and
  `PosixSignalRegistration.Create` succeed, for any signal (even `SIGKILL`, which Linux refuses), and their handlers
  never run; `SIGTERM` keeps its default action. *KasperskyOS, Port.*
- **At most 512 descriptors per process** (`OPEN_MAX`, `sysconf(_SC_OPEN_MAX)`); the next one fails with `EMFILE`.
  *KasperskyOS.*
- **No diagnostics tracing through LTTng.** The Linux EventSource-to-LTTng bridge is not built. *KasperskyOS.*

## Files

The samples' image gives the program one file system, the SDK's VfsRamFs (HOWTO step 9): RAM, `/` and `/tmp`
included, with devices at `/dev`. What follows was measured there; other VFS programs were not
tried.

- **VfsRamFs runs single-threaded in the samples' image.** Under concurrent file calls from one program VfsRamFs can
  fault or hang, and every client then loses its file system. The KasperskyOS team's workaround, until an SDK release
  fixes it, is one server thread per client and per process (`VFS_SERVER_MAX_THREADS_PER_CLIENT` and
  `..._PER_PROCESS` 1 in its `EXTRA_ENV`); the samples' `kos-image/CMakeLists.txt` sets both
  (`VFSRAMFS_MAX_THREADS`). File calls then wait for each other: System.IO.FileSystem.Tests took about 30% longer, with
  the same results. An image of your own needs the same setting. Report:
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
- **Preallocation beyond the free memory is not reported as .NET expects.** `posix_fallocate` returns -1 rather than
  an error number, and the `WhenDiskIsFullTheErrorMessageContainsAllDetails` tests fail. *SDK (finding 6e).*
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
- **Without a VFS program, no files or stdout.** In an image without one, file calls and `Console.Out` throw
  `IOException` and only stderr reaches the console; a program linked with the VFS client first waits about 11 s for a
  server, then logs `Can't establish IPC connetion to VFS server` (the SDK's spelling). *KasperskyOS; the wait: SDK
  (finding 14).*

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
  VfsNet's `EXTRA_ENV`, from the cache variable `VFSNET_MAX_THREADS_PER_CLIENT`), and an image of your own needs the
  same. *KasperskyOS.*
- **Host names resolve through VfsNet**, from its own `/etc/hosts` or its DNS server (8.8.8.8 by default). Without a
  hosts file not even `localhost` resolves, so the samples' image gives VfsNet one in its ROMFS
  (`kos-image/resources/romfs/etc/hosts`); an `/etc/hosts` the program writes is not VfsNet's and changes nothing.
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

- **Cryptography is the SDK's OpenSSL 1.1.1t**, linked statically. OpenSSL 1.1.1 is past its upstream end of life
  (2023-09-11). What .NET builds on OpenSSL 3 APIs throws `PlatformNotSupportedException`: ML-KEM, ML-DSA, SLH-DSA,
  KMAC and reading SHAKE output incrementally; SHA-3, one-shot SHAKE and ChaCha20-Poly1305 work. The SDK ships no
  OpenSSL headers; the port compiles against the 1.1.1t release's. *SDK (finding 1).*
- **No CA certificates in the image.** The default trust refuses every server, public ones included, and the
  `LocalMachine` `Root` store is empty. Either trust CAs in code (`X509ChainPolicy.CustomTrustStore`, as the showcase
  does), or set `SSL_CERT_FILE` in the program's environment (`init.yaml`) to a PEM bundle the program can read: a
  probe that wrote a CA bundle to `/tmp` before its first request fetched `https://example.com/` with the default
  trust. `SSL_CERT_DIR` was not tried. *Image.*
- **No Kerberos.** The SDK has no GSSAPI library, so there is no `System.Net.Security.Native`; NTLM and Negotiate use
  .NET's managed implementation, which does NTLM only. *SDK (finding 3).*
- **`SslStream.NegotiateClientCertificateAsync` over TLS 1.3**, when the client offered no post-handshake
  authentication, reports an internal error rather than the client's refusal: a .NET defect with OpenSSL 1.1.1
  ([dotnet/runtime#134640](https://github.com/dotnet/runtime/issues/134640)), not KasperskyOS's. *.NET.*

## Globalization, time, console

- **ICU adds about 22 MB.** ICU is linked unless the program sets `InvariantGlobalization=true`, as the HelloWorld and
  web server samples do; the showcase grows from about 31 MB to about 53 MB with it (unstripped). Its data leaves out
  only ICU features .NET never calls (HOWTO step 5); System.Globalization's test suites give the same results with and
  without that filter. *Port.*
- **No time zone database.** .NET reads tzdata files under `/usr/share/zoneinfo`, which the image lacks, so looking up
  a zone such as `Europe/London` fails. A program that needs zones ships the files and sets `TZDIR` to their directory
  (`init.yaml`); not tried on KasperskyOS. *Image.*
- **No terminfo database in the image.** What .NET's console does without one was not examined on KasperskyOS.
  *Image.*
- **Serial ports: no hardware flow control.** KasperskyOS's `termios.h` has no `CRTSCTS`, so configuring a port with
  `Handshake.RequestToSend` fails, and `RequestToSendXOnXOff` gets XON/XOFF only. No serial port was tried.
  *KasperskyOS.*

## What was tested

The dotnet/runtime library test suites below, built as NativeAOT for KasperskyOS and run in the samples' image under
QEMU, with the CI's test filters. "Failed" counts are those of the run named; the causes are above.

| Suite | Date | Result | Failures |
| --- | --- | --- | --- |
| System.Runtime.Tests | 2026-09-26 | 76744 run, 0 failed, 187 skipped | |
| System.Collections.Tests | 2026-09-24 | 33679 run, 0 failed, 134 skipped | |
| System.Linq.Tests | 2026-09-24 | 52250 run, 0 failed, 24 skipped | |
| System.Globalization.Tests (ICU) | 2026-09-26 | 61639 run, 0 failed, 11 skipped | |
| System.Globalization.Extensions.Tests, .Calendars.Tests | 2026-09-26 | 689 and 1477 run, 0 failed | |
| System.Threading.Channels.Tests | 2026-09-24 | 1589 run, 0 failed, 14 skipped | |
| System.Console.Tests | 2026-09-25 | 4287 run, 0 failed, 22 skipped | |
| System.IO.FileSystem.Tests | 2026-09-29 | 8468 run, 0 failed, 261 skipped | named-pipe class run separately: 128 run, 0 failed |
| System.IO.FileSystem.DriveInfo.Tests | 2026-09-26 | 6 run, 0 failed | |
| System.Net.Sockets.Tests | 2026-09-29 | 2124 run, 1 failed, 378 skipped | `ConnectAsync_WithLargeBuffer_*`: its fixed 10 s is too short for emulated sockets under the suite's load; passes alone |
| System.Net.NetworkInformation.Functional.Tests | 2026-09-26 | 245 run, 0 failed, 13 skipped | |
| System.Net.Security.Tests | 2026-09-24 | 5010 run, 6 failed, 28 skipped | 4 `SslStream_RandomSizeWrites_OK`: emulation too slow for the fixed 60 s (pass with 10 min); CA certificates (skipped since); dotnet/runtime#134640 |
| System.Security.Cryptography.Tests | 2026-09-24 | 19267 run, 1 failed, 789 skipped | CA certificates (skipped since) |
| System.Diagnostics.Process.Tests | 2026-09-26 | 451 run, 0 failed, 255 skipped | |
| System.Runtime.InteropServices.Tests | 2026-09-26 | 2461 run, 0 failed, 164 skipped | |
| System.IO.Ports.Tests | 2026-09-25 | 919 run, 0 failed, 797 skipped | no serial ports, as on Linux without them |
| System.Text.RegularExpressions.Tests | 2026-09-24 | 23404 run, 8 failed, 15 skipped | the same 8 fail as NativeAOT on linux-x64 |
| System.Text.Json.Tests (`System.Text.Json.Tests` namespace, reflection on) | 2026-09-24, 2026-09-25 | 17916 run, 71 failed | NativeAOT's: every failure also fails as NativeAOT on linux-x64 |

Each KasperskyOS-specific skip carries its marker in its skip reason or beside its condition in the source.

## SDK differences the port absorbs

These need nothing from a program; they are listed so that the markers in the sources can be looked up. The ids are
those of `TODO-KOS(<id>)`; 8c is marked `KOS-DOC(posix_uns_ifaces)`, which documents the refusal but not its error.

| Id | KasperskyOS CE 1.4.0.102 | What the port does |
| --- | --- | --- |
| 2 | `libcrypto.a` references a `getentropy()` no library defines | defines it on the kernel's random generator |
| 4 | headers declare XSI and optional functions (`getgrent`, `shm_open`, robust mutexes) no library defines | stays off those paths; a user's groups are their primary group only |
| 4a | `pthread_condattr_init` fails on memory that looks initialized | zeroes the attribute first |
| 4b | `dlerror()` is declared `const char *` | casts |
| 4c | no `SA_RESETHAND`, `SA_NODEFER` | compiles them out |
| 5 | `fallocate()` without `FALLOC_FL_*` flags | does not use it |
| 6 | headers portable code probes are absent (`sys/statfs.h`, `mntent.h`, `cpu_set_t`, ...) | NetBSD equivalents |
| 6a | `mkstemps()` fails with `EINVAL` | creates temporary files itself (`Path.GetTempFileName` works) |
| 6c | `pread`, `pwrite`, `ftruncate`, `fsync` fail with `ENOSYS` on `/dev/null` and pipes | maps to the errors .NET handles (`/dev/null` and pipe I/O work) |
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

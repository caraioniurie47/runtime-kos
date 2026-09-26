using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

// A tour of .NET NativeAOT on KasperskyOS. Each section runs on its own and ends with a PASS or FAIL
// line, so one missing platform feature does not hide the rest. Output goes to stderr, which the
// KasperskyOS console shows without a VFS program in the image; stdout and files need one.

// First, for boot timelines: the KasperskyOS log's timestamps are UTC with milliseconds too.
DateTime mainStarted = DateTime.UtcNow;
TextWriter o = Console.Error;
var total = Stopwatch.StartNew();
o.WriteLine($"Main started {mainStarted:yyyy-MM-ddTHH:mm:ss.fff} UTC");
int passed = 0, skipped = 0, failed = 0;
bool invariantGlobalization = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant;

o.WriteLine();
o.WriteLine("  +------------------------------------------+");
o.WriteLine("  |   .NET on KasperskyOS                    |");
o.WriteLine("  |   NativeAOT, statically linked, arm64    |");
o.WriteLine("  +------------------------------------------+");

Section("Runtime", () =>
{
    o.WriteLine($"  Framework     {RuntimeInformation.FrameworkDescription}");
    // The KOS SDK's product name and version, from the header it generates: uname() itself reports
    // constants ("KOS", "1.0", "1.0"). It is the SDK the runtime was built with, kernel included.
    o.WriteLine($"  OS            {RuntimeInformation.OSDescription}");
    o.WriteLine($"  Runtime ID    {RuntimeInformation.RuntimeIdentifier}");
    o.WriteLine($"  Architecture  {RuntimeInformation.ProcessArchitecture}");
    o.WriteLine($"  Processors    {Environment.ProcessorCount}");
    o.WriteLine($"  Dynamic code  {RuntimeFeature.IsDynamicCodeSupported}");
    o.WriteLine($"  GC            server {GCSettings.IsServerGC}, latency mode {GCSettings.LatencyMode}");
    o.WriteLine($"  UTC now       {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
    o.WriteLine($"  Globalization {(invariantGlobalization ? "invariant (no ICU)" : "ICU")}");
    Check(RuntimeInformation.ProcessArchitecture == Architecture.Arm64, "arm64 process");
});

Section("Files and stdout", () =>
{
    // Without a VFS program each task's libc uses a stub whose file calls fail with EIO (5 in the SDK's
    // sys/errno.h); .NET puts the errno in IOException.HResult.
    const int EIO = 5;
    string directory = Path.Combine(Path.GetTempPath(), "showcase-kos");
    try
    {
        Directory.CreateDirectory(directory);
    }
    catch (IOException e) when (e.HResult == EIO)
    {
        Skip($"no file system ({e.Message}); the image needs a VFS program, see HOWTO-KOS.md");
    }

    string path = Path.Combine(directory, "readings.txt");
    string[] lines = ["temperature 21.5", "humidity 48.25", "Zürich ✓"];
    File.WriteAllLines(path, lines);
    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
    {
        stream.Write("pressure 1013\n"u8);
    }
    string[] back = File.ReadAllLines(path);
    string[] expected = [.. lines, "pressure 1013"];
    o.WriteLine($"  wrote and read {path}: {back.Length} lines, {new FileInfo(path).Length} bytes");
    Check(back.AsSpan().SequenceEqual(expected), "the lines read back equal the lines written, UTF-8 included");

    File.Move(path, Path.Combine(directory, "readings-old.txt"));
    File.WriteAllText(Path.Combine(directory, "notes.txt"), "second file");
    string[] listed = [.. Directory.EnumerateFiles(directory).Select(file => Path.GetFileName(file)).Order()];
    string[] expectedListing = ["notes.txt", "readings-old.txt"];
    o.WriteLine($"  {directory} lists {string.Join(", ", listed)}");
    Check(listed.AsSpan().SequenceEqual(expectedListing), "the directory lists the renamed file and the new one");

    string[] mountPoints = [.. DriveInfo.GetDrives().Select(drive => drive.Name)];
    o.WriteLine($"  mount points: {string.Join(", ", mountPoints)}");
    Check(mountPoints.Contains(Path.GetTempPath().TrimEnd('/')), "the temporary directory's file system is listed");

    Directory.Delete(directory, recursive: true);
    Check(!Directory.Exists(directory), "the directory is gone after Directory.Delete");

    o.WriteLine($"  stdout redirected: {Console.IsOutputRedirected}; the next line is written to Console.Out");
    Console.Out.WriteLine("  (this line was written to stdout)");
    Console.Out.Flush();
});

Section("TCP sockets", () => TcpTour(o).GetAwaiter().GetResult());

Section("Cryptography with OpenSSL", () =>
{
    // The KOS SDK's static libcrypto.a and libssl.a, linked into this binary.
    if (OperatingSystem.IsLinux())
    {
        o.WriteLine($"  OpenSSL version number 0x{SafeEvpPKeyHandle.OpenSslVersion:X}");
    }

    Check(Convert.ToHexStringLower(SHA256.HashData("abc"u8)) ==
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "SHA-256(\"abc\") is the FIPS 180-2 value");
    Check(Convert.ToHexStringLower(HMACSHA256.HashData("Jefe"u8, "what do ya want for nothing?"u8)) ==
        "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", "HMAC-SHA256 is RFC 4231 test case 2");

    byte[] key = RandomNumberGenerator.GetBytes(32);
    Check(!key.AsSpan().SequenceEqual(RandomNumberGenerator.GetBytes(32)), "two 32-byte random draws differ");

    byte[] plain = "sensor 7: 21.5 C"u8.ToArray();
    byte[] nonce = RandomNumberGenerator.GetBytes(12), sealedText = new byte[plain.Length], tag = new byte[16], opened = new byte[plain.Length];
    using (var aes = new AesGcm(key, tag.Length))
    {
        aes.Encrypt(nonce, plain, sealedText, tag);
        aes.Decrypt(nonce, sealedText, tag, opened);
        Check(opened.AsSpan().SequenceEqual(plain), "AES-256-GCM decrypts what it encrypted");
        sealedText[0] ^= 1;
        bool rejected = false;
        try
        {
            aes.Decrypt(nonce, sealedText, tag, opened);
        }
        catch (AuthenticationTagMismatchException)
        {
            rejected = true;
        }
        Check(rejected, "AES-GCM rejects a modified ciphertext");
    }

    var clock = Stopwatch.StartNew();
    using RSA rsa = RSA.Create(2048);
    byte[] signature = rsa.SignData(plain, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    o.WriteLine($"  RSA-2048 key generated and data signed (PSS) in {clock.ElapsedMilliseconds} ms");
    Check(rsa.VerifyData(plain, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "the RSA-PSS signature verifies");

    using ECDiffieHellman alice = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    using ECDiffieHellman bob = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    Check(alice.DeriveKeyFromHash(bob.PublicKey, HashAlgorithmName.SHA256).AsSpan()
        .SequenceEqual(bob.DeriveKeyFromHash(alice.PublicKey, HashAlgorithmName.SHA256)), "ECDH P-256: both sides derive the same key");

    // KOS has no GSSAPI, so NTLM is .NET's managed implementation (the KOS targets set it).
    using var ntlm = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
    {
        Package = "NTLM",
        Credential = new NetworkCredential("user", "password", "DOMAIN"),
        TargetName = "HTTP/server.example",
    });
    byte[]? negotiate = ntlm.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out NegotiateAuthenticationStatusCode status);
    o.WriteLine($"  NTLM client, first message: {status}, {negotiate?.Length ?? 0} bytes");
    Check(status == NegotiateAuthenticationStatusCode.ContinueNeeded && negotiate is not null &&
        negotiate.AsSpan().StartsWith("NTLMSSP\0"u8), "NTLM produces a negotiate message without GSSAPI");
});

Section("HTTPS over loopback", () => HttpsTour(o).GetAwaiter().GetResult());

Section("Globalization with ICU", () =>
{
    if (invariantGlobalization)
    {
        Skip("published with InvariantGlobalization=true; publish with -p:InvariantGlobalization=false to link ICU");
    }

    var fr = CultureInfo.GetCultureInfo("fr-FR");
    Check(fr.NumberFormat.NumberDecimalSeparator == ",", "fr-FR decimal separator is ',' (ICU data linked)");

    decimal amount = 1234567.89m;
    var date = new DateTime(2026, 9, 18);
    foreach (string name in new[] { "en-US", "fr-FR", "de-DE", "ja-JP", "ar-EG", "hi-IN" })
    {
        var culture = CultureInfo.GetCultureInfo(name);
        o.WriteLine($"  {name,-6}  {amount.ToString("C", culture),-18}  {date.ToString("D", culture)}");
    }

    string[] words = ["zebra", "Äpfel", "apple", "Zürich", "Øre", "ångström"];
    foreach (string name in new[] { "de-DE", "sv-SE" })
    {
        string[] sorted = [.. words];
        Array.Sort(sorted, StringComparer.Create(CultureInfo.GetCultureInfo(name), ignoreCase: false));
        o.WriteLine($"  sort {name}  {string.Join(", ", sorted)}");
    }
    Check(string.Compare("ångström", "zebra", CultureInfo.GetCultureInfo("sv-SE"), CompareOptions.None) > 0,
        "sv-SE sorts 'å' after 'z'");
    Check(string.Compare("Äpfel", "zebra", CultureInfo.GetCultureInfo("de-DE"), CompareOptions.None) < 0,
        "de-DE sorts 'Ä' with 'A'");

    string upper = "istanbul".ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
    o.WriteLine($"  \"istanbul\".ToUpper() in tr-TR  {upper}");
    Check(upper == "İSTANBUL", "tr-TR uppercases 'i' to dotted 'İ'");
});

Section("Parallel.For: Mandelbrot", () =>
{
    const int Width = 72, Height = 22, Samples = 3, MaxIterations = 2000;
    const string Shades = " .:-=+*#%@";

    static char Cell(int column, int row)
    {
        double shade = 0;
        for (int sy = 0; sy < Samples; sy++)
        {
            for (int sx = 0; sx < Samples; sx++)
            {
                double cr = -2.2 + (column + (sx + 0.5) / Samples) * 3.2 / Width;
                double ci = -1.2 + (row + (sy + 0.5) / Samples) * 2.4 / Height;
                double zr = 0, zi = 0;
                int i = 0;
                while (i < MaxIterations && zr * zr + zi * zi <= 4)
                {
                    double t = zr * zr - zi * zi + cr;
                    zi = 2 * zr * zi + ci;
                    zr = t;
                    i++;
                }
                shade += i == MaxIterations ? 1 : Math.Log(i + 1) / Math.Log(MaxIterations);
            }
        }
        return Shades[(int)(shade / (Samples * Samples) * (Shades.Length - 1))];
    }

    var threadIds = new HashSet<int>();
    char[][] Render(bool parallel)
    {
        var rows = new char[Height][];
        void RenderRow(int row)
        {
            var line = new char[Width];
            for (int column = 0; column < Width; column++)
            {
                line[column] = Cell(column, row);
            }
            rows[row] = line;
            if (parallel)
            {
                lock (threadIds) threadIds.Add(Environment.CurrentManagedThreadId);
            }
        }
        if (parallel)
        {
            Parallel.For(0, Height, RenderRow);
        }
        else
        {
            for (int row = 0; row < Height; row++) RenderRow(row);
        }
        return rows;
    }

    var clock = Stopwatch.StartNew();
    char[][] sequential = Render(parallel: false);
    long sequentialMs = clock.ElapsedMilliseconds;
    clock.Restart();
    char[][] parallelRows = Render(parallel: true);
    long parallelMs = clock.ElapsedMilliseconds;

    foreach (char[] line in parallelRows)
    {
        o.WriteLine("  " + new string(line));
    }
    o.WriteLine($"  1 thread {sequentialMs} ms; Parallel.For {parallelMs} ms on {threadIds.Count} threads; " +
        $"speedup {(double)sequentialMs / Math.Max(1, parallelMs):F2}x");
    for (int row = 0; row < Height; row++)
    {
        Check(sequential[row].AsSpan().SequenceEqual(parallelRows[row]), "parallel image equals the sequential one");
    }
});

Section("async/await, timers and channels", () => AsyncTour(o).GetAwaiter().GetResult());

Section("System.Text.Json, source generated", () =>
{
    List<Reading> readings =
    [
        new("temperature", 21.5, new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc), ["room-1", "celsius"]),
        new("humidity", 48.25, new DateTime(2026, 9, 18, 12, 0, 5, DateTimeKind.Utc), ["room-1"]),
    ];
    string json = JsonSerializer.Serialize(readings, DemoJson.Default.ListReading);
    o.WriteLine($"  {json}");

    List<Reading> back = JsonSerializer.Deserialize(json, DemoJson.Default.ListReading)!;
    Check(back.Count == 2 && back[1].Value == 48.25 && back[0].Tags[1] == "celsius", "values survive the round trip");
    Check(JsonSerializer.Serialize(back, DemoJson.Default.ListReading) == json, "re-serialized JSON is identical");

    using JsonDocument document = JsonDocument.Parse(json);
    o.WriteLine($"  JsonDocument: {document.RootElement.GetArrayLength()} elements, " +
        $"first sensor '{document.RootElement[0].GetProperty("sensor").GetString()}'");
});

Section("Regex, source generated", () =>
{
    const string Text = "name=showcase; target=kasperskyos-arm64; mode=nativeaot; linking=static";
    foreach (Match match in Patterns.KeyValue().Matches(Text))
    {
        o.WriteLine($"  {match.Groups["key"].Value,-14} = {match.Groups["value"].Value}");
    }
    Check(Patterns.KeyValue().Count(Text) == 4, "four key=value pairs");
});

Section("LINQ and generic math", () =>
{
    int[] primes = [.. Enumerable.Range(2, 49_999).Where(IsPrime)];
    o.WriteLine($"  {primes.Length} primes up to 50000, the last five: {string.Join(", ", primes[^5..])}");
    var byLastDigit = primes.GroupBy(p => p % 10).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}");
    o.WriteLine($"  by last digit  {string.Join("  ", byLastDigit)}");
    Check(primes.Length == 5133, "5133 primes up to 50000");

    o.WriteLine($"  Sum<int>      {Sum<int>([1, 2, 3, 4])}");
    o.WriteLine($"  Sum<double>   {Sum<double>([0.5, 0.25, 0.125]).ToString(CultureInfo.InvariantCulture)}");
    o.WriteLine($"  Sum<decimal>  {Sum<decimal>([0.1m, 0.2m]).ToString(CultureInfo.InvariantCulture)}");
    Check(Sum<decimal>([0.1m, 0.2m]) == 0.3m, "decimal 0.1 + 0.2 is exactly 0.3");
});

Section("Garbage collector under load", () =>
{
    int[] before = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    long allocatedBefore = GC.GetTotalAllocatedBytes();
    var window = new byte[256][];
    var random = new Random(47);
    for (int i = 0; i < 40_000; i++)
    {
        // Mostly short-lived 8 KB buffers, plus a large-object-heap array every 1000th.
        var buffer = new byte[i % 1000 == 999 ? 256_000 : 8_000];
        buffer[random.Next(buffer.Length)] = 1;
        window[i % window.Length] = buffer;
    }
    long allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
    GCMemoryInfo info = GC.GetGCMemoryInfo();
    o.WriteLine($"  allocated {allocated / (1024 * 1024)} MiB; collections: gen0 {GC.CollectionCount(0) - before[0]}, " +
        $"gen1 {GC.CollectionCount(1) - before[1]}, gen2 {GC.CollectionCount(2) - before[2]}");
    o.WriteLine($"  heap {info.HeapSizeBytes / 1024} KiB; memory available to the GC " +
        $"{info.TotalAvailableMemoryBytes / (1024 * 1024)} MiB");
    // The GC records the system's memory load in whole percent, so this is 0 below 1%.
    o.WriteLine($"  system memory in use at the last GC {info.MemoryLoadBytes / (1024 * 1024)} MiB");
    Check(GC.CollectionCount(0) > before[0], "gen0 collections ran");
});

Section("GC while a thread loops without calls", () =>
{
    // KasperskyOS has no signals to interrupt a thread running managed code, so the compiler puts a GC poll in
    // loops like this one. Without it, GC.Collect waits until the loop ends by itself after 1.5 billion
    // iterations: the thread that would stop it is suspended for the GC too.
    int stop = 0;
    bool spinning = false;
    long spins = 0;
    var spinner = new Thread(() =>
    {
        long n = 0;
        Volatile.Write(ref spinning, true);
        while (Volatile.Read(ref stop) == 0 && n < 1_500_000_000)
        {
            n++;
        }
        spins = n;
    });
    spinner.Start();
    while (!Volatile.Read(ref spinning))
    {
        Thread.Sleep(10);
    }
    Thread.Sleep(200);
    var clock = Stopwatch.StartNew();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    long collectMs = clock.ElapsedMilliseconds;
    Volatile.Write(ref stop, 1);
    spinner.Join();
    o.WriteLine($"  GC.Collect took {collectMs} ms; the loop ran {spins} iterations" +
        (spins < 1_500_000_000 ? " and stopped when asked" : ", its full count"));
    Check(collectMs < 2000, "GC.Collect did not wait for the loop");
});

Section("Exceptions, filters and stack traces", () =>
{
    bool finallyRan = false;
    try
    {
        try
        {
            ParseNumber("forty-seven");
        }
        finally
        {
            finallyRan = true;
        }
    }
    catch (FormatException e) when (e.Data.Contains("input"))
    {
        o.WriteLine($"  caught {e.GetType().Name} for input '{e.Data["input"]}'");
        string[] frames = (e.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string frame in frames.Take(3))
        {
            o.WriteLine($"    {frame}");
        }
        Check(frames.Length > 0, "the stack trace has frames");
    }
    Check(finallyRan, "the finally block ran");

    int zero = int.Parse("0", CultureInfo.InvariantCulture);
    try
    {
        o.WriteLine($"  47 / 0 = {47 / zero}");
        Check(false, "division by zero throws");
    }
    catch (DivideByZeroException e)
    {
        o.WriteLine($"  47 / 0 -> {e.GetType().Name}");
    }
});

Section("Null dereference in managed code", () =>
{
    // KasperskyOS has no SIGSEGV. The runtime's process exception handler turns the page fault into a
    // NullReferenceException thrown from the faulting method, as the SIGSEGV handler does elsewhere.
    var live = new Holder { Value = 47 };
    Check(ReadValue(live) == 47, "a read through a live reference returns its value");

    long sum = 0;
    for (int i = 1; i <= 3; i++)
    {
        try
        {
            sum += ReadValue(null);
            Check(false, "a read through null throws");
        }
        catch (NullReferenceException e)
        {
            sum += 10 * i;
            if (i == 1)
            {
                o.WriteLine($"  read through null -> {e.GetType().Name}");
            }
        }
    }
    Check(sum == 60,"three reads through null in a row each throw, and the loop's locals survive");

    try
    {
        StoreRef(null, live);
        Check(false, "a reference store through null throws");
    }
    catch (NullReferenceException)
    {
        o.WriteLine("  reference store through null -> NullReferenceException");
    }

    bool threadCaught = false;
    var worker = new Thread(() =>
    {
        try
        {
            ReadValue(null);
        }
        catch (NullReferenceException)
        {
            threadCaught = true;
        }
    });
    worker.Start();
    worker.Join();
    Check(threadCaught, "a read through null on a new thread throws NullReferenceException there");
});

o.WriteLine();
o.WriteLine($"SHOWCASE DONE: {passed} passed, {skipped} skipped, {failed} failed, {total.ElapsedMilliseconds} ms");
return failed == 0 ? 0 : 1;

void Section(string name, Action body)
{
    o.WriteLine();
    o.WriteLine($"=== {name}");
    var clock = Stopwatch.StartNew();
    try
    {
        body();
        passed++;
        o.WriteLine($"--- PASS  {name}  ({clock.ElapsedMilliseconds} ms)");
    }
    catch (SectionSkippedException e)
    {
        skipped++;
        o.WriteLine($"--- SKIP  {name}: {e.Message}");
    }
    catch (Exception e)
    {
        failed++;
        o.WriteLine($"--- FAIL  {name}: {e.GetType().FullName}: {e.Message}");
        // A TypeInitializationException, for one, says nothing until its inner exception is shown.
        for (Exception? current = e; current is not null; current = current.InnerException)
        {
            if (current != e)
            {
                o.WriteLine($"    inner: {current.GetType().FullName}: {current.Message}");
            }
            foreach (string frame in (current.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8))
            {
                o.WriteLine($"      {frame.Trim()}");
            }
        }
    }
}

static void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new InvalidOperationException($"check failed: {what}");
    }
}

static void Skip(string reason) => throw new SectionSkippedException(reason);

static async Task AsyncTour(TextWriter o)
{
    var channel = Channel.CreateUnbounded<string>();

    async Task<int> Worker(int id)
    {
        await Task.Delay(40 * id);
        await channel.Writer.WriteAsync($"worker {id} finished on thread {Environment.CurrentManagedThreadId}");
        return id * id;
    }

    var clock = Stopwatch.StartNew();
    Task reader = Task.Run(async () =>
    {
        await foreach (string message in channel.Reader.ReadAllAsync())
        {
            o.WriteLine($"  {message}");
        }
    });
    int[] results = await Task.WhenAll(Enumerable.Range(1, 5).Select(Worker));
    channel.Writer.Complete();
    await reader;
    o.WriteLine($"  Task.WhenAll -> [{string.Join(", ", results)}] after {clock.ElapsedMilliseconds} ms");
    Check(results.Sum() == 55, "the squares of 1..5 sum to 55");

    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    clock.Restart();
    int ticks = 0;
    while (ticks < 5 && await timer.WaitForNextTickAsync())
    {
        ticks++;
    }
    o.WriteLine($"  PeriodicTimer: {ticks} ticks of 100 ms in {clock.ElapsedMilliseconds} ms");
    Check(clock.ElapsedMilliseconds >= 450, "the timer does not fire early");
}

static async Task HttpsTour(TextWriter o)
{
    try
    {
        new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp).Dispose();
    }
    catch (SocketException e) when (e.SocketErrorCode == SocketError.SocketError)
    {
        Skip($"no network ({e.Message}); the image needs a network VFS program, see HOWTO-KOS.md");
    }

    // A test CA and a server certificate for 127.0.0.1 that it signs, both made here.
    DateTimeOffset now = DateTimeOffset.UtcNow;
    using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var caRequest = new CertificateRequest("CN=showcase-kos test CA", caKey, HashAlgorithmName.SHA256);
    caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    using X509Certificate2 ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

    using ECDsa serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256);
    var names = new SubjectAlternativeNameBuilder();
    names.AddIpAddress(IPAddress.Loopback);
    serverRequest.CertificateExtensions.Add(names.Build());
    serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
    using X509Certificate2 issued = serverRequest.Create(ca, now.AddHours(-1), now.AddDays(7), RandomNumberGenerator.GetBytes(8));
    using X509Certificate2 serverCertificate = issued.CopyWithPrivateKey(serverKey);

    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    string url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/status";

    // Trusting exactly the test CA: OpenSSL still builds and checks the chain, and the host name is checked too.
    var trusting = new SocketsHttpHandler();
    trusting.SslOptions.CertificateChainPolicy = new X509ChainPolicy
    {
        TrustMode = X509ChainTrustMode.CustomRootTrust,
        RevocationMode = X509RevocationMode.NoCheck,
    };
    trusting.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(ca);
    using (var client = new HttpClient(trusting))
    {
        Task<(SslProtocols, TlsCipherSuite)> server = ServeOnce(listener, serverCertificate);
        var clock = Stopwatch.StartNew();
        string body = await client.GetStringAsync(url);
        (SslProtocols protocol, TlsCipherSuite cipherSuite) = await server;
        o.WriteLine($"  GET {url} in {clock.ElapsedMilliseconds} ms: \"{body}\"");
        o.WriteLine($"  negotiated {protocol}, {cipherSuite}");
        Check(body == "hello over TLS; the request was GET /status HTTP/1.1", "the HTTPS response body arrived");
        Check(protocol is SslProtocols.Tls12 or SslProtocols.Tls13, "TLS 1.2 or 1.3 negotiated");
    }

    // The same server certificate with the default trust (the image has no CA certificates): refused.
    using (var client = new HttpClient())
    {
        Task<(SslProtocols, TlsCipherSuite)> server = ServeOnce(listener, serverCertificate);
        Exception? refused = null;
        try
        {
            await client.GetStringAsync(url);
        }
        catch (HttpRequestException e)
        {
            refused = e;
        }
        try
        {
            await server;
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            // The client closed the connection during the handshake.
        }
        o.WriteLine($"  default trust: {refused?.InnerException?.GetType().Name}: {refused?.InnerException?.Message}");
        Check(refused?.InnerException is AuthenticationException, "a certificate from an untrusted CA is refused");
    }

    // One HTTP/1.1 exchange over TLS on the next accepted connection; returns what the handshake negotiated.
    static async Task<(SslProtocols, TlsCipherSuite)> ServeOnce(TcpListener listener, X509Certificate2 certificate)
    {
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        using var tls = new SslStream(peer.GetStream());
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
        var request = new StringBuilder();
        var buffer = new byte[4096];
        int count;
        while (!request.ToString().Contains("\r\n\r\n") && (count = await tls.ReadAsync(buffer)) > 0)
        {
            request.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }
        byte[] body = Encoding.UTF8.GetBytes($"hello over TLS; the request was {request.ToString().Split("\r\n")[0]}");
        await tls.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        await tls.WriteAsync(body);
        await tls.ShutdownAsync();
        return (tls.SslProtocol, tls.NegotiatedCipherSuite);
    }
}

static async Task TcpTour(TextWriter o)
{
    // Sockets go to the network VFS program (VfsNet in kos-image/).
    try
    {
        new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp).Dispose();
    }
    catch (SocketException e) when (e.SocketErrorCode == SocketError.SocketError)
    {
        // Without a network VFS program libc's stub fails socket() with EIO, which has no SocketError value.
        Skip($"no network ({e.Message}); the image needs a network VFS program, see HOWTO-KOS.md");
    }

    // Loopback echo with the async API: the server copies everything back, the client sends and receives at once.
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    Task echo = Task.Run(async () =>
    {
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = peer.GetStream();
        await stream.CopyToAsync(stream);
    });

    byte[] payload = new byte[256 * 1024];
    new Random(47).NextBytes(payload);
    var clock = Stopwatch.StartNew();
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(endpoint);
        NetworkStream stream = client.GetStream();
        Task send = Task.Run(async () =>
        {
            await stream.WriteAsync(payload);
            client.Client.Shutdown(SocketShutdown.Send);
        });
        var received = new MemoryStream();
        await stream.CopyToAsync(received);
        await Task.WhenAll(send, echo);
        o.WriteLine($"  async loopback echo via {endpoint}: {received.Length / 1024} KiB back in {clock.ElapsedMilliseconds} ms");
        Check(received.GetBuffer().AsSpan(0, (int)received.Length).SequenceEqual(payload), "the echoed bytes equal the bytes sent");
    }

    // Blocking calls, with a receive buffer larger than one VFS IPC message (65536 bytes).
    using (var server = new TcpListener(IPAddress.Loopback, 0))
    {
        server.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)server.LocalEndpoint);
        using TcpClient peer = server.AcceptTcpClient();
        byte[] message = new byte[100_000];
        new Random(11).NextBytes(message);
        // Sent from another thread: the message may not fit the socket buffers until it is read.
        Task send = Task.Run(() =>
        {
            client.Client.Send(message);
            client.Client.Shutdown(SocketShutdown.Send);
        });
        var buffer = new byte[81920];
        int total = 0, largest = 0, count;
        var back = new MemoryStream();
        while ((count = peer.Client.Receive(buffer)) > 0)
        {
            back.Write(buffer, 0, count);
            total += count;
            largest = Math.Max(largest, count);
        }
        await send;
        o.WriteLine($"  blocking Receive into an 81920-byte buffer: {total} bytes, at most {largest} per call");
        Check(back.GetBuffer().AsSpan(0, (int)back.Length).SequenceEqual(message), "the bytes received equal the bytes sent");
    }

    // A client on the host: only when the image forwards a port (kos-image's HOST_TCP_PORT option).
    string? hostPort = Environment.GetEnvironmentVariable("HOST_TCP_PORT");
    if (hostPort is null)
    {
        o.WriteLine("  HOST_TCP_PORT unset: no port forwarded from the host, no host client expected");
        return;
    }
    using var hostListener = new TcpListener(IPAddress.Any, int.Parse(hostPort, CultureInfo.InvariantCulture));
    hostListener.Start();
    const int WaitSeconds = 120;
    o.WriteLine($"  listening on {hostListener.LocalEndpoint} for a host client, up to {WaitSeconds} s " +
        $"(on the host: nc localhost {hostPort})");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(WaitSeconds));
    // Accept until a client sends a line: a connection can arrive already closed by the host side.
    while (true)
    {
        TcpClient host;
        try
        {
            host = await hostListener.AcceptTcpClientAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Check(false, $"a host client sent a line within {WaitSeconds} s");
            return;
        }
        using (host)
        {
            using var reader = new StreamReader(host.GetStream());
            using var writer = new StreamWriter(host.GetStream()) { AutoFlush = true, NewLine = "\n" };
            string? line;
            try
            {
                await writer.WriteLineAsync("hello from .NET on KasperskyOS; send a line");
                line = await reader.ReadLineAsync(timeout.Token);
            }
            catch (IOException e)
            {
                o.WriteLine($"  host connection from {host.Client.RemoteEndPoint} failed: {e.Message}");
                continue;
            }
            if (line is null)
            {
                o.WriteLine($"  host connection from {host.Client.RemoteEndPoint} closed without a line");
                continue;
            }
            o.WriteLine($"  host client {host.Client.RemoteEndPoint} sent: {line}");
            await writer.WriteLineAsync($"KOS echo: {line}");
            return;
        }
    }
}

static bool IsPrime(int n)
{
    for (int d = 2; (long)d * d <= n; d++)
    {
        if (n % d == 0)
        {
            return false;
        }
    }
    return n >= 2;
}

static T Sum<T>(ReadOnlySpan<T> values) where T : INumber<T>
{
    T sum = T.Zero;
    foreach (T value in values)
    {
        sum += value;
    }
    return sum;
}

static int ParseNumber(string text)
{
    try
    {
        return int.Parse(text, CultureInfo.InvariantCulture);
    }
    catch (FormatException e)
    {
        e.Data["input"] = text;
        throw;
    }
}

// Not inlined, so that the null reaches a real load or store.
[MethodImpl(MethodImplOptions.NoInlining)]
static int ReadValue(Holder? holder) => holder!.Value;

[MethodImpl(MethodImplOptions.NoInlining)]
static void StoreRef(Holder? holder, object value) => holder!.Ref = value;

record Reading(string Sensor, double Value, DateTime At, string[] Tags);

sealed class Holder
{
    public int Value;
    public object? Ref;
}

sealed class SectionSkippedException(string reason) : Exception(reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<Reading>))]
partial class DemoJson : JsonSerializerContext
{
}

static partial class Patterns
{
    [GeneratedRegex(@"(?<key>\w+)=(?<value>[^;]+)")]
    public static partial Regex KeyValue();
}

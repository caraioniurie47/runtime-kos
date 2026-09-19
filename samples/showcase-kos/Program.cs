using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

// A tour of .NET NativeAOT on KasperskyOS. Each section runs on its own and ends with a PASS or FAIL
// line, so one missing platform feature does not hide the rest. Output goes to stderr, which the
// KasperskyOS console shows.

TextWriter o = Console.Error;
var total = Stopwatch.StartNew();
int passed = 0, skipped = 0, failed = 0;
bool invariantGlobalization = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant;

// On KasperskyOS under QEMU only stderr reaches the console; writing to stdout throws.
string stdout;
try
{
    Console.Out.WriteLine("(this line was written to stdout)");
    stdout = "writable";
}
catch (IOException e)
{
    stdout = $"not writable: {e.Message}";
}

o.WriteLine();
o.WriteLine("  +------------------------------------------+");
o.WriteLine("  |   .NET on KasperskyOS                    |");
o.WriteLine("  |   NativeAOT, statically linked, arm64    |");
o.WriteLine("  +------------------------------------------+");

Section("Runtime", () =>
{
    o.WriteLine($"  Framework     {RuntimeInformation.FrameworkDescription}");
    // OSDescription is uname()'s sysname, release and version, which SDK 1.4.0.102's libc fills with
    // constants ("KOS", "1.0", "1.0"), not the OS version.
    o.WriteLine($"  uname         {RuntimeInformation.OSDescription}");
    o.WriteLine($"  Runtime ID    {RuntimeInformation.RuntimeIdentifier}");
    o.WriteLine($"  Architecture  {RuntimeInformation.ProcessArchitecture}");
    o.WriteLine($"  Processors    {Environment.ProcessorCount}");
    o.WriteLine($"  Dynamic code  {RuntimeFeature.IsDynamicCodeSupported}");
    o.WriteLine($"  GC            server {GCSettings.IsServerGC}, latency mode {GCSettings.LatencyMode}");
    o.WriteLine($"  UTC now       {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
    o.WriteLine($"  Globalization {(invariantGlobalization ? "invariant (no ICU)" : "ICU")}");
    o.WriteLine($"  stdout        {stdout}");
    Check(RuntimeInformation.ProcessArchitecture == Architecture.Arm64, "arm64 process");
});

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
    Check(GC.CollectionCount(0) > before[0], "gen0 collections ran");
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

record Reading(string Sensor, double Value, DateTime At, string[] Tags);

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

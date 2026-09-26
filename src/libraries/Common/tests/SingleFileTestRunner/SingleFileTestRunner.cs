// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

// @TODO medium-to-longer term, we should try to get rid of the special-unicorn-single-file runner in favor of making the real runner work for single file.
// https://github.com/dotnet/runtime/issues/70432
public class SingleFileTestRunner : XunitTestFramework
{
    private SingleFileTestRunner(IMessageSink messageSink)
    : base(messageSink) { }

    public static int Main(string[] args)
    {
        var asm = typeof(SingleFileTestRunner).Assembly;

        // Where the console is the only way results leave the device (KasperskyOS under QEMU), the runner reports on a
        // standard error stream of its own: tests may replace or dispose Console.Out and Console.Error.
        TextWriter resultsWriter = Environment.GetEnvironmentVariable("DOTNET_TEST_RESULTS_TO_STDERR") == "1" ?
            TextWriter.Synchronized(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true }) :
            null;
        Action<string> report = resultsWriter != null ? resultsWriter.WriteLine : Console.WriteLine;

        report("Running assembly:" + asm.FullName);

        // Where the device has no copy of the test's content files (TestData and the like, beside the executable on
        // other platforms), they come as a ustar archive in a read-only file system: unpack it into the current
        // directory first.
        string contentArchive = Environment.GetEnvironmentVariable("DOTNET_TEST_CONTENT_TAR");
        if (!string.IsNullOrEmpty(contentArchive))
        {
            report($"Content: {UnpackTar(contentArchive, Environment.CurrentDirectory)} files from {contentArchive} into {Environment.CurrentDirectory} (base directory {AppContext.BaseDirectory})");
        }

        // The current RemoteExecutor implementation is not compatible with the SingleFileTestRunner.
        Environment.SetEnvironmentVariable("DOTNET_REMOTEEXECUTOR_SUPPORTED", "0");

        // To detect ReadyToRun testing mode, we set a constant in
        // eng/testing/tests.singlefile.targets, which we use in the following
        // preprocessor directive. In the case that it is defined, we set an
        // environment variable that we consume later to implement
        // PlatformDetection.IsReadyToRunCompiled. This last value is used for the
        // [ActiveIssue] annotations designed to exclude tests from running.

#if TEST_READY_TO_RUN_COMPILED
        Environment.SetEnvironmentVariable("TEST_READY_TO_RUN_MODE" ,"1");
#endif

        var diagnosticSink = new ConsoleDiagnosticMessageSink();
        var testsFinished = new TaskCompletionSource();
        var testSink = new TestMessageSink();

#pragma warning disable CS0618 // Delegating*Sink types are marked obsolete
        var summarySink = new DelegatingExecutionSummarySink(testSink,
            () => false,
            (completed, summary) => report($"Tests run: {summary.Total}, Errors: {summary.Errors}, Failures: {summary.Failed}, Skipped: {summary.Skipped}. Time: {TimeSpan.FromSeconds((double)summary.Time).TotalSeconds}s"));
        var resultsXmlAssembly = new XElement("assembly");
        var resultsSink = new DelegatingXmlCreationSink(summarySink, resultsXmlAssembly);
#pragma warning restore CS0618

        testSink.Execution.TestSkippedEvent += args => { report($"[SKIP] {args.Message.Test.DisplayName}"); };
        testSink.Execution.TestFailedEvent += args => { report($"[FAIL] {args.Message.Test.DisplayName}{Environment.NewLine}{Xunit.ExceptionUtility.CombineMessages(args.Message)}{Environment.NewLine}{Xunit.ExceptionUtility.CombineStackTraces(args.Message)}"); };

        testSink.Execution.TestAssemblyFinishedEvent += args =>
        {
            report($"Finished {args.Message.TestAssembly.Assembly}{Environment.NewLine}");
            testsFinished.SetResult();
        };

        var assemblyConfig = new TestAssemblyConfiguration()
        {
            // Turn off pre-enumeration of theories, since there is no theory selection UI in this runner
            PreEnumerateTheories = false,
        };

        var xunitTestFx = new SingleFileTestRunner(diagnosticSink);
        var asmInfo = Reflector.Wrap(asm);
        var asmName = asm.GetName();

        var discoverySink = new TestDiscoverySink();
        var discoverer = xunitTestFx.CreateDiscoverer(asmInfo);
        // Reporting on stderr: name each class as discovery reaches it, since discovery runs theory data.
        discoverer.Find(false, resultsWriter == null ? discoverySink : new ClassReportingSink(discoverySink, report),
            TestFrameworkOptions.ForDiscovery(assemblyConfig));
        discoverySink.Finished.WaitOne();

        string testResultsDirectory = Environment.GetEnvironmentVariable("TEST_RESULTS_DIR");
        string xmlResultFileName = string.IsNullOrEmpty(testResultsDirectory)
            ? null
            : Path.Combine(testResultsDirectory, "testResults.xml");
        XunitFilters filters = new XunitFilters();
        // Quick hack wo much validation to get args that are passed (notrait, xml)
        Dictionary<string, List<string>> noTraits = new Dictionary<string, List<string>>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("-notrait", StringComparison.OrdinalIgnoreCase))
            {
                var traitKeyValue = args[i + 1].Split("=", StringSplitOptions.TrimEntries);

                if (!noTraits.TryGetValue(traitKeyValue[0], out List<string> values))
                {
                    noTraits.Add(traitKeyValue[0], values = new List<string>());
                }

                values.Add(traitKeyValue[1]);
                i++;
            }

            if (args[i].Equals("-xml", StringComparison.OrdinalIgnoreCase))
            {
                xmlResultFileName = args[i + 1].Trim();
                i++;
            }

            if (args[i].Equals("-class", StringComparison.OrdinalIgnoreCase))
            {
                filters.IncludedClasses.Add(args[i + 1].Trim());
                i++;
            }

            if (args[i].Equals("-noclass", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-class-", StringComparison.OrdinalIgnoreCase))
            {
                filters.ExcludedClasses.Add(args[i + 1].Trim());
                i++;
            }

            if (args[i].Equals("-method", StringComparison.OrdinalIgnoreCase))
            {
                filters.IncludedMethods.Add(args[i + 1].Trim());
                i++;
            }

            if (args[i].Equals("-nomethod", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-method-", StringComparison.OrdinalIgnoreCase))
            {
                filters.ExcludedMethods.Add(args[i + 1].Trim());
                i++;
            }

            if (args[i].Equals("-namespace", StringComparison.OrdinalIgnoreCase))
            {
                filters.IncludedNamespaces.Add(args[i + 1].Trim());
                i++;
            }

            if (args[i].Equals("-nonamespace", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-namespace-", StringComparison.OrdinalIgnoreCase))
            {
                filters.ExcludedNamespaces.Add(args[i + 1].Trim());
                i++;
            }

            if (args[i].Equals("-parallel", StringComparison.OrdinalIgnoreCase))
            {
                string parallelismArg = args[i + 1].Trim().ToLower();
                var (parallelizeAssemblies, parallelizeTestCollections) = parallelismArg switch
                {
                    "all" => (true, true),
                    "assemblies" => (true, false),
                    "collections" => (false, true),
                    "none" => (false, false),
                    _ => throw new ArgumentException($"Unknown parallelism option '{parallelismArg}'.")
                };

                assemblyConfig.ParallelizeAssembly = parallelizeAssemblies;
                assemblyConfig.ParallelizeTestCollections = parallelizeTestCollections;
                i++;
            }
        }

        foreach (KeyValuePair<string, List<string>> kvp in noTraits)
        {
            filters.ExcludedTraits.Add(kvp.Key, kvp.Value);
        }

        var filteredTestCases = discoverySink.TestCases.Where(filters.Filter).ToList();
        if (resultsWriter != null)
        {
            report($"Discovered {discoverySink.TestCases.Count} test cases, running {filteredTestCases.Count}");
        }
        var executor = xunitTestFx.CreateExecutor(asmName);
#pragma warning disable CS0618 // Delegating*Sink types are marked obsolete
        // Reporting on stderr (see resultsWriter): also name the tests still running after two minutes, every two
        // minutes, so that a hung test can be told from a slow one on a device reached only through its console.
        IExecutionSink executionSink = resultsWriter == null ? resultsSink :
            new DelegatingLongRunningTestDetectionSink(resultsSink, TimeSpan.FromMinutes(2), summary =>
            {
                foreach (KeyValuePair<ITestCase, TimeSpan> running in summary.TestCases)
                {
                    report($"[LONG] {running.Key.DisplayName} running {running.Value:hh\\:mm\\:ss}");
                }
            });
#pragma warning restore CS0618
        executor.RunTests(filteredTestCases, new DynamicSkipSink(executionSink), TestFrameworkOptions.ForExecution(assemblyConfig));

        resultsSink.Finished.WaitOne();

        // Helix need to see results file in the drive to detect if the test has failed or not
        if(xmlResultFileName != null)
        {
            resultsXmlAssembly.Save(xmlResultFileName);
        }

        // The results that are not passes, one record per line: "@@R <length> <FNV-1a 64 hex> <base64 of the element's
        // UTF-8 XML>". The console may drop bytes under load (KasperskyOS under QEMU, 2026-09-23), so each record carries
        // its own check, and records are paced.
        if (resultsWriter != null)
        {
            // -countmethods: how many tests each test method ran, passes included, to compare a run's total with
            // another platform's results.xml without sending every test's name over the console.
            if (args.Contains("-countmethods", StringComparer.OrdinalIgnoreCase))
            {
                foreach (IGrouping<string, XElement> method in resultsXmlAssembly.Descendants("test")
                    .GroupBy(t => (string)t.Attribute("type") + "." + (string)t.Attribute("method"))
                    .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    resultsWriter.WriteLine($"[COUNT] {method.Key} {method.Count()}");
                    System.Threading.Thread.Sleep(5);
                }
            }

            resultsWriter.WriteLine("=== NON-PASSING TEST RESULTS BEGIN ===");
            foreach (XElement element in resultsXmlAssembly.Descendants("test").Where(t => (string)t.Attribute("result") != "Pass")
                .Concat(resultsXmlAssembly.Descendants("error")))
            {
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting));
                ulong hash = 14695981039346656037;
                foreach (byte b in utf8)
                {
                    hash = unchecked((hash ^ b) * 1099511628211);
                }
                resultsWriter.WriteLine($"@@R {utf8.Length} {hash:x16} {Convert.ToBase64String(utf8)}");
                System.Threading.Thread.Sleep(5);
            }
            resultsWriter.WriteLine($"=== NON-PASSING TEST RESULTS END: total {resultsSink.ExecutionSummary.Total}, failed {resultsSink.ExecutionSummary.Failed}, errors {resultsSink.ExecutionSummary.Errors}, skipped {resultsSink.ExecutionSummary.Skipped} ===");
        }

        var failed = resultsSink.ExecutionSummary.Failed > 0 || resultsSink.ExecutionSummary.Errors > 0;
        return failed ? 1 : 0;
    }

    // Regular files and directories of a ustar archive (tar --format=ustar), unpacked under a directory; returns the
    // number of files.
    private static int UnpackTar(string archive, string destination)
    {
        int files = 0;
        using FileStream tar = File.OpenRead(archive);
        byte[] header = new byte[512];
        while (tar.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header[0] != 0)
        {
            string name = Field(header, 0, 100);
            string prefix = Field(header, 345, 155);
            if (prefix.Length > 0)
            {
                name = prefix + "/" + name;
            }
            long size = Convert.ToInt64(Field(header, 124, 12).Trim(), 8);
            char type = (char)header[156];
            string path = Path.Combine(destination, name.TrimStart('.', '/'));
            long skip = (size + 511) / 512 * 512; // the entry's data, padded to the next header
            if (type == '5')
            {
                Directory.CreateDirectory(path);
            }
            else if (type == '0' || type == '\0')
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (FileStream file = File.Create(path))
                {
                    byte[] data = new byte[size];
                    tar.ReadExactly(data);
                    file.Write(data);
                }
                files++;
                skip -= size;
            }
            tar.Seek(skip, SeekOrigin.Current);
        }
        return files;

        static string Field(byte[] header, int offset, int length)
        {
            int end = Array.IndexOf(header, (byte)0, offset, length);
            return System.Text.Encoding.ASCII.GetString(header, offset, (end < 0 ? offset + length : end) - offset);
        }
    }
}

// This is about running on desktop FX, which we don't do
#pragma warning disable xUnit3000
internal class ConsoleDiagnosticMessageSink : IMessageSink
{
    public bool OnMessage(IMessageSinkMessage message)
    {
        if (message is IDiagnosticMessage diagnosticMessage)
        {
            return true;
        }
        return false;
    }
}

// A test skipped at run time (SkipException.ForSkip) fails with a message starting "$XunitDynamicSkip$", which xunit's
// console runners report as a skip: do the same, and correct the assembly's counts.
internal class DynamicSkipSink : IMessageSinkWithTypes
{
    private const string Prefix = "$XunitDynamicSkip$";
    private readonly IMessageSinkWithTypes _inner;
    private int _skipped;

    public DynamicSkipSink(IMessageSinkWithTypes inner) => _inner = inner;

    public void Dispose() => _inner.Dispose();

    public bool OnMessageWithTypes(IMessageSinkMessage message, HashSet<string> messageTypes)
    {
        if (message is ITestFailed failed && failed.Messages.Length > 0 && failed.Messages[0] != null &&
            failed.Messages[0].StartsWith(Prefix, StringComparison.Ordinal))
        {
            System.Threading.Interlocked.Increment(ref _skipped);
            return Forward(new Xunit.Sdk.TestSkipped(failed.Test, failed.Messages[0].Substring(Prefix.Length)));
        }
        if (message is ITestAssemblyFinished finished && _skipped > 0)
        {
            return Forward(new Xunit.Sdk.TestAssemblyFinished(finished.TestCases, finished.TestAssembly, finished.ExecutionTime,
                finished.TestsRun, finished.TestsFailed - _skipped, finished.TestsSkipped + _skipped));
        }
        return _inner.OnMessageWithTypes(message, messageTypes);
    }

    private bool Forward(IMessageSinkMessage message) =>
        _inner.OnMessageWithTypes(message, new HashSet<string>(message.GetType().GetInterfaces().Select(i => i.FullName)));
}

// Forwards discovery messages, naming each test class as discovery reaches it.
internal class ClassReportingSink : IMessageSink
{
    private readonly IMessageSink _inner;
    private readonly Action<string> _report;
    private string _lastClass;

    public ClassReportingSink(IMessageSink inner, Action<string> report)
    {
        _inner = inner;
        _report = report;
    }

    public bool OnMessage(IMessageSinkMessage message)
    {
        if (message is ITestCaseDiscoveryMessage discovered)
        {
            string className = discovered.TestCase.TestMethod.TestClass.Class.Name;
            if (className != _lastClass)
            {
                _lastClass = className;
                _report($"[DISC] {className}");
            }
        }
        return _inner.OnMessage(message);
    }
}
#pragma warning restore xUnit3000

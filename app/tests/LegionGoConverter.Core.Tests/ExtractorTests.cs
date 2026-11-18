using System.Diagnostics;

namespace LegionGoConverter.Core.Tests;

public class ExtractorTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "lgc-ext-" + Guid.NewGuid().ToString("N"));

    public ExtractorTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    sealed class FakeRunner : IProcessRunner
    {
        public List<(string Exe, List<string> Args)> Calls { get; } = new();
        public ProcessResult Result { get; set; } = new(0, "");
        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct)
        {
            Calls.Add((exe, args.ToList()));
            return Task.FromResult(Result);
        }
    }

    public static IEnumerable<object[]> Extractors()
    {
        yield return new object[] { (Func<IProcessRunner, IExtractor>)(r => new SevenZipExtractor(r, "7z.exe")) };
        yield return new object[] { (Func<IProcessRunner, IExtractor>)(r => new InnoExtractor(r, "innoextract.exe")) };
    }

    [Theory, MemberData(nameof(Extractors))]
    public async Task Extract_NonEmptyTarget_ThrowsBeforeRunning(Func<IProcessRunner, IExtractor> make)
    {
        var target = Path.Combine(_tmp, "t");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "x.txt"), "x");
        var runner = new FakeRunner();
        await Assert.ThrowsAsync<ExtractionException>(() => make(runner).ExtractAsync("a.exe", target, null, CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Theory, MemberData(nameof(Extractors))]
    public async Task Extract_PathWithSpacesAndNonAscii_PassedAsSingleArgument(Func<IProcessRunner, IExtractor> make)
    {
        var installer = @"C:\In Dir\Instalación ünï.exe";
        var target = Path.Combine(_tmp, "out dir ñ");
        var runner = new FakeRunner();
        await make(runner).ExtractAsync(installer, target, null, CancellationToken.None);
        var args = Assert.Single(runner.Calls).Args;
        Assert.Contains(installer, args);
        Assert.Contains(args, a => a.Contains(target));
        Assert.DoesNotContain(args, a => a.Contains(' ') && a.Contains(installer) && a != installer);
    }

    [Theory, MemberData(nameof(Extractors))]
    public async Task Extract_ToolFails_ThrowsWithToolOutput(Func<IProcessRunner, IExtractor> make)
    {
        var runner = new FakeRunner { Result = new ProcessResult(2, "boom: disk on fire") };
        var ex = await Assert.ThrowsAsync<ExtractionException>(
            () => make(runner).ExtractAsync("a.exe", Path.Combine(_tmp, "o"), null, CancellationToken.None));
        Assert.Contains("boom: disk on fire", ex.Message);
    }

    [Fact]
    public async Task Cancel_KillsProcess()
    {
        using var cts = new CancellationTokenSource();
        var pidSeen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress(l =>
        {
            if (l.StartsWith("PID=") && int.TryParse(l.AsSpan(4), out var id)) pidSeen.TrySetResult(id);
        });
        var sw = Stopwatch.StartNew();
        var task = new ProcessRunner().RunAsync("powershell.exe",
            new[] { "-NoProfile", "-Command", "Write-Output \"PID=$PID\"; Start-Sleep -Seconds 60" }, progress, cts.Token);

        var done = await Task.WhenAny(pidSeen.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == pidSeen.Task, "child never reported its PID");
        var pid = await pidSeen.Task;
        using (var alive = Process.GetProcessById(pid))
            Assert.False(alive.HasExited, "child should be alive before cancel");

        sw.Restart();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"cancel took {sw.Elapsed}");

        try
        {
            using var p = Process.GetProcessById(pid);
            Assert.True(p.HasExited, "child still running after cancel");
        }
        catch (ArgumentException) { /* no such process: killed */ }
    }

    [Fact]
    public async Task ProcessRunner_HugeOutput_KeepsBoundedTailButReportsAllLines()
    {
        const int total = 5000;
        var reported = 0;
        var progress = new SyncProgress(_ => Interlocked.Increment(ref reported));
        var res = await new ProcessRunner().RunAsync("powershell.exe",
            new[] { "-NoProfile", "-Command", $"1..{total} | ForEach-Object {{ \"line$_\" }}" }, progress, CancellationToken.None);
        Assert.Equal(0, res.ExitCode);
        Assert.Equal(total, Volatile.Read(ref reported));
        var kept = res.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(kept.Length <= ProcessRunner.MaxTailLines);
        Assert.Equal($"line{total}", kept[^1]);
        Assert.DoesNotContain("line1", kept);
    }

    [Fact]
    public async Task ProcessRunner_NonExecutableFile_Throws()
    {
        var bad = Path.Combine(_tmp, "notexe.exe");
        File.WriteAllText(bad, "not a program");
        await Assert.ThrowsAnyAsync<Exception>(() => new ProcessRunner().RunAsync(bad, Array.Empty<string>(), null, CancellationToken.None));
    }

    [Theory, MemberData(nameof(Extractors))]
    public async Task Extract_StartFailure_ThrowsExtractionException(Func<IProcessRunner, IExtractor> _)
    {
        var bad = Path.Combine(_tmp, "notexe.exe");
        File.WriteAllText(bad, "not a program");
        var runner = new ProcessRunner();
        foreach (var ex in new IExtractor[] { new SevenZipExtractor(runner, bad), new InnoExtractor(runner, bad) })
            await Assert.ThrowsAsync<ExtractionException>(() =>
                ex.ExtractAsync("a.exe", Path.Combine(_tmp, "o" + Guid.NewGuid().ToString("N")), null, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessRunner_CapturesStdoutStderrAndReportsLines()
    {
        var lines = new List<string>();
        var res = await new ProcessRunner().RunAsync("cmd.exe", new[] { "/c", "echo out1& echo err1 1>&2" },
            new SyncProgress(lines.Add), CancellationToken.None);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("out1", res.Output);
        Assert.Contains("err1", res.Output);
        Assert.Contains(lines, l => l.Contains("out1"));
        Assert.Contains(lines, l => l.Contains("err1"));
    }

    sealed class SyncProgress(Action<string> a) : IProgress<string>
    {
        public void Report(string v) { lock (a) a(v); }
    }

    [Fact]
    public void ToolLocator_MissingTool_NamesFile()
    {
        var dir = Path.Combine(_tmp, "tools");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "7z.exe"), "");
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(dir));
        Assert.Contains("innoextract.exe", ex.Message);
    }

    [Fact]
    public void ToolLocator_AllPresent_ReturnsPaths()
    {
        var dir = Path.Combine(_tmp, "tools");
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { "7z.exe", "innoextract.exe", "makecat.exe", "signtool.exe" })
            File.WriteAllText(Path.Combine(dir, n), "");
        var t = ToolLocator.Resolve(dir, Array.Empty<string>(), new AcceptAllVerifier());
        Assert.Equal(Path.Combine(dir, "7z.exe"), t.SevenZip);
        Assert.Equal(Path.Combine(dir, "signtool.exe"), t.SignTool);
    }

    const string SevenZip = SevenZipFactAttribute.Path;

    [SevenZipFact]
    public async Task SevenZip_RealTool_ExtractsSmallArchive()
    {
        var src = Path.Combine(_tmp, "src dir");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "héllo.txt"), "hi");
        var archive = Path.Combine(_tmp, "a b.7z");
        var runner = new ProcessRunner();
        var make = await runner.RunAsync(SevenZip, new[] { "a", archive, Path.Combine(src, "*") }, null, CancellationToken.None);
        Assert.Equal(0, make.ExitCode);
        var target = Path.Combine(_tmp, "extracted out");
        await new SevenZipExtractor(runner, SevenZip).ExtractAsync(archive, target, null, CancellationToken.None);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(target, "héllo.txt")));
    }
}

/// <summary>A fact skipped when the local 7z.exe is absent.</summary>
public sealed class SevenZipFactAttribute : FactAttribute
{
    public const string Path = @"C:\Program Files\7-Zip\7z.exe";
    public SevenZipFactAttribute()
    {
        if (!File.Exists(Path)) Skip = $"7z.exe not found: {Path}";
    }
}

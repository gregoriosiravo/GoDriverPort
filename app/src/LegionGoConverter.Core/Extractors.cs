namespace LegionGoConverter.Core;

public class ExtractionException : Exception
{
    public ExtractionException(string message) : base(message) { }
}

public interface IExtractor
{
    Task ExtractAsync(string installer, string targetDir, IProgress<string>? lines, CancellationToken ct);
}

public abstract class ExtractorBase : IExtractor
{
    readonly IProcessRunner _runner;
    readonly string _exe;
    internal ExtractorBase(IProcessRunner runner, string exe) { _runner = runner; _exe = exe; }

    protected abstract string ToolName { get; }
    protected abstract IReadOnlyList<string> BuildArgs(string installer, string targetDir);

    public Task ExtractAsync(string installer, string targetDir, IProgress<string>? lines, CancellationToken ct) =>
        RunExtractionAsync(BuildArgs(installer, targetDir), targetDir, lines, ct);

    protected async Task RunExtractionAsync(IReadOnlyList<string> args, string targetDir, IProgress<string>? lines, CancellationToken ct)
    {
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            throw new ExtractionException($"Target folder is not empty: {targetDir}");
        ProcessResult r;
        try { r = await _runner.RunAsync(_exe, args, lines, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new ExtractionException($"Could not run {ToolName} ({_exe}): {ex.Message}");
        }
        if (r.ExitCode != 0)
            throw new ExtractionException($"{ToolName} failed with exit code {r.ExitCode}: {r.Output}");
    }
}

/// <summary>Extracts with 7-Zip (AMD installer, ccc2_install.exe).</summary>
public sealed class SevenZipExtractor : ExtractorBase
{
    public SevenZipExtractor(IProcessRunner runner, string sevenZipExe) : base(runner, sevenZipExe) { }
    protected override string ToolName => "7z";
    protected override IReadOnlyList<string> BuildArgs(string installer, string targetDir) =>
        new[] { "x", "-y", "-bb0", "-o" + targetDir, "--", installer };

    /// <summary>Extracts only the archive paths matching <paramref name="patterns"/> (7-Zip wildcards relative to the archive root).</summary>
    public Task ExtractSelectedAsync(string installer, string targetDir, IReadOnlyList<string> patterns, IProgress<string>? lines, CancellationToken ct) =>
        RunExtractionAsync(new[] { "x", "-y", "-bb0", "-o" + targetDir, "--", installer }.Concat(patterns).ToArray(), targetDir, lines, ct);
}

/// <summary>Extracts with innoextract (Lenovo installer).</summary>
public sealed class InnoExtractor : ExtractorBase
{
    public InnoExtractor(IProcessRunner runner, string innoextractExe) : base(runner, innoextractExe) { }
    protected override string ToolName => "innoextract";
    protected override IReadOnlyList<string> BuildArgs(string installer, string targetDir) =>
        new[] { "--silent", "-d", targetDir, "--", installer };
}

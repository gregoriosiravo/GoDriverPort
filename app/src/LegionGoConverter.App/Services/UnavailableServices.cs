using LegionGoConverter.Core;

namespace LegionGoConverter.App;

/// <summary>Used when the bundled tools are missing: reports the problem instead of crashing.</summary>
public sealed class UnavailableConversionRunner(string message) : IConversionRunner
{
    public Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<StageProgress> progress, IUserPrompt prompt, CancellationToken ct) =>
        Task.FromResult(new ConversionResult(false, false, "", message));
}

public sealed class UnavailableInstallerIdentifier(string message) : IInstallerIdentifier
{
    public Task<InstallerInfo> IdentifyAsync(string path, CancellationToken ct) =>
        Task.FromResult(new InstallerInfo(InstallerType.Unknown, message));
}

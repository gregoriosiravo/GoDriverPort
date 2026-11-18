using LegionGoConverter.Core;

namespace LegionGoConverter.App;

/// <summary>Runs the conversion pipeline (the real one wraps <see cref="ConversionPipeline.RunAsync"/>).</summary>
public interface IConversionRunner
{
    Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<StageProgress> progress, IUserPrompt prompt, CancellationToken ct);
}

/// <summary>Rolls back the latest session (the real one loads its manifest and runs <see cref="RollbackService"/>; wired in 10b).</summary>
public interface IRollbackRunner
{
    bool HasPreviousSession { get; }
    string? LatestSessionDir { get; }
    Task<RollbackResult> RunAsync(CancellationToken ct);
}

public enum InstallerType { Unknown, Lenovo, Amd }

/// <summary>What an installer file turned out to be, judged by content. <see cref="Version"/> is null when not known.</summary>
public sealed record InstallerInfo(InstallerType Type, string Message, string? Version = null);

/// <summary>Named differently from <see cref="Core.IInstallerInspector"/> (which answers one yes/no question) to avoid ambiguity.</summary>
public interface IInstallerIdentifier
{
    Task<InstallerInfo> IdentifyAsync(string path, CancellationToken ct);
}

public interface IFilePicker
{
    /// <summary>Returns the chosen path, or null when cancelled.</summary>
    string? PickFile(string title, string filter);
    string? PickFolder(string title);
}

/// <summary><see cref="Security"/> is null when it could not be read (shown as "Unknown").</summary>
public sealed record DeviceInfo(string? HardwareId, SecurityState? Security, string? Error = null);

/// <summary>Read-only view of the live AMD device and Secure Boot / HVCI state.</summary>
public interface IDeviceInfoProvider
{
    DeviceInfo Get();
}

public interface ISystemActions
{
    void RestartWindows();
    void OpenFolder(string path);
}

using LegionGoConverter.Core;

namespace LegionGoConverter.App;

/// <summary>Identifies an installer using Core's content inspector (never the file name). Never throws except on cancellation.</summary>
public sealed class ContentInstallerIdentifier(Core.IInstallerInspector inspector) : IInstallerIdentifier
{
    public async Task<InstallerInfo> IdentifyAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(InstallerType.Unknown, "Choose a file.");
        try
        {
            if (await inspector.IsAsync(path, InstallerKind.Lenovo, ct)) return new(InstallerType.Lenovo, "Lenovo display driver installer.");
            if (await inspector.IsAsync(path, InstallerKind.Amd, ct)) return new(InstallerType.Amd, "AMD driver installer.");
            return new(InstallerType.Unknown, "This file is not a Lenovo or AMD display driver installer.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return new(InstallerType.Unknown, "Could not inspect this file: " + e.Message);
        }
    }
}

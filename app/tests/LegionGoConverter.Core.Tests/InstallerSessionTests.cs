using LegionGoConverter.App;
using LegionGoConverter.Core;

namespace LegionGoConverter.Core.Tests;

public class InstallerSessionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "lgo-ui-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    sealed class FakeCore(bool lenovo, bool amd) : Core.IInstallerInspector
    {
        public Task<bool> IsAsync(string installer, InstallerKind kind, CancellationToken ct) =>
            Task.FromResult(kind == InstallerKind.Lenovo ? lenovo : amd);
    }

    [Theory]
    [InlineData(true, false, InstallerType.Lenovo)]
    [InlineData(false, true, InstallerType.Amd)]
    [InlineData(false, false, InstallerType.Unknown)]
    public async Task Identifier_MapsCoreInspector(bool l, bool a, InstallerType expected)
    {
        var info = await new ContentInstallerIdentifier(new FakeCore(l, a)).IdentifyAsync("x.exe", default);
        Assert.Equal(expected, info.Type);
    }

    [Fact]
    public async Task Identifier_MissingFile_IsUnknownNotThrown()
    {
        var info = await new ContentInstallerIdentifier(new ThrowingCore()).IdentifyAsync("x.exe", default);
        Assert.Equal(InstallerType.Unknown, info.Type);
        Assert.False(string.IsNullOrWhiteSpace(info.Message));
    }

    sealed class ThrowingCore : Core.IInstallerInspector
    {
        public Task<bool> IsAsync(string installer, InstallerKind kind, CancellationToken ct) => throw new ToolMissingException("no tool");
    }

    [Fact]
    public void SessionLocator_FindsLatestManifest()
    {
        Assert.Null(SessionLocator.FindLatestSessionDir(_root)); // missing root
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "c")); // no manifest
        // (final review finding 1: only manifests that load count; "{}" no longer does)
        var m = new SessionManifest("1", "AB", SigningCertificate.DefaultSubject, Array.Empty<PackageRef>(), @"C:\x\u0202094.inf",
            Array.Empty<string>(), Array.Empty<string>());
        m.Save(Path.Combine(a, "manifest.json"));
        m.Save(Path.Combine(b, "manifest.json"));
        File.SetLastWriteTimeUtc(Path.Combine(a, "manifest.json"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.Combine(b, "manifest.json"), DateTime.UtcNow.AddHours(-1));
        Assert.Equal(b, SessionLocator.FindLatestSessionDir(_root));
    }
}

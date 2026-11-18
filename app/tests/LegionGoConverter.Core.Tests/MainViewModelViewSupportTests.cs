using LegionGoConverter.App;

namespace LegionGoConverter.Core.Tests;

/// <summary>Small view-support members of <see cref="MainViewModel"/> used by the XAML views.</summary>
public class MainViewModelViewSupportTests
{
    readonly FakeFiles _files = new();

    MainViewModel Vm() => new(new FakeRunner(), new FakeRollback(), new FakeIdentifier(), _files, new FakeDevice(), new FakeSystemActions(), a => a());

    [Fact]
    public void IsExportOnly_FollowsExportFolder_AndRaisesChange()
    {
        var vm = Vm();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(vm.IsExportOnly);
        vm.ExportFolder = @"C:\out";
        Assert.True(vm.IsExportOnly);
        Assert.Contains(nameof(MainViewModel.IsExportOnly), changed);
        vm.ExportFolder = "   ";
        Assert.False(vm.IsExportOnly);
    }

    [Fact]
    public async Task StageMessage_TracksLatestProgress_AndResetsOnStart()
    {
        var runner = new FakeRunner();
        var seen = new List<string?>();
        MainViewModel? vm = null;
        runner.Behavior = (_, p, _, _) =>
        {
            p.Report(new StageProgress(Stage.Build, "Building the package", 0.5));
            seen.Add(vm!.StageMessage);
            p.Report(new StageProgress(Stage.Sign, "Signing", null));
            seen.Add(vm.StageMessage);
            return Task.FromResult(new ConversionResult(true, false, @"C:\s", null));
        };
        vm = new MainViewModel(runner, new FakeRollback(), new FakeIdentifier(), _files, new FakeDevice(), new FakeSystemActions(), a => a());
        vm.LenovoInstallerPath = "lenovo.exe";
        vm.AmdInstallerPath = "amd.exe";
        await vm.PendingValidation;
        vm.NextCommand.Execute(null);
        vm.StageMessage = "stale";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Building the package", "Signing" }, seen);
        runner.Behavior = (_, _, _, _) => { seen.Add(vm.StageMessage); return Task.FromResult(new ConversionResult(true, false, @"C:\s", null)); };
        vm.BackCommand.Execute(null);
        vm.NextCommand.Execute(null);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(seen[^1]);
    }

    [Fact]
    public void ClearExport_ReturnsToInstallMode()
    {
        var vm = Vm();
        _files.NextFolder = @"C:\out";
        vm.BrowseExportCommand.Execute(null);
        Assert.True(vm.IsExportOnly);
        vm.ClearExportCommand.Execute(null);
        Assert.Null(vm.ExportFolder);
        Assert.False(vm.IsExportOnly);
    }
}

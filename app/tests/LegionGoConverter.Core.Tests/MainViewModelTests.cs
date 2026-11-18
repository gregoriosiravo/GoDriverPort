using LegionGoConverter.App;
using LegionGoConverter.Core;

namespace LegionGoConverter.Core.Tests;

sealed class FakeRunner : IConversionRunner
{
    public ConversionRequest? Request;
    public int Calls;
    public Func<ConversionRequest, IProgress<StageProgress>, IUserPrompt, CancellationToken, Task<ConversionResult>> Behavior =
        (_, _, _, _) => Task.FromResult(new ConversionResult(true, false, @"C:\sessions\s1", null));
    public Task<ConversionResult> RunAsync(ConversionRequest r, IProgress<StageProgress> p, IUserPrompt prompt, CancellationToken ct)
    {
        Calls++; Request = r; return Behavior(r, p, prompt, ct);
    }
}

sealed class FakeRollback : IRollbackRunner
{
    public bool HasPreviousSession { get; set; }
    public string? LatestSessionDir { get; set; } = @"C:\sessions\s0";
    public int Calls;
    public Func<Task<RollbackResult>> Behavior = () => Task.FromResult(new RollbackResult(true, new[] { "oem5.inf" }));
    public Task<RollbackResult> RunAsync(CancellationToken ct) { Calls++; return Behavior(); }
}

sealed class FakeIdentifier : IInstallerIdentifier
{
    public Dictionary<string, InstallerInfo> Map = new()
    {
        ["lenovo.exe"] = new(InstallerType.Lenovo, "Lenovo package", "32.0.23044.19"),
        ["amd.exe"] = new(InstallerType.Amd, "AMD package", "32.0.32015.12012"),
    };
    public Task<InstallerInfo> IdentifyAsync(string path, CancellationToken ct) =>
        Task.FromResult(Map.TryGetValue(path, out var i) ? i : new InstallerInfo(InstallerType.Unknown, "unknown"));
}

sealed class FakeDevice : IDeviceInfoProvider
{
    public DeviceInfo Get() => new(@"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04", new SecurityState(true, false, false));
}

sealed class FakeFiles : IFilePicker
{
    public string? NextFile; public string? NextFolder;
    public string? PickFile(string title, string filter) => NextFile;
    public string? PickFolder(string title) => NextFolder;
}

sealed class FakeSystemActions : ISystemActions
{
    public int Restarts; public List<string> Opened = new();
    public void RestartWindows() => Restarts++;
    public void OpenFolder(string path) => Opened.Add(path);
}

public class MainViewModelTests
{
    readonly FakeRunner _runner = new();
    readonly FakeRollback _rollback = new();
    readonly FakeFiles _files = new();
    readonly FakeSystemActions _sys = new();
    readonly FakeIdentifier _ident = new();

    MainViewModel Vm() => new(_runner, _rollback, _ident, _files, new FakeDevice(), _sys, a => a());

    async Task<MainViewModel> ReadyAsync()
    {
        var vm = Vm();
        vm.LenovoInstallerPath = "lenovo.exe";
        vm.AmdInstallerPath = "amd.exe";
        await vm.PendingValidation;
        vm.NextCommand.Execute(null);
        return vm;
    }

    static async Task YesAsync(MainViewModel vm)
    {
        Assert.NotNull(vm.PendingPrompt);
        vm.PendingPrompt!.YesCommand.Execute(null);
        await Task.Yield();
    }

    [Fact]
    public async Task Next_Disabled_UntilBothInstallersValid()
    {
        var vm = Vm();
        Assert.False(vm.NextCommand.CanExecute(null));
        vm.LenovoInstallerPath = "lenovo.exe";
        await vm.PendingValidation;
        Assert.True(vm.LenovoInfo.IsValid);
        Assert.Equal("32.0.23044.19", vm.LenovoInfo.Version);
        Assert.False(vm.NextCommand.CanExecute(null));
        vm.AmdInstallerPath = "amd.exe";
        await vm.PendingValidation;
        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        Assert.Equal(WizardStep.Review, vm.Step);
        Assert.Equal(@"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04", vm.DeviceId);
        Assert.True(vm.Security!.SecureBoot);
    }

    [Fact]
    public async Task SwappedInstallers_AreRejectedWithMessage()
    {
        var vm = Vm();
        vm.LenovoInstallerPath = "amd.exe";
        vm.AmdInstallerPath = "lenovo.exe";
        await vm.PendingValidation;
        Assert.False(vm.LenovoInfo.IsValid);
        Assert.False(vm.AmdInfo.IsValid);
        Assert.Contains("AMD", vm.LenovoInfo.Message);
        Assert.Contains("swapped", vm.LenovoInfo.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnknownFile_IsRejected_AndChangingPathResetsValidation()
    {
        var vm = Vm();
        vm.LenovoInstallerPath = "lenovo.exe";
        vm.AmdInstallerPath = "amd.exe";
        await vm.PendingValidation;
        Assert.True(vm.NextCommand.CanExecute(null));
        vm.LenovoInstallerPath = "other.exe";
        Assert.False(vm.NextCommand.CanExecute(null)); // reset immediately, before re-check completes
        await vm.PendingValidation;
        Assert.False(vm.LenovoInfo.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(vm.LenovoInfo.Message));
    }

    [Fact]
    public async Task Browse_SetsPath()
    {
        var vm = Vm();
        _files.NextFile = "lenovo.exe";
        vm.BrowseLenovoCommand.Execute(null);
        await vm.PendingValidation;
        Assert.Equal("lenovo.exe", vm.LenovoInstallerPath);
        _files.NextFile = null; // cancelled dialog keeps the old value
        vm.BrowseLenovoCommand.Execute(null);
        Assert.Equal("lenovo.exe", vm.LenovoInstallerPath);
    }

    [Fact]
    public async Task Start_PassesOptionsToRunner()
    {
        var vm = await ReadyAsync();
        vm.InstallDriver = true;
        vm.InstallAmdSoftware = true;
        await vm.StartCommand.ExecuteAsync(null);
        var r = _runner.Request!;
        Assert.Equal("amd.exe", r.AmdInstaller);
        Assert.Equal("lenovo.exe", r.LenovoInstaller);
        Assert.Equal(ConversionPipeline.DefaultWorkRoot, r.WorkRoot);
        Assert.True(r.InstallDriver);
        Assert.True(r.InstallAmdSoftware);
        Assert.Null(r.ExportFolder);
        Assert.Equal(WizardStep.Result, vm.Step);
        Assert.True(vm.Success);
        Assert.Equal(@"C:\sessions\s1", vm.SessionDir);
    }

    [Fact]
    public async Task ExportFolder_IsExportOnly()
    {
        var vm = await ReadyAsync();
        vm.ExportFolder = @"D:\out";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\out", _runner.Request!.ExportFolder);
        Assert.False(_runner.Request.InstallDriver);
        Assert.False(_runner.Request.InstallAmdSoftware);
    }

    [Fact]
    public void AmdSoftware_ForcesDriver()
    {
        var vm = Vm();
        vm.InstallDriver = false;
        vm.InstallAmdSoftware = true;
        Assert.True(vm.InstallDriver);
        vm.InstallAmdSoftware = true;
        vm.InstallDriver = false;
        Assert.False(vm.InstallAmdSoftware);
        Assert.False(vm.InstallDriver);
    }

    [Fact]
    public async Task ConfirmPrompt_PipelineAwaits_YesResolvesTrue()
    {
        var vm = await ReadyAsync();
        var asked = new TaskCompletionSource();
        bool? answer = null;
        _runner.Behavior = async (_, _, prompt, ct) =>
        {
            var t = prompt.ConfirmAsync("Install?", "Really?", ct);
            asked.SetResult();
            answer = await t;
            return new ConversionResult(true, false, "s", null);
        };
        var run = vm.StartCommand.ExecuteAsync(null);
        await asked.Task;
        Assert.NotNull(vm.PendingPrompt);
        Assert.Equal("Install?", vm.PendingPrompt!.Title);
        Assert.Equal("Really?", vm.PendingPrompt.Message);
        Assert.True(vm.PendingPrompt.DefaultIsNo);
        Assert.Null(answer);
        vm.PendingPrompt.YesCommand.Execute(null);
        await run;
        Assert.True(answer);
        Assert.Null(vm.PendingPrompt);
    }

    [Fact]
    public async Task ConfirmPrompt_NoResolvesFalse()
    {
        var vm = await ReadyAsync();
        var asked = new TaskCompletionSource();
        bool? answer = null;
        _runner.Behavior = async (_, _, prompt, ct) =>
        {
            var t = prompt.ConfirmAsync("t", "m", ct);
            asked.SetResult();
            answer = await t;
            return new ConversionResult(false, false, "s", "declined");
        };
        var run = vm.StartCommand.ExecuteAsync(null);
        await asked.Task;
        vm.PendingPrompt!.NoCommand.Execute(null);
        await run;
        Assert.False(answer);
        Assert.Null(vm.PendingPrompt);
    }

    [Fact]
    public async Task Cancel_DuringBuild_ReturnsToReview_WithoutOtherCalls()
    {
        var vm = await ReadyAsync();
        var started = new TaskCompletionSource();
        _runner.Behavior = async (_, p, prompt, ct) =>
        {
            p.Report(new StageProgress(Stage.Build, "Building", 0.3));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return default!;
        };
        var run = vm.StartCommand.ExecuteAsync(null);
        await started.Task;
        Assert.Equal(WizardStep.Progress, vm.Step);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await run;
        Assert.Equal(WizardStep.Review, vm.Step);
        Assert.Contains("cancel", vm.Notice ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _runner.Calls);
        Assert.Equal(0, _rollback.Calls);
        Assert.Equal(0, _sys.Restarts);
        Assert.Empty(_sys.Opened);
    }

    [Fact]
    public async Task Cancel_IsDisabledOnceTheChangesStart_AndBEscDoNothing()
    {
        // final review finding 6: after the confirmation is accepted nothing can be cancelled
        var vm = await ReadyAsync();
        var confirmStage = new TaskCompletionSource();
        var installing = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var installReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        _runner.Behavior = async (_, p, _, ct) =>
        {
            seen = ct;
            p.Report(new StageProgress(Stage.Confirm, "Waiting for your confirmation", null));
            confirmStage.SetResult();
            await installing.Task;
            p.Report(new StageProgress(Stage.RestorePoint, "Creating a System Restore point", null));
            installReported.SetResult();
            await finish.Task;
            return new ConversionResult(true, false, "s", null);
        };
        var run = vm.StartCommand.ExecuteAsync(null);
        await confirmStage.Task;
        Assert.True(vm.CanCancel);
        Assert.False(vm.InstallStarted);
        Assert.True(vm.CancelCommand.CanExecute(null));

        installing.SetResult();
        await installReported.Task;
        Assert.True(vm.InstallStarted);
        Assert.False(vm.CanCancel);
        Assert.False(vm.CancelCommand.CanExecute(null));
        AppComposition.Back(vm); // B / Esc
        Assert.False(seen.IsCancellationRequested);
        Assert.Equal(WizardStep.Progress, vm.Step);

        finish.SetResult();
        await run;
        Assert.True(vm.Success);
        Assert.False(vm.CanCancel);
    }

    [Fact]
    public async Task InstallStarted_IsResetForTheNextRun()
    {
        var vm = await ReadyAsync();
        _runner.Behavior = (_, p, _, _) =>
        {
            p.Report(new StageProgress(Stage.InstallDriver, "x", null));
            return Task.FromResult(new ConversionResult(false, false, "s", "failed"));
        };
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.InstallStarted);
        vm.BackCommand.Execute(null);
        vm.NextCommand.Execute(null);
        bool? lockedAtStart = null;
        _runner.Behavior = (_, _, _, _) => { lockedAtStart = vm.InstallStarted; return Task.FromResult(new ConversionResult(true, false, "s", null)); };
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(lockedAtStart);
    }

    [Fact]
    public async Task ExportOnly_ExportStage_KeepsCancelAvailable()
    {
        var vm = await ReadyAsync();
        var reported = new TaskCompletionSource();
        _runner.Behavior = async (_, p, _, ct) =>
        {
            p.Report(new StageProgress(Stage.Export, "Exporting", 0.5));
            reported.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return default!;
        };
        vm.ExportFolder = @"D:\out";
        var run = vm.StartCommand.ExecuteAsync(null);
        await reported.Task;
        Assert.True(vm.CanCancel);
        vm.CancelCommand.Execute(null);
        await run;
        Assert.Equal(WizardStep.Review, vm.Step);
    }

    [Fact]
    public async Task Cancel_WhilePromptPending_DeniesIt()
    {
        var vm = await ReadyAsync();
        var asked = new TaskCompletionSource();
        bool? answer = null;
        _runner.Behavior = async (_, _, prompt, ct) =>
        {
            var t = prompt.ConfirmAsync("t", "m", CancellationToken.None);
            asked.SetResult();
            answer = await t;
            ct.ThrowIfCancellationRequested();
            return new ConversionResult(false, false, "s", "x");
        };
        var run = vm.StartCommand.ExecuteAsync(null);
        await asked.Task;
        vm.CancelCommand.Execute(null);
        await run;
        Assert.False(answer);
        Assert.Null(vm.PendingPrompt);
        Assert.Equal(WizardStep.Review, vm.Step);
    }

    [Fact]
    public async Task PipelineException_BecomesFailedResult()
    {
        var vm = await ReadyAsync();
        _runner.Behavior = (_, _, _, _) => throw new InvalidOperationException("boom");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Result, vm.Step);
        Assert.False(vm.Success);
        Assert.Contains("boom", vm.Error);
    }

    [Fact]
    public async Task Failure_ShowsErrorAndRollbackOption()
    {
        _rollback.HasPreviousSession = true;
        var vm = await ReadyAsync();
        _runner.Behavior = (_, _, _, _) => Task.FromResult(new ConversionResult(false, false, "s", "driver rejected"));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(vm.Success);
        Assert.Equal("driver rejected", vm.Error);
        Assert.True(vm.RollbackCommand.CanExecute(null));
    }

    [Fact]
    public async Task VerificationPending_ShowsPendingStateAndNotice()
    {
        var vm = await ReadyAsync();
        _runner.Behavior = (_, _, _, _) => Task.FromResult(new ConversionResult(true, true, "s", null, true, "Restart and check Device Manager."));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.Success);
        Assert.True(vm.VerificationPending);
        Assert.Equal("Restart and check Device Manager.", vm.Notice);
        Assert.Contains("not verified", vm.ResultHeadline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", vm.ResultHeadline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reboot_OnlyWhenRequired_AndBehindConfirm()
    {
        var vm = await ReadyAsync();
        await vm.StartCommand.ExecuteAsync(null); // RebootRequired = false
        Assert.False(vm.RebootCommand.CanExecute(null));

        vm = await ReadyAsync();
        _runner.Behavior = (_, _, _, _) => Task.FromResult(new ConversionResult(true, true, "s", null));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.RebootCommand.CanExecute(null));
        var t = vm.RebootCommand.ExecuteAsync(null);
        Assert.Equal(0, _sys.Restarts); // not before the answer
        Assert.True(vm.PendingPrompt!.DefaultIsNo);
        vm.PendingPrompt.NoCommand.Execute(null);
        await t;
        Assert.Equal(0, _sys.Restarts);

        t = vm.RebootCommand.ExecuteAsync(null);
        vm.PendingPrompt!.YesCommand.Execute(null);
        await t;
        Assert.Equal(1, _sys.Restarts);
    }

    [Fact]
    public void Rollback_OnlyOnFirstStep_WhenPreviousSessionExists()
    {
        var vm = Vm();
        Assert.False(vm.RollbackCommand.CanExecute(null));
        _rollback.HasPreviousSession = true;
        vm.RefreshSessions();
        Assert.True(vm.HasPreviousSession);
        Assert.True(vm.RollbackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Rollback_BehindConfirm_ReportsResultWithCertificateKept()
    {
        _rollback.HasPreviousSession = true;
        _rollback.Behavior = () => Task.FromResult(new RollbackResult(true, new[] { "oem5.inf", "oem6.inf" }, CertificateKept: true));
        var vm = Vm();
        var t = vm.RollbackCommand.ExecuteAsync(null);
        Assert.Equal(0, _rollback.Calls);
        Assert.True(vm.PendingPrompt!.DefaultIsNo);
        vm.PendingPrompt.NoCommand.Execute(null);
        await t;
        Assert.Equal(0, _rollback.Calls);
        Assert.Equal(WizardStep.Select, vm.Step);

        t = vm.RollbackCommand.ExecuteAsync(null);
        vm.PendingPrompt!.YesCommand.Execute(null);
        await t;
        Assert.Equal(1, _rollback.Calls);
        Assert.Equal(WizardStep.Result, vm.Step);
        Assert.True(vm.Success);
        Assert.True(vm.RebootRequired);
        Assert.Contains("2", vm.Notice);
        Assert.Contains("certificate", vm.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.RebootCommand.CanExecute(null));
    }

    [Fact]
    public async Task Rollback_QuestionIsHonest_AndResultShowsAmdSoftwareNote()
    {
        // final review finding 5
        _rollback.HasPreviousSession = true;
        _rollback.Behavior = () => Task.FromResult(new RollbackResult(false, new[] { "oem5.inf" }) { SoftwareNote = "AMD Software installed by that run was uninstalled ({X})." });
        var vm = Vm();
        var t = vm.RollbackCommand.ExecuteAsync(null);
        Assert.Contains("AMD Software", vm.PendingPrompt!.Message);
        Assert.Contains("not restored", vm.PendingPrompt.Message);
        Assert.Contains("Microsoft Store", vm.PendingPrompt.Message);
        vm.PendingPrompt.YesCommand.Execute(null);
        await t;
        Assert.Contains("uninstalled ({X})", vm.Notice);
    }

    [Fact]
    public async Task Rollback_GpuNotBackOnLenovo_ShowsTheReprobeAndOffersARestart()
    {
        // final review finding 12: never report a plain success without re-checking the GPU
        _rollback.HasPreviousSession = true;
        _rollback.Behavior = () => Task.FromResult(new RollbackResult(false, new[] { "oem5.inf" })
        { GpuOnLenovo = false, GpuState = "However, the GPU reports driver 32.0.32015.12012 with problem code 43. pnputil /add-driver \"X\" /install" });
        var vm = Vm();
        var t = vm.RollbackCommand.ExecuteAsync(null);
        vm.PendingPrompt!.YesCommand.Execute(null);
        await t;
        Assert.Contains("problem code 43", vm.Notice);
        Assert.Contains("pnputil /add-driver", vm.Notice);
        Assert.True(vm.RebootRequired);
        Assert.True(vm.RebootCommand.CanExecute(null));
    }

    [Fact]
    public async Task PipelineException_HasNoSessionFolder_OpenLogHidden()
    {
        // triage 10a: an exception must not show the previous session's folder
        var vm = await ReadyAsync();
        _rollback.LatestSessionDir = @"C:\sessions\previous";
        _runner.Behavior = (_, _, _, _) => throw new InvalidOperationException("boom");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(vm.SessionDir);
        vm.OpenLogCommand.Execute(null);
        Assert.Empty(_sys.Opened);
    }

    [Fact]
    public async Task Rollback_Exception_BecomesFailure()
    {
        _rollback.HasPreviousSession = true;
        _rollback.Behavior = () => throw new RollbackException("store busy");
        var vm = Vm();
        var t = vm.RollbackCommand.ExecuteAsync(null);
        vm.PendingPrompt!.YesCommand.Execute(null);
        await t;
        Assert.Equal(WizardStep.Result, vm.Step);
        Assert.False(vm.Success);
        Assert.Contains("store busy", vm.Error);
    }

    [Fact]
    public async Task Progress_UpdatesStagesAndBoundedLog()
    {
        var vm = await ReadyAsync();
        var seen = new List<StageState>();
        _runner.Behavior = (_, p, _, _) =>
        {
            p.Report(new StageProgress(Stage.Preflight, "checking", null));
            p.Report(new StageProgress(Stage.Build, "building", 0.5));
            for (int i = 0; i < 600; i++) p.Report(new StageProgress(Stage.Build, "line " + i, null));
            seen.Add(vm.Stages.First(s => s.Stage == Stage.Preflight).State);
            seen.Add(vm.Stages.First(s => s.Stage == Stage.Build).State);
            return Task.FromResult(new ConversionResult(false, false, "s", "stop"));
        };
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(StageState.Done, seen[0]);
        Assert.Equal(StageState.Running, seen[1]);
        Assert.Equal(500, vm.Log.Count);
        Assert.Contains("line 599", vm.Log[^1]);
        Assert.Equal(StageState.Failed, vm.Stages.First(s => s.Stage == Stage.Build).State);
        Assert.Equal(StageState.Pending, vm.Stages.First(s => s.Stage == Stage.Sign).State);
    }

    [Fact]
    public async Task OpenLog_OpensSessionDir_AndBackReturnsToSelect()
    {
        var vm = await ReadyAsync();
        await vm.StartCommand.ExecuteAsync(null);
        vm.OpenLogCommand.Execute(null);
        Assert.Equal(new[] { @"C:\sessions\s1" }, _sys.Opened);
        vm.BackCommand.Execute(null);
        Assert.Equal(WizardStep.Select, vm.Step);
    }

    [Fact]
    public async Task Back_FromReview_ReturnsToSelect()
    {
        var vm = await ReadyAsync();
        vm.BackCommand.Execute(null);
        Assert.Equal(WizardStep.Select, vm.Step);
    }
}

public class MainViewModelExportTests
{
    [Fact]
    public void BrowseExport_SetsFolder()
    {
        var files = new FakeFiles { NextFolder = @"D:\out" };
        var vm = new MainViewModel(new FakeRunner(), new FakeRollback(), new FakeIdentifier(), files, new FakeDevice(), new FakeSystemActions(), a => a());
        vm.BrowseExportCommand.Execute(null);
        Assert.Equal(@"D:\out", vm.ExportFolder);
    }
}

public class MainViewModelPromptRaceTests
{
    [Fact]
    public async Task Cancel_BeforeQueuedPromptSet_DoesNotResurrectDialog()
    {
        var queue = new Queue<Action>();
        void Drain() { while (queue.Count > 0) queue.Dequeue()(); }
        var runner = new FakeRunner();
        var vm = new MainViewModel(runner, new FakeRollback(), new FakeIdentifier(), new FakeFiles(), new FakeDevice(), new FakeSystemActions(), queue.Enqueue);
        vm.LenovoInstallerPath = "lenovo.exe";
        vm.AmdInstallerPath = "amd.exe";
        await vm.PendingValidation;
        Drain();
        vm.NextCommand.Execute(null);

        var asked = new TaskCompletionSource();
        Task<bool>? answer = null;
        runner.Behavior = async (_, _, prompt, ct) =>
        {
            answer = prompt.ConfirmAsync("t", "m", CancellationToken.None);
            asked.SetResult();
            var a = await answer;
            ct.ThrowIfCancellationRequested();
            return new ConversionResult(a, false, "s", null);
        };
        var run = vm.StartCommand.ExecuteAsync(null);
        await asked.Task;
        Assert.Null(vm.PendingPrompt);              // the set is still queued
        vm.CancelCommand.Execute(null);             // Cancel lands first
        await run;
        Drain();                                    // now the queue runs: set must be a no-op
        Assert.Null(vm.PendingPrompt);
        Assert.False(await answer!);
        Assert.Equal(WizardStep.Review, vm.Step);
    }
}

public class MainViewModelStartupWarningTests
{
    [Fact]
    public void StartupWarning_IsNullByDefault_SettableAndNotifies()
    {
        var vm = new MainViewModel(new FakeRunner(), new FakeRollback(), new FakeIdentifier(), new FakeFiles(), new FakeDevice(), new FakeSystemActions(), a => a());
        Assert.Null(vm.StartupWarning);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.StartupWarning = "Required tool 7z.exe not found in X";
        Assert.Equal("Required tool 7z.exe not found in X", vm.StartupWarning);
        Assert.Contains(nameof(MainViewModel.StartupWarning), changed);
    }
}

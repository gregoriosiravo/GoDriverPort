using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegionGoConverter.Core;

namespace LegionGoConverter.App;

public enum WizardStep { Select, Review, Progress, Result }
public enum StageState { Pending, Running, Done, Failed }

public sealed partial class StageItem(Stage stage, string title) : ObservableObject
{
    public Stage Stage { get; } = stage;
    public string Title { get; } = title;
    [ObservableProperty] StageState _state;
}

/// <summary>Detected state of one installer slot. Empty path = not valid, no message.</summary>
public sealed partial class InstallerStatus : ObservableObject
{
    [ObservableProperty] bool _isValid;
    [ObservableProperty] bool _isChecking;
    [ObservableProperty] string _message = "";
    [ObservableProperty] string? _version;
    internal void Set(bool valid, bool checking, string message, string? version)
    { IsValid = valid; IsChecking = checking; Message = message; Version = version; }
}

/// <summary>A yes/no question the view shows as a dialog. The safe choice (No) is the default.</summary>
public sealed class PromptRequest
{
    public PromptRequest(string title, string message, Action<bool> answer)
    {
        Title = title; Message = message;
        YesCommand = new RelayCommand(() => answer(true));
        NoCommand = new RelayCommand(() => answer(false));
    }
    public string Title { get; }
    public string Message { get; }
    public bool DefaultIsNo => true;
    public IRelayCommand YesCommand { get; }
    public IRelayCommand NoCommand { get; }
}

/// <summary>Screen state and commands for the wizard. Never throws to the UI. Pass <paramref name="post"/> (e.g. the
/// WPF dispatcher) to marshal every UI-bound mutation after an await or off-thread callback onto the UI thread. Required: passing <c>a => a()</c> (inline) is for tests only.</summary>
public sealed partial class MainViewModel : ObservableObject, IUserPrompt
{
    public const int MaxLogLines = 500;

    readonly IConversionRunner _runner;
    readonly IRollbackRunner _rollback;
    readonly IInstallerIdentifier _identifier;
    readonly IFilePicker _files;
    readonly IDeviceInfoProvider _device;
    readonly ISystemActions _system;
    readonly Action<Action> _post;
    CancellationTokenSource? _cts;
    TaskCompletionSource<bool>? _promptTcs;
    int _lenovoSeq, _amdSeq;
    Task _lenovoValidation = Task.CompletedTask, _amdValidation = Task.CompletedTask;

    public MainViewModel(IConversionRunner runner, IRollbackRunner rollback, IInstallerIdentifier identifier,
        IFilePicker files, IDeviceInfoProvider device, ISystemActions system, Action<Action> post)
    {
        _runner = runner; _rollback = rollback; _identifier = identifier; _files = files; _device = device; _system = system;
        _post = post ?? throw new ArgumentNullException(nameof(post));
        Stages = new(Enum.GetValues<Stage>().Where(s => s != Stage.Done).Select(s => new StageItem(s, s.ToString())));
        RefreshSessions();
    }

    public string WorkRoot { get; init; } = ConversionPipeline.DefaultWorkRoot;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(StartCommand), nameof(CancelCommand), nameof(RollbackCommand), nameof(RebootCommand))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    WizardStep _step = WizardStep.Select;

    /// <summary>True once the run reported a stage after the confirmation (restore point onwards): from then on the run
    /// cannot be cancelled, B/Esc do nothing and the view shows "Installing: cannot be cancelled" instead of Cancel.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelCommand)), NotifyPropertyChangedFor(nameof(CanCancel))]
    bool _installStarted;

    /// <summary>Whether Cancel is available now (running, and the confirmed changes have not started).</summary>
    public bool CanCancel => Step == WizardStep.Progress && _cts is not null && !InstallStarted;

    static bool IsAfterConfirmation(Stage s) =>
        s is Stage.RestorePoint or Stage.StageLenovo or Stage.TrustCertificate or Stage.InstallDriver or Stage.VerifyDriver or Stage.InstallAmdSoftware;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(StartCommand), nameof(RollbackCommand), nameof(RebootCommand))]
    bool _isBusy;

    // ---- inputs
    [ObservableProperty] string _lenovoInstallerPath = "";
    [ObservableProperty] string _amdInstallerPath = "";
    public InstallerStatus LenovoInfo { get; } = new();
    public InstallerStatus AmdInfo { get; } = new();

    [ObservableProperty] bool _installDriver = true;
    [ObservableProperty] bool _installAmdSoftware;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsExportOnly))] string? _exportFolder;

    /// <summary>True when an export folder is chosen: the run then only exports and installs nothing (same rule as Start).</summary>
    public bool IsExportOnly => !string.IsNullOrWhiteSpace(ExportFolder);

    // ---- review
    [ObservableProperty] string? _deviceId;
    [ObservableProperty] SecurityState? _security;
    [ObservableProperty] string? _deviceError;

    /// <summary>Shown by the view when the app started with a problem (e.g. tool files missing); null when all is well.</summary>
    [ObservableProperty] string? _startupWarning;

    // ---- progress
    public ObservableCollection<StageItem> Stages { get; }
    public ObservableCollection<string> Log { get; } = new();
    [ObservableProperty] double? _fraction;
    /// <summary>The latest progress message (the "current stage" line on the progress screen); null before the first report.</summary>
    [ObservableProperty] string? _stageMessage;
    [ObservableProperty] PromptRequest? _pendingPrompt;

    // ---- result
    [ObservableProperty] bool _success;
    [ObservableProperty] string? _error;
    [ObservableProperty] string? _notice;
    [ObservableProperty] bool _verificationPending;
    [ObservableProperty] bool _rebootRequired;
    [ObservableProperty] string? _sessionDir;
    [ObservableProperty] bool _hasPreviousSession;

    public string ResultHeadline =>
        !Success ? "The conversion did not finish"
        : VerificationPending ? "Installed, but not verified yet: restart and check that the GPU works"
        : RebootRequired ? "Done: restart Windows to finish"
        : "Done";

    partial void OnSuccessChanged(bool value) => OnPropertyChanged(nameof(ResultHeadline));
    partial void OnVerificationPendingChanged(bool value) => OnPropertyChanged(nameof(ResultHeadline));
    partial void OnRebootRequiredChanged(bool value) { OnPropertyChanged(nameof(ResultHeadline)); RebootCommand.NotifyCanExecuteChanged(); }
    partial void OnHasPreviousSessionChanged(bool value) => RollbackCommand.NotifyCanExecuteChanged();

    partial void OnInstallAmdSoftwareChanged(bool value) { if (value && !InstallDriver) InstallDriver = true; }
    partial void OnInstallDriverChanged(bool value) { if (!value && InstallAmdSoftware) InstallAmdSoftware = false; }

    partial void OnLenovoInstallerPathChanged(string value) => _lenovoValidation = Validate(LenovoInfo, value, InstallerType.Lenovo, ref _lenovoSeq);
    partial void OnAmdInstallerPathChanged(string value) => _amdValidation = Validate(AmdInfo, value, InstallerType.Amd, ref _amdSeq);

    /// <summary>Completes when the latest installer checks have finished (for tests and for views that want to wait).</summary>
    public Task PendingValidation => Task.WhenAll(_lenovoValidation, _amdValidation);

    Task Validate(InstallerStatus slot, string path, InstallerType expected, ref int seq)
    {
        int mine = ++seq;
        NextCommand.NotifyCanExecuteChanged();
        if (string.IsNullOrWhiteSpace(path)) { slot.Set(false, false, "", null); return Task.CompletedTask; }
        slot.Set(false, true, "Checking...", null);
        return RunValidation(slot, path, expected, mine, expected == InstallerType.Lenovo ? () => _lenovoSeq : () => _amdSeq);
    }

    async Task RunValidation(InstallerStatus slot, string path, InstallerType expected, int mine, Func<int> current)
    {
        InstallerInfo info;
        try { info = await _identifier.IdentifyAsync(path, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { info = new(InstallerType.Unknown, "Could not inspect this file: " + e.Message); }
        _post(() =>
        {
            if (mine != current()) return; // the path changed meanwhile
            string label = expected == InstallerType.Lenovo ? "Lenovo" : "AMD";
            if (info.Type == expected) slot.Set(true, false, info.Message, info.Version);
            else if (info.Type == InstallerType.Unknown) slot.Set(false, false, $"This is not the {label} driver installer. {info.Message}", null);
            else
            {
                string other = info.Type == InstallerType.Lenovo ? "Lenovo" : "AMD";
                slot.Set(false, false, $"This is the {other} driver installer, not the {label} one. The two files look swapped: put it in the {other} box.", null);
            }
            NextCommand.NotifyCanExecuteChanged();
        });
    }

    // ---- commands
    [RelayCommand]
    void BrowseLenovo()
    {
        var p = SafePick(() => _files.PickFile("Choose the Lenovo driver installer", "Installer (*.exe)|*.exe|All files (*.*)|*.*"));
        if (p is not null) LenovoInstallerPath = p;
    }

    [RelayCommand]
    void BrowseAmd()
    {
        var p = SafePick(() => _files.PickFile("Choose the AMD driver installer", "Installer (*.exe)|*.exe|All files (*.*)|*.*"));
        if (p is not null) AmdInstallerPath = p;
    }

    [RelayCommand]
    void BrowseExport()
    {
        var p = SafePick(() => _files.PickFolder("Choose the export folder"));
        if (p is not null) ExportFolder = p;
    }

    /// <summary>Drops the export folder so the run installs again.</summary>
    [RelayCommand]
    void ClearExport() => ExportFolder = null;

    string? SafePick(Func<string?> pick)
    {
        try { return pick(); } catch (Exception e) { Notice = "Could not open the file dialog: " + e.Message; return null; }
    }

    bool CanNext() => Step == WizardStep.Select && !IsBusy && LenovoInfo.IsValid && AmdInfo.IsValid;

    [RelayCommand(CanExecute = nameof(CanNext))]
    void Next()
    {
        Notice = null;
        try
        {
            var d = _device.Get();
            DeviceId = d.HardwareId; Security = d.Security; DeviceError = d.Error;
        }
        catch (Exception e) { DeviceId = null; Security = null; DeviceError = e.Message; }
        Step = WizardStep.Review;
    }

    bool CanBack() => Step is WizardStep.Review or WizardStep.Result;

    [RelayCommand(CanExecute = nameof(CanBack))]
    void Back()
    {
        Notice = null; Error = null;
        Step = WizardStep.Select;
        RefreshSessions();
    }

    bool CanStart() => Step == WizardStep.Review && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task StartAsync()
    {
        bool exportOnly = IsExportOnly;
        var request = new ConversionRequest(AmdInstallerPath, LenovoInstallerPath, WorkRoot,
            InstallDriver: !exportOnly && InstallDriver,
            InstallAmdSoftware: !exportOnly && InstallDriver && InstallAmdSoftware,
            ExportFolder: exportOnly ? ExportFolder : null);

        Notice = null; Error = null; Success = false; VerificationPending = false; RebootRequired = false; Fraction = null; StageMessage = null;
        InstallStarted = false;
        Log.Clear();
        foreach (var s in Stages) s.State = StageState.Pending;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        Step = WizardStep.Progress;
        try
        {
            var result = await Task.Run(() => _runner.RunAsync(request, new InlineProgress(OnProgress), this, ct)).ConfigureAwait(false);
            _post(() => ShowResult(result));
        }
        catch (OperationCanceledException)
        {
            _post(() =>
            {
                FinishStages(failed: false);
                Notice = "Cancelled. You can review the options and start again.";
                Step = WizardStep.Review;
            });
        }
        catch (Exception e)
        {
            // No session folder is known for this run: never show the previous session's (Open log stays hidden).
            _post(() => ShowResult(new ConversionResult(false, false, "", e.Message)));
        }
        finally
        {
            _post(() =>
            {
                DenyPendingPrompt();
                IsBusy = false;
                _cts?.Dispose(); _cts = null;
                RefreshSessions();
                CancelCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanCancel));
            });
        }
    }

    void ShowResult(ConversionResult r)
    {
        FinishStages(failed: !r.Success);
        Success = r.Success; Error = r.Error; Notice = r.Notice; VerificationPending = r.VerificationPending;
        RebootRequired = r.RebootRequired; SessionDir = string.IsNullOrWhiteSpace(r.SessionDir) ? null : r.SessionDir;
        Step = WizardStep.Result;
    }

    void FinishStages(bool failed)
    {
        foreach (var s in Stages.Where(s => s.State == StageState.Running)) s.State = failed ? StageState.Failed : StageState.Done;
    }

    void OnProgress(StageProgress p) => _post(() =>
    {
        foreach (var s in Stages)
        {
            if (s.Stage == p.Stage) { if (s.State != StageState.Running) s.State = StageState.Running; }
            else if (s.State == StageState.Running) s.State = StageState.Done;
        }
        if (p.Stage == Stage.Done) FinishStages(false);
        if (IsAfterConfirmation(p.Stage)) InstallStarted = true;
        Fraction = p.Fraction;
        StageMessage = p.Message;
        Log.Add($"[{p.Stage}] {p.Message}");
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    });

    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        DenyPendingPrompt();
    }

    // ---- rollback / reboot / log
    public const string RollbackQuestion =
        "This removes the converted AMD driver and the AMD Software that the last conversion installed, and puts the Legion Go back on " +
        "Lenovo's driver. Lenovo's Microsoft Store app \"AMD Radeon Software\" is not restored: reinstall it from the Microsoft Store " +
        "afterwards if you want it. Windows may need a restart afterwards. Continue?";

    bool CanRollback() => HasPreviousSession && !IsBusy && Step is WizardStep.Select or WizardStep.Result;

    [RelayCommand(CanExecute = nameof(CanRollback))]
    async Task RollbackAsync()
    {
        if (!await AskAsync("Roll back to Lenovo's driver?", RollbackQuestion, CancellationToken.None)) return;
        IsBusy = true;
        try
        {
            var r = await Task.Run(() => _rollback.RunAsync(CancellationToken.None)).ConfigureAwait(false);
            _post(() =>
            {
                var note = $"Rolled back: {r.Removed.Count} driver package(s) removed.";
                if (r.CertificateKept) note += " The signing certificate was kept because another converted driver still uses it.";
                if (r.GpuState is not null) note += " " + r.GpuState;
                if (r.SoftwareNote is not null) note += " " + r.SoftwareNote;
                Success = true; Error = null; Notice = note; VerificationPending = false;
                // Not back on Lenovo's driver yet: a restart is the next step (the notice gives the manual fallback).
                RebootRequired = r.RebootRequired || r.GpuOnLenovo == false; SessionDir = _rollback.LatestSessionDir;
                Step = WizardStep.Result;
            });
        }
        catch (Exception e)
        {
            _post(() =>
            {
                Success = false; Error = e.Message; Notice = null; VerificationPending = false;
                RebootRequired = false; SessionDir = _rollback.LatestSessionDir;
                Step = WizardStep.Result;
            });
        }
        finally { _post(() => { IsBusy = false; RefreshSessions(); }); }
    }

    bool CanReboot() => Step == WizardStep.Result && RebootRequired && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReboot))]
    async Task RebootAsync()
    {
        if (!await AskAsync("Restart Windows now?", "Save your work first. Windows will restart.", CancellationToken.None)) return;
        try { _system.RestartWindows(); }
        catch (Exception e) { var m = e.Message; _post(() => Notice = "Could not restart Windows: " + m); }
    }

    [RelayCommand]
    void OpenLog()
    {
        if (string.IsNullOrEmpty(SessionDir)) return;
        try { _system.OpenFolder(SessionDir); }
        catch (Exception e) { Notice = "Could not open the log folder: " + e.Message; }
    }

    public void RefreshSessions()
    {
        try { HasPreviousSession = _rollback.HasPreviousSession; } catch { HasPreviousSession = false; }
    }

    // ---- prompts
    // The prompt slot (_promptTcs) is claimed under _promptLock, so "one at a time" is atomic. Everything that touches
    // PendingPrompt is posted to the UI, in order, and only acts if the slot still says so; a Cancel that lands between
    // the claim and the posted set therefore can never resurrect a dialog.
    readonly object _promptLock = new();

    Task<bool> AskAsync(string title, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_promptLock)
        {
            if (_promptTcs is not null) return Task.FromResult(false); // one question at a time; extra ones are declined
            _promptTcs = tcs;
        }
        CancellationTokenRegistration reg = default;
        void Answer(bool v)
        {
            reg.Dispose();
            lock (_promptLock) { if (ReferenceEquals(_promptTcs, tcs)) _promptTcs = null; }
            PostClearPrompt();
            tcs.TrySetResult(v);
        }
        var request = new PromptRequest(title, message, Answer);
        _post(() => { lock (_promptLock) { if (ReferenceEquals(_promptTcs, tcs)) PendingPrompt = request; } });
        if (ct.CanBeCanceled) reg = ct.Register(() => Answer(false));
        return tcs.Task;
    }

    Task<bool> IUserPrompt.ConfirmAsync(string title, string message, CancellationToken ct) => AskAsync(title, message, ct);

    void DenyPendingPrompt()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_promptLock) { tcs = _promptTcs; _promptTcs = null; }
        PostClearPrompt();
        tcs?.TrySetResult(false);
    }

    /// <summary>Clears the dialog on the UI, unless a newer prompt has claimed the slot in the meantime.</summary>
    void PostClearPrompt() => _post(() => { lock (_promptLock) { if (_promptTcs is null) PendingPrompt = null; } });

    sealed class InlineProgress(Action<StageProgress> h) : IProgress<StageProgress>
    {
        public void Report(StageProgress value) => h(value);
    }
}

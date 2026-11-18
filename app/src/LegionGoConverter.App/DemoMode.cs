#if DEBUG
using System.Globalization;
using System.Windows;
using LegionGoConverter.Core;

namespace LegionGoConverter.App;

/// <summary>DEBUG ONLY (compiled out of Release). Puts the real view model into one screen state with fake services so each
/// screen can be looked at and screenshotted: <c>--demo=select|review|review-export|progress|result-ok|result-pending|result-fail|prompt|prompt-long</c>,
/// optional <c>--size=WxH</c> (window size in device-independent pixels) and <c>--theme=light|dark</c>.
/// Nothing here touches the system: no pipeline, no rollback, no restart, no file dialogs, no device queries.</summary>
internal sealed class DemoMode
{
    static readonly string[] States = ["select", "review", "review-export", "progress", "result-ok", "result-pending", "result-fail", "prompt", "prompt-long"];

    public required string State { get; init; }
    public (double W, double H)? Size { get; init; }
    public bool? Dark { get; init; }

    public static DemoMode? Parse(string[] args)
    {
        string? state = null, bindLog = null, snapshot = null; (double, double)? size = null; bool? dark = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase)) state = a[7..].ToLowerInvariant();
            else if (a.StartsWith("--size=", StringComparison.OrdinalIgnoreCase))
            {
                var p = a[7..].Split('x', 'X');
                if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                                  && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) size = (w, h);
            }
            else if (a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase)) dark = a[8..].Equals("dark", StringComparison.OrdinalIgnoreCase);
            else if (a.StartsWith("--bindlog=", StringComparison.OrdinalIgnoreCase)) bindLog = a[10..];
            else if (a.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase)) snapshot = a[11..];
        }
        if (state is null) return null;
        if (!States.Contains(state)) throw new ArgumentException($"Unknown demo state '{state}'. Use one of: {string.Join(", ", States)}.");
        return new DemoMode { State = state, Size = size, Dark = dark, BindingLog = bindLog, Snapshot = snapshot };
    }

    /// <summary>Optional <c>--bindlog=path</c>: WPF binding warnings and errors are appended to this file.</summary>
    public string? BindingLog { get; init; }

    /// <summary>Optional <c>--snapshot=file.png</c>: after the state settles, the window content is rendered off-screen at
    /// <see cref="Size"/> (so sizes larger than this screen work), saved as PNG at 1:1 device-independent pixels, and the app exits.</summary>
    public string? Snapshot { get; init; }

    /// <summary>Builds the window over a fake-backed view model, shows it and drives it into the requested state.</summary>
    public GamepadNavigator Run(Application app)
    {
        if (BindingLog is { } logPath)
        {
            System.Diagnostics.PresentationTraceSources.Refresh(); // enables WPF tracing without a debugger attached
            var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
            source.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            source.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(logPath) { TraceOutputOptions = System.Diagnostics.TraceOptions.None });
            System.Diagnostics.Trace.AutoFlush = true;
        }
        if (Dark is { } d) ThemeManager.Force(d); else ThemeManager.FollowSystem();
        var runner = new DemoRunner(State);
        var vm = new MainViewModel(runner, new DemoRollback(), new DemoIdentifier(), new DemoFiles(), new DemoDevice(), new DemoSystem(),
            a => app.Dispatcher.BeginInvoke(a));
        if (State == "select")
            vm.StartupWarning = "The tools folder is incomplete (makecat.exe is missing). Reinstall the app so its tools folder is complete. Conversion is unavailable until then; rolling back an earlier conversion still works.";

        var window = new MainWindow { DataContext = vm, Title = $"Legion Go Driver Port (demo: {State})" };
        if (Size is { } s)
        {
            window.WindowState = WindowState.Normal;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0; window.Top = 0; window.Width = s.W; window.Height = s.H;
        }
        app.MainWindow = window;
        window.Show();
        _ = DriveAsync(vm);
        if (Snapshot is { } shot) _ = SnapshotAsync(app, window, shot);

        var pad = new GamepadNavigator(app.Dispatcher, () => AppComposition.Back(vm));
        pad.Start();
        return pad;
    }

    async Task SnapshotAsync(Application app, MainWindow window, string path)
    {
        await Task.Delay(2500);
        var root = (FrameworkElement)window.Content;
        var (w, h) = Size ?? (root.ActualWidth, root.ActualHeight);
        // Detached from the window, so carry over what the window passed down by inheritance.
        var bg = new System.Windows.Controls.Border { Width = w, Height = h, Background = window.Background, DataContext = window.DataContext };
        bg.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
        bg.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
        bg.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
        window.Content = null;
        bg.Child = root;
        bg.Measure(new System.Windows.Size(w, h));
        bg.Arrange(new Rect(0, 0, w, h));
        bg.UpdateLayout();
        await Task.Delay(300);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(bg);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        app.Shutdown();
    }

    async Task DriveAsync(MainViewModel vm)
    {
        vm.LenovoInstallerPath = @"C:\Users\Player\Downloads\lnvgy_dr_amd_graphics_25.30.44_legion_go.exe";
        vm.AmdInstallerPath = State == "select"
            ? @"C:\Users\Player\Downloads\swapped_lenovo_package.exe"
            : @"C:\Users\Player\Downloads\whql-amd-software-adrenalin-edition-26.10.2-win11-c.exe";
        for (int i = 0; i < 100 && (vm.LenovoInfo.IsChecking || vm.AmdInfo.IsChecking); i++) await Task.Delay(20);
        if (State == "select") return;

        vm.InstallAmdSoftware = true;
        vm.NextCommand.Execute(null);
        if (State == "review") return;
        if (State == "review-export") { vm.BrowseExportCommand.Execute(null); return; }

        await vm.StartCommand.ExecuteAsync(null); // the fake runner decides how far it gets
    }

    // ---- fakes (no system access)

    sealed class DemoRunner(string state) : IConversionRunner
    {
        static readonly (Stage Stage, string Message, double? Fraction)[] Script =
        [
            (Stage.Preflight, "Checking this PC: Secure Boot on, Memory Integrity on, 38 GB free", 0.04),
            (Stage.ExtractLenovo, "Unpacking the Lenovo package (412 files)", 0.12),
            (Stage.ExtractAmd, "Unpacking the AMD package (1,906 files)", 0.24),
            (Stage.Analyze, "Found Legion Go IDs PCI\\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04 and 2 amdgcf records", 0.33),
            (Stage.Build, "Writing u0205499.inf and amdgcf.dat; building the catalog", 0.42),
        ];

        const string LongConsent =
            "1. A System Restore point is created first, so Windows can be put back as it was today.\n\n" +
            "2. The app adds its own signing certificate (\"Legion Go Driver Port\") to this PC's Trusted Root and Trusted Publisher " +
            "stores. Windows will then accept drivers signed with it. The certificate stays until you roll back.\n\n" +
            "3. Lenovo's graphics driver 31.0.24033.1003 is replaced by the converted AMD driver 32.0.32015.12012. Lenovo's driver stays " +
            "in the driver store so you can roll back.\n\n" +
            "4. Lenovo's AMD Software app from the Microsoft Store is removed, then AMD Software 26.10.2 is installed.\n\n" +
            "5. The screen may go black for a few seconds during the install. Keep the Legion Go plugged in and do not close the app.\n\n" +
            "6. Secure Boot and Memory Integrity stay on. Windows may ask for a restart at the end.\n\n" +
            "You can roll back to Lenovo's driver from this app at any time. Continue?";

        public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<StageProgress> progress, IUserPrompt prompt, CancellationToken ct)
        {
            const string session = @"C:\ProgramData\LegionGoConverter\sessions\2026-11-17_101500";
            foreach (var (stage, message, fraction) in Script)
            {
                progress.Report(new StageProgress(stage, message, fraction));
                for (int i = 0; i < 3; i++) progress.Report(new StageProgress(stage, $"{message}: detail line {i + 1}", fraction));
            }
            switch (state)
            {
                case "progress":
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
                case "prompt":
                    progress.Report(new StageProgress(Stage.Confirm, "Waiting for your confirmation", null));
                    await prompt.ConfirmAsync("Install the converted driver now?",
                        "The app will create a restore point, trust its own signing certificate on this PC and replace Lenovo's graphics driver with AMD 26.10.2. " +
                        "The screen may go black for a few seconds during the install. Secure Boot and Memory Integrity stay on.\n\n" +
                        "You can roll back to Lenovo's driver from this app at any time.", ct);
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
                case "prompt-long":
                    progress.Report(new StageProgress(Stage.Confirm, "Waiting for your confirmation", null));
                    await prompt.ConfirmAsync("Install the converted driver now?", LongConsent, ct);
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
                case "result-fail":
                    progress.Report(new StageProgress(Stage.Sign, "Signing the catalog", 0.5));
                    return new ConversionResult(false, false, session,
                        "Signing failed: signtool.exe exited with code 1 (SignerSign() failed, 0x80092004). Nothing was installed; your Legion Go is unchanged.");
                case "result-pending":
                    return new ConversionResult(true, true, session, null, VerificationPending: true,
                        Notice: "AMD Software was installed. Lenovo's AMD Software app from the Microsoft Store was removed.");
                default: // result-ok
                    return new ConversionResult(true, true, session, null,
                        Notice: "AMD Software was installed. Lenovo's AMD Software app from the Microsoft Store was removed.");
            }
            throw new OperationCanceledException(ct);
        }
    }

    sealed class DemoRollback : IRollbackRunner
    {
        public bool HasPreviousSession => true;
        public string? LatestSessionDir => @"C:\ProgramData\LegionGoConverter\sessions\2026-11-10_201233";
        public Task<RollbackResult> RunAsync(CancellationToken ct) =>
            Task.FromResult(new RollbackResult(true, ["oem42.inf (demo: nothing was removed)"]));
    }

    sealed class DemoIdentifier : IInstallerIdentifier
    {
        public async Task<InstallerInfo> IdentifyAsync(string path, CancellationToken ct)
        {
            await Task.Delay(150, ct);
            return path.Contains("lnvgy", StringComparison.OrdinalIgnoreCase) || path.Contains("lenovo", StringComparison.OrdinalIgnoreCase)
                ? new InstallerInfo(InstallerType.Lenovo, "Lenovo Legion Go graphics package with the Legion Go hardware IDs.", "31.0.24033.1003")
                : new InstallerInfo(InstallerType.Amd, "AMD Software: Adrenalin Edition with the Radeon 780M display driver.", "32.0.32015.12012");
        }
    }

    sealed class DemoFiles : IFilePicker
    {
        public string? PickFile(string title, string filter) => null;
        public string? PickFolder(string title) => @"D:\Drivers\LegionGo-AMD-26.10.2";
    }

    sealed class DemoDevice : IDeviceInfoProvider
    {
        public DeviceInfo Get() => new(@"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04", new SecurityState(true, true, false));
    }

    sealed class DemoSystem : ISystemActions
    {
        public void RestartWindows() => MessageBox.Show("Demo mode: Windows would restart now. Nothing happened.");
        public void OpenFolder(string path) => MessageBox.Show($"Demo mode: would open {path}.");
    }
}
#endif

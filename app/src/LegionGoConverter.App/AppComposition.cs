using System.IO;
using System.Windows.Threading;
using LegionGoConverter.Core;

namespace LegionGoConverter.App;

/// <summary>Builds the real object graph. A missing tools folder does not crash: the view model shows a startup warning and
/// conversion reports the problem when started. Rollback, the device info and the file pickers are built from pieces that need no
/// bundled tools (system pnputil, trust store, WMI), so rollback genuinely keeps working.</summary>
public sealed class AppComposition
{
    public MainViewModel ViewModel { get; }
    public GamepadNavigator Gamepad { get; }

    public AppComposition(Dispatcher dispatcher)
        : this(dispatcher, () => ToolLocator.Resolve(), PipelineServices.DataRoot, () => DataRootSecurity.ForDefaultRoot().Ensure()) { }

    /// <summary>Test seam: <paramref name="resolveTools"/> replaces <see cref="ToolLocator.Resolve"/>; <paramref name="dataRoot"/>
    /// and <paramref name="secureDataRoot"/> replace the real data folder and its protection for rollback (tests use temp folders).</summary>
    public AppComposition(Dispatcher dispatcher, Func<ToolPaths> resolveTools, string dataRoot, Func<string>? secureDataRoot)
    {
        string? warning = null;
        IConversionRunner runner;
        IInstallerIdentifier identifier;
        var runnerProc = new ProcessRunner();
        var trust = new LocalMachineTrustStore();
        try
        {
            var services = PipelineServices.CreateDefault(resolveTools());
            runner = new ConversionRunner(new ConversionPipeline(services));
            identifier = new ContentInstallerIdentifier(services.Inspector);
        }
        catch (ToolMissingException e)
        {
            warning = e.Message + "\n\nConversion is unavailable until then; rolling back an earlier conversion still works.";
            runner = new UnavailableConversionRunner(warning);
            identifier = new UnavailableInstallerIdentifier("The tools folder is incomplete, so installers cannot be checked.");
        }

        // Finding 4: warn (do not refuse) when the app's folder or tools\ can be changed without administrator rights.
        string? location;
        try { location = InstallLocationCheck.Warning(AppContext.BaseDirectory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { location = null; }
        if (location is not null) warning = warning is null ? location : warning + "\n\n" + location;

        ViewModel = new MainViewModel(runner,
            new RollbackRunner(Path.Combine(dataRoot, "sessions"), log => new PnpUtilDriverInstaller(runnerProc, log), trust,
                log => new AmdSoftwareUninstaller(runnerProc, log), secureDataRoot, new WmiDeviceProbe()),
            identifier, new FilePicker(), new DeviceInfoProvider(new WmiDeviceProbe(), new WindowsEnvironmentProbe()),
            new SystemActions(), a => dispatcher.BeginInvoke(a))
        { StartupWarning = warning };

        Gamepad = new GamepadNavigator(dispatcher, () => Back(ViewModel));
    }

    /// <summary>B / Esc: deny a pending prompt first, otherwise Cancel while running, otherwise step back.</summary>
    public static void Back(MainViewModel vm)
    {
        if (vm.PendingPrompt is { } p) p.NoCommand.Execute(null);
        else if (vm.CancelCommand.CanExecute(null)) vm.CancelCommand.Execute(null);
        else if (vm.BackCommand.CanExecute(null)) vm.BackCommand.Execute(null);
    }
}

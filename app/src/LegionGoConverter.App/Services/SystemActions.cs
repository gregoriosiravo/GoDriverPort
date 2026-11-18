using System.Diagnostics;

namespace LegionGoConverter.App;

/// <summary>Restarts Windows and opens folders. Launch failures are reported as exceptions with a clear message
/// (the view model turns them into a notice); the argument lists are pure and unit tested.</summary>
public sealed class SystemActions : ISystemActions
{
    public const string RestartComment = "Legion Go Driver Port: restarting Windows to finish the driver change.";

    /// <summary>shutdown.exe arguments: restart after 15 seconds (time to abort with <c>shutdown /a</c>).</summary>
    public static IReadOnlyList<string> RestartArguments() => new[] { "/r", "/t", "15", "/c", RestartComment };

    public static IReadOnlyList<string> OpenFolderArguments(string folder) => new[] { folder };

    /// <summary>Full paths: a bare name would let CreateProcess find a planted copy in the app folder first.</summary>
    public static string ShutdownExe => System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe");
    public static string ExplorerExe => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public void RestartWindows() => Launch(ShutdownExe, RestartArguments(), "restart Windows");

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("There is no folder to open.");
        Launch(ExplorerExe, OpenFolderArguments(path), "open the folder");
    }

    static void Launch(string exe, IReadOnlyList<string> args, string what)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) throw new InvalidOperationException(exe + " did not start.");
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not {what}: {e.Message}", e);
        }
    }
}

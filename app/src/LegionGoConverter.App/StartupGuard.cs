using System.IO;
namespace LegionGoConverter.App;

/// <summary>Runs startup composition so an unexpected failure becomes a message box, a best-effort log file and a clean exit
/// instead of a crash dialog. The seams (show, log) keep it testable without WPF.</summary>
public static class StartupGuard
{
    public static string LogPath { get; } =
        Path.Combine(Core.PipelineServices.DataRoot, "startup-error.log");

    /// <summary>Returns 0 when <paramref name="compose"/> succeeded, otherwise 1 after reporting.</summary>
    public static int Run(Action compose, Action<string> showMessage, Action<string, Exception> writeLog)
    {
        try { compose(); return 0; }
        catch (Exception e)
        {
            try { writeLog(LogPath, e); } catch { /* best effort */ }
            try { showMessage($"The Legion Go Driver Port could not start.\n\n{e.Message}\n\nDetails were written to {LogPath} (if that folder is writable)."); }
            catch { /* best effort */ }
            return 1;
        }
    }

    /// <summary>Appends to the startup log after the data folder was secured (<see cref="Core.DataRootSecurity"/>): the elevated
    /// app never writes into a folder that standard users can change.</summary>
    public static void WriteLogFile(string path, Exception e) =>
        WriteLogFile(path, e, () => Core.DataRootSecurity.ForRoot(Path.GetDirectoryName(path)!).Ensure());

    /// <summary>Test seam: <paramref name="secureFolder"/> secures the log's folder or throws (then nothing is written).</summary>
    public static void WriteLogFile(string path, Exception e, Func<string> secureFolder)
    {
        secureFolder();
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"{path} is a link; the startup log is not written through it.");
        File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{e}{Environment.NewLine}{Environment.NewLine}");
    }
}

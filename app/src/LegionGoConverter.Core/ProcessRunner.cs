using System.Diagnostics;
using System.Text;

namespace LegionGoConverter.Core;

public sealed record ProcessResult(int ExitCode, string Output);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct);
}

/// <summary>Runs a real process with an argument list (never a joined string); kills the process tree on cancel.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public const int MaxTailLines = 200;

    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var tail = new Queue<string>();
        var gate = new object();
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (gate) { tail.Enqueue(e.Data); while (tail.Count > MaxTailLines) tail.Dequeue(); }
            lines?.Report(e.Data);
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;
        p.Start();
        try
        {
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var reg = ct.Register(() => KillQuietly(p));
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            p.WaitForExit(); // flush async output handlers
            lock (gate) return new ProcessResult(p.ExitCode, string.Join(Environment.NewLine, tail));
        }
        catch
        {
            // Anything after a successful Start (including cancellation) must not leave the child running.
            KillQuietly(p);
            try { p.WaitForExit(5000); } catch { }
            throw;
        }
    }

    static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
}

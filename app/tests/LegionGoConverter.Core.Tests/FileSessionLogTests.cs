using System.Globalization;
using LegionGoConverter.Core;

namespace LegionGoConverter.Core.Tests;

public class FileSessionLogTests
{
    [Fact]
    public void FileSessionLog_WritesTimestampedLinesAndFlushes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new FileSessionLog(dir);
            log.Info("first line");
            log.Warn("second line");

            string text;
            using (var fs = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, lines.Length);
            Assert.Contains("first line", lines[0]);
            Assert.Contains("second line", lines[1]);
            foreach (var line in lines)
            {
                var stamp = line.Split(' ')[0];
                Assert.True(DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), $"Bad timestamp: {line}");
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", line);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void FileSessionLog_WriteAfterDispose_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileSessionLog(dir);
            log.Info("before");
            log.Dispose();
            var ex = Record.Exception(() =>
            {
                log.Info("after");
                log.Error("after", new InvalidOperationException("x"));
                log.Dispose();
            });
            Assert.Null(ex);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

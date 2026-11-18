using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LegionGoConverter.Core;

public static class CdfWriter
{
    const string OsAttr = "0x10010001:OSAttr:2:10.0";

    /// <summary>The system ANSI code page; makecat reads the CDF in it. Throws on unrepresentable characters.</summary>
    public static Encoding AnsiEncoding { get; } = CreateAnsi();

    static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>Encodes CDF text for makecat (system ANSI code page).</summary>
    public static byte[] Encode(string cdf) => AnsiEncoding.GetBytes(cdf);

    /// <summary>Builds the makecat CDF text (CRLF). Member tags are the file names, so they must be unique and
    /// contain no '=', whitespace or newline; all paths must be representable in the ANSI code page.</summary>
    public static string Build(string catalogName, string resultDir, IEnumerable<string> memberFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultDir);
        ArgumentNullException.ThrowIfNull(memberFiles);
        var members = memberFiles.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            var name = Path.GetFileName(m);
            if (name.Length == 0 || name.Any(c => c == '=' || char.IsWhiteSpace(c)))
                throw new ArgumentException($"Catalog member file name '{name}' ({m}) must not contain '=', whitespace or newlines.", nameof(memberFiles));
            if (!seen.Add(name)) throw new ArgumentException($"Duplicate catalog member name: {name}", nameof(memberFiles));
            CheckEncodable(m);
        }
        CheckEncodable(catalogName);
        CheckEncodable(resultDir);

        var sb = new StringBuilder();
        sb.Append("[CatalogHeader]\r\n");
        sb.Append($"Name={catalogName}\r\n");
        sb.Append($"ResultDir={resultDir}\r\n");
        sb.Append("PublicVersion=0x0000001\r\n");
        sb.Append("CatalogVersion=2\r\n");
        sb.Append("HashAlgorithms=SHA256\r\n");
        sb.Append("EncodingType=0x00010001\r\n");
        sb.Append($"CATATTR1={OsAttr}\r\n");
        sb.Append("[CatalogFiles]\r\n");
        foreach (var m in members)
        {
            var name = Path.GetFileName(m);
            sb.Append($"<hash>{name}={m}\r\n");
            sb.Append($"<hash>{name}ATTR1={OsAttr}\r\n");
            sb.Append($"<hash>{name}ATTR2=0x10010001:File:{name}\r\n");
        }
        return sb.ToString();
    }

    static void CheckEncodable(string path)
    {
        try { AnsiEncoding.GetBytes(path); }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                $"The path '{path}' contains characters the system ANSI code page cannot represent, so makecat cannot read it. Use a different work folder.");
        }
    }
}

public sealed class CatalogBuilder(IProcessRunner runner, ToolPaths tools)
{
    /// <summary>Catalogs the INF plus every file directly in <paramref name="binariesDir"/>, writes the .cat into
    /// <paramref name="displayDir"/> and signs it with <paramref name="cert"/> (CurrentUser\My). On any failure or
    /// cancellation no .cat is left behind. No timestamp is applied to the signature (acceptable for a local
    /// self-signed certificate).</summary>
    public async Task BuildAndSignAsync(string displayDir, string infName, string binariesDir, string catName,
        X509Certificate2 cert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cert);
        var inf = Path.Combine(displayDir, infName);
        if (!File.Exists(inf)) throw new FileNotFoundException("INF not found", inf);
        var members = new List<string> { inf };
        members.AddRange(Directory.GetFiles(binariesDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        var catPath = Path.Combine(displayDir, catName);
        if (File.Exists(catPath)) File.Delete(catPath);

        var cdfPath = Path.Combine(Path.GetTempPath(), "lgo-" + Guid.NewGuid().ToString("N") + ".cdf");
        try
        {
            File.WriteAllBytes(cdfPath, CdfWriter.Encode(CdfWriter.Build(catName, displayDir, members)));
            var mk = await runner.RunAsync(tools.MakeCat, new[] { "-v", cdfPath }, null, ct).ConfigureAwait(false);
            if (mk.ExitCode != 0) throw new InvalidOperationException($"makecat failed (exit {mk.ExitCode}): {mk.Output}");
            if (!File.Exists(catPath)) throw new InvalidOperationException($"makecat did not produce {catPath}");

            var sg = await runner.RunAsync(tools.SignTool,
                new[] { "sign", "/fd", "SHA256", "/sha1", cert.Thumbprint, "/s", "My", catPath }, null, ct).ConfigureAwait(false);
            if (sg.ExitCode != 0) throw new InvalidOperationException($"signtool failed (exit {sg.ExitCode}): {sg.Output}");
        }
        catch
        {
            TryDelete(catPath);
            throw;
        }
        finally { TryDelete(cdfPath); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

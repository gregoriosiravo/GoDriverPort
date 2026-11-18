namespace LegionGoConverter.Core.Tests;

public static class RealPackages
{
    public static string AmdRoot { get; } =
        Environment.GetEnvironmentVariable("LGO_AMD_ROOT") ?? "";

    public static string LenovoRoot { get; } =
        Environment.GetEnvironmentVariable("LGO_LENOVO_ROOT") ?? "";
}

public enum RealPackage { Amd, Lenovo }

/// <summary>A fact that is skipped when the real extracted package folder is absent.</summary>
public sealed class RealPackageFactAttribute : FactAttribute
{
    public RealPackageFactAttribute(RealPackage package = RealPackage.Amd)
    {
        var root = package == RealPackage.Amd ? RealPackages.AmdRoot : RealPackages.LenovoRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            Skip = $"Real package folder not configured or not found (set {(package == RealPackage.Amd ? "LGO_AMD_ROOT" : "LGO_LENOVO_ROOT")}): '{root}'";
    }
}

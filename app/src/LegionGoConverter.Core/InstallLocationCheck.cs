using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionGoConverter.Core;

/// <summary>Startup check (final review finding 4): the app runs as administrator and starts the programs in its
/// <c>tools\</c> folder, so its own folder and <c>tools\</c> must not be writable by standard users (otherwise unelevated
/// code could replace a file and have it run with administrator rights). Not a refusal: this is a personal tool, so the
/// result is a warning that says why and where to move the app.</summary>
public static class InstallLocationCheck
{
    public const string RecommendedFolder = @"C:\Program Files\LegionGoDriverPort";

    /// <summary>Null when the folder and its <c>tools\</c> subfolder can only be changed by administrators (or SYSTEM /
    /// TrustedInstaller; <paramref name="isAdmin"/> overrides that rule for tests); otherwise the warning text.</summary>
    public static string? Warning(string appDir, Func<SecurityIdentifier, bool>? isAdmin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDir);
        isAdmin ??= DataRootSecurity.IsAdminOwner;
        var problems = new List<string>();
        foreach (var dir in new[] { Path.GetFullPath(appDir), Path.Combine(Path.GetFullPath(appDir), "tools") })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var acl = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
                var writers = DataRootSecurity.NonAdminWriters(acl, isAdmin, includeInheritOnly: false);
                if (writers.Count > 0) problems.Add($"{dir} ({string.Join(", ", writers)} can change it)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add($"{dir} (its permissions could not be read: {e.Message})");
            }
        }
        if (problems.Count == 0) return null;
        return "This app runs as administrator, but it was started from a folder that programs without administrator rights can change: " +
               string.Join("; ", problems) + ". Something running without administrator rights could replace a file there and have it run " +
               $"with administrator rights. Close the app, copy the whole folder (with tools) to {RecommendedFolder} as administrator, " +
               "and start it from there.";
    }
}

using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionGoConverter.Core;

/// <summary>The data folder cannot be trusted (owned by a non-administrator, a link, or a file). Nothing was changed.</summary>
public sealed class DataRootSecurityException : Exception
{
    public DataRootSecurityException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Makes sure the app's data folder (<c>C:\ProgramData\LegionGoDriverConverter</c>) can only be changed by
/// administrators before the elevated app trusts anything in it. ProgramData lets every standard user create files and
/// folders, so the folder is created with a PROTECTED DACL: SYSTEM and Administrators full control, Users read and execute
/// only, inherited by everything below. An existing folder (or one of its known subfolders) that is a link, or is owned by
/// anyone but SYSTEM, Administrators or TrustedInstaller, is refused and left as it is (never "fixed": its content cannot
/// be trusted). An existing, admin-owned one whose DACL lets non-administrators write is reset to the protected DACL.
/// The root and the trust rules are injectable so tests run on temp folders.</summary>
public sealed class DataRootSecurity
{
    public static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    /// <summary>NT SERVICE\TrustedInstaller.</summary>
    public static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Subfolders the app reads from or runs things in; each is checked like the root when it exists.</summary>
    public static readonly IReadOnlyList<string> KnownSubfolders = new[] { "sessions", "work", "lenovo-original" };

    const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        (FileSystemRights)0x40000000 /* GENERIC_WRITE */ | (FileSystemRights)0x10000000 /* GENERIC_ALL */;

    readonly IReadOnlyList<SecurityIdentifier> _fullControl;
    readonly Func<SecurityIdentifier, bool> _isTrustedOwner;
    readonly SecurityIdentifier _ownerForNew;

    public string Root { get; }

    /// <param name="root">The data folder.</param>
    /// <param name="fullControl">The principals given full control in the protected DACL (Users always get read and execute).</param>
    /// <param name="isTrustedOwner">Which owners are acceptable for an existing folder; also "admin" for the DACL check.</param>
    /// <param name="ownerForNew">The owner set on a folder this class creates.</param>
    public DataRootSecurity(string root, IReadOnlyList<SecurityIdentifier> fullControl, Func<SecurityIdentifier, bool> isTrustedOwner,
        SecurityIdentifier ownerForNew)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _fullControl = fullControl ?? throw new ArgumentNullException(nameof(fullControl));
        _isTrustedOwner = isTrustedOwner ?? throw new ArgumentNullException(nameof(isTrustedOwner));
        _ownerForNew = ownerForNew ?? throw new ArgumentNullException(nameof(ownerForNew));
    }

    /// <summary>Production rules for any folder: SYSTEM/Administrators full control, owner SYSTEM/Administrators/TrustedInstaller.</summary>
    public static DataRootSecurity ForRoot(string root) =>
        new(root, new[] { SystemSid, AdministratorsSid }, IsAdminOwner, AdministratorsSid);

    /// <summary>Production rules for <see cref="PipelineServices.DataRoot"/>.</summary>
    public static DataRootSecurity ForDefaultRoot() => ForRoot(PipelineServices.DataRoot);

    public static bool IsAdminOwner(SecurityIdentifier sid) =>
        sid == SystemSid || sid == AdministratorsSid || sid == TrustedInstallerSid;

    /// <summary>Creates or checks the folder (see the class summary). Returns one line saying what was done, for the log.
    /// Throws <see cref="DataRootSecurityException"/> when the folder cannot be trusted; nothing is changed then.</summary>
    public string Ensure()
    {
        try
        {
            var notes = new List<string>();
            var di = new DirectoryInfo(Root);
            if (File.Exists(Root))
                throw new DataRootSecurityException($"{Root} is a file, not the app's data folder. Rename or remove it as administrator, then start the app again.");
            if (!di.Exists)
            {
                var parent = Path.GetDirectoryName(Root);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                var sec = ProtectedAcl();
                sec.SetOwner(_ownerForNew);
                di.Create(sec);
                di.Refresh();
                notes.Add($"{Root} created with a protected ACL (administrators full control, users read only)");
            }
            // Check everything first (refusals change nothing), then repair.
            var targets = new List<DirectoryInfo> { di };
            targets.AddRange(KnownSubfolders.Select(s => new DirectoryInfo(Path.Combine(Root, s))).Where(d => d.Exists || File.Exists(d.FullName)));
            var weak = new List<(DirectoryInfo Dir, IReadOnlyList<string> Writers)>();
            foreach (var d in targets)
            {
                if (File.Exists(d.FullName))
                    throw Refuse(d.FullName, "is a file where the app expects a folder");
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw Refuse(d.FullName, "is a link (junction or symbolic link) to another place");
                var acl = d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner is null || !_isTrustedOwner(owner))
                    throw Refuse(d.FullName, $"is owned by {Name(owner)}, not by Administrators or SYSTEM");
                var writers = NonAdminWriters(acl, _isTrustedOwner, includeInheritOnly: true);
                bool isRoot = ReferenceEquals(d, di);
                if (writers.Count > 0 || (isRoot && !acl.AreAccessRulesProtected)) weak.Add((d, writers));
            }
            foreach (var (d, writers) in weak)
            {
                d.SetAccessControl(ProtectedAcl());
                var after = NonAdminWriters(d.GetAccessControl(AccessControlSections.Access), _isTrustedOwner, includeInheritOnly: true);
                if (after.Count > 0)
                    throw new DataRootSecurityException($"Could not make {d.FullName} writable by administrators only: {string.Join(", ", after)} can still write.");
                notes.Add($"{d.FullName}: ACL reset to the protected one" + (writers.Count > 0 ? $" (it let {string.Join(", ", writers)} write)" : " (it inherited its permissions)"));
            }
            return notes.Count == 0 ? $"{Root} is already protected (administrators only can change it)." : string.Join("; ", notes) + ".";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            throw new DataRootSecurityException($"Could not secure the data folder {Root}: {e.Message} Nothing else was changed.", e);
        }
    }

    DataRootSecurityException Refuse(string path, string why) => new(
        $"The data folder {path} {why}, so a program without administrator rights could have changed what is inside. The app does not use it. " +
        $"From an administrator prompt, rename the folder {Root} (for example to {Path.GetFileName(Root)}.untrusted) and start the app again: it creates a " +
        "fresh, protected folder. Look at the renamed folder's contents yourself before using anything in it.");

    DirectorySecurity ProtectedAcl()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in _fullControl.Distinct())
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, all,
            PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>Names of the principals that are not trusted (<paramref name="isAdmin"/>) and that an allow ACE grants any
    /// write, delete, permission or ownership right. <paramref name="includeInheritOnly"/> also counts ACEs that only apply
    /// to children (relevant for a folder whose children the app trusts).</summary>
    public static IReadOnlyList<string> NonAdminWriters(FileSystemSecurity acl, Func<SecurityIdentifier, bool> isAdmin, bool includeInheritOnly)
    {
        ArgumentNullException.ThrowIfNull(acl);
        var result = new List<string>();
        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (!includeInheritOnly && r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((r.FileSystemRights & WriteRights) == 0) continue;
            if (r.IdentityReference is not SecurityIdentifier sid || isAdmin(sid)) continue;
            var name = Name(sid);
            if (!result.Contains(name)) result.Add(name);
        }
        return result;
    }

    static string Name(SecurityIdentifier? sid)
    {
        if (sid is null) return "an unknown account";
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (SystemException) { return sid.Value; }
    }
}

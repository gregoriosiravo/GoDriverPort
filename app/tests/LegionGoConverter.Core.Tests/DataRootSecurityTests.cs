using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionGoConverter.Core.Tests;

/// <summary>Final review finding 3. Everything runs on temp folders owned by the (unelevated) test user, who plays the
/// role of "Administrators" through the injectable trusted-owner predicate and full-control list. The real
/// C:\ProgramData\LegionGoDriverConverter is never touched.</summary>
public class DataRootSecurityTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "lgc-dr-" + Guid.NewGuid().ToString("N"));
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public DataRootSecurityTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    string Root => Path.Combine(_tmp, "LegionGoDriverConverter");

    /// <summary>The test user stands in for Administrators.</summary>
    DataRootSecurity AsAdmin() => new(Root, new[] { DataRootSecurity.SystemSid, DataRootSecurity.AdministratorsSid, Me },
        sid => sid == Me || DataRootSecurity.IsAdminOwner(sid), Me);

    /// <summary>Production-like trust rules where neither the test user nor Administrators is a trusted owner (a CI runner's
    /// user is an elevated admin, so folders it creates are owned by Administrators and would pass the real predicate).</summary>
    DataRootSecurity Production() => new(Root, new[] { DataRootSecurity.SystemSid, DataRootSecurity.AdministratorsSid },
        sid => sid == DataRootSecurity.SystemSid, DataRootSecurity.AdministratorsSid);

    static DirectorySecurity Acl(string dir) => new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
    static string Sddl(string dir) => Acl(dir).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);

    [Fact]
    public void Missing_IsCreatedWithAProtectedAcl_AdminsFull_UsersReadOnly_InheritedByChildren()
    {
        var note = AsAdmin().Ensure();
        Assert.Contains("created", note);
        var acl = Acl(Root);
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(Me, acl.GetOwner(typeof(SecurityIdentifier)));
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Contains(rules, r => r.IdentityReference.Equals(DataRootSecurity.SystemSid) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, r => r.IdentityReference.Equals(DataRootSecurity.AdministratorsSid) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        var users = Assert.Single(rules, r => r.IdentityReference.Equals(Users));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.AppendData));
        Assert.Empty(DataRootSecurity.NonAdminWriters(acl, s => s == Me || DataRootSecurity.IsAdminOwner(s), includeInheritOnly: true));

        // children inherit: the Users ACE on a new session folder is read-only
        var child = Directory.CreateDirectory(Path.Combine(Root, "sessions", "x")).FullName;
        var childUsers = Acl(child).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.IdentityReference.Equals(Users)).ToList();
        Assert.NotEmpty(childUsers);
        Assert.All(childUsers, r => Assert.False(r.FileSystemRights.HasFlag(FileSystemRights.WriteData)));
    }

    [Fact]
    public void Existing_OwnedByANonAdmin_IsRefused_AndLeftUnchanged()
    {
        Directory.CreateDirectory(Root); // owned by the (non-admin) test user, as if pre-created by a standard user
        var before = Sddl(Root);
        var ex = Assert.Throws<DataRootSecurityException>(() => Production().Ensure());
        Assert.Contains(Root, ex.Message);
        Assert.Contains("owned by", ex.Message);
        Assert.Contains("rename", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Sddl(Root));
    }

    [Fact]
    public void Existing_TrustedOwner_WeakAcl_IsResetAndReported()
    {
        Directory.CreateDirectory(Root);
        var acl = Acl(Root);
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
        Assert.NotEmpty(DataRootSecurity.NonAdminWriters(Acl(Root), s => s == Me || DataRootSecurity.IsAdminOwner(s), includeInheritOnly: true));

        var note = AsAdmin().Ensure();
        Assert.Contains("reset", note);
        Assert.Contains("Users", note);
        Assert.True(Acl(Root).AreAccessRulesProtected);
        Assert.Empty(DataRootSecurity.NonAdminWriters(Acl(Root), s => s == Me || DataRootSecurity.IsAdminOwner(s), includeInheritOnly: true));
    }

    [Fact]
    public void AlreadyProtected_IsLeftAsIs()
    {
        AsAdmin().Ensure();
        var before = Sddl(Root);
        var note = AsAdmin().Ensure();
        Assert.Contains("already", note);
        Assert.Equal(before, Sddl(Root));
    }

    [Fact]
    public void KnownSubfolder_WithItsOwnWeakAcl_IsReset()
    {
        // e.g. "sessions" created while the root still inherited ProgramData's "Users may create" ACE, with explicit ACEs
        AsAdmin().Ensure();
        var sessions = Directory.CreateDirectory(Path.Combine(Root, "sessions")).FullName;
        var acl = Acl(sessions);
        acl.SetAccessRuleProtection(true, true);
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(sessions).SetAccessControl(acl);

        var note = AsAdmin().Ensure();
        Assert.Contains(sessions, note);
        Assert.Empty(DataRootSecurity.NonAdminWriters(Acl(sessions), s => s == Me || DataRootSecurity.IsAdminOwner(s), includeInheritOnly: true));
    }

    [Fact]
    public void KnownSubfolder_IsAJunction_IsRefused()
    {
        AsAdmin().Ensure();
        var target = Directory.CreateDirectory(Path.Combine(_tmp, "elsewhere2")).FullName;
        var link = Path.Combine(Root, "lenovo-original");
        Junction(link, target);
        var ex = Assert.Throws<DataRootSecurityException>(() => AsAdmin().Ensure());
        Assert.Contains(link, ex.Message);
        Directory.Delete(link);
    }

    static void Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create the junction for the test");
    }

    [Fact]
    public void RootIsAFile_IsRefused()
    {
        File.WriteAllText(Root, "x");
        Assert.Throws<DataRootSecurityException>(() => AsAdmin().Ensure());
    }

    [Fact]
    public void RootIsAJunction_IsRefused()
    {
        var target = Directory.CreateDirectory(Path.Combine(_tmp, "elsewhere")).FullName;
        Junction(Root, target);
        var ex = Assert.Throws<DataRootSecurityException>(() => AsAdmin().Ensure());
        Assert.Contains("link", ex.Message, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(Root); // removes the junction only
    }

    [Fact]
    public void ForDefaultRoot_PointsAtProgramData_WithoutTouchingIt()
    {
        var d = DataRootSecurity.ForDefaultRoot();
        Assert.Equal(PipelineServices.DataRoot, d.Root);
    }
}

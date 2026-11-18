using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LegionGoConverter.App;

namespace LegionGoConverter.Core.Tests;

/// <summary>Final review finding 4: the elevated app must not load or start code from places a standard user can write.</summary>
public class HardeningTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "lgc-hard-" + Guid.NewGuid().ToString("N"));
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public HardeningTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Theory]
    [InlineData(typeof(MsiInfo))]                // Core
    [InlineData(typeof(MainViewModel))]          // App
    public void EveryDllImport_SearchesSystem32Only(Type inAssembly)
    {
        var imports = inAssembly.Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() != null)
            .ToList();
        Assert.NotEmpty(imports);
        foreach (var m in imports)
        {
            var a = m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
            Assert.True(a != null, $"{m.DeclaringType}.{m.Name} has no [DefaultDllImportSearchPaths]");
            Assert.Equal(DllImportSearchPath.System32, a!.Paths);
        }
    }

    [Fact]
    public void SystemActions_StartsShutdownAndExplorerByFullPath()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), SystemActions.ShutdownExe);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), SystemActions.ExplorerExe);
        Assert.True(Path.IsPathFullyQualified(SystemActions.ShutdownExe));
        Assert.True(Path.IsPathFullyQualified(SystemActions.ExplorerExe));
    }

    [Fact]
    public void InstallLocation_UserWritableFolder_Warns_WithWhyAndWhere()
    {
        // a folder under the user's profile (like Documents\...\app\publish): the user can write it
        var app = Directory.CreateDirectory(Path.Combine(_tmp, "publish")).FullName;
        Directory.CreateDirectory(Path.Combine(app, "tools"));
        var w = InstallLocationCheck.Warning(app);
        Assert.NotNull(w);
        Assert.Contains(app, w);
        Assert.Contains(@"C:\Program Files\LegionGoDriverPort", w);
        Assert.Contains("administrator", w);
    }

    [Fact]
    public void InstallLocation_AdminOnlyFolderAndTools_NoWarning()
    {
        // the test user stands in for Administrators; Users may only read
        var app = Directory.CreateDirectory(Path.Combine(_tmp, "pf")).FullName;
        Directory.CreateDirectory(Path.Combine(app, "tools"));
        Protect(app);
        Assert.Null(InstallLocationCheck.Warning(app, sid => sid == Me || DataRootSecurity.IsAdminOwner(sid)));
    }

    [Fact]
    public void InstallLocation_ToolsWritableByUsers_Warns()
    {
        var app = Directory.CreateDirectory(Path.Combine(_tmp, "pf2")).FullName;
        var tools = Directory.CreateDirectory(Path.Combine(app, "tools")).FullName;
        Protect(app);
        var acl = new DirectoryInfo(tools).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(tools).SetAccessControl(acl);
        var w = InstallLocationCheck.Warning(app, sid => sid == Me || DataRootSecurity.IsAdminOwner(sid));
        Assert.NotNull(w);
        Assert.Contains("Users", w);
        Assert.Contains(tools, w);
    }

    static void Protect(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(sec);
    }
}

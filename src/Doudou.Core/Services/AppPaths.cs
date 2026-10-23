namespace Doudou.Core.Services;

/// <summary>程序目录约定：从可执行文件所在目录向上找到含 assets/palettes 的根目录。</summary>
public static class AppPaths
{
    private static string? _root;

    public static string Root => _root ??= FindRoot();

    public static string Palettes => Path.Combine(Root, "assets", "palettes");
    public static string Python => Path.Combine(Root, "runtime", "python", "python.exe");
    public static string Hy3dWorker => Path.Combine(Root, "python", "hy3d_worker.py");
    public static string Models => Path.Combine(Root, "models");
    public static string Fonts => Path.Combine(Root, "assets", "fonts");

    private static string? _userData;

    /// <summary>用户数据目录：优先放在程序目录旁（便携）；程序目录不可写（如装在 Program Files）时改用 %LOCALAPPDATA%\豆豆工坊。</summary>
    public static string UserData => _userData ??= ResolveUserData();

    private static string ResolveUserData()
    {
        var d = Path.Combine(Root, "userdata");
        try
        {
            Directory.CreateDirectory(d);
            var probe = Path.Combine(d, ".write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return d;
        }
        catch (Exception)
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "拼豆先锋");
            Directory.CreateDirectory(local);
            return local;
        }
    }

    public static string Settings => Path.Combine(UserData, "settings.json");

    public static string NewWorkDir(string prefix)
    {
        var d = Path.Combine(UserData, "work", $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        Directory.CreateDirectory(d);
        return d;
    }

    public static void OverrideRoot(string root) => _root = root;

    private static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("DOUDOU_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "assets"))) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "assets", "palettes"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}

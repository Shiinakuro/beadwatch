using Microsoft.UI.Xaml;

namespace Doudou.WinUI;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var log = Path.Combine(Doudou.Core.Services.AppPaths.UserData, "error.log");
            File.AppendAllText(log, $"[{DateTime.Now}] {e.Exception}\n\n");
            e.Handled = true;
            _window?.ShowError("出现了未处理的错误：" + e.Exception.Message + "（详情见 userdata 目录下的 error.log）");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception e)
        {
            // 启动失败时不能静默退出：写日志并弹出系统对话框
            var log = Path.Combine(Doudou.Core.Services.AppPaths.UserData, "error.log");
            File.AppendAllText(log, $"[{DateTime.Now}] 启动失败 {e}\n\n");
            MessageBoxW(IntPtr.Zero, Theme.AppName + "启动失败：\n" + e.Message + "\n\n详细信息已写入：\n" + log, Theme.AppName, 0x10);
            Exit();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

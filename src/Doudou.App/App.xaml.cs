using System.Windows;
using System.Windows.Threading;

using Doudou.Editor;
namespace Doudou.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = System.IO.Path.Combine(Doudou.Core.Services.AppPaths.UserData, "error.log");
        System.IO.File.AppendAllText(log, $"[{DateTime.Now}] {e.Exception}\n\n");
        MessageBox.Show("出现了未处理的错误：\n" + e.Exception.Message + "\n\n详细信息已写入 " + log, "豆豆工坊", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

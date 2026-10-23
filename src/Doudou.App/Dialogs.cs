using System.Windows;
using System.Windows.Controls;
using Doudou.Core.Services;

using Doudou.Editor;
namespace Doudou.App;

/// <summary>新建空白模型：画布尺寸。</summary>
public sealed class NewModelDialog : Window
{
    public int SX { get; private set; } = 32;
    public int SY { get; private set; } = 40;
    public int SZ { get; private set; } = 32;

    public NewModelDialog()
    {
        Title = "新建空白模型";
        Width = 360; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Res("Panel");
        var x = new TextBox { Text = "32" }; var y = new TextBox { Text = "40" }; var z = new TextBox { Text = "32" };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Ui.Hint("画布尺寸（颗）。宽 × 高 × 深，最大 256。豆径 2.6mm，40 颗高约 10.4cm。"));
        sp.Children.Add(Ui.Row("宽 X", x));
        sp.Children.Add(Ui.Row("高 Y", y));
        sp.Children.Add(Ui.Row("深 Z", z));
        var ok = Ui.Btn("创建", (_, _) =>
        {
            if (int.TryParse(x.Text, out var a) && int.TryParse(y.Text, out var b) && int.TryParse(z.Text, out var c)
                && a is > 0 and <= 256 && b is > 0 and <= 256 && c is > 0 and <= 256)
            {
                SX = a; SY = b; SZ = c; DialogResult = true;
            }
            else MessageBox.Show(this, "尺寸需在 1~256 之间");
        }, primary: true);
        ok.IsDefault = true;
        var row = Ui.Wrap(ok, Ui.Btn("取消", (_, _) => DialogResult = false));
        row.HorizontalAlignment = HorizontalAlignment.Right;
        sp.Children.Add(row);
        Content = sp;
    }
}

/// <summary>设置：AI 服务、默认色卡、作者信息。</summary>
public sealed class SettingsWindow : Window
{
    public SettingsWindow(Session s)
    {
        Title = "设置";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Res("Panel");
        var st = s.Settings;
        var api = new TextBox { Text = st.ImageApiBase };
        var key = new PasswordBox { Password = st.ImageApiKey };
        var model = new ComboBox { IsEditable = true, Text = st.ImageModel };
        var brands = s.Lib.Brands.Values.OrderBy(b => b.Id).ToList();
        var brand = new ComboBox { ItemsSource = brands.Select(b => b.Title).ToList(), SelectedIndex = Math.Max(0, brands.FindIndex(b => b.Id == st.DefaultBrand)) };
        var author = new TextBox { Text = st.Author };
        var wm = new TextBox { Text = st.Watermark };
        var pitch = new TextBox { Text = st.BeadPitchMm.ToString("0.0") };

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Ui.H2("AI 图像服务（OpenAI 兼容 /v1/images/edits）"));
        sp.Children.Add(Ui.Row("接口地址", api));
        sp.Children.Add(Ui.Row("API Key", key));
        sp.Children.Add(Ui.Row("模型", model));
        sp.Children.Add(Ui.Hint("用于：由随手拍照片生成标准视图、补全缺失视图、平涂取色；不会直接生成豆图。填入服务根地址与 Key 后，模型名可从服务商的 /v1/models 接口获取。Key 只保存在本机 userdata\\settings.json。"));
        sp.Children.Add(Ui.H2("默认"));
        sp.Children.Add(Ui.Row("色卡", brand));
        sp.Children.Add(Ui.Row("作者 / 拆图", author));
        sp.Children.Add(Ui.Row("图纸水印", wm));
        sp.Children.Add(Ui.Row("豆径 mm", pitch));
        sp.Children.Add(Ui.H2("本地模型"));
        sp.Children.Add(Ui.Hint(Hy3dClient.IsInstalled(out var why) ? "混元3D-2mv：已安装（" + AppPaths.Models + "）" : "混元3D：" + why));
        var ok = Ui.Btn("保存", (_, _) =>
        {
            st.ImageApiBase = api.Text.Trim();
            st.ImageApiKey = key.Password.Trim();
            st.ImageModel = model.Text.Trim();
            if (brand.SelectedIndex >= 0) st.DefaultBrand = brands[brand.SelectedIndex].Id;
            st.Author = author.Text.Trim();
            st.Watermark = wm.Text.Trim();
            if (double.TryParse(pitch.Text, out var p) && p > 1 && p < 10) st.BeadPitchMm = p;
            st.Save();
            DialogResult = true;
        }, primary: true);
        var row = Ui.Wrap(ok, Ui.Btn("取消", (_, _) => DialogResult = false));
        row.HorizontalAlignment = HorizontalAlignment.Right;
        sp.Children.Add(row);
        Content = sp;
    }
}

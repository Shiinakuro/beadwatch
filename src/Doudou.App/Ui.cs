using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

using Doudou.Editor;
namespace Doudou.App;

/// <summary>纯代码构建界面的小工具（统一间距与样式）。</summary>
public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock H1(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["H1"] };
    public static TextBlock H2(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["H2"] };
    public static TextBlock Hint(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 2, 0, 4) };
    public static TextBlock Text(string t, double size = 13) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap };

    public static Button Btn(string t, RoutedEventHandler click, string? tip = null, bool primary = false)
    {
        var b = new Button { Content = t, ToolTip = tip };
        if (primary) b.Style = (Style)Application.Current.Resources["Primary"];
        b.Click += click;
        return b;
    }

    public static ToggleButton Toggle(string t, string? tip = null) => new() { Content = t, ToolTip = tip, MinWidth = 34 };

    public static WrapPanel Wrap(params UIElement[] items)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var i in items) w.Children.Add(i);
        return w;
    }

    public static Grid Row(string label, UIElement control, double labelWidth = 92)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new TextBlock { Text = label, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(l);
        Grid.SetColumn((FrameworkElement)control, 1);
        g.Children.Add(control);
        return g;
    }

    public static (Grid Row, Slider Slider) SliderRow(string label, double min, double max, double value, Action<double> changed, double step = 1, string fmt = "0")
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 5, IsSnapToTickEnabled = step > 0, TickFrequency = step, VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { Text = value.ToString(fmt), Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, e) => { v.Text = e.NewValue.ToString(fmt); changed(e.NewValue); };
        var dp = new DockPanel();
        DockPanel.SetDock(v, Dock.Right);
        dp.Children.Add(v);
        dp.Children.Add(s);
        return (Row(label, dp), s);
    }

    public static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed)
    {
        var c = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, Margin = new Thickness(0, 2, 0, 2) };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    public static Border Card(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return new Border { Style = (Style)Application.Current.Resources["Card"], Child = sp };
    }

    public static ScrollViewer Page(StackPanel content)
    {
        content.Margin = new Thickness(14, 12, 14, 20);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public static SolidColorBrush Brush(Doudou.Core.Color.Rgb c) => new(Color.FromRgb(c.R, c.G, c.B));
}

/// <summary>单行文本输入对话框。</summary>
public static class Prompt
{
    public static string? Ask(Window owner, string title, string label, string def)
    {
        var w = new Window
        {
            Title = title, Owner = owner, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("Panel"),
        };
        var tb = new TextBox { Text = def, Margin = new Thickness(0, 6, 0, 10) };
        string? result = null;
        var ok = Ui.Btn("确定", (_, _) => { result = tb.Text; w.DialogResult = true; }, primary: true);
        ok.IsDefault = true;
        var cancel = Ui.Btn("取消", (_, _) => w.DialogResult = false);
        cancel.IsCancel = true;
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Ui.Text(label));
        sp.Children.Add(tb);
        var btns = Ui.Wrap(ok, cancel);
        btns.HorizontalAlignment = HorizontalAlignment.Right;
        sp.Children.Add(btns);
        w.Content = sp;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }
}

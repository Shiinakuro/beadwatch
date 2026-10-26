using Doudou.Core.Color;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Doudou.WinUI;

/// <summary>纯代码构建 Fluent 风格界面的小工具。</summary>
public static class UiKit
{
    /// <summary>查主题资源；找不到时返回 null（不抛异常，避免界面因个别资源缺失而无法启动）。</summary>
    public static T? Res<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is T t ? t : null;

    public static Brush Brush(string key) => Res<Brush>(key) ?? key switch
    {
        "TextFillColorSecondaryBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF)),
        "TextFillColorPrimaryBrush" => new SolidColorBrush(Colors.White),
        "CardStrokeColorDefaultBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        "SolidBackgroundFillColorBaseBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
    };

    public static Style? Style(string key) => Res<Style>(key);

    public static SolidColorBrush Solid(Rgb c, byte a = 255) => new(Windows.UI.Color.FromArgb(a, c.R, c.G, c.B));
    public static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 255) => new(Windows.UI.Color.FromArgb(a, r, g, b));

    public static readonly SolidColorBrush Accent = Solid(0xF9, 0x9E, 0x1A);

    // ───── 文字 ─────
    public static TextBlock Title(string t) => new() { Text = t, Style = Style("SubtitleTextBlockStyle") };
    public static TextBlock Strong(string t) => new() { Text = t, Style = Style("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
    public static TextBlock Body(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };
    /// <summary>次要说明文字：12px、二级文字色、行高 18、限宽避免行太长。</summary>
    public static TextBlock Caption(string t) => new()
    {
        Text = t, FontSize = Theme.F2, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
        Foreground = Theme.B(Theme.Text2), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>三级文字（标签、计数）。</summary>
    public static TextBlock Muted(string t, double size = Theme.F2) => new()
    {
        Text = t, FontSize = size, Foreground = Theme.B(Theme.Text3), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets") };

    /// <summary>按钮里的图标比文字柔和一级（图标面积大，同色会显得更重）。</summary>
    private static FontIcon SoftIcon(string glyph, double size) { var i = Icon(glyph, size); i.Opacity = 0.8; return i; }

    // ───── 按钮 ─────
    public static Button Btn(string text, Action click, string? glyph = null, bool accent = false, string? tip = null)
    {
        var b = new Button { Content = Label(text, glyph), Margin = new Thickness(0, 0, Theme.S2, Theme.S2), CornerRadius = Theme.RSmall, Padding = new Thickness(Theme.S3, 5, Theme.S3, 6) };
        if (accent) b.Style = Style("AccentButtonStyle");
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => click();
        return b;
    }

    public static Button WideBtn(string text, Action click, string? glyph = null, bool accent = true)
    {
        var b = Btn(text, click, glyph, accent);
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.Margin = new Thickness(0, Theme.S1, 0, 0);
        b.Padding = new Thickness(Theme.S3, 10, Theme.S3, 10);
        b.FontFamily = Theme.Display;
        b.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        b.FontSize = Theme.F4;
        b.CornerRadius = Theme.RCard;
        return b;
    }

    public static UIElement Label(string text, string? glyph)
    {
        if (glyph == null) return new TextBlock { Text = text };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(SoftIcon(glyph, 14));
        sp.Children.Add(new TextBlock { Text = text });
        return sp;
    }

    public static Button IconBtn(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = Icon(glyph, 16), Width = 40, Height = 34, Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>工具栏上的「图标 + 小字」切换按钮。</summary>
    public static ToggleButton ToolToggle(string glyph, string label, string tip)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock
        {
            Text = label, FontSize = Theme.F1, HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center,
        });
        var t = new ToggleButton { Content = sp, Width = 58, Height = 50, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6) };
        ToolTipService.SetToolTip(t, tip);
        AutomationProperties.SetName(t, label);
        return t;
    }

    /// <summary>页脚里的次级「下一步」链接。</summary>
    public static HyperlinkButton NextLink(string text, Action click)
    {
        var b = new HyperlinkButton
        {
            Content = text, Padding = new Thickness(0), MinWidth = 0, FontSize = Theme.F2,
            Foreground = Theme.B(Theme.Orange), HorizontalAlignment = HorizontalAlignment.Right,
        };
        b.Click += (_, _) => click();
        return b;
    }

    public static ToggleButton TextToggle(string label, string tip, double width = 52)
    {
        var t = new ToggleButton { Content = label, Width = width, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, Theme.S1, Theme.S1), FontSize = Theme.F2, CornerRadius = Theme.RSmall };
        ToolTipService.SetToolTip(t, tip);
        AutomationProperties.SetName(t, label);
        return t;
    }

    // ───── 布局 ─────
    /// <summary>
    /// 卡片：只用背景明度与面板区分（不叠边框/阴影）。
    /// 标题与说明 4、说明与内容 12、内容之间 8；卡片之间 16（组间大于组内）。
    /// </summary>
    public static Border Card(string? title, string? subtitle, params UIElement[] children)
    {
        var sp = new StackPanel { Spacing = Theme.S2 };
        if (title != null || subtitle != null)
        {
            var head = new StackPanel { Spacing = Theme.S1, Margin = new Thickness(0, 0, 0, Theme.S1) };
            if (title != null)
                head.Children.Add(new TextBlock
                {
                    Text = title, FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = Theme.F4,
                    Foreground = Theme.B(Theme.Text1), RenderTransform = new SkewTransform { AngleX = -8 },
                });
            if (subtitle != null) head.Children.Add(Caption(subtitle));
            sp.Children.Add(head);
        }
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            Child = sp,
            Background = Theme.B(Theme.Surface2),
            CornerRadius = Theme.RCard,
            Padding = new Thickness(Theme.S4),
            Margin = new Thickness(0, 0, 0, Theme.S4),
        };
    }

    /// <summary>空状态：图标 + 一句话 + 可选的下一步动作。</summary>
    public static Border Empty(string glyph, string text, string? actionText = null, Action? action = null)
    {
        var sp = new StackPanel { Spacing = Theme.S2, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(Theme.S4, Theme.S5, Theme.S4, Theme.S5) };
        var ic = Icon(glyph, 28);
        ic.Foreground = Theme.B(Theme.Text3);
        ic.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = text, FontSize = Theme.F2, Foreground = Theme.B(Theme.Text2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 280 });
        if (actionText != null && action != null)
        {
            var b2 = Btn(actionText, action);
            b2.HorizontalAlignment = HorizontalAlignment.Center;
            b2.Margin = new Thickness(0, Theme.S1, 0, 0);
            sp.Children.Add(b2);
        }
        return new Border { Child = sp, Background = Theme.B(Theme.Surface3), CornerRadius = Theme.RSmall };
    }

    public static Grid Row(string label, FrameworkElement control, double labelWidth = 84)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2), ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = Theme.F2, Foreground = Theme.B(Theme.Text2) });
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        g.Children.Add(control);
        return g;
    }

    public static (Grid Row, Slider Slider) SliderRow(string label, double min, double max, double value, Action<double> changed, double step = 1, string fmt = "0", string unit = "")
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, StepFrequency = step, VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { Text = value.ToString(fmt) + unit, Width = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        s.ValueChanged += (_, e) => { v.Text = e.NewValue.ToString(fmt) + unit; changed(e.NewValue); };
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(s);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return (Row(label, g), s);
    }

    public static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed)
    {
        var c = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    public static ToggleSwitch Switch(string header, bool on, Action<bool> changed)
    {
        var t = new ToggleSwitch { Header = null, OnContent = header, OffContent = header, IsOn = on, Margin = new Thickness(0, -4, 0, -4) };
        t.Toggled += (_, _) => changed(t.IsOn);
        return t;
    }

    public static CheckBox Check(string text, bool on, Action<bool>? changed = null)
    {
        var c = new CheckBox { Content = text, IsChecked = on, MinWidth = 0 };
        if (changed != null) { c.Checked += (_, _) => changed(true); c.Unchecked += (_, _) => changed(false); }
        return c;
    }

    public static StackPanel HStack(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    public static VariableSizedWrapGrid Wrap(params UIElement[] items)
    {
        var w = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
        foreach (var i in items) w.Children.Add(i);
        return w;
    }

    /// <summary>自动换行的按钮行。</summary>
    public static ItemsWrap Flow(params UIElement[] items)
    {
        var w = new ItemsWrap();
        foreach (var i in items) w.Children.Add(i);
        return w;
    }

    public static Border Swatch(Rgb c, double size = 28, string? text = null)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(5), Background = Solid(c),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
        if (text != null)
            b.Child = new TextBlock
            {
                Text = text, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = c.Luma < 140 ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black),
            };
        return b;
    }
}

/// <summary>最简单的自动换行面板（WinUI 没有内置 WrapPanel）。</summary>
public sealed class ItemsWrap : Panel
{
    public double HSpacing { get; set; } = 0;
    public double VSpacing { get; set; } = 0;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, rowH = 0, maxW = 0;
        foreach (var c in Children)
        {
            c.Measure(available);
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > available.Width) { y += rowH + VSpacing; x = 0; rowH = 0; }
            x += s.Width + HSpacing;
            rowH = Math.Max(rowH, s.Height);
            maxW = Math.Max(maxW, x);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? maxW : available.Width, y + rowH);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, rowH = 0;
        foreach (var c in Children)
        {
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += rowH + VSpacing; x = 0; rowH = 0; }
            c.Arrange(new Windows.Foundation.Rect(x, y, s.Width, s.Height));
            x += s.Width + HSpacing;
            rowH = Math.Max(rowH, s.Height);
        }
        return final;
    }
}

/// <summary>每个步骤页的公共接口。</summary>
public interface IStepPage
{
    string Header { get; }
    string Description { get; }
    UIElement View { get; }
    /// <summary>固定在右侧面板底部的主操作；为空则不占空间。</summary>
    UIElement? Footer => null;
    void OnEnter();
    /// <summary>离开本页：停掉只在本页需要的实时刷新（否则换页后仍在后台重算）。</summary>
    void OnLeave() { }
}

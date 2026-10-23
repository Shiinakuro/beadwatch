using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace Doudou.WinUI;

/// <summary>
/// 「拼豆先锋 BEADWATCH」主题：深蓝底、橙蓝强调色、斜切标题与六边形元素。
/// 所有图形均为原创绘制。
/// </summary>
public static class Theme
{
    public const string AppName = "拼豆先锋";
    public const string AppNameEn = "BEADWATCH";
    public const string Slogan = "从照片到立体拼豆，一条线完成";

    public static readonly Color Orange = Color.FromArgb(255, 0xF9, 0x9E, 0x1A);
    public static readonly Color OrangeDeep = Color.FromArgb(255, 0xE0, 0x7B, 0x00);
    public static readonly Color Blue = Color.FromArgb(255, 0x21, 0x8F, 0xFE);
    public static readonly Color Navy = Color.FromArgb(255, 0x14, 0x1C, 0x2A);
    public static readonly Color Panel = Color.FromArgb(0xE6, 0x1C, 0x26, 0x38);
    public static readonly Color Red = Color.FromArgb(255, 0xE8, 0x3B, 0x3B);
    public static readonly Color Green = Color.FromArgb(255, 0x3F, 0xD1, 0x7A);

    // ───── 设计令牌（Refactoring UI：所有数值来自一套刻度）─────
    /// <summary>间距刻度：4 / 8 / 12 / 16 / 24 / 32。组内间距小于组间间距。</summary>
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 24, S6 = 32;
    /// <summary>字号刻度：11 / 12 / 14 / 16 / 20 / 26 / 40。</summary>
    public const double F1 = 11, F2 = 12, F3 = 14, F4 = 16, F5 = 20, F6 = 26, F7 = 40;
    /// <summary>圆角：小控件 4；卡片与面板统一用 4/12 的斜切感。</summary>
    public static readonly CornerRadius RSmall = new(4), RCard = new(4, 12, 4, 12);

    /// <summary>三级文字色（偏冷蓝，与深蓝底同色相；不用半透明白）。</summary>
    public static readonly Color Text1 = Color.FromArgb(255, 0xF2, 0xF5, 0xFA);
    public static readonly Color Text2 = Color.FromArgb(255, 0xB4, 0xBF, 0xD0);
    public static readonly Color Text3 = Color.FromArgb(255, 0x7D, 0x8B, 0xA1);
    /// <summary>深蓝层级：卡片 / 卡片内凹（输入框、列表）。靠明度差分层，不叠边框。</summary>
    public static readonly Color Surface2 = Color.FromArgb(255, 0x1F, 0x2A, 0x3C);
    public static readonly Color Surface3 = Color.FromArgb(255, 0x14, 0x1C, 0x29);

    public static SolidColorBrush B(Color c) => new(c);
    public static SolidColorBrush B(Color c, byte a) => new(Color.FromArgb(a, c.R, c.G, c.B));

    /// <summary>窄体粗体字（系统自带 Bahnschrift），配合倾斜变换模仿游戏界面的标题风格。</summary>
    public static readonly FontFamily Display = new("Bahnschrift");

    /// <summary>斜切大标题：中文 + 英文小字。</summary>
    public static StackPanel Heading(string zh, string? en = null, double size = 26, Color? color = null)
    {
        var sp = new StackPanel { RenderTransform = new SkewTransform { AngleX = -10 } };
        sp.Children.Add(new TextBlock
        {
            Text = zh, FontFamily = Display, FontWeight = FontWeights.Bold, FontSize = size,
            FontStretch = Windows.UI.Text.FontStretch.Condensed, Foreground = B(color ?? Colors.White),
        });
        if (en != null)
            sp.Children.Add(new TextBlock
            {
                Text = en.ToUpperInvariant(), FontFamily = Display, FontWeight = FontWeights.SemiBold, FontSize = Math.Max(10, size * 0.42),
                CharacterSpacing = 160, Foreground = B(Orange), Margin = new Thickness(1, -2, 0, 0),
            });
        return sp;
    }

    /// <summary>斜切色块标签（如「导入」「导出」）。</summary>
    public static Border Tag(string text, Color bg, double size = 13, Color? fg = null) => new()
    {
        Background = B(bg),
        Padding = new Thickness(12, 3, 12, 3),
        RenderTransform = new SkewTransform { AngleX = -14 },
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock
        {
            Text = text, FontFamily = Display, FontWeight = FontWeights.Bold, FontSize = size,
            FontStretch = Windows.UI.Text.FontStretch.Condensed, Foreground = B(fg ?? Navy), CharacterSpacing = 40,
        },
    };

    /// <summary>通过 / 有问题 大横幅。</summary>
    public static Border Banner(bool victory, string detail)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock
        {
            Text = victory ? "通过" : "有问题", FontFamily = Display, FontWeight = FontWeights.Black, FontSize = 40,
            FontStretch = Windows.UI.Text.FontStretch.Condensed, Foreground = B(victory ? Orange : Red),
        });
        sp.Children.Add(new TextBlock
        {
            Text = victory ? "PASS" : "ISSUES", FontFamily = Display, FontWeight = FontWeights.Bold, FontSize = 14,
            CharacterSpacing = 400, Foreground = B(Colors.White, 0xCC), Margin = new Thickness(2, -6, 0, 4),
        });
        sp.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Opacity = 0.85 });
        return new Border
        {
            Child = sp,
            Padding = new Thickness(18, 10, 18, 12),
            Margin = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(4, 0, 0, 0),
            BorderBrush = B(victory ? Orange : Red),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = victory ? Color.FromArgb(0x55, 0xF9, 0x9E, 0x1A) : Color.FromArgb(0x55, 0xE8, 0x3B, 0x3B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1 },
                },
            },
        };
    }

    public static ImageBrush Background()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "bg.jpg");
        return new ImageBrush { ImageSource = new BitmapImage(new Uri(path)), Stretch = Stretch.UniformToFill };
    }

    /// <summary>加载提示（读条时轮播）。</summary>
    public static readonly string[] Tips =
    {
        "托比昂：炮台还没搭完，豆子先搭起来了。",
        "天使：英雄不朽！Ctrl+Z 也是。",
        "莱因哈特：护盾挡得住伤害，挡不住漏色——去「检查」页。",
        "美：冻住！这一层先别动——分层视图按 [ ] 换层。",
        "源氏：我需要治疗！零件太碎就试试「碎件换向」。",
        "安娜：睡一觉吧，模型马上就生成好了。",
        "黑影：漏色？我早就黑进去看过了。",
        "温斯顿：省下来的豆子，可以换花生酱。",
        "D.Va：拼豆不是游戏！……好吧，它是。",
        "卡西迪：午时已到——该熨烫了。",
        "半藏：龙……不，是豆。",
        "秩序之光：平面笔刷一拖就是一整层，秩序井然。",
        "路霸：钩过来！——吸色是 Alt + 左键。",
        "士兵76：卡通化视图能让五官更清楚，这是命令。",
        "猎空：骑兵到了！右键拖动可以旋转视角。",
        "禅雅塔：体验拼豆，即是体验宁静。",
        "法老之鹰：天降正义——按 F 把模型拉回视野中央。",
        "死神：死亡绽放？不，这次只是 Ctrl+Z。",
        "黑百合：一枪一个——单格笔刷按 V，落点更准。",
        "查莉娅：重力喷涌！矩形笔刷按 B，一次框住一片。",
        "堡垒：哔——填充笔刷按 R，整面一次到位。",
        "奥丽莎：站住别动——平面笔刷适合整层上色。",
        "破坏球：滚起来！中键拖动可以平移视角。",
        "卢西奥：加速！按住左键拖动就能连续贴豆。",
        "莫伊拉：科学证明，Shift+X/Y/Z 镜像更省时间。",
        "布里吉塔：盾牌举好，外壳厚度 1 颗就够，记得空心化。",
        "艾什：鲍勃，把这张照片扔进导入页。",
        "巴蒂斯特：维生力场撑住——先保存，再试装。",
        "西格玛：重力无所谓，按 F 让模型居中。",
        "回声：我可以复制——镜像会复制你的每一次落豆。",
        "索杰恩：超频！细节多的挂件可以做到 80~100 颗高。",
        "雾子：净化一切——取色前先去外描边。",
        "渣客女王：拆开！能单独熨烫的才算一个零件。",
        "伊拉锐：阳光照不到的角落，按 C 检查内腔。",
        "生命之梭：把花瓣拉回来——Ctrl+Y 重做。",
        "毛加：鼓点响起来，进度条马上就走完。",
        "探奇：钻下去看看——检查页能定位漏拆的豆。",
        "小提示：参考照片越清晰，生成的四视图越准。",
        "小提示：人物公仔常用 40~64 颗高；细节多的挂件可用 80~100 颗。",
        "小提示：导出前先到「检查」页跑一遍，能省下返工。",
        "小提示：A3 整卷 PDF 适合直接拿到打印店。",
    };

    private static readonly Random Rng = new();
    public static string RandomTip() => Tips[Rng.Next(Tips.Length)];

    /// <summary>步骤对应的一句提示。</summary>
    public static string StepLine(int step) => step switch
    {
        1 => "把照片或已有作品导入工程。",
        2 => "在三维视图中修形，像搭积木一样。",
        3 => "拆成可以单独熨烫的零件。",
        4 => "检查漏色、破洞与装配冲突。",
        5 => "生成整套图纸与装配动图。",
        _ => "",
    };
}

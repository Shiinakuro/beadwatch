using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Beadwatch
{
    /// <summary>安装器与卸载器共用的自绘界面：暗蓝底、橙色斜切、六边形底纹。</summary>
    internal static class Ow
    {
        public const float DesignW = 920f;
        public const float DesignH = 600f;

        public static readonly Color Bg0 = Color.FromArgb(9, 14, 23);
        public static readonly Color Bg1 = Color.FromArgb(21, 31, 48);
        public static readonly Color Panel = Color.FromArgb(27, 38, 56);
        public static readonly Color PanelHi = Color.FromArgb(35, 49, 71);
        public static readonly Color Line = Color.FromArgb(46, 61, 85);
        public static readonly Color Orange = Color.FromArgb(249, 158, 26);
        public static readonly Color OrangeDeep = Color.FromArgb(201, 123, 15);
        public static readonly Color Blue = Color.FromArgb(33, 143, 254);
        public static readonly Color Green = Color.FromArgb(63, 209, 122);
        public static readonly Color Red = Color.FromArgb(255, 92, 92);
        public static readonly Color TextHi = Color.FromArgb(242, 246, 255);
        public static readonly Color Text = Color.FromArgb(184, 196, 214);
        public static readonly Color TextDim = Color.FromArgb(125, 139, 162);

        public static Font Font(string family, float px, FontStyle style)
        {
            try { return new Font(family, px, style, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
        }

        public static readonly Font BrandZh = Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
        public static readonly Font BrandEn = Font("Bahnschrift", 9f, FontStyle.Bold);
        public static readonly Font Tag = Font("Bahnschrift", 10f, FontStyle.Bold);
        public static readonly Font HeroZh = Font("Microsoft YaHei UI", 46f, FontStyle.Bold);
        public static readonly Font HeroEn = Font("Bahnschrift", 15f, FontStyle.Bold);
        public static readonly Font H2 = Font("Microsoft YaHei UI", 36f, FontStyle.Bold);
        public static readonly Font H3 = Font("Microsoft YaHei UI", 20f, FontStyle.Bold);
        public static readonly Font Body = Font("Microsoft YaHei UI", 13f, FontStyle.Regular);
        public static readonly Font BodyBold = Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        public static readonly Font Small = Font("Microsoft YaHei UI", 11f, FontStyle.Regular);
        public static readonly Font Dim = Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
        public static readonly Font Btn = Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold);
        public static readonly Font BtnSmall = Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        public static readonly Font Percent = Font("Bahnschrift", 58f, FontStyle.Bold);
        public static readonly Font BigNum = Font("Bahnschrift", 22f, FontStyle.Bold);
        public static readonly Font TipTitle = Font("Bahnschrift", 9.5f, FontStyle.Bold);
        public static readonly Font Tip = Font("Microsoft YaHei UI", 12.5f, FontStyle.Regular);

        private static Image _icon;
        private static Stream _iconStream;
        private static Icon _winIcon;

        /// <summary>任务栏 / Alt+Tab 使用的窗口图标（csc /resource 载入）。</summary>
        public static Icon WindowIcon()
        {
            if (_winIcon != null) return _winIcon;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("Beadwatch.icon.ico"))
                {
                    if (s != null) _winIcon = new Icon(s, new Size(64, 64));
                }
            }
            catch { }
            if (_winIcon == null)
            {
                try { _winIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { }
            }
            return _winIcon;
        }

        /// <summary>嵌入的 256px 应用图标（csc /resource 载入）。</summary>
        public static Image AppIcon()
        {
            if (_icon != null) return _icon;
            var asm = Assembly.GetExecutingAssembly();
            // ICO 的大尺寸帧是 PNG 压缩的，System.Drawing 会退回小图标；直接用 256px PNG 保证清晰。
            try
            {
                using (var s = asm.GetManifestResourceStream("Beadwatch.icon.png"))
                {
                    if (s != null)
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        ms.Position = 0;
                        _iconStream = ms;
                        _icon = Image.FromStream(ms);
                    }
                }
            }
            catch { }
            if (_icon == null)
            {
                try
                {
                    using (var s = asm.GetManifestResourceStream("Beadwatch.icon.ico"))
                    {
                        if (s != null)
                        {
                            using (var ico = new Icon(s, new Size(128, 128)))
                                _icon = ico.ToBitmap();
                        }
                    }
                }
                catch { }
            }
            if (_icon == null)
            {
                try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); }
                catch { }
            }
            return _icon;
        }

        public static GraphicsPath Chamfer(RectangleF r, float tl, float tr, float br, float bl)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new PointF[]
            {
                new PointF(r.Left + tl, r.Top),
                new PointF(r.Right - tr, r.Top),
                new PointF(r.Right, r.Top + tr),
                new PointF(r.Right, r.Bottom - br),
                new PointF(r.Right - br, r.Bottom),
                new PointF(r.Left + bl, r.Bottom),
                new PointF(r.Left, r.Bottom - bl),
                new PointF(r.Left, r.Top + tl),
            });
            p.CloseFigure();
            return p;
        }

        public static void FillChamfer(Graphics g, RectangleF r, float cut, Color c)
        {
            using (var p = Chamfer(r, cut, cut, cut, cut))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void StrokeChamfer(Graphics g, RectangleF r, float cut, Color c, float width)
        {
            using (var p = Chamfer(r, cut, cut, cut, cut))
            using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B);
        }

        public static void PaintBackground(Graphics g, float w, float h)
        {
            using (var b = new LinearGradientBrush(new RectangleF(0, 0, w, h), Bg0, Bg1, 16f))
                g.FillRectangle(b, 0, 0, w, h);

            // 左上暖光
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-180, -260, 680, 680);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterColor = Alpha(Orange, 34);
                    pg.SurroundColors = new Color[] { Alpha(Orange, 0) };
                    g.FillPath(pg, path);
                }
            }
            // 右下冷光
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(w - 460, h - 320, 700, 600);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterColor = Alpha(Blue, 20);
                    pg.SurroundColors = new Color[] { Alpha(Blue, 0) };
                    g.FillPath(pg, path);
                }
            }

            // 右侧淡六边形阵
            DrawHexField(g, new RectangleF(w - 470, -30, 520, 500), 34f, Alpha(Color.FromArgb(150, 178, 214), 12));

            // 右上斜条纹
            using (var b = new SolidBrush(Alpha(Color.White, 5)))
            {
                for (int i = 0; i < 4; i++)
                {
                    float x = w - 300 + i * 74;
                    g.FillPolygon(b, new PointF[]
                    {
                        new PointF(x, 0), new PointF(x + 34, 0),
                        new PointF(x - 120, h), new PointF(x - 154, h),
                    });
                }
            }

            // 顶部橙色条与斜切口
            using (var b = new SolidBrush(Orange)) g.FillRectangle(b, 0, 0, w, 3);
            using (var b = new SolidBrush(Alpha(Orange, 90)))
                g.FillPolygon(b, new PointF[] { new PointF(0, 3), new PointF(240, 3), new PointF(208, 9), new PointF(0, 9) });

            // 底部渐隐
            using (var b = new LinearGradientBrush(new RectangleF(0, h - 150, w, 150), Alpha(Bg0, 0), Alpha(Bg0, 170), 90f))
                g.FillRectangle(b, 0, h - 150, w, 150);
        }

        public static void DrawHexField(Graphics g, RectangleF area, float r, Color color)
        {
            var old = g.Clip;
            g.SetClip(area);
            using (var pen = new Pen(color, 1f))
            {
                float hw = (float)(Math.Sqrt(3) * r);
                float vsp = r * 1.5f;
                int row = 0;
                for (float cy = area.Top + r; cy < area.Bottom + r; cy += vsp, row++)
                {
                    float off = (row % 2 == 0) ? 0 : hw / 2f;
                    for (float cx = area.Left + off; cx < area.Right + hw; cx += hw)
                        DrawHex(g, pen, cx, cy, r);
                }
            }
            g.Clip = old;
        }

        private static void DrawHex(Graphics g, Pen pen, float cx, float cy, float r)
        {
            var pts = new PointF[6];
            for (int i = 0; i < 6; i++)
            {
                double a = Math.PI / 180.0 * (60 * i + 30);
                pts[i] = new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r));
            }
            g.DrawPolygon(pen, pts);
        }

        /// <summary>斜切标题（向左下倾斜）。</summary>
        public static void SkewText(Graphics g, string text, Font f, Color c, float x, float y, float skew)
        {
            var state = g.Save();
            g.TranslateTransform(x, y);
            g.MultiplyTransform(new Matrix(1f, 0f, skew, 1f, 0f, 0f));
            using (var b = new SolidBrush(c)) g.DrawString(text, f, b, 0, 0, StringFormat.GenericTypographic);
            g.Restore(state);
        }

        public static float Measure(Graphics g, string s, Font f)
        {
            return g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        public static float Tracked(Graphics g, string s, Font f, Color c, float x, float y, float tracking)
        {
            using (var b = new SolidBrush(c))
            {
                foreach (char ch in s)
                {
                    string one = ch.ToString();
                    g.DrawString(one, f, b, x, y, StringFormat.GenericTypographic);
                    x += g.MeasureString(one, f, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
                }
            }
            return x;
        }

        public static float TrackedWidth(Graphics g, string s, Font f, float tracking)
        {
            float x = 0;
            foreach (char ch in s)
            {
                string one = ch.ToString();
                x += g.MeasureString(one, f, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
            }
            return x;
        }

        public static void TrackedCenter(Graphics g, string s, Font f, Color c, float cx, float y, float tracking)
        {
            float w = TrackedWidth(g, s, f, tracking);
            Tracked(g, s, f, c, cx - w / 2f, y, tracking);
        }

        /// <summary>小标签：橙色斜杠 + 字距拉开的英文。</summary>
        public static void SectionTag(Graphics g, string s, float x, float y)
        {
            using (var b = new SolidBrush(Orange))
                g.FillPolygon(b, new PointF[] { new PointF(x, y + 3), new PointF(x + 12, y + 3), new PointF(x + 8, y + 14), new PointF(x - 4, y + 14) });
            Tracked(g, s, Tag, Orange, x + 20, y, 2.2f);
        }

        public static void Check(Graphics g, Rectangle r, bool on, bool hover)
        {
            Color border = on ? Orange : (hover ? Mix(Line, Text, 0.35f) : Line);
            using (var p = Chamfer(r, 4, 4, 4, 4))
            {
                using (var b = new SolidBrush(on ? Orange : Alpha(Color.White, 8))) g.FillPath(b, p);
                using (var pen = new Pen(border, 1.4f)) g.DrawPath(pen, p);
            }
            if (on)
            {
                using (var pen = new Pen(Color.FromArgb(16, 23, 34), 2.4f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new PointF[]
                    {
                        new PointF(r.X + 4, r.Y + r.Height * 0.52f),
                        new PointF(r.X + r.Width * 0.42f, r.Y + r.Height - 4.5f),
                        new PointF(r.X + r.Width - 4, r.Y + 4.5f),
                    });
                }
            }
        }

        public static void Button(Graphics g, Rectangle r, string text, bool primary, bool hover, bool enabled, Font f)
        {
            float cut = 12f;
            using (var p = Chamfer(r, cut, 0, cut, 0))
            {
                if (primary)
                {
                    Color c = enabled ? (hover ? Mix(Orange, Color.White, 0.16f) : Orange) : Alpha(Orange, 70);
                    using (var b = new SolidBrush(c)) g.FillPath(b, p);
                    if (enabled)
                    {
                        using (var pen = new Pen(Alpha(Color.White, 70), 1f))
                            g.DrawLine(pen, r.Left + cut, r.Top + 1, r.Right - 2, r.Top + 1);
                    }
                    using (var fmt = new StringFormat())
                    {
                        fmt.Alignment = StringAlignment.Center;
                        fmt.LineAlignment = StringAlignment.Center;
                        using (var b = new SolidBrush(enabled ? Color.FromArgb(14, 21, 31) : Alpha(Color.FromArgb(14, 21, 31), 150)))
                            g.DrawString(text, f, b, r, fmt);
                    }
                }
                else
                {
                    using (var b = new SolidBrush(Alpha(Color.White, hover && enabled ? 20 : 8))) g.FillPath(b, p);
                    using (var pen = new Pen(hover && enabled ? Alpha(Orange, 190) : Line, 1.2f)) g.DrawPath(pen, p);
                    using (var fmt = new StringFormat())
                    {
                        fmt.Alignment = StringAlignment.Center;
                        fmt.LineAlignment = StringAlignment.Center;
                        using (var b = new SolidBrush(enabled ? TextHi : Alpha(TextDim, 120)))
                            g.DrawString(text, f, b, r, fmt);
                    }
                }
            }
        }

        public static void Card(Graphics g, RectangleF r, float cut, Color fill, Color border)
        {
            using (var p = Chamfer(r, cut, cut, cut, cut))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(border, 1f)) g.DrawPath(pen, p);
            }
        }

        public static void Diamond(Graphics g, PointF c, float radius, Color color, float width)
        {
            using (var pen = new Pen(color, width))
                g.DrawPolygon(pen, new PointF[]
                {
                    new PointF(c.X, c.Y - radius), new PointF(c.X + radius, c.Y),
                    new PointF(c.X, c.Y + radius), new PointF(c.X - radius, c.Y),
                });
        }

        public static void Brackets(Graphics g, RectangleF r, float len, Color color)
        {
            using (var pen = new Pen(color, 2f))
            {
                g.DrawLine(pen, r.Left, r.Top, r.Left + len, r.Top);
                g.DrawLine(pen, r.Left, r.Top, r.Left, r.Top + len);
                g.DrawLine(pen, r.Right, r.Bottom, r.Right - len, r.Bottom);
                g.DrawLine(pen, r.Right, r.Bottom, r.Right, r.Bottom - len);
            }
        }

        public static string Elide(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return "…" + s.Substring(s.Length - max + 1);
        }

        public static void CenterAt(Form f)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + (wa.Height - f.Height) / 2);
        }
    }

    internal class OwForm : Form
    {
        private float _scale = 1f;
        private bool _dragging;
        private bool _pageDown;
        private Point _dragStart;

        protected bool DlgOpen;
        private string _dlgTitle = "";
        private string _dlgBody = "";
        private string _dlgYes = "确定";
        private string _dlgNo = "";
        private Action _dlgAction;

        protected float S { get { return _scale; } }
        protected Rectangle MinRect { get { return new Rectangle(824, 8, 44, 32); } }
        protected Rectangle CloseRect { get { return new Rectangle(868, 8, 44, 32); } }

        /// <summary>鼠标位置换算到 920×600 设计坐标（高 DPI 下绘制与命中都按设计坐标）。</summary>
        protected Point CursorLogical
        {
            get
            {
                var p = PointToClient(Cursor.Position);
                return new Point((int)(p.X / _scale), (int)(p.Y / _scale));
            }
        }

        protected virtual string HeaderTag { get { return "SETUP"; } }

        public OwForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ow.Bg0;
            Font = Ow.Body;
            KeyPreview = true;
            Text = "拼豆先锋";
            Icon = Ow.WindowIcon();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyScale(DeviceDpi / 96f, false);
            try
            {
                int round = 2;   // Win11：圆角窗口；旧系统忽略
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch { }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyScale(e.DeviceDpiNew / 96f, true);
        }

        private void ApplyScale(float s, bool keepCenter)
        {
            if (s < 0.5f) s = 0.5f;
            if (s > 4f) s = 4f;
            _scale = s;
            ClientSize = new Size((int)Math.Round(Ow.DesignW * s), (int)Math.Round(Ow.DesignH * s));
            if (!keepCenter) Ow.CenterAt(this);
            OnScaleApplied();
            Invalidate();
        }

        /// <summary>缩放变化后重排真实子控件（路径输入框）。</summary>
        protected virtual void OnScaleApplied() { }

        private Point ToLogical(Point p)
        {
            return new Point((int)(p.X / _scale), (int)(p.Y / _scale));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            e.Graphics.ScaleTransform(_scale, _scale);
            Ow.PaintBackground(e.Graphics, Ow.DesignW, Ow.DesignH);
            DrawHeader(e.Graphics);
            DrawPage(e.Graphics);
            if (DlgOpen) DrawDialog(e.Graphics);
        }

        protected virtual void DrawHeader(Graphics g)
        {
            using (var pen = new Pen(Ow.Alpha(Ow.Line, 170), 1f)) g.DrawLine(pen, 56, 63.5f, 864, 63.5f);

            var icon = Ow.AppIcon();
            if (icon != null) g.DrawImage(icon, new RectangleF(56, 20, 24, 24));
            g.DrawString("拼豆先锋", Ow.BrandZh, new SolidBrush(Ow.TextHi), 88, 21);
            Ow.Tracked(g, "BEADWATCH", Ow.BrandEn, Ow.Alpha(Ow.Orange, 220), 168, 27, 2.4f);

            string tag = HeaderTag + "  //  v" + BuildInfo.Version;
            float tw = Ow.TrackedWidth(g, tag, Ow.BrandEn, 2f);
            Ow.Tracked(g, tag, Ow.BrandEn, Ow.TextDim, 812 - tw, 27, 2f);

            // 窗口按钮
            DrawWindowButton(g, MinRect, false);
            DrawWindowButton(g, CloseRect, true);
        }

        private void DrawWindowButton(Graphics g, Rectangle r, bool close)
        {
            bool hover = r.Contains(CursorLogical);
            if (hover)
            {
                Color c = close ? Ow.Alpha(Ow.Red, 200) : Ow.Alpha(Color.White, 22);
                Ow.FillChamfer(g, r, 4, c);
            }
            using (var pen = new Pen(hover ? (close ? Color.White : Ow.TextHi) : Ow.TextDim, 1.5f))
            {
                if (close)
                {
                    g.DrawLine(pen, r.X + 16, r.Y + 11, r.X + 28, r.Y + 23);
                    g.DrawLine(pen, r.X + 28, r.Y + 11, r.X + 16, r.Y + 23);
                }
                else
                {
                    g.DrawLine(pen, r.X + 15, r.Y + 21, r.X + 29, r.Y + 21);
                }
            }
        }

        protected virtual void DrawPage(Graphics g) { }

        protected virtual Cursor CursorAt(Point p) { return Cursors.Default; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = ToLogical(e.Location);
            if (_dragging)
            {
                Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
                return;
            }
            Cursor = MinRect.Contains(p) || CloseRect.Contains(p) ? Cursors.Hand : CursorAt(p);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var p = ToLogical(e.Location);
            if (DlgOpen)
            {
                DialogClick(p);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (CloseRect.Contains(p)) { CloseClicked(); return; }
            if (MinRect.Contains(p)) { WindowState = FormWindowState.Minimized; return; }
            if (p.Y < 64) { _dragging = true; _dragStart = e.Location; return; }
            _pageDown = true;
            PageMouseDown(p);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            bool wasPage = _pageDown;
            _pageDown = false;
            if (!DlgOpen && wasPage && e.Button == MouseButtons.Left) PageMouseUp(ToLogical(e.Location));
        }

        protected virtual void PageMouseDown(Point p) { }
        protected virtual void PageMouseUp(Point p) { }

        protected virtual void CloseClicked() { Close(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (DlgOpen)
            {
                if (e.KeyCode == Keys.Escape && _dlgNo.Length > 0) { DlgOpen = false; Invalidate(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { var a = _dlgAction; DlgOpen = false; Invalidate(); if (a != null) a(); e.Handled = true; e.SuppressKeyPress = true; }
                return;
            }
            PageKey(e);
        }

        protected virtual void PageKey(KeyEventArgs e) { }

        // ── 内置确认框（不弹系统 MessageBox，保持风格统一） ──

        protected void Ask(string title, string body, string yesText, Action onYes)
        {
            _dlgTitle = title; _dlgBody = body; _dlgYes = yesText; _dlgNo = "取消"; _dlgAction = onYes;
            DlgOpen = true; Invalidate();
        }

        protected void Alert(string title, string body, string okText, Action onOk)
        {
            _dlgTitle = title; _dlgBody = body; _dlgYes = okText; _dlgNo = ""; _dlgAction = onOk;
            DlgOpen = true; Invalidate();
        }

        private Rectangle DlgPanel()
        {
            return new Rectangle(180, 178, 560, 244);
        }

        private Rectangle DlgYesRect()
        {
            var p = DlgPanel();
            if (_dlgNo.Length == 0) return new Rectangle(p.Right - 168, p.Bottom - 64, 140, 40);
            return new Rectangle(p.Right - 292, p.Bottom - 64, 140, 40);
        }

        private Rectangle DlgNoRect()
        {
            var p = DlgPanel();
            return new Rectangle(p.Right - 136, p.Bottom - 64, 108, 40);
        }

        private void DrawDialog(Graphics g)
        {
            using (var b = new SolidBrush(Color.FromArgb(185, 3, 7, 13)))
                g.FillRectangle(b, 0, 0, Ow.DesignW, Ow.DesignH);
            var p = DlgPanel();
            // 阴影
            using (var b = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            {
                using (var sh = Ow.Chamfer(new RectangleF(p.X + 8, p.Y + 10, p.Width, p.Height), 14, 14, 14, 14))
                    g.FillPath(b, sh);
            }
            Ow.Card(g, p, 14, Ow.Panel, Ow.Alpha(Ow.Orange, 120));
            using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, p.X + 14, p.Y, 120, 3);
            Ow.Brackets(g, new RectangleF(p.X - 8, p.Y - 8, p.Width + 16, p.Height + 16), 18, Ow.Alpha(Ow.Orange, 160));

            g.DrawString(_dlgTitle, Ow.H3, new SolidBrush(Ow.TextHi), p.X + 30, p.Y + 26);

            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisWord;
                using (var b = new SolidBrush(Ow.Text))
                    g.DrawString(_dlgBody, Ow.Body, b, new RectangleF(p.X + 30, p.Y + 66, p.Width - 60, 92), fmt);
            }

            var yes = DlgYesRect();
            bool hoverY = yes.Contains(CursorLogical);
            Ow.Button(g, yes, _dlgYes, true, hoverY, true, Ow.BtnSmall);
            if (_dlgNo.Length > 0)
            {
                var no = DlgNoRect();
                Ow.Button(g, no, _dlgNo, false, no.Contains(CursorLogical), true, Ow.BtnSmall);
            }
        }

        private void DialogClick(Point lp)
        {
            if (DlgYesRect().Contains(lp))
            {
                var a = _dlgAction;
                DlgOpen = false; Invalidate();
                if (a != null) a();
                return;
            }
            if (_dlgNo.Length > 0 && DlgNoRect().Contains(lp))
            {
                DlgOpen = false; Invalidate();
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}

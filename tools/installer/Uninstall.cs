using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Beadwatch
{
    internal static class UninstallProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (Has(args, "/silent") || Has(args, "-silent"))
            {
                Environment.Exit(UninstallSilent.Run(args));
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UninstallForm());
        }

        private static bool Has(string[] args, string a)
        {
            foreach (var s in args) if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal static class UninstallEngine
    {
        public static string SelfDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static string UserDataDir
        {
            get { return Path.Combine(SelfDir, "userdata"); }
        }

        public static string AppExe
        {
            get { return Path.Combine(SelfDir, "app", "拼豆先锋.exe"); }
        }

        public static long DirSize()
        {
            try
            {
                long sum = 0;
                string ud = Path.GetFullPath(UserDataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (var f in Directory.GetFiles(SelfDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (Path.GetFullPath(f).StartsWith(ud, StringComparison.OrdinalIgnoreCase)) continue;
                        sum += new FileInfo(f).Length;
                    }
                    catch { }
                }
                return sum;
            }
            catch { return 0; }
        }

        public static bool AnyRunning()
        {
            foreach (var p in Process.GetProcessesByName("拼豆先锋"))
            {
                try { p.Dispose(); } catch { }
                return true;
            }
            return false;
        }

        public static bool CloseRunning()
        {
            var list = Process.GetProcessesByName("拼豆先锋");
            foreach (var p in list) { try { p.CloseMainWindow(); } catch { } }
            foreach (var p in list)
            {
                try
                {
                    if (!p.HasExited && !p.WaitForExit(5000)) return false;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return !AnyRunning();
        }

        private static bool Inside(string dir, string file)
        {
            string d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(file).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> CollectFiles(bool keepData)
        {
            var list = new List<string>();
            string self = Path.GetFullPath(Application.ExecutablePath);
            string ud = Path.GetFullPath(UserDataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            try
            {
                foreach (var f in Directory.GetFiles(SelfDir, "*", SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(f);
                    if (string.Equals(full, self, StringComparison.OrdinalIgnoreCase)) continue;
                    if (keepData && full.StartsWith(ud, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(full);
                }
            }
            catch { }
            return list;
        }

        public static int RemoveShortcuts()
        {
            int n = 0;
            string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "拼豆先锋.lnk");
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "拼豆先锋.lnk");
            foreach (var f in new string[] { start, desktop })
            {
                try { if (File.Exists(f)) { File.Delete(f); n++; } } catch { }
            }
            return n;
        }

        public static void RemoveRegistry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\拼豆先锋", false);
            }
            catch { }
        }

        /// <summary>删除文件与空目录，最后计划自删除。失败的文件数通过 failed 返回。</summary>
        public static int Run(bool keepData, Action<int, int, string> report, out int removed)
        {
            removed = 0;
            var files = CollectFiles(keepData);
            int total = files.Count;
            int failed = 0;
            int i = 0;

            // 深的文件先删，便于后面清理空目录
            files.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });

            foreach (var f in files)
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    removed++;
                }
                catch { failed++; }
                i++;
                if (report != null && (i % 20 == 0 || i == total)) report(i, total, f);
            }

            RemoveShortcuts();
            RemoveRegistry();

            // 清理空目录（保留自身所在目录与 userdata）
            try
            {
                var dirs = new List<string>(Directory.GetDirectories(SelfDir, "*", SearchOption.AllDirectories));
                dirs.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
                string ud = Path.GetFullPath(UserDataDir).TrimEnd(Path.DirectorySeparatorChar);
                foreach (var d in dirs)
                {
                    string full = Path.GetFullPath(d);
                    if (keepData && (string.Equals(full, ud, StringComparison.OrdinalIgnoreCase) ||
                                     full.StartsWith(ud + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    try
                    {
                        if (Directory.GetFiles(d).Length == 0 && Directory.GetDirectories(d).Length == 0)
                            Directory.Delete(d);
                    }
                    catch { }
                }
            }
            catch { }

            return failed;
        }

        /// <summary>退出后删除卸载器自身；未保留数据时连整个目录一起删。</summary>
        public static void ScheduleSelfDelete(bool keepData)
        {
            try
            {
                string self = Application.ExecutablePath;
                string dir = SelfDir;
                string cmd = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + self + "\"";
                cmd += keepData
                    ? " & rmdir \"" + dir + "\""
                    : " & rmdir /s /q \"" + dir + "\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch { }
        }
    }

    internal static class UninstallSilent
    {
        public static int Run(string[] args)
        {
            string log = Path.Combine(Path.GetTempPath(), "beadwatch_uninstall.log");
            try
            {
                if (UninstallEngine.AnyRunning())
                    throw new InvalidOperationException("拼豆先锋正在运行，请先关闭。");
                bool keep = true;
                foreach (var s in args)
                    if (string.Equals(s, "/deletedata", StringComparison.OrdinalIgnoreCase)) keep = false;
                int removed;
                int failed = UninstallEngine.Run(keep, null, out removed);
                UninstallEngine.ScheduleSelfDelete(keep);
                File.WriteAllText(log, "OK\tremoved=" + removed + "\tfailed=" + failed + "\tkeepdata=" + keep + "\r\n");
                return failed == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, "FAIL\t" + ex + "\r\n"); } catch { }
                return 1;
            }
        }
    }

    internal sealed class UninstallForm : OwForm
    {
        private int _page;
        private bool _keepData = true;
        private bool _uninstalling;
        private bool _scheduled;
        private int _doneFiles;
        private int _totalFiles;
        private int _removed;
        private int _failed;
        private bool _valid;
        private string _status = "";
        private float _phase;
        private System.Windows.Forms.Timer _timer;
        private long _appSize;

        private static readonly Rectangle CancelBtn = new Rectangle(540, 532, 112, 48);
        private static readonly Rectangle RemoveBtn = new Rectangle(664, 532, 200, 48);
        private static readonly Rectangle KeepRect = new Rectangle(58, 328, 520, 26);
        private static readonly Rectangle CloseBtn = new Rectangle(674, 532, 190, 48);
        private static readonly Rectangle OpenBtn = new Rectangle(534, 532, 124, 48);

        protected override string HeaderTag { get { return "UNINSTALL"; } }

        public UninstallForm()
        {
            Text = "卸载拼豆先锋";
            _valid = File.Exists(UninstallEngine.AppExe) || Directory.Exists(UninstallEngine.UserDataDir);
            _appSize = UninstallEngine.DirSize();
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 33;
            _timer.Tick += delegate
            {
                _phase += 0.04f;
                if (_page == 1) Invalidate();
            };
        }

        protected override void DrawPage(Graphics g)
        {
            if (_page == 0) DrawConfirm(g);
            else if (_page == 1) DrawProgress(g);
            else DrawDone(g);
        }

        private void DrawConfirm(Graphics g)
        {
            Ow.SectionTag(g, "UNINSTALL  //  卸载", 58, 92);
            Ow.SkewText(g, "卸载拼豆先锋", Ow.HeroZh, Ow.TextHi, 68, 108, -0.11f);
            g.DrawString("移除程序、快捷方式与「应用」列表中的卸载入口。", Ow.Body, new SolidBrush(Ow.TextDim), 58, 184);

            var card = new RectangleF(58, 218, 520, 86);
            Ow.Card(g, card, 10, Ow.Alpha(Ow.Panel, 235), Ow.Alpha(Ow.Line, 200));
            using (var b = new SolidBrush(Ow.Alpha(Ow.Orange, 90))) g.FillRectangle(b, 58, 218, 3, 86);
            Ow.Tracked(g, "INSTALL LOCATION", Ow.Tag, Ow.TextDim, 76, 234, 1.8f);
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisPath;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                using (var b = new SolidBrush(Ow.TextHi))
                    g.DrawString(UninstallEngine.SelfDir, Ow.Small, b, new RectangleF(76, 254, 480, 18), fmt);
            }
            string size = _appSize > 0 ? "程序与资源约 " + Mb(_appSize) + " MB" : "程序文件";
            g.DrawString(size, Ow.Small, new SolidBrush(Ow.TextDim), 76, 278);

            Ow.Check(g, new Rectangle(60, 330, 22, 22), _keepData, KeepRect.Contains(CursorLogical));
            g.DrawString("保留设置和用户数据（userdata）", Ow.Body, new SolidBrush(Ow.TextHi), 92, 330);
            g.DrawString("包括设置、API Key 与工程，重装后自动沿用。", Ow.Small,
                new SolidBrush(Ow.TextDim), 58, 364);

            var warn = new RectangleF(58, 404, 520, 74);
            Ow.Card(g, warn, 8, Ow.Alpha(_keepData ? Ow.Panel : Ow.Alpha(Ow.Red, 40), 220),
                Ow.Alpha(_keepData ? Ow.Line : Ow.Red, 170));
            using (var b = new SolidBrush(_keepData ? Ow.Orange : Ow.Red)) g.FillRectangle(b, 58, 404, 3, 74);
            string text = _keepData
                ? "删除程序、快捷方式与卸载入口，保留 userdata。"
                : "删除程序与 userdata，无法恢复。";
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisWord;
                using (var b = new SolidBrush(_keepData ? Ow.Text : Ow.Alpha(Color.White, 230)))
                    g.DrawString(text, Ow.Tip, b, new RectangleF(78, 420, 480, 44), fmt);
            }

            DrawFooter(g);
            g.DrawString("卸载无法撤销。", Ow.Small, new SolidBrush(Ow.TextDim), 74, 547);
            Ow.Button(g, CancelBtn, "取消", false, CancelBtn.Contains(CursorLogical), true, Ow.Btn);
            Ow.Button(g, RemoveBtn, "确认卸载", true, RemoveBtn.Contains(CursorLogical), _valid, Ow.Btn);
            DrawHero(g);

            if (!_valid && !DlgOpen)
            {
                g.DrawString("找不到安装信息：这个程序可能已被移动。", Ow.Small, new SolidBrush(Ow.Red), 58, 486);
            }
        }

        private void DrawProgress(Graphics g)
        {
            Ow.SectionTag(g, "REMOVING  //  清理中", 58, 92);
            Ow.SkewText(g, "正在卸载", Ow.H2, Ow.TextHi, 66, 106, -0.10f);

            float frac = _totalFiles > 0 ? (float)Math.Min(1.0, (double)_doneFiles / _totalFiles) : 0f;
            int pct = (int)Math.Round(frac * 100f);
            string ps = pct + "%";
            float pw = Ow.Measure(g, ps, Ow.Percent);
            using (var b = new SolidBrush(Ow.Orange))
                g.DrawString(ps, Ow.Percent, b, 864 - pw, 92);
            g.DrawString("正在移除程序文件并清理快捷方式。", Ow.Body, new SolidBrush(Ow.TextDim), 58, 184);

            var bar = new Rectangle(58, 246, 806, 22);
            int segs = 48;
            float gap = 4f;
            float segW = (bar.Width - (segs - 1) * gap) / segs;
            for (int i = 0; i < segs; i++)
            {
                var r = new RectangleF(bar.X + i * (segW + gap), bar.Y, segW, bar.Height);
                bool on = (i + 0.5f) / segs <= frac;
                if (on)
                {
                    using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, r);
                }
                else
                {
                    using (var b = new SolidBrush(Ow.Alpha(Color.White, 9))) g.FillRectangle(b, r);
                    using (var pen = new Pen(Ow.Alpha(Ow.Line, 140), 1f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }
            }

            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisPath;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                using (var b = new SolidBrush(Ow.Text))
                    g.DrawString("正在删除  " + Ow.Elide(_status, 78), Ow.Small, b, new RectangleF(58, 284, 806, 20), fmt);
            }
            g.DrawString("文件 " + _doneFiles + " / " + _totalFiles, Ow.Small, new SolidBrush(Ow.TextDim), 58, 308);

            var tip = new RectangleF(58, 356, 806, 96);
            Ow.Card(g, tip, 10, Ow.Alpha(Ow.Panel, 235), Ow.Alpha(Ow.Line, 200));
            using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, 58, 356, 3, 96);
            Ow.Tracked(g, "NOTES  //  说明", Ow.TipTitle, Ow.Orange, 78, 371, 2f);
            string t = _keepData
                ? "userdata 会保留：设置、API Key 与工程都还在，下次安装后会自动沿用。"
                : "userdata 会与程序一起删除。若改变主意，请现在关闭窗口。";
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisWord;
                using (var b = new SolidBrush(Ow.Text))
                    g.DrawString(t, Ow.Tip, b, new RectangleF(78, 390, 760, 48), fmt);
            }

            DrawFooter(g);
            g.DrawString("完成后会停在此页。", Ow.Small, new SolidBrush(Ow.TextDim), 74, 547);
            DrawHero(g);
        }

        private void DrawDone(Graphics g)
        {
            using (var p = Ow.Chamfer(new RectangleF(58, 104, 54, 54), 10, 0, 10, 0))
            {
                using (var b = new SolidBrush(_failed == 0 ? Ow.Orange : Ow.Red)) g.FillPath(b, p);
                if (_failed == 0)
                {
                    using (var pen = new Pen(Color.FromArgb(15, 22, 32), 3f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(72, 132), new PointF(82, 142), new PointF(99, 120),
                        });
                    }
                }
                else
                {
                    using (var pen = new Pen(Color.White, 3f))
                    {
                        g.DrawLine(pen, 72, 118, 98, 144);
                        g.DrawLine(pen, 98, 118, 72, 144);
                    }
                }
            }
            Ow.SectionTag(g, "UNINSTALL COMPLETE  //  卸载完毕", 128, 102);
            Ow.SkewText(g, _failed == 0 ? "已卸载" : "已卸载（部分文件被占用）", Ow.H2, Ow.TextHi, 134, 114, -0.10f);
            g.DrawString(_failed == 0 ? "拼豆先锋已从本机移除。" : "有 " + _failed + " 个文件无法删除；关闭占用它们的程序后可再试。",
                Ow.Body, new SolidBrush(Ow.TextDim), 58, 178);

            float y = 220;
            Row(g, "安装位置", UninstallEngine.SelfDir, ref y);
            Row(g, "删除文件", _removed + " 个", ref y);
            Row(g, "用户数据", _keepData ? "已保留（userdata）" : "已删除", ref y);

            var note = new RectangleF(58, 314, 520, 68);
            Ow.Card(g, note, 8, Ow.Alpha(Ow.Panel, 220), Ow.Alpha(Ow.Line, 180));
            using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, 58, 314, 3, 68);
            string text = _keepData
                ? "设置与工程保留在：\n" + Path.Combine(UninstallEngine.SelfDir, "userdata")
                : "userdata 已一并删除，没有留下个人数据。";
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisPath;
                using (var b = new SolidBrush(Ow.Text))
                    g.DrawString(text, Ow.Tip, b, new RectangleF(78, 328, 480, 46), fmt);
            }

            DrawFooter(g);
            g.DrawString("感谢使用。", Ow.Small, new SolidBrush(Ow.TextDim), 74, 547);
            if (_keepData && Directory.Exists(UninstallEngine.UserDataDir))
                Ow.Button(g, OpenBtn, "打开文件夹", false, OpenBtn.Contains(CursorLogical), true, Ow.Btn);
            Ow.Button(g, CloseBtn, "关闭", true, CloseBtn.Contains(CursorLogical), true, Ow.Btn);
            DrawHero(g);
        }

        private void Row(Graphics g, string label, string value, ref float y)
        {
            Ow.Tracked(g, label, Ow.Tag, Ow.TextDim, 58, y + 3, 1.6f);
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisPath;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                using (var b = new SolidBrush(Ow.TextHi))
                    g.DrawString(value, Ow.Small, b, new RectangleF(150, y, 430, 18), fmt);
            }
            using (var pen = new Pen(Ow.Alpha(Ow.Line, 110), 1f))
                g.DrawLine(pen, 58, y + 26, 580, y + 26);
            y += 34;
        }

        private void DrawFooter(Graphics g)
        {
            using (var pen = new Pen(Ow.Alpha(Ow.Line, 170), 1f)) g.DrawLine(pen, 56, 516.5f, 864, 516.5f);
            using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, 58, 532, 2, 24);
        }

        private void DrawHero(Graphics g)
        {
            var panel = new RectangleF(604, 96, 260, 386);
            Ow.Card(g, panel, 12, Ow.Alpha(Ow.Panel, 170), Ow.Alpha(Ow.Line, 200));
            using (var b = new SolidBrush(Ow.Alpha(Ow.Red, 14)))
                g.FillPolygon(b, new PointF[]
                {
                    new PointF(panel.Right - 96, panel.Top), new PointF(panel.Right, panel.Top),
                    new PointF(panel.Right, panel.Top + 96),
                });
            Ow.Brackets(g, new RectangleF(panel.X - 8, panel.Y - 8, panel.Width + 16, panel.Height + 16), 20, Ow.Alpha(Ow.Orange, 120));

            var tg = new RectangleF(778, 110, 64, 20);
            using (var p = Ow.Chamfer(tg, 6, 0, 6, 0))
            using (var b = new SolidBrush(Ow.Alpha(Ow.Red, 210))) g.FillPath(b, p);
            Ow.Tracked(g, "EXIT", Ow.BrandEn, Color.White, 790, 113, 1f);

            var icon = Ow.AppIcon();
            Ow.Diamond(g, new PointF(734, 222), 106, Ow.Alpha(Ow.Orange, 45), 1.2f);
            if (icon != null) g.DrawImage(icon, new RectangleF(658, 146, 152, 152));

            Ow.TrackedCenter(g, "UNINSTALL", Ow.HeroEn, Ow.TextHi, 734, 330, 3f);
            Ow.TrackedCenter(g, "BEADWATCH  //  v" + BuildInfo.Version, Ow.Tag, Ow.TextDim, 734, 358, 1.6f);
            using (var pen = new Pen(Ow.Alpha(Ow.Orange, 90), 1f)) g.DrawLine(pen, 648, 388, 820, 388);
            Ow.TrackedCenter(g, "移除本机上的拼豆先锋。", Ow.Small, Ow.Alpha(Ow.Text, 210), 734, 404, 1f);
        }

        protected override Cursor CursorAt(Point p)
        {
            if (_page == 0)
            {
                if (KeepRect.Contains(p) || CancelBtn.Contains(p) || RemoveBtn.Contains(p)) return Cursors.Hand;
            }
            else if (_page == 2)
            {
                if (CloseBtn.Contains(p) || (_keepData && OpenBtn.Contains(p))) return Cursors.Hand;
            }
            return Cursors.Default;
        }

        protected override void PageMouseUp(Point p)
        {
            if (_page == 0)
            {
                if (KeepRect.Contains(p)) { _keepData = !_keepData; return; }
                if (CancelBtn.Contains(p)) { Close(); return; }
                if (RemoveBtn.Contains(p) && _valid) { StartUninstall(); return; }
            }
            else if (_page == 2)
            {
                if (OpenBtn.Contains(p) && _keepData)
                {
                    try { Process.Start("explorer.exe", "\"" + UninstallEngine.UserDataDir + "\""); } catch { }
                    return;
                }
                if (CloseBtn.Contains(p)) { CloseAndCleanup(); return; }
            }
            Invalidate();
        }

        protected override void PageKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _page == 0) { Close(); e.Handled = true; }
        }

        protected override void CloseClicked()
        {
            if (_uninstalling) return;
            if (_page == 2) { CloseAndCleanup(); return; }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_uninstalling)
            {
                e.Cancel = true;
                return;
            }
            if (_page == 2 && !_scheduled)
            {
                _scheduled = true;
                UninstallEngine.ScheduleSelfDelete(_keepData);
            }
            base.OnFormClosing(e);
        }

        private void CloseAndCleanup()
        {
            if (!_scheduled && _page == 2)
            {
                _scheduled = true;
                UninstallEngine.ScheduleSelfDelete(_keepData);
            }
            Close();
        }

        private void StartUninstall()
        {
            if (UninstallEngine.AnyRunning())
            {
                Ask("程序正在运行", "卸载前需要先关闭拼豆先锋（未保存的编辑可能丢失）。", "关闭并继续", delegate
                {
                    if (UninstallEngine.CloseRunning()) Begin();
                    else Alert("请手动关闭", "拼豆先锋仍在运行，请关闭后再试。", "返回", null);
                });
                return;
            }
            Begin();
        }

        private void Begin()
        {
            _page = 1;
            _uninstalling = true;
            _totalFiles = UninstallEngine.CollectFiles(_keepData).Count;
            _doneFiles = 0;
            _status = "正在准备…";
            _timer.Start();
            Invalidate();

            var th = new Thread(Worker);
            th.IsBackground = true;
            th.Start();
        }

        private void Worker()
        {
            try
            {
                int removed;
                int failed = UninstallEngine.Run(_keepData, delegate(int done, int total, string name)
                {
                    Post(delegate
                    {
                        _doneFiles = done;
                        _totalFiles = total;
                        _status = name;
                        Invalidate();
                    });
                }, out removed);
                Post(delegate
                {
                    _uninstalling = false;
                    _page = 2;
                    _timer.Stop();
                    _removed = removed;
                    _failed = failed;
                    Invalidate();
                });
            }
            catch (Exception ex)
            {
                Post(delegate
                {
                    _uninstalling = false;
                    _page = 0;
                    _timer.Stop();
                    Alert("卸载未完成", ex.Message, "返回", null);
                });
            }
        }

        private void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        private static string Mb(long bytes)
        {
            return ((long)Math.Ceiling(bytes / 1048576.0)).ToString();
        }
    }
}

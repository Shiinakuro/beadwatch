using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Beadwatch
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (HasArg(args, "/silent") || HasArg(args, "-silent"))
            {
                Environment.Exit(Silent.Run(args));
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        private static bool HasArg(string[] args, string a)
        {
            foreach (var s in args) if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal sealed class PayloadEntry
    {
        public string Name;
        public long Size;
        public long Compressed;
        public long DataOffset;
    }

    /// <summary>
    /// 追加在安装程序尾部的自解压数据区：BDWA1 + 条目表 + 原始 deflate 数据；
    /// 文件最后 24 字节为 BDWPAY1\n + 偏移 + 长度。全部用 .NET Framework 自带的 DeflateStream 解压。
    /// </summary>
    internal sealed class Payload : IDisposable
    {
        private FileStream _fs;
        public readonly List<PayloadEntry> Entries = new List<PayloadEntry>();
        public long TotalSize;

        public static Payload Open()
        {
            var p = new Payload();
            p._fs = new FileStream(Application.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long len = p._fs.Length;
            if (len < 64) throw new InvalidDataException("安装包不完整：没有找到内置数据。");
            p._fs.Seek(len - 24, SeekOrigin.Begin);
            var foot = new byte[24];
            ReadFull(p._fs, foot, 0, 24);
            if (Encoding.ASCII.GetString(foot, 0, 8) != "BDWPAY1\n")
                throw new InvalidDataException("安装包不完整：没有找到内置数据。");
            long off = BitConverter.ToInt64(foot, 8);
            long size = BitConverter.ToInt64(foot, 16);
            if (off < 0 || size < 0 || off + size > len - 24)
                throw new InvalidDataException("安装包数据区损坏，请重新下载。");

            p._fs.Seek(off, SeekOrigin.Begin);
            var head = new byte[9];
            ReadFull(p._fs, head, 0, 9);
            if (Encoding.ASCII.GetString(head, 0, 5) != "BDWA1")
                throw new InvalidDataException("安装包内容格式不正确。");
            int count = BitConverter.ToInt32(head, 5);
            if (count <= 0 || count > 100000) throw new InvalidDataException("安装包条目数量异常。");

            for (int i = 0; i < count; i++)
            {
                var nl = new byte[4];
                ReadFull(p._fs, nl, 0, 4);
                int nlen = BitConverter.ToInt32(nl, 0);
                if (nlen <= 0 || nlen > 4096) throw new InvalidDataException("安装包目录损坏。");
                var nb = new byte[nlen];
                ReadFull(p._fs, nb, 0, nlen);
                var meta = new byte[16];
                ReadFull(p._fs, meta, 0, 16);
                var e = new PayloadEntry();
                e.Name = Encoding.UTF8.GetString(nb).Replace('\\', '/');
                e.Size = BitConverter.ToInt64(meta, 0);
                e.Compressed = BitConverter.ToInt64(meta, 8);
                if (e.Size < 0 || e.Compressed < 0) throw new InvalidDataException("安装包目录损坏。");
                e.DataOffset = p._fs.Position;
                p._fs.Seek(e.Compressed, SeekOrigin.Current);
                p.Entries.Add(e);
                p.TotalSize += e.Size;
            }
            if (p._fs.Position > len - 24) throw new InvalidDataException("安装包目录损坏。");
            p._fs.Seek(off, SeekOrigin.Begin);
            return p;
        }

        public static void Info(out long total, out int count)
        {
            using (var p = Open())
            {
                total = p.TotalSize;
                count = p.Entries.Count;
            }
        }

        public void Extract(PayloadEntry e, string dest, Action<long> onBytes)
        {
            _fs.Position = e.DataOffset;
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var src = new DeflateStream(_fs, CompressionMode.Decompress, true))
            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                var buf = new byte[65536];
                long total = 0;
                while (true)
                {
                    int n = src.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    dst.Write(buf, 0, n);
                    total += n;
                    if (onBytes != null) onBytes(n);
                }
                if (total != e.Size)
                    throw new InvalidDataException("写入校验失败：" + e.Name);
            }
        }

        internal static void ReadFull(Stream s, byte[] buf, int off, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, off + got, count - got);
                if (n <= 0) throw new EndOfStreamException("安装包数据不完整。");
                got += n;
            }
        }

        public void Dispose()
        {
            if (_fs != null) { _fs.Dispose(); _fs = null; }
        }
    }

    internal static class InstallEngine
    {
        public const string AppExeName = "拼豆先锋.exe";

        public static string DefaultDir()
        {
            string existing = InstalledLocation();
            if (!string.IsNullOrEmpty(existing) && Directory.Exists(existing)) return existing;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                @"Programs\拼豆先锋");
        }

        public static string InstalledLocation()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\拼豆先锋"))
                {
                    if (k == null) return null;
                    return k.GetValue("InstallLocation") as string;
                }
            }
            catch { return null; }
        }

        public static void Run(string dir, bool desktopShortcut,
            Action<int, int, long, string> report, Func<bool> cancelled,
            out int files, out long bytes)
        {
            files = 0;
            bytes = 0;
            int doneFiles = 0;
            long doneBytes = 0;
            using (var payload = Payload.Open())
            {
                Directory.CreateDirectory(dir);
                foreach (var e in payload.Entries)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    string dest = Path.GetFullPath(Path.Combine(dir, e.Name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装包路径异常：" + e.Name);
                    string name = e.Name;
                    payload.Extract(e, dest, delegate(long n)
                    {
                        doneBytes += n;
                        if (report != null) report(doneFiles, payload.Entries.Count, doneBytes, name);
                    });
                    doneFiles++;
                }

                string appDir = Path.Combine(dir, "app");
                string exe = Path.Combine(appDir, AppExeName);
                if (File.Exists(exe))
                {
                    CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "拼豆先锋.lnk"),
                        exe, appDir);
                    if (desktopShortcut)
                        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "拼豆先锋.lnk"),
                            exe, appDir);
                    else
                        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "拼豆先锋.lnk"));
                    WriteUninstallEntry(dir, exe, payload.TotalSize);
                }
            }
            files = doneFiles;
            bytes = doneBytes;
        }

        public static void CreateShortcut(string lnk, string target, string workdir)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workdir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "拼豆先锋 BEADWATCH · 立体拼豆设计" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            catch { /* 快捷方式失败不影响安装 */ }
        }

        private static void WriteUninstallEntry(string dir, string exe, long totalSize)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\拼豆先锋"))
            {
                if (k == null) return;
                k.SetValue("DisplayName", "拼豆先锋 BEADWATCH", RegistryValueKind.String);
                k.SetValue("DisplayVersion", BuildInfo.Version, RegistryValueKind.String);
                k.SetValue("Publisher", "拼豆先锋", RegistryValueKind.String);
                k.SetValue("DisplayIcon", exe + ",0", RegistryValueKind.String);
                k.SetValue("InstallLocation", dir, RegistryValueKind.String);
                k.SetValue("UninstallString", "\"" + Path.Combine(dir, "卸载.exe") + "\"", RegistryValueKind.String);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                k.SetValue("EstimatedSize", (int)Math.Max(1, totalSize / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
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
            foreach (var p in list)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            foreach (var p in list)
            {
                try
                {
                    if (!p.HasExited && !p.WaitForExit(5000))
                    {
                        return false;
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return !AnyRunning();
        }
    }

    internal static class Silent
    {
        public static int Run(string[] args)
        {
            string log = Path.Combine(Path.GetTempPath(), "beadwatch_setup.log");
            try
            {
                string dir = Arg(args, "/dir=");
                if (string.IsNullOrEmpty(dir)) dir = InstallEngine.DefaultDir();
                dir = Path.GetFullPath(dir);
                if (InstallEngine.AnyRunning())
                    throw new InvalidOperationException("拼豆先锋正在运行，请先关闭。");
                int files;
                long bytes;
                var sw = Stopwatch.StartNew();
                InstallEngine.Run(dir, !Has(args, "/nodesktop"), null, null, out files, out bytes);
                File.WriteAllText(log, "OK\t" + dir + "\tfiles=" + files + "\tbytes=" + bytes + "\t" + sw.ElapsedMilliseconds + "ms\r\n");
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, "FAIL\t" + ex + "\r\n"); } catch { }
                return 1;
            }
        }

        private static string Arg(string[] args, string prefix)
        {
            foreach (var s in args)
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return s.Substring(prefix.Length);
            return null;
        }

        private static bool Has(string[] args, string a)
        {
            foreach (var s in args) if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal sealed class SetupForm : OwForm
    {
        private static readonly string[] Tips = new string[]
        {
            "首次启动会显示使用引导：按 → 翻页，Esc 跳过；之后可在「帮助」中重看。",
            "视口右键拖动旋转，中键平移，滚轮缩放，F 键居中。",
            "导入照片后可框选主体，用 AI 视图或轮廓雕刻生成模型。",
            "按 C 进入成品检查，按 H 临时隐藏选中的豆子。",
            "检查页可导出整套图纸、A3 PDF、装配动图与 CSV 豆单。",
            "零件太碎时，试试「碎件换向」；外壳记得空心化。",
        };

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private int _page;      // 0 欢迎 1 进度 2 完成
        private bool _installing;
        private bool _cancel;
        private bool _createdRoot;
        private bool _desktop = true;
        private bool _launch = true;
        private string _status = "正在准备…";
        private string _error;
        private int _doneFiles;
        private int _totalFiles;
        private long _doneBytes;
        private long _totalBytes;
        private int _payloadCount;
        private bool _payloadOk;
        private bool _upgrade;
        private int _tipIndex;
        private int _tipTick;
        private float _phase;
        private DateTime _lastReport;
        private string _finishDir = "";

        private static readonly Rectangle PathRect = new Rectangle(58, 306, 458, 46);
        private static readonly Rectangle BrowseRect = new Rectangle(526, 306, 70, 46);
        private static readonly Rectangle DeskRect = new Rectangle(58, 396, 300, 26);
        private static readonly Rectangle CancelBtn = new Rectangle(540, 532, 112, 48);
        private static readonly Rectangle InstallBtn = new Rectangle(664, 532, 200, 48);
        private static readonly Rectangle ProgressCancelBtn = new Rectangle(752, 532, 112, 48);
        private static readonly Rectangle OpenBtn = new Rectangle(524, 532, 124, 48);
        private static readonly Rectangle DoneBtn = new Rectangle(664, 532, 200, 48);
        private static readonly Rectangle LaunchRect = new Rectangle(58, 418, 320, 26);

        public SetupForm()
        {
            Text = "拼豆先锋 安装程序";
            _installDir = InstallEngine.DefaultDir();
            try
            {
                Payload.Info(out _totalBytes, out _payloadCount);
                _payloadOk = _payloadCount > 0;
            }
            catch { _payloadOk = false; }

            _upgrade = !string.IsNullOrEmpty(InstallEngine.InstalledLocation());

            _timer.Interval = 33;
            _timer.Tick += TimerTick;

            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a.StartsWith("/preview=", StringComparison.OrdinalIgnoreCase))
                {
                    int pg;
                    if (int.TryParse(a.Substring(9), out pg)) { _preview = true; _previewPage = pg; }
                }
            }
            if (_preview)
            {
                _installing = false;
                _page = _previewPage == 2 ? 2 : 1;
                _totalBytes = 286 * 1024 * 1024;
                _payloadCount = 512;
                _finishDir = _installDir;
                _doneBytes = _page == 1 ? 118 * 1024 * 1024 : _totalBytes;
                _doneFiles = _page == 1 ? 214 : 512;
                _totalFiles = 512;
                _status = @"app\Assets\Fonts\NotoSansSC-Regular.otf";
                _tipIndex = 0;
                if (_page == 1) _timer.Start();
            }
        }

        private void TimerTick(object sender, EventArgs e)
        {
            _phase += 0.04f;
            if (_page == 1)
            {
                _tipTick++;
                if (_tipTick >= 180)
                {
                    _tipTick = 0;
                    _tipIndex = (_tipIndex + 1) % Tips.Length;
                }
                Invalidate();
            }
        }

        // ───────────── 绘制 ─────────────

        protected override void DrawPage(Graphics g)
        {
            if (_page == 0) DrawWelcome(g);
            else if (_page == 1) DrawProgress(g);
            else DrawFinish(g);
        }

        private void DrawWelcome(Graphics g)
        {
            Ow.SectionTag(g, "SETUP  //  WELCOME", 58, 92);

            Ow.SkewText(g, "拼豆先锋", Ow.HeroZh, Ow.TextHi, 68, 108, -0.12f);

            Ow.Tracked(g, "BEADWATCH", Ow.HeroEn, Ow.Orange, 58, 176, 3.2f);
            float bw = Ow.TrackedWidth(g, "BEADWATCH", Ow.HeroEn, 3.2f);
            using (var b = new SolidBrush(Ow.TextDim))
                g.DrawString("立体拼豆设计", Ow.Body, b, 58 + bw + 16, 176);

            g.DrawString("把照片变成可以真正拼出来的立体拼豆模型。", Ow.Body, new SolidBrush(Ow.Text), 58, 210);
            g.DrawString("三维重建 · 编辑 · 拆件 · 检查 · 图纸导出", Ow.Body, new SolidBrush(Ow.TextDim), 58, 234);

            using (var b = new SolidBrush(Ow.TextHi)) g.DrawString("安装位置", Ow.BodyBold, b, 58, 264);
            float lw = Ow.Measure(g, "安装位置", Ow.BodyBold);
            Ow.Tracked(g, "INSTALL LOCATION", Ow.Tag, Ow.TextDim, 58 + lw + 14, 269, 2.2f);

            bool hoverPath = PathRect.Contains(CursorLogical);
            using (var p = Ow.Chamfer(new RectangleF(PathRect.X, PathRect.Y, PathRect.Width, PathRect.Height), 8, 0, 8, 0))
            {
                using (var b = new SolidBrush(hoverPath ? Ow.PanelHi : Ow.Panel)) g.FillPath(b, p);
                using (var pen = new Pen(hoverPath ? Ow.Orange : Ow.Line, hoverPath ? 1.2f : 1f)) g.DrawPath(pen, p);
            }
            using (var b = new SolidBrush(Ow.Alpha(Ow.Orange, hoverPath ? 210 : 90)))
                g.FillRectangle(b, 58, 306, 3, 46);
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisPath;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                fmt.LineAlignment = StringAlignment.Center;
                using (var b = new SolidBrush(Ow.TextHi))
                    g.DrawString(_installDir, Ow.Body, b, new RectangleF(74, 306, 426, 46), fmt);
            }

            bool hoverBrowse = BrowseRect.Contains(CursorLogical);
            Ow.Button(g, BrowseRect, "更改…", false, hoverBrowse, true, Ow.BtnSmall);

            if (!_payloadOk)
            {
                g.DrawString("未找到内置安装数据：请使用完整的安装程序运行。", Ow.Small, new SolidBrush(Ow.Red), 58, 362);
            }
            else
            {
                string hint = "需要约 " + Math.Max(1, _totalBytes / 1048576L) + " MB 可用空间 · 仅安装到当前用户";
                if (_upgrade)
                    hint = "检测到已安装版本，将覆盖升级（设置与工程会保留）· 需要约 " + Math.Max(1, _totalBytes / 1048576L) + " MB";
                g.DrawString(hint, Ow.Small, new SolidBrush(Ow.TextDim), 58, 362);
            }

            Ow.Check(g, new Rectangle(60, 398, 22, 22), _desktop, DeskRect.Contains(CursorLogical));
            g.DrawString("创建桌面快捷方式", Ow.Body, new SolidBrush(Ow.TextHi), 92, 398);
            g.DrawString("自动创建开始菜单快捷方式与卸载入口。", Ow.Small, new SolidBrush(Ow.TextDim), 58, 428);
            g.DrawString("升级不会删除设置、工程与 API Key。", Ow.Small, new SolidBrush(Ow.Alpha(Ow.TextDim, 200)), 58, 450);

            DrawFooterLine(g);
            g.DrawString("v" + BuildInfo.Version + "  ·  Windows 10 1809 及以上", Ow.Small,
                new SolidBrush(Ow.TextDim), 74, 547);

            bool hoverCancel = CancelBtn.Contains(CursorLogical);
            bool hoverInstall = InstallBtn.Contains(CursorLogical);
            Ow.Button(g, CancelBtn, "取消", false, hoverCancel, true, Ow.Btn);
            Ow.Button(g, InstallBtn, "开始安装  →", true, hoverInstall, _payloadOk, Ow.Btn);

            DrawHero(g, "SETUP", false);
        }

        private void DrawProgress(Graphics g)
        {
            Ow.SectionTag(g, "INSTALLING  //  安装中", 58, 92);
            Ow.SkewText(g, "正在安装", Ow.H2, Ow.TextHi, 66, 106, -0.10f);

            float frac = _totalBytes > 0 ? (float)Math.Min(1.0, (double)_doneBytes / _totalBytes) : 0f;
            int pct = (int)Math.Round(frac * 100f);
            string ps = pct + "%";
            float pw = Ow.Measure(g, ps, Ow.Percent);
            using (var b = new SolidBrush(_error != null ? Ow.Red : Ow.Orange))
                g.DrawString(ps, Ow.Percent, b, 864 - pw, 92);

            g.DrawString(_error != null ? "安装中断。" : "正在释放程序文件，通常不到一分钟。",
                Ow.Body, new SolidBrush(Ow.TextDim), 58, 184);

            var bar = new Rectangle(58, 246, 806, 22);
            int segs = 48;
            float gap = 4f;
            float segW = (bar.Width - (segs - 1) * gap) / segs;
            var filled = new List<RectangleF>();
            for (int i = 0; i < segs; i++)
            {
                var r = new RectangleF(bar.X + i * (segW + gap), bar.Y, segW, bar.Height);
                bool on = (i + 0.5f) / segs <= frac;
                if (on)
                {
                    filled.Add(r);
                    Color c = Ow.Orange;
                    if (i == (int)(frac * segs) - 1) c = Ow.Mix(Ow.Orange, Color.White, 0.35f);
                    using (var b = new SolidBrush(c)) g.FillRectangle(b, r);
                }
                else
                {
                    using (var b = new SolidBrush(Ow.Alpha(Color.White, 9))) g.FillRectangle(b, r);
                    using (var pen = new Pen(Ow.Alpha(Ow.Line, 140), 1f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }
            }
            // 已填充区域的扫光
            if (filled.Count > 0)
            {
                var clipRect = new RectangleF(bar.X, bar.Y, bar.Width * frac, bar.Height);
                var old = g.Clip;
                g.SetClip(clipRect);
                float sx = bar.X + ((_phase * 190f) % (bar.Width + 260f)) - 130f;
                using (var lb = new LinearGradientBrush(new RectangleF(sx - 70, bar.Y, 140, bar.Height),
                    Ow.Alpha(Color.White, 0), Ow.Alpha(Color.White, 85), 0f))
                {
                    var cb = new ColorBlend(3);
                    cb.Colors = new Color[] { Ow.Alpha(Color.White, 0), Ow.Alpha(Color.White, 85), Ow.Alpha(Color.White, 0) };
                    cb.Positions = new float[] { 0f, 0.5f, 1f };
                    lb.InterpolationColors = cb;
                    g.FillRectangle(lb, new RectangleF(sx - 70, bar.Y, 140, bar.Height));
                }
                g.Clip = old;
            }

            string st = _error != null ? "错误：" + _error : "正在写入  " + Ow.Elide(_status, 78);
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisPath;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                using (var b = new SolidBrush(_error != null ? Ow.Red : Ow.Text))
                    g.DrawString(st, Ow.Small, b, new RectangleF(58, 284, 806, 20), fmt);
            }
            string stat = "文件 " + _doneFiles + " / " + _totalFiles +
                          "     已释放 " + Mb(_doneBytes) + " MB / " + Mb(_totalBytes) + " MB";
            g.DrawString(stat, Ow.Small, new SolidBrush(Ow.TextDim), 58, 308);

            // 提示卡
            var tipRect = new RectangleF(58, 356, 806, 96);
            Ow.Card(g, tipRect, 10, Ow.Alpha(Ow.Panel, 235), Ow.Alpha(Ow.Line, 200));
            using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, 58, 356, 3, 96);
            Ow.Tracked(g, "TIPS  //  使用提示", Ow.TipTitle, Ow.Orange, 78, 371, 2f);
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisWord;
                using (var b = new SolidBrush(Ow.Text))
                    g.DrawString(Tips[_tipIndex], Ow.Tip, b, new RectangleF(78, 390, 760, 48), fmt);
            }

            DrawFooterLine(g);
            g.DrawString(_cancel ? "正在取消，请稍候…" : "安装过程中请勿关闭窗口。", Ow.Small, new SolidBrush(Ow.TextDim), 74, 547);
            Ow.Button(g, ProgressCancelBtn, "取消", false, ProgressCancelBtn.Contains(CursorLogical), !_cancel, Ow.Btn);
        }

        private void DrawFinish(Graphics g)
        {
            using (var p = Ow.Chamfer(new RectangleF(58, 104, 54, 54), 10, 0, 10, 0))
            {
                using (var b = new SolidBrush(Ow.Orange)) g.FillPath(b, p);
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
            Ow.SectionTag(g, "INSTALL COMPLETE  //  安装完毕", 128, 102);
            Ow.SkewText(g, "安装完成", Ow.H2, Ow.TextHi, 134, 114, -0.10f);
            g.DrawString("拼豆先锋已安装到本机。", Ow.Body, new SolidBrush(Ow.TextDim), 58, 178);

            float y = 220;
            Row(g, "安装位置", _finishDir, ref y);
            Row(g, "占用空间", Mb(_totalBytes) + " MB  ·  " + _payloadCount + " 个文件", ref y);
            Row(g, "快捷方式", "开始菜单" + (_desktop ? "  ·  桌面" : ""), ref y);

            var note = new RectangleF(58, 314, 520, 68);
            Ow.Card(g, note, 8, Ow.Alpha(Ow.Panel, 220), Ow.Alpha(Ow.Line, 180));
            using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, 58, 314, 3, 68);
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.EllipsisWord;
                using (var b = new SolidBrush(Ow.Text))
                    g.DrawString("首次启动会显示使用引导：按 → 翻页，Esc 跳过。\n之后可在「帮助」中重看。", Ow.Tip, b,
                        new RectangleF(78, 328, 480, 46), fmt);
            }

            Ow.Check(g, new Rectangle(60, 420, 22, 22), _launch, LaunchRect.Contains(CursorLogical));
            g.DrawString("立即启动拼豆先锋", Ow.Body, new SolidBrush(Ow.TextHi), 92, 420);

            DrawFooterLine(g);
            g.DrawString("安装包可保留，用于修复或重新安装。", Ow.Small, new SolidBrush(Ow.TextDim), 74, 547);
            Ow.Button(g, OpenBtn, "打开文件夹", false, OpenBtn.Contains(CursorLogical), true, Ow.Btn);
            Ow.Button(g, DoneBtn, "完成", true, DoneBtn.Contains(CursorLogical), true, Ow.Btn);

            DrawHero(g, "INSTALL COMPLETE", true);
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

        private void DrawFooterLine(Graphics g)
        {
            using (var pen = new Pen(Ow.Alpha(Ow.Line, 170), 1f)) g.DrawLine(pen, 56, 516.5f, 864, 516.5f);
            using (var b = new SolidBrush(Ow.Orange)) g.FillRectangle(b, 58, 532, 2, 24);
        }

        private void DrawHero(Graphics g, string tag, bool finish)
        {
            var panel = new RectangleF(604, 96, 260, 386);
            Ow.Card(g, panel, 12, Ow.Alpha(Ow.Panel, 170), Ow.Alpha(Ow.Line, 200));
            using (var b = new SolidBrush(Ow.Alpha(Ow.Orange, 18)))
                g.FillPolygon(b, new PointF[]
                {
                    new PointF(panel.Right - 96, panel.Top), new PointF(panel.Right, panel.Top),
                    new PointF(panel.Right, panel.Top + 96),
                });
            Ow.Brackets(g, new RectangleF(panel.X - 8, panel.Y - 8, panel.Width + 16, panel.Height + 16), 20, Ow.Alpha(Ow.Orange, 140));

            // 右上角小牌
            var tg = new RectangleF(784, 110, 58, 20);
            using (var p = Ow.Chamfer(tg, 6, 0, 6, 0))
            using (var b = new SolidBrush(Ow.Orange)) g.FillPath(b, p);
            Ow.Tracked(g, tag == "SETUP" ? "SETUP" : "DONE", Ow.BrandEn, Color.FromArgb(16, 22, 32), 793, 113, 1.2f);

            var icon = Ow.AppIcon();
            Ow.Diamond(g, new PointF(734, 222), 106, Ow.Alpha(Ow.Orange, 55), 1.2f);
            Ow.Diamond(g, new PointF(734, 222), 90, Ow.Alpha(Ow.Blue, 35), 1f);
            if (icon != null) g.DrawImage(icon, new RectangleF(658, 146, 152, 152));

            if (!finish)
            {
                Ow.TrackedCenter(g, "BEADWATCH", Ow.HeroEn, Ow.TextHi, 734, 328, 3.4f);
                Ow.TrackedCenter(g, "v" + BuildInfo.Version + "  //  " + BuildInfo.Date, Ow.Tag, Ow.TextDim, 734, 356, 1.6f);
                using (var pen = new Pen(Ow.Alpha(Ow.Orange, 90), 1f)) g.DrawLine(pen, 648, 386, 820, 386);
                string[] names = new string[] { "三维", "拆件", "图纸" };
                float[] xs = new float[] { 676, 734, 792 };
                for (int i = 0; i < 3; i++)
                {
                    using (var b = new SolidBrush(Ow.Orange))
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(xs[i], 404), new PointF(xs[i] + 4, 408),
                            new PointF(xs[i], 412), new PointF(xs[i] - 4, 408),
                        });
                    var fmt = new StringFormat();
                    fmt.Alignment = StringAlignment.Center;
                    using (var b = new SolidBrush(Ow.Text))
                        g.DrawString(names[i], Ow.Small, b, new RectangleF(xs[i] - 36, 418, 72, 20), fmt);
                }
                Ow.TrackedCenter(g, "BEADWATCH  //  2026", Ow.BrandEn, Ow.Alpha(Ow.TextDim, 150), 734, 452, 1.4f);
            }
            else
            {
                Ow.TrackedCenter(g, "INSTALL COMPLETE", Ow.BrandEn, Ow.Orange, 734, 330, 2.4f);
                Ow.TrackedCenter(g, "拼豆先锋", Ow.Small, Ow.TextHi, 734, 352, 1f);
                using (var pen = new Pen(Ow.Alpha(Ow.Orange, 90), 1f)) g.DrawLine(pen, 648, 380, 820, 380);
                Ow.TrackedCenter(g, "BEADWATCH  //  v" + BuildInfo.Version, Ow.Tag, Ow.TextDim, 734, 394, 1.6f);
                Ow.TrackedCenter(g, "从一张照片开始。", Ow.Small, Ow.Alpha(Ow.Text, 190), 734, 424, 1f);
            }
        }

        // ───────────── 交互 ─────────────

        protected override Cursor CursorAt(Point p)
        {
            if (_page == 0)
            {
                if (PathRect.Contains(p) || BrowseRect.Contains(p) || DeskRect.Contains(p) || CancelBtn.Contains(p) || InstallBtn.Contains(p))
                    return Cursors.Hand;
            }
            else if (_page == 1)
            {
                if (ProgressCancelBtn.Contains(p)) return Cursors.Hand;
            }
            else if (_page == 2)
            {
                if (OpenBtn.Contains(p) || DoneBtn.Contains(p) || LaunchRect.Contains(p)) return Cursors.Hand;
            }
            return Cursors.Default;
        }

        protected override void PageMouseUp(Point p)
        {
            if (_page == 0)
            {
                if (BrowseRect.Contains(p) || PathRect.Contains(p)) { Browse(); return; }
                if (DeskRect.Contains(p)) { _desktop = !_desktop; return; }
                if (CancelBtn.Contains(p)) { Close(); return; }
                if (InstallBtn.Contains(p) && _payloadOk) { StartInstall(); return; }
            }
            else if (_page == 1)
            {
                if (ProgressCancelBtn.Contains(p) && !_cancel) AskCancel();
            }
            else if (_page == 2)
            {
                if (LaunchRect.Contains(p)) { _launch = !_launch; return; }
                if (OpenBtn.Contains(p)) { OpenFolder(); return; }
                if (DoneBtn.Contains(p)) { Finish(); return; }
            }
            Invalidate();
        }

        protected override void PageKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _page == 0) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && _page == 0 && _payloadOk) { StartInstall(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && _page == 2) { Finish(); e.Handled = true; }
        }

        protected override void CloseClicked()
        {
            if (_installing) { AskCancel(); return; }
            Close();
        }

        private void AskCancel()
        {
            Ask("取消安装？", "程序文件还没有全部释放。现在退出会清理本次已创建的文件（升级安装会保留原有版本），之后可以重新运行安装程序。",
                "取消安装", delegate { _cancel = true; _status = "正在取消…"; Invalidate(); });
        }

        private void Browse()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "选择拼豆先锋的安装位置";
                d.ShowNewFolderButton = true;
                try { if (Directory.Exists(_installDir)) d.SelectedPath = _installDir; } catch { }
                if (d.ShowDialog(this) == DialogResult.OK) _installDir = d.SelectedPath;
            }
        }

        private void StartInstall()
        {
            string dir;
            try { dir = Path.GetFullPath(_installDir); }
            catch { dir = ""; }
            if (dir.Length == 0 || dir.TrimEnd('\\').Length <= 2)
            {
                Alert("安装位置无效", "请选择一个具体的文件夹，例如：\n" + InstallEngine.DefaultDir(), "返回", null);
                return;
            }
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (dir.StartsWith(win, StringComparison.OrdinalIgnoreCase))
            {
                Alert("不能安装到系统目录", "请换一个位置，推荐使用默认目录：\n" + InstallEngine.DefaultDir(), "返回", null);
                return;
            }
            if (!_payloadOk)
            {
                Alert("安装包不完整", "没有找到内置的安装数据，请重新下载安装程序后再试。", "返回", null);
                return;
            }

            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                Alert("无法写入该位置", "换一个文件夹，或用默认目录（当前用户无需管理员权限）。\n\n" + ex.Message, "返回", null);
                return;
            }

            try
            {
                var di = new DriveInfo(Path.GetPathRoot(dir));
                long need = _totalBytes + 64L * 1024 * 1024;
                if (di.AvailableFreeSpace > 0 && di.AvailableFreeSpace < need)
                {
                    Alert("磁盘空间不足", "这个位置所在磁盘只剩 " + Mb(di.AvailableFreeSpace) + " MB，需要约 " + Mb(need) + " MB。", "返回", null);
                    return;
                }
            }
            catch { }

            _installDir = dir;
            if (InstallEngine.AnyRunning())
            {
                Ask("程序正在运行", "检测到拼豆先锋正在运行。安装前需要先关闭它（未保存的编辑可能丢失）。",
                    "关闭并继续", delegate
                    {
                        if (InstallEngine.CloseRunning()) BeginInstall();
                        else Alert("请手动关闭", "拼豆先锋仍在运行，请关闭后再点「开始安装」。", "返回", null);
                    });
                return;
            }
            BeginInstall();
        }

        private string _installDir = "";
        private readonly bool _preview;
        private readonly int _previewPage;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_installing)
            {
                e.Cancel = true;
                AskCancel();
                return;
            }
            base.OnFormClosing(e);
        }

        private void BeginInstall()
        {
            _page = 1;
            _installing = true;
            _cancel = false;
            _error = null;
            _status = "正在准备…";
            _doneBytes = 0;
            _doneFiles = 0;
            _totalFiles = _payloadCount;
            _finishDir = _installDir;
            _createdRoot = !Directory.Exists(_installDir);
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
                int files;
                long bytes;
                InstallEngine.Run(_installDir, _desktop, delegate(int done, int total, long b, string name)
                {
                    if ((DateTime.UtcNow - _lastReport).TotalMilliseconds < 60) return;
                    _lastReport = DateTime.UtcNow;
                    int d = done; long bb = b; string nm = name;
                    Post(delegate
                    {
                        _doneFiles = d;
                        _totalFiles = total;
                        _doneBytes = bb;
                        _status = nm;
                        Invalidate();
                    });
                }, delegate { return _cancel; }, out files, out bytes);

                Post(delegate
                {
                    _doneFiles = files;
                    _doneBytes = bytes;
                    _totalBytes = bytes;
                    _installing = false;
                    _page = 2;
                    _timer.Stop();
                    _launch = true;
                    Invalidate();
                });
            }
            catch (OperationCanceledException)
            {
                if (_createdRoot) InstallEngine.TryDeleteDir(_installDir);
                Post(delegate
                {
                    _installing = false;
                    _cancel = false;
                    _page = 0;
                    _timer.Stop();
                    Alert("已取消安装", _createdRoot
                        ? "本次安装创建的文件已清理，没有改动系统里的其他内容。"
                        : "安装已中断。原有版本的文件可能已被部分覆盖，建议重新运行安装程序完成修复。", "返回", null);
                });
            }
            catch (Exception ex)
            {
                if (_createdRoot) InstallEngine.TryDeleteDir(_installDir);
                Post(delegate
                {
                    _installing = false;
                    _page = 0;
                    _timer.Stop();
                    Alert("安装未完成", ex.Message + "\n\n可以换一个安装位置，或关闭杀毒软件后重试。", "返回", null);
                });
            }
        }

        private void Post(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch { }
        }

        private void Finish()
        {
            if (_launch)
            {
                string exe = Path.Combine(_finishDir, @"app\" + InstallEngine.AppExeName);
                try
                {
                    if (File.Exists(exe))
                        Process.Start(new ProcessStartInfo(exe)
                        {
                            WorkingDirectory = Path.GetDirectoryName(exe),
                            UseShellExecute = true,
                        });
                }
                catch { }
            }
            Close();
        }

        private void OpenFolder()
        {
            try { Process.Start("explorer.exe", "\"" + _finishDir + "\""); } catch { }
        }

        private static string Mb(long bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0") + " GB";
            return ((long)Math.Ceiling(mb)).ToString();
        }
    }
}

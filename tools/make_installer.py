"""
拼豆先锋 BEADWATCH 安装程序打包。

  1. dotnet publish（Release / 自包含 / win-x64）到 _scratch/installer/stage/app
  2. 用 .NET Framework 自带 csc 编译 卸载.exe（不依赖额外运行时）
  3. 组装 stage：app / assets / python / 示例 / 使用说明.md / 卸载.exe
  4. 编译安装器 Setup.exe（自绘安装界面）
  5. 把 stage 用 deflate 压缩后追加到安装器尾部，输出到 发行版/

用法：
  python tools/make_installer.py [版本号] [--skip-publish]

示例工程里「风铃挂件_图纸」约 187MB（导出的 PNG），属于演示产物，不塞进安装包；
其余示例（.doudou 工程与 AI 四视图）都会带上。userdata 永远不会被打包。
"""
import hashlib
import os
import shutil
import struct
import subprocess
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, "_scratch", "installer")
STAGE = os.path.join(WORK, "stage")
OUT_DIR = os.path.join(ROOT, "发行版")
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
ICON = os.path.join(ROOT, "src", "Doudou.WinUI", "Assets", "app.ico")
ICON_PNG = os.path.join(ROOT, "src", "Doudou.WinUI", "Assets", "app.png")
MANIFEST = os.path.join(ROOT, "tools", "installer", "app.manifest")
INSTALLER_SRC = os.path.join(ROOT, "tools", "installer")
EXCLUDE_EXAMPLES = {"风铃挂件_图纸"}


def run(*args, **kw):
    print(">", " ".join(str(a) for a in args), flush=True)
    subprocess.check_call(args, cwd=ROOT, **kw)


def csc(out, sources, extra=None):
    args = [
        CSC, "-nologo", "-target:winexe", "-optimize+", "-platform:anycpu",
        "-out:" + out,
        "-win32icon:" + ICON,
        "-resource:" + ICON + ",Beadwatch.icon.ico",
        "-resource:" + ICON_PNG + ",Beadwatch.icon.png",
        "-win32manifest:" + MANIFEST,
        "-r:System.dll", "-r:System.Core.dll", "-r:System.Drawing.dll", "-r:System.Windows.Forms.dll",
    ]
    if extra:
        args += extra
    args += sources
    run(*args)


def write_build_info(version, out):
    with open(out, "w", encoding="utf-8-sig", newline="") as f:
        f.write("namespace Beadwatch\n{\n")
        f.write("    internal static class BuildInfo\n    {\n")
        f.write('        public const string Version = "%s";\n' % version)
        f.write('        public const string Date = "%s";\n' % __import__("datetime").date.today().isoformat())
        f.write("    }\n}\n")


def copy_examples(dst):
    src = os.path.join(ROOT, "示例")
    if not os.path.isdir(src):
        print("  跳过示例（目录不存在）")
        return
    os.makedirs(dst, exist_ok=True)
    for name in os.listdir(src):
        if name in EXCLUDE_EXAMPLES:
            continue
        s = os.path.join(src, name)
        d = os.path.join(dst, name)
        if os.path.isdir(s):
            shutil.copytree(s, d, dirs_exist_ok=True)
        else:
            shutil.copy2(s, d)


def stage(version):
    app = os.path.join(STAGE, "app")
    if os.path.isdir(app):
        shutil.rmtree(app)
    os.makedirs(STAGE, exist_ok=True)
    run("dotnet", "publish", "src/Doudou.WinUI", "-c", "Release", "-r", "win-x64",
        "-p:Platform=x64", "--self-contained", "true", "-p:PublishReadyToRun=true",
        "-p:Version=" + version, "-o", app)
    # 调试符号不进安装包
    for d, _, files in os.walk(app):
        for fn in files:
            if fn.endswith(".pdb"):
                os.remove(os.path.join(d, fn))

    for name in ("assets", "python"):
        src = os.path.join(ROOT, name)
        dst = os.path.join(STAGE, name)
        if os.path.isdir(dst):
            shutil.rmtree(dst)
        if os.path.isdir(src):
            shutil.copytree(src, dst)
    # 混元代码只有 577KB，随安装包走；Python 运行时和模型由设置页按需下载
    hy = os.path.join(ROOT, "runtime", "hy3dgen")
    if os.path.isdir(hy):
        dst = os.path.join(STAGE, "runtime", "hy3dgen")
        if os.path.isdir(dst):
            shutil.rmtree(dst)
        shutil.copytree(hy, dst)
    copy_examples(os.path.join(STAGE, "示例"))
    shutil.copy2(os.path.join(ROOT, "README.md"), os.path.join(STAGE, "使用说明.md"))


def collect():
    entries = []
    for d, dirs, files in os.walk(STAGE):
        # userdata 里有用户设置和 API Key，任何情况下都不能进安装包
        dirs[:] = sorted(x for x in dirs if x != "userdata")
        for fn in sorted(files):
            full = os.path.join(d, fn)
            rel = os.path.relpath(full, STAGE).replace(os.sep, "/")
            if rel == "卸载.exe":
                continue   # 卸载器由编译产出，最后加入
            entries.append((rel, full))
    return entries


def build_payload(setup_exe, entries, out_exe):
    print("  压缩 %d 个文件 → %s" % (len(entries), os.path.basename(out_exe)))
    if os.path.exists(out_exe):
        os.remove(out_exe)
    shutil.copy2(setup_exe, out_exe)
    total_raw = 0
    total_zip = 0
    with open(out_exe, "r+b") as f:
        f.seek(0, os.SEEK_END)
        offset = f.tell()
        f.write(b"BDWA1")
        f.write(struct.pack("<i", len(entries)))
        for i, (rel, full) in enumerate(entries):
            data = open(full, "rb").read()
            co = zlib.compressobj(9, zlib.DEFLATED, -15)
            blob = co.compress(data) + co.flush()
            name = rel.encode("utf-8")
            f.write(struct.pack("<i", len(name)))
            f.write(name)
            f.write(struct.pack("<qq", len(data), len(blob)))
            f.write(blob)
            total_raw += len(data)
            total_zip += len(blob)
            if (i + 1) % 50 == 0 or i + 1 == len(entries):
                print("    %4d / %d" % (i + 1, len(entries)), flush=True)
        end = f.tell()
        f.write(b"BDWPAY1\n")
        f.write(struct.pack("<qq", offset, end - offset))
    print("  原始 %.1f MB → 压缩 %.1f MB" % (total_raw / 1048576, total_zip / 1048576))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    version = args[0] if args else "1.0"
    skip_publish = "--skip-publish" in sys.argv
    os.makedirs(WORK, exist_ok=True)
    os.makedirs(OUT_DIR, exist_ok=True)

    if not os.path.exists(CSC):
        raise SystemExit("找不到 .NET Framework 编译器：" + CSC)

    build_info = os.path.join(WORK, "BuildInfo.cs")
    write_build_info(version, build_info)

    if skip_publish and os.path.isdir(os.path.join(STAGE, "app")):
        print("使用已有发布输出")
    else:
        stage(version)

    uninstaller = os.path.join(STAGE, "卸载.exe")
    print("编译卸载器…")
    csc(uninstaller, [os.path.join(INSTALLER_SRC, "Ui.cs"),
                      os.path.join(INSTALLER_SRC, "Uninstall.cs"), build_info])

    print("编译安装器…")
    setup = os.path.join(WORK, "Setup.exe")
    csc(setup, [os.path.join(INSTALLER_SRC, "Ui.cs"),
                os.path.join(INSTALLER_SRC, "Setup.cs"), build_info])

    entries = collect()
    entries.append(("卸载.exe", uninstaller))
    entries.sort(key=lambda e: e[0].lower())

    out_exe = os.path.join(OUT_DIR, "拼豆先锋_v%s_安装程序.exe" % version)
    build_payload(setup, entries, out_exe)

    size = os.path.getsize(out_exe)
    h = hashlib.sha256(open(out_exe, "rb").read()).hexdigest()
    with open(out_exe + ".sha256", "w", encoding="utf-8", newline="") as f:
        f.write("%s  %s\n" % (h, os.path.basename(out_exe)))
    print("")
    print("完成：%s" % out_exe)
    print("大小：%.1f MB" % (size / 1048576.0))
    print("SHA256：%s" % h)


if __name__ == "__main__":
    main()

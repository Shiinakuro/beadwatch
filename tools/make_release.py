"""
拼豆先锋 BEADWATCH 发行版打包。
  完整版：程序 + 色卡 + 混元3D 运行时与模型（约 11GB，模型/运行时用硬链接，不额外占盘）
  精简版：程序 + 色卡 + 示例（zip，约 100MB；无本地混元，重建用 AI 视图 + 轮廓雕刻）
用法：python tools/make_release.py [版本号]
"""
import os, shutil, subprocess, sys, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VER = sys.argv[1] if len(sys.argv) > 1 else "1.0"
OUT = os.path.join(ROOT, "发行版")
FULL = os.path.join(OUT, f"拼豆先锋_v{VER}_完整版")
LITE = os.path.join(OUT, f"拼豆先锋_v{VER}_精简版")


def run(*args):
    print(">", " ".join(args), flush=True)
    subprocess.check_call(args, cwd=ROOT)


def copytree(src, dst, link=False):
    if not os.path.exists(src):
        print("  跳过（不存在）", src)
        return
    if link:
        # 同一磁盘上用硬链接，几秒完成且不占额外空间
        shutil.copytree(src, dst, copy_function=os.link, dirs_exist_ok=True)
    else:
        shutil.copytree(src, dst, dirs_exist_ok=True, ignore=shutil.ignore_patterns("__pycache__"))


LAUNCHER = '@echo off\r\nstart "" "%~dp0app\拼豆先锋.exe" %*\r\n'
SHORTCUT = r'''# 在桌面和开始菜单创建「拼豆先锋」快捷方式（右键 → 使用 PowerShell 运行）
$exe = Join-Path $PSScriptRoot "app\拼豆先锋.exe"
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath("Desktop"), (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"))) {
    $lnk = $shell.CreateShortcut((Join-Path $dir "拼豆先锋.lnk"))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = Split-Path $exe
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "拼豆先锋 BEADWATCH · 立体拼豆设计"
    $lnk.Save()
}
Write-Host "已创建桌面与开始菜单快捷方式"
'''


def stage(dst, full):
    # 保留用户数据（设置、API Key、工作目录）：只清掉程序与资源
    if os.path.exists(dst):
        for name in os.listdir(dst):
            if name == "userdata":
                continue
            p = os.path.join(dst, name)
            shutil.rmtree(p) if os.path.isdir(p) else os.remove(p)
    os.makedirs(dst, exist_ok=True)
    run("dotnet", "publish", "src/Doudou.WinUI", "-c", "Release", "-r", "win-x64", "-p:Platform=x64",
        "--self-contained", "true", "-p:PublishReadyToRun=true", f"-p:Version={VER}", "-o", os.path.join(dst, "app"))
    copytree(os.path.join(ROOT, "assets"), os.path.join(dst, "assets"))
    copytree(os.path.join(ROOT, "python"), os.path.join(dst, "python"))
    copytree(os.path.join(ROOT, "runtime", "hy3dgen"), os.path.join(dst, "runtime", "hy3dgen"))
    copytree(os.path.join(ROOT, "示例"), os.path.join(dst, "示例"))
    shutil.copy(os.path.join(ROOT, "README.md"), os.path.join(dst, "使用说明.md"))
    with open(os.path.join(dst, "启动拼豆先锋.cmd"), "w", encoding="gbk", newline="") as f:
        f.write(LAUNCHER)
    with open(os.path.join(dst, "创建快捷方式.ps1"), "w", encoding="utf-8-sig") as f:
        f.write(SHORTCUT)
    if full:
        copytree(os.path.join(ROOT, "runtime"), os.path.join(dst, "runtime"), link=True)
        copytree(os.path.join(ROOT, "models"), os.path.join(dst, "models"), link=True)


def zipdir(src, zpath):
    if os.path.exists(zpath):
        os.remove(zpath)
    base = os.path.basename(src)
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for d, _, files in os.walk(src):
            if "userdata" in os.path.relpath(d, src).split(os.sep):
                continue   # 不把个人设置（含 API Key）打进压缩包
            for fn in files:
                p = os.path.join(d, fn)
                z.write(p, os.path.join(base, os.path.relpath(p, src)))
    print("  ->", zpath, f"{os.path.getsize(zpath) / 1e6:.0f} MB")


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    stage(LITE, full=False)
    zipdir(LITE, LITE + ".zip")
    stage(FULL, full=True)
    print("完成：")
    print("  完整版目录", FULL)
    print("  精简版压缩包", LITE + ".zip")

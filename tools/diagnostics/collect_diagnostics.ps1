param(
    [string]$AppDir = ""
)

$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$work = Join-Path $env:TEMP ("beadwatch_diag_" + $stamp)
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Save-Text([string]$Name, [string]$Text) {
    $path = Join-Path $work $Name
    [System.IO.File]::WriteAllText($path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Capture([string]$Exe, [string[]]$Arguments) {
    if (-not $Exe) { return "not found" }
    try {
        return (& $Exe @Arguments 2>&1 | Out-String)
    } catch {
        return "ERROR: " + $_.Exception.Message
    }
}

function Find-AppDir([string]$Hint) {
    $candidates = @()
    if ($Hint) { $candidates += $Hint }
    $candidates += (Join-Path $env:LOCALAPPDATA "Programs\拼豆先锋")
    $candidates += (Join-Path $env:ProgramFiles "拼豆先锋")
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "拼豆先锋") }

    foreach ($dir in $candidates) {
        if ($dir -and (Test-Path (Join-Path $dir "app\拼豆先锋.exe"))) { return $dir }
        if ($dir -and (Test-Path (Join-Path $dir "拼豆先锋.exe"))) { return $dir }
    }

    try {
        $entries = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue
        $entries += Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue
        $entry = $entries | Where-Object { $_.DisplayName -like "*拼豆先锋*" -and $_.InstallLocation } | Select-Object -First 1
        if ($entry) {
            $dir = $entry.InstallLocation.Trim('"')
            if (Test-Path (Join-Path $dir "app\拼豆先锋.exe")) { return $dir }
            if (Test-Path (Join-Path $dir "拼豆先锋.exe")) { return $dir }
        }
    } catch { }

    return ""
}

$appDir = Find-AppDir $AppDir
$appExe = ""
$appVersion = "未找到"
if ($appDir) {
    $a = Join-Path $appDir "app\拼豆先锋.exe"
    $b = Join-Path $appDir "拼豆先锋.exe"
    if (Test-Path $a) { $appExe = $a } elseif (Test-Path $b) { $appExe = $b }
    if ($appExe) {
        $vi = (Get-Item $appExe).VersionInfo
        $appVersion = ($vi.FileVersion + " / " + $vi.ProductVersion)
    }
}

$gpu = Capture "nvidia-smi" @("--query-gpu=name,driver_version,compute_cap,memory.total", "--format=csv,noheader")
$gpuFull = Capture "nvidia-smi" @()
try { $video = (Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM | Format-List | Out-String) } catch { $video = $_.Exception.Message }
try { $os = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture | Format-List | Out-String) } catch { $os = $_.Exception.Message }
try { $cpu = (Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors | Format-List | Out-String) } catch { $cpu = $_.Exception.Message }
try { $mem = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1).ToString() + " GB" } catch { $mem = $_.Exception.Message }
$nvcc = Capture "nvcc" @("--version")
$cudaPath = "CUDA_PATH=" + $env:CUDA_PATH + "`r`nCUDA_HOME=" + $env:CUDA_HOME
$pathHits = (($env:PATH -split ';') | Where-Object { $_ -match 'cuda|nvidia|python' }) -join "`r`n"

Save-Text "system.txt" @"
生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
应用目录: $appDir
应用版本: $appVersion

=== GPU (nvidia-smi) ===
$gpu

=== GPU (Windows) ===
$video

=== 系统 ===
$os
$cpu
内存: $mem

=== CUDA / PATH ===
$cudaPath
$pathHits

=== nvcc ===
$nvcc
"@

Save-Text "nvidia-smi.txt" $gpuFull

$py = ""
if ($appDir) {
    $p1 = Join-Path $appDir "runtime\python\python.exe"
    $p2 = Join-Path $appDir "python\python.exe"
    if (Test-Path $p1) { $py = $p1 } elseif (Test-Path $p2) { $py = $p2 }
}

$torchOut = "未找到本地 Python 运行时（只安装了主程序也能收集显卡/系统信息）。"
if ($py) {
    $torchScript = Join-Path $work "torch_check.py"
    $pythonCode = @'
import os, sys, platform, traceback

print("python:", sys.version.replace("\n", " "))
print("executable:", sys.executable)

try:
    import torch
    print("torch:", torch.__version__)
    print("torch.version.cuda:", torch.version.cuda)
    print("torch.version.git_version:", getattr(torch.version, "git_version", ""))
    print("cuda_available:", torch.cuda.is_available())
    if torch.cuda.is_available():
        print("device_count:", torch.cuda.device_count())
        for i in range(torch.cuda.device_count()):
            print("device[%d]:" % i, torch.cuda.get_device_name(i), torch.cuda.get_device_capability(i))
        try:
            print("arch_list:", torch.cuda.get_arch_list())
        except Exception:
            traceback.print_exc()
        try:
            print("cudnn:", torch.backends.cudnn.version())
        except Exception as e:
            print("cudnn_error:", repr(e))
        try:
            print("bf16_supported:", torch.cuda.is_bf16_supported())
        except Exception as e:
            print("bf16_error:", repr(e))
        try:
            x = torch.randn(512, 512, device="cuda", dtype=torch.float16)
            y = x @ x
            torch.cuda.synchronize()
            print("SMOKE_OK:", float(y.float().abs().mean()))
        except Exception:
            print("SMOKE_FAILED")
            traceback.print_exc()
    try:
        import torchvision
        print("torchvision:", torchvision.__version__)
    except Exception:
        traceback.print_exc()
    print("--- torch.__config__.show() ---")
    try:
        print(torch.__config__.show())
    except Exception:
        traceback.print_exc()
except Exception:
    print("TORCH_IMPORT_FAILED")
    traceback.print_exc()
'@
    [System.IO.File]::WriteAllText($torchScript, $pythonCode, (New-Object System.Text.UTF8Encoding($false)))
    $oldEncoding = $env:PYTHONIOENCODING
    $env:PYTHONIOENCODING = "utf-8"
    $torchOut = Capture $py @($torchScript)
    if ($oldEncoding) { $env:PYTHONIOENCODING = $oldEncoding }

    Save-Text "pip-list.txt" (Capture $py @("-m", "pip", "list", "--format=freeze"))
    Save-Text "pip-check.txt" (Capture $py @("-m", "pip", "check"))
}

Save-Text "torch.txt" $torchOut

if ($appDir -and (Test-Path (Join-Path $appDir "userdata"))) {
    Get-ChildItem (Join-Path $appDir "userdata") -File -Filter "*.log" -ErrorAction SilentlyContinue |
        Copy-Item -Destination $work -Force
    Save-Text "app-files.txt" ((Get-ChildItem $appDir -Force | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String))
    Save-Text "userdata-files.txt" ((Get-ChildItem (Join-Path $appDir "userdata") -Force | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String))
    $models = Join-Path $appDir "models"
    if (Test-Path $models) {
        Save-Text "models.txt" ((Get-ChildItem $models -Recurse -File -ErrorAction SilentlyContinue | Select-Object FullName, Length | Format-Table -AutoSize | Out-String))
    }
    $site = Join-Path $appDir "runtime\python\Lib\site-packages"
    if (Test-Path $site) {
        Save-Text "site-packages.txt" ((Get-ChildItem $site -Directory | Where-Object { $_.Name -match 'torch|nvidia|cuda|onnx|rembg|hy3d' } | Select-Object Name | Format-Table -AutoSize | Out-String))
    }
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $manifest = Invoke-RestMethod -Uri "https://shiinakuro.github.io/beadwatch/deps/manifest.json" -TimeoutSec 15
    Save-Text "manifest-torch.json" ($manifest.torch | ConvertTo-Json -Depth 8)
} catch {
    Save-Text "manifest-torch.json" ("无法读取在线依赖清单: " + $_.Exception.Message)
}

$summary = @"
拼豆先锋诊断摘要
生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
应用目录: $appDir
应用版本: $appVersion
本地 Python: $py

=== 显卡 ===
$gpu

=== PyTorch / CUDA 自检 ===
$torchOut

=== 判断提示 ===
RTX 50 系计算能力为 12.0 (sm_120)。如果 arch_list 不含 sm_120，或出现 SMOKE_FAILED / no kernel image，
说明当前 PyTorch 的 CUDA 构建不包含 Blackwell 内核，需要 cu128 或更高版本的构建。
"@
Save-Text "summary.txt" $summary

$desktop = [Environment]::GetFolderPath("Desktop")
if (-not $desktop) { $desktop = (Get-Location).Path }
$zip = Join-Path $desktop ("拼豆先锋_诊断日志_" + $stamp + ".zip")
Compress-Archive -Path (Join-Path $work "*") -DestinationPath $zip -Force
Remove-Item $work -Recurse -Force

Write-Host ""
Write-Host "诊断日志已生成：" -ForegroundColor Green
Write-Host $zip -ForegroundColor Yellow
Write-Host ""
Write-Host "日志只包含显卡/驱动、Python/PyTorch 版本、CUDA 自检、应用版本与 *.log。"
Write-Host "不会收集 API Key、settings.json、工程文件、缓存图片或模型文件。"
Write-Host "请把这个 zip 发给开发者。"

# AppForge 单文件打包（自解压 SFX，零第三方依赖）
#   产物：dist\AppForge.exe  —— 一个文件，双击即用
# 原理：启动器(exe) + payload.zip + 16 字节尾标记（长度 + 魔数）
#       首次运行解压到 %LOCALAPPDATA%\AppForge\payload\<版本>\ 后启动真身
param(
    [string]$Config = "Release",
    # 可选：打包后直接用证书签名（彻底消除 SmartScreen 提示）
    [string]$SignThumbprint = "",
    [string]$SignPfx = "",
    [string]$SignPfxPassword = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$hostProj = Join-Path $root "src\AppForge.Host\AppForge.Host.csproj"
$launcherProj = Join-Path $root "src\AppForge.Launcher\AppForge.Launcher.csproj"

function Invoke-SignIfRequested([string]$target) {
    if ([string]::IsNullOrWhiteSpace($SignThumbprint) -and [string]::IsNullOrWhiteSpace($SignPfx)) { return }
    $signScript = Join-Path $PSScriptRoot "sign.ps1"
    $signParams = @{ Path = $target }
    if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) { $signParams.Thumbprint = $SignThumbprint }
    if (-not [string]::IsNullOrWhiteSpace($SignPfx)) { $signParams.Pfx = $SignPfx }
    if (-not [string]::IsNullOrWhiteSpace($SignPfxPassword)) { $signParams.PfxPassword = $SignPfxPassword }
    Write-Host "== 签名 $target ==" -ForegroundColor Cyan
    & $signScript @signParams
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$target" }
}

function Set-MediumIntegrity([string]$target) {
    if (-not (Test-Path -LiteralPath $target)) { return }
    try {
        $item = Get-Item -LiteralPath $target -Force
        $icacls = Join-Path $env:SystemRoot "System32\icacls.exe"
        if (-not (Test-Path -LiteralPath $icacls)) { return }
        if ($item.PSIsContainer) {
            & $icacls $target /setintegritylevel "(OI)(CI)Medium" /T /C 2>$null | Out-Null
        } else {
            & $icacls $target /setintegritylevel Medium 2>$null | Out-Null
        }
    } catch {
        Write-Host "  提示：未能调整完整性标签（$target），如打开目录报 Win32 5，请手工执行 icacls 命令。" -ForegroundColor Yellow
    }
}

$iconPath = Join-Path $root "build\AppForge.ico"
if (-not (Test-Path $iconPath)) {
    Write-Host "== 生成应用图标 ==" -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "make-icon.ps1")
}

Write-Host "== 构建宿主 ==" -ForegroundColor Cyan
& dotnet build $hostProj -c $Config --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "宿主构建失败" }

Write-Host "== 构建启动器 ==" -ForegroundColor Cyan
& dotnet build $launcherProj -c $Config --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "启动器构建失败" }

$hostOut = Join-Path $root "src\AppForge.Host\bin\$Config"
$launcherExe = Join-Path $root "src\AppForge.Launcher\bin\$Config\AppForge.Launcher.exe"
if (-not (Test-Path $launcherExe)) { throw "找不到启动器：$launcherExe" }

$dist = Join-Path $root "dist"
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Force -Path $dist | Out-Null }
$stage = Join-Path $dist "payload"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host "== 收集 payload（不含 pdb / dev wwwroot / WPF 桥）==" -ForegroundColor Cyan
Get-ChildItem $hostOut -File | Where-Object {
    ($_.Extension -eq '.exe' -or $_.Extension -eq '.dll' -or $_.Extension -eq '.config') -and
    $_.Name -notlike '*.pdb' -and
    $_.Name -notlike '*WebView2.Wpf.dll'
} | Copy-Item -Destination $stage -Force

$runtimes = Join-Path $hostOut "runtimes"
if (Test-Path $runtimes) { Copy-Item $runtimes $stage -Recurse -Force }

$payloadSize = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("  payload 文件：{0} 个，{1:N2} MB" -f (Get-ChildItem $stage -Recurse -File).Count, ($payloadSize / 1MB))

Write-Host "== 压缩 payload ==" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $dist "payload.zip"
# 必须先删：上一次打包中断（脚本报错/被 Ctrl+C）会留下 payload.zip，
# 而 CreateFromDirectory 遇到已存在的文件会直接抛异常 —— 那时 dist\AppForge.exe 已经被
# 覆盖成"只有启动器、没有内置数据"的残包，双击就报「单文件包已损坏」。
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force

Write-Host "== 拼装单文件 ==" -ForegroundColor Cyan
$outExe = Join-Path $dist "AppForge.exe"
# 先拼到一个临时文件，确认尾标记写完整了再替换正式产物：
# 中途失败时不会把已有的好 exe 换成半成品。
$tmpExe = Join-Path $dist "AppForge.exe.new"
Copy-Item $launcherExe $tmpExe -Force

$zipBytes = [System.IO.File]::ReadAllBytes($zip)
$stream = [System.IO.File]::Open($tmpExe, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
try {
    $stream.Write($zipBytes, 0, $zipBytes.Length)
    $stream.Write([System.BitConverter]::GetBytes([long]$zipBytes.Length), 0, 8)
    $stream.Write([System.Text.Encoding]::ASCII.GetBytes('AFSFX001'), 0, 8)
} finally {
    $stream.Close()
}
Remove-Item $zip -Force

# 自检：临时包必须"启动器 + payload + 16 字节尾标记"
$expect = (Get-Item $launcherExe).Length + $zipBytes.Length + 16
$actual = (Get-Item $tmpExe).Length
if ($actual -ne $expect) {
    Remove-Item $tmpExe -Force -ErrorAction SilentlyContinue
    throw ("拼装失败：期望 {0} 字节，实际 {1} 字节（dist\AppForge.exe 未被替换）" -f $expect, $actual)
}
Move-Item $tmpExe $outExe -Force

Invoke-SignIfRequested $outExe

$finalSize = (Get-Item $outExe).Length

# 一次性解除 SmartScreen 的辅助脚本（Windows 对"从网上下载 + 未签名"的程序会拦一次，
# 程序自身启动时会清掉该标记，这个 cmd 让用户第一下也能绕过并顺手清理）
$helper = Join-Path $dist "首次运行-点我（解除SmartScreen提示）.cmd"
$helperLines = @(
    '@echo off',
    'setlocal',
    'set "EXE=%~dp0AppForge.exe"',
    'if not exist "%EXE%" (',
    '  echo AppForge.exe not found next to this .cmd file.',
    '  pause',
    '  exit /b 1',
    ')',
    'rem Clear the "downloaded from the Internet" mark (Zone.Identifier) that makes',
    'rem Windows SmartScreen show "Windows protected your PC" on every launch.',
    'powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -Path ''%~dp0*'' -Include *.exe,*.dll -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1',
    'start "" "%EXE%"'
)
Set-Content -Path $helper -Value ($helperLines -join "`r`n") -Encoding ASCII

# 沙箱/同步目录可能把文件标成 Low 完整性；低完整性进程调用 ShellExecuteEx 会直接 Win32 5，
# 表现就是“打开目录打不开”。产物必须显式提升到 Medium。
Set-MediumIntegrity $dist
Set-MediumIntegrity $outExe

Write-Host ""
Write-Host ("✅ 单文件已生成：{0}" -f $outExe) -ForegroundColor Green
Write-Host ("   大小：{0:N2} MB（目标 ≤5MB）" -f ($finalSize / 1MB))
Write-Host ("   辅助脚本：{0}" -f $helper) -ForegroundColor Gray
Write-Host "   验证：dist\AppForge.exe --selftest --data-dir=<目录>" -ForegroundColor Gray

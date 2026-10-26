# AppForge 单文件打包（自解压 SFX，零第三方依赖）
#   产物：dist\AppForge.exe  —— 一个文件，双击即用
# 原理：启动器(exe) + payload.zip + 16 字节尾标记（长度 + 魔数）
#       首次运行解压到 %LOCALAPPDATA%\AppForge\payload\<版本>\ 后启动真身
param(
    [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$hostProj = Join-Path $root "src\AppForge.Host\AppForge.Host.csproj"
$launcherProj = Join-Path $root "src\AppForge.Launcher\AppForge.Launcher.csproj"

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
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force

Write-Host "== 拼装单文件 ==" -ForegroundColor Cyan
$outExe = Join-Path $dist "AppForge.exe"
Copy-Item $launcherExe $outExe -Force

$zipBytes = [System.IO.File]::ReadAllBytes($zip)
$stream = [System.IO.File]::Open($outExe, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
try {
    $stream.Write($zipBytes, 0, $zipBytes.Length)
    $stream.Write([System.BitConverter]::GetBytes([long]$zipBytes.Length), 0, 8)
    $stream.Write([System.Text.Encoding]::ASCII.GetBytes('AFSFX001'), 0, 8)
} finally {
    $stream.Close()
}
Remove-Item $zip -Force

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

Write-Host ""
Write-Host ("✅ 单文件已生成：{0}" -f $outExe) -ForegroundColor Green
Write-Host ("   大小：{0:N2} MB（目标 ≤5MB）" -f ($finalSize / 1MB))
Write-Host ("   辅助脚本：{0}" -f $helper) -ForegroundColor Gray
Write-Host "   验证：dist\AppForge.exe --selftest --data-dir=<目录>" -ForegroundColor Gray

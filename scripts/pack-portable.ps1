# 生成绿色便携包（推荐分发方式）：
#   dist\AppForge-portable.zip -> 解压到任意目录，双击 AppForge.exe 即可
# 为什么推荐：不涉及自解压/追加数据，杀毒软件与 SmartScreen 误判概率最低
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

Write-Host "== 构建宿主 ==" -ForegroundColor Cyan
& dotnet build $hostProj -c $Config --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$hostOut = Join-Path $root "src\AppForge.Host\bin\$Config"
$dist = Join-Path $root "dist"
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Force -Path $dist | Out-Null }
$stage = Join-Path $dist "portable"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Get-ChildItem $hostOut -File | Where-Object {
    ($_.Extension -eq '.exe' -or $_.Extension -eq '.dll' -or $_.Extension -eq '.config') -and
    $_.Name -notlike '*.pdb' -and
    $_.Name -notlike '*WebView2.Wpf.dll'
} | Copy-Item -Destination $stage -Force

$runtimes = Join-Path $hostOut "runtimes"
if (Test-Path $runtimes) { Copy-Item $runtimes $stage -Recurse -Force }

$readmeLines = @(
    'AppForge 造物台（绿色便携版）',
    '================================',
    '',
    '1. 双击 AppForge.exe 启动（免安装、不需要管理员权限）。',
    '   如果 Windows 弹出「已保护你的电脑 / 发布者未知」（SmartScreen）：',
    '   · 最快：双击同目录的「首次运行-点我（解除SmartScreen提示）.cmd」；',
    '   · 或：右键 AppForge.exe → 属性 → 勾选「解除锁定」→ 确定；',
    '   · 或：命令行执行  AppForge.exe --unblock',
    '   这是因为程序没有代码签名证书（不是病毒）；解除一次后不再提示。',
    '2. 首次使用：设置 -> 粘贴 API Key -> 保存密钥 -> 测试连接。',
    '3. 数据默认在 %LOCALAPPDATA%\AppForge；该目录不可写时自动改用 %APPDATA%\AppForge',
    '   （日志第一行会写明实际用的目录，设置页「数据目录」里也能看到）。',
    '   如需强制便携模式，在 exe 同目录新建 portable.flag 文件。',
    '4. 装火绒/360 的机器如果「打开目录」打不开：把本目录加入安全软件信任区。',
    '   自查命令：AppForge.exe --open-folder="%LOCALAPPDATA%\AppForge"',
    '   （输出的 stages 会显示是哪一级被拦；也不用担心，程序会直接用应用内文件列表兜底）',
    '5. 详细说明见项目内 docs\06-用户手册.md；已知问题见 docs\07-已知问题与限制.md。',
    '6. 卸载：先在设置里关闭开机自启，再删除本目录与 %LOCALAPPDATA%\AppForge。',
    '   想要彻底不弹 SmartScreen：需要代码签名证书（见 scripts\sign.ps1）。'
)
$readme = $readmeLines -join [Environment]::NewLine
Set-Content -Path (Join-Path $stage "使用说明.txt") -Value $readme -Encoding UTF8

# 一次性解除 SmartScreen 的辅助脚本（内容与单文件版一致，ASCII 以免 cmd 编码问题）
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
Set-Content -Path (Join-Path $stage "首次运行-点我（解除SmartScreen提示）.cmd") -Value ($helperLines -join "`r`n") -Encoding ASCII

# 必须在压缩前签名：签名会改写 exe，压缩后再签就需要解开 zip 重新打包。
Invoke-SignIfRequested (Join-Path $stage "AppForge.exe")

$zip = Join-Path $dist "AppForge-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force

Set-MediumIntegrity $dist
Set-MediumIntegrity $zip

Write-Host ""
Write-Host ("便携包已生成：" + $zip + "（" + [math]::Round((Get-Item $zip).Length / 1MB, 2) + " MB）") -ForegroundColor Green
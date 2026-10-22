<#
  AppForge 造物台 · 创建桌面快捷方式
  ---------------------------------------------------------------
  用法（一般由根目录的「创建桌面快捷方式.cmd」双击调用）：
    scripts\create-shortcut.ps1                # 桌面快捷方式
    scripts\create-shortcut.ps1 -StartMenu     # 同时加开始菜单
    scripts\create-shortcut.ps1 -Uninstall     # 删除快捷方式
#>
[CmdletBinding()]
param(
    [switch]$StartMenu,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Item($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Tip($text) { Write-Host "    $text" -ForegroundColor Yellow }
function Write-Fail($text) { Write-Host "    $text" -ForegroundColor Red }

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }

$project = Join-Path $root "src\AppForge.Host\AppForge.Host.csproj"
$exe = Join-Path $root "src\AppForge.Host\bin\Release\AppForge.exe"
$linkName = "AppForge 造物台.lnk"

$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) $linkName
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) $linkName

if ($Uninstall) {
    Write-Step "删除快捷方式"
    foreach ($p in @($desktopLink, $startMenuLink)) {
        if (Test-Path $p) { Remove-Item $p -Force; Write-Item "已删除：$p" }
    }
    exit 0
}

# 没有产物就先构建
if (-not (Test-Path $exe)) {
    Write-Step "首次使用，先构建产物"
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Fail "找不到 .NET SDK（dotnet）。请先双击「启动AppForge.cmd」或在有 SDK 的机器上构建。"
        exit 1
    }
    & dotnet build $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Fail "构建失败"; exit 1 }
    if (-not (Test-Path $exe)) { Write-Fail "构建完成但找不到产物：$exe"; exit 1 }
    Write-Item "构建完成"
}

function New-AppLink([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path $exe -Parent
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = "AppForge 造物台 · AI 现场写软件"
    $shortcut.WindowStyle = 1
    $shortcut.Save()
}

Write-Step "创建桌面快捷方式"
New-AppLink $desktopLink
Write-Item $desktopLink

if ($StartMenu) {
    Write-Step "创建开始菜单快捷方式"
    New-AppLink $startMenuLink
    Write-Item $startMenuLink
}

Write-Host ""
Write-Item "完成。之后双击桌面上的「AppForge 造物台」即可启动（不弹控制台、不需要构建）。"
Write-Host "  如需删除：scripts\create-shortcut.ps1 -Uninstall" -ForegroundColor DarkGray
exit 0

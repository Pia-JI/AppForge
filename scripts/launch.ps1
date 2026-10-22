<#
  AppForge 造物台 · 一键启动
  ---------------------------------------------------------------
  行为：
    1. 检查产物是否存在 / 源码是否比产物新（是则增量构建）
    2. 启动 AppForge.exe（GUI 免安装）
    3. 失败时给出可操作的提示，不静默失败

  用法（一般由根目录的「启动AppForge.cmd」双击调用）：
    scripts\launch.ps1                     # 构建（按需）+ 启动
    scripts\launch.ps1 -NoBuild            # 只启动
    scripts\launch.ps1 -ForceBuild         # 强制重新构建
    scripts\launch.ps1 -BuildOnly          # 只构建，不启动
    scripts\launch.ps1 -DataDir D:\afdata  # 指定数据目录
    scripts\launch.ps1 -OpenApp apdemo0001 # 启动后直接打开某个应用
    scripts\launch.ps1 -DryRun             # 演练：只打印将要做什么
#>
[CmdletBinding()]
param(
    [string]$DataDir = "",
    [string]$OpenApp = "",
    [switch]$ForceBuild,
    [switch]$NoBuild,
    [switch]$DryRun,
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Item($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Tip($text) { Write-Host "    $text" -ForegroundColor Yellow }
function Write-Fail($text) { Write-Host "    $text" -ForegroundColor Red }

# 项目根目录（本脚本在 scripts\ 下）
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }

$project = Join-Path $root "src\AppForge.Host\AppForge.Host.csproj"
$exe = Join-Path $root "src\AppForge.Host\bin\Release\AppForge.exe"

function Test-NeedsBuild {
    if (-not (Test-Path $exe)) { return $true }

    $exeTime = (Get-Item $exe).LastWriteTimeUtc
    $dirs = @(
        (Join-Path $root "src\AppForge.Host"),
        (Join-Path $root "src\AppForge.Shared")
    ) | Where-Object { Test-Path $_ }

    if (-not $dirs) { return $false }

    $patterns = @("*.cs", "*.csproj", "*.manifest", "*.html", "*.css", "*.js")
    $newer = Get-ChildItem -Path $dirs -Recurse -File -Include $patterns -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $exeTime } |
        Select-Object -First 1

    return ($null -ne $newer)
}

Write-Step "AppForge 造物台 · 一键启动"

$needBuild = $false
if (-not $NoBuild) {
    if ($ForceBuild) { $needBuild = $true } else { $needBuild = Test-NeedsBuild }
}

if ($needBuild) {
    # 旧实例会占用 bin 下的 dll，导致构建失败 → 先优雅关闭
    $running = Get-Process -Name AppForge -ErrorAction SilentlyContinue
    if ($running) {
        Write-Step "检测到旧实例在运行，先关闭以便更新"
        $running | ForEach-Object {
            try { $_.CloseMainWindow() | Out-Null } catch { /* 忽略 */ }
        }
        Start-Sleep -Milliseconds 900
        $still = Get-Process -Name AppForge -ErrorAction SilentlyContinue
        if ($still) {
            $still | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 700
        }
        Write-Item "旧实例已关闭"
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Fail "找不到 .NET SDK（dotnet 命令）"
        Write-Tip "首次构建需要 .NET SDK：https://dotnet.microsoft.com/download"
        Write-Tip "如果已经有构建产物，可加 -NoBuild 直接启动。"
        exit 1
    }

    Write-Step "构建中（首次约 10 秒）…"
    if ($DryRun) {
        Write-Item "（演练）将执行：dotnet build `"$project`" -c Release"
    } else {
        & dotnet build $project -c Release --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "构建失败（错误见上方输出）"
            exit 1
        }
        Write-Item "构建完成"
    }
} else {
    Write-Item "产物已是最新，跳过构建"
}

if (-not (Test-Path $exe)) {
    Write-Fail "找不到可执行文件：$exe"
    exit 1
}

if ($BuildOnly) {
    Write-Item "仅构建：完成（$exe）"
    exit 0
}

$launchArgs = @()
if ($DataDir) { $launchArgs += "--data-dir=$DataDir" }
if ($OpenApp) { $launchArgs += "--open-app=$OpenApp" }

Write-Step "启动 AppForge…"
if ($DryRun) {
    $suffix = if ($launchArgs.Count -gt 0) { " " + ($launchArgs -join " ") } else { "" }
    Write-Item ("（演练）将启动：" + $exe + $suffix)
    exit 0
}

try {
    if ($launchArgs.Count -gt 0) {
        Start-Process -FilePath $exe -ArgumentList $launchArgs -ErrorAction Stop | Out-Null
    } else {
        Start-Process -FilePath $exe -ErrorAction Stop | Out-Null
    }
} catch {
    Write-Tip "Start-Process 不可用（$($_.Exception.Message)），改用直接启动"
    & $exe @launchArgs
}

$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "AppForge"
Write-Item "已启动"
Write-Host ""
Write-Host "  数据目录：$dataRoot" -ForegroundColor DarkGray
Write-Host "  首次使用：点右上角 ⚙ → 粘贴 API Key → 保存密钥 → 测试连接" -ForegroundColor DarkGray
Write-Host "  若窗口未出现：查看 $dataRoot\logs\app-*.log" -ForegroundColor DarkGray
exit 0

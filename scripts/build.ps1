# AppForge 构建脚本（只用 .NET SDK 与 PowerShell，无其他依赖）
# 用法：
#   .\scripts\build.ps1              # Release 构建
#   .\scripts\build.ps1 -Config Debug
#   .\scripts\build.ps1 -Test        # 构建后跑 --selftest
param(
    [string]$Config = "Release",
    [switch]$Test,
    [switch]$Bench
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$hostProj = Join-Path $root "src\AppForge.Host\AppForge.Host.csproj"

Write-Host "== 构建 AppForge ($Config) ==" -ForegroundColor Cyan
& dotnet build $hostProj -c $Config --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$exe = Join-Path $root "src\AppForge.Host\bin\$Config\AppForge.exe"
if (-not (Test-Path $exe)) { throw "找不到产物：$exe" }

$size = [math]::Round((Get-ChildItem (Split-Path $exe) -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "产物：$exe（目录合计 $size MB）" -ForegroundColor Green

function Invoke-HostCli([string]$arguments, [string]$label) {
    # 宿主是 WinExe，直接 & 调用不会等待、也拿不到退出码；用 Start-Process 等待并接管输出。
    $log = Join-Path $env:TEMP ("appforge-" + $label + "-" + [guid]::NewGuid().ToString("N") + ".log")
    $proc = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $log -WindowStyle Hidden
    if (Test-Path -LiteralPath $log) {
        Get-Content -LiteralPath $log -Encoding UTF8
        try { [IO.File]::Delete($log) } catch { /* 忽略 */ }
    }
    if ($proc.ExitCode -ne 0) { throw "$label 失败（退出码 $($proc.ExitCode)）" }
}

if ($Test) {
    Write-Host "`n== 自检 ==" -ForegroundColor Cyan
    Invoke-HostCli "--selftest" "selftest"
}

if ($Bench) {
    Write-Host "`n== 启动基准 ==" -ForegroundColor Cyan
    Invoke-HostCli "--bench" "bench"
}

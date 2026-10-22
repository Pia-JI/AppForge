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
$host = Join-Path $root "src\AppForge.Host\AppForge.Host.csproj"

Write-Host "== 构建 AppForge ($Config) ==" -ForegroundColor Cyan
& dotnet build $host -c $Config --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$exe = Join-Path $root "src\AppForge.Host\bin\$Config\AppForge.exe"
if (-not (Test-Path $exe)) { throw "找不到产物：$exe" }

$size = [math]::Round((Get-ChildItem (Split-Path $exe) -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "产物：$exe（目录合计 $size MB）" -ForegroundColor Green

if ($Test) {
    Write-Host "`n== 自检 ==" -ForegroundColor Cyan
    & $exe --selftest
    if ($LASTEXITCODE -ne 0) { throw "自检失败" }
}

if ($Bench) {
    Write-Host "`n== 启动基准 ==" -ForegroundColor Cyan
    & $exe --bench
}

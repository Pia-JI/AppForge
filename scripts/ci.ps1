# 本地 CI：构建 → 无界面自检 → UI 静态检查 → 启动基准
# 用法：.\scripts\ci.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\AppForge.Host\AppForge.Host.csproj"
$exe = Join-Path $root "src\AppForge.Host\bin\Release\AppForge.exe"
$dataDir = Join-Path $root "dev-data"
$failed = $false

function Step($name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }

Step "构建 Release"
& dotnet build $project -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

Step "无界面自检（--selftest）"
$p = Start-Process -FilePath $exe -ArgumentList @("--selftest", "--data-dir=$dataDir") -Wait -PassThru
Write-Host "exit=$($p.ExitCode)"
if ($p.ExitCode -ne 0) { $failed = $true }

$selftestLog = Join-Path $dataDir "logs\cli-selftest.log"
if (Test-Path $selftestLog) {
    Get-Content $selftestLog -Encoding UTF8 | Select-Object -Last 3
}

Step "UI 静态检查（语法 / import / 外链）"
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    & node --experimental-vm-modules (Join-Path $root "scripts\check-ui.mjs")
    if ($LASTEXITCODE -ne 0) { $failed = $true }
} else {
    Write-Host "未找到 node，跳过 UI 静态检查（不影响应用运行）" -ForegroundColor Yellow
}

Step "启动基准（--bench，需真实桌面会话）"
$b = Start-Process -FilePath $exe -ArgumentList @("--bench", "--data-dir=$dataDir") -Wait -PassThru
Write-Host "exit=$($b.ExitCode)"
$benchLog = Join-Path $dataDir "logs\cli-bench.log"
if (Test-Path $benchLog) {
    $bench = Get-Content $benchLog -Encoding UTF8 -Raw
    Write-Host $bench
    if ($bench -match '"gui":"unavailable"') {
        Write-Host "提示：当前无桌面会话，GUI 无法验证（属预期）。请在桌面上运行 run.ps1 实测首帧耗时。" -ForegroundColor Yellow
    } elseif ($b.ExitCode -ne 0) { $failed = $true }
}

if ($failed) { Write-Host "`nCI 失败" -ForegroundColor Red; exit 1 }
Write-Host "`nCI 通过" -ForegroundColor Green

# 开发用快捷方式：等价于 scripts\launch.ps1（保留旧入口，避免两处逻辑分叉）
#   .\scripts\run.ps1                 # 按需构建 + 启动
#   .\scripts\run.ps1 -NoBuild        # 只启动
#   .\scripts\run.ps1 -DataDir D:\af  # 指定数据目录
#   .\scripts\run.ps1 -OpenApp apdemo0001
param(
    [switch]$NoBuild,
    [string]$DataDir = "",
    [string]$OpenApp = ""
)

$launch = Join-Path $PSScriptRoot "launch.ps1"
& $launch -NoBuild:$NoBuild -DataDir $DataDir -OpenApp $OpenApp
exit $LASTEXITCODE

# AppForge 环境自检：定位"打开目录打不开"和"提示风险"到底卡在哪
#
# 用法（在仓库根目录）：
#   .\scripts\diagnose-env.ps1
#   .\scripts\diagnose-env.ps1 -Exe "C:\path\to\AppForge.exe"
#
# 只读诊断：不改注册表、不改文件、不联网。

param(
    [string]$Exe = ""
)

$ErrorActionPreference = "Continue"

# AppForge 的 CLI 输出是 UTF-8；不设这一行，中文在 GBK 控制台下会变乱码、JSON 也解析不了
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Section($title) {
    Write-Host ""
    Write-Host ("=== " + $title + " " + ("=" * [Math]::Max(0, 60 - $title.Length))) -ForegroundColor Cyan
}

# ---------------- 1. 目标文件 ----------------
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $candidates = @(
        (Join-Path $root "dist\AppForge.exe"),
        (Join-Path $root "dist\AppForge-portable\AppForge.exe"),
        (Join-Path $root "src\AppForge.Host\bin\Release\AppForge.exe")
    ) | Where-Object { Test-Path $_ }
    if ($candidates.Count -eq 0) {
        Write-Host "找不到 AppForge.exe，请用 -Exe 指定路径。" -ForegroundColor Yellow
        return
    }
    $Exe = $candidates[0]
}

Section "目标程序"
Write-Host ("路径：" + $Exe)

$item = Get-Item $Exe
Write-Host ("大小：" + [math]::Round($item.Length / 1MB, 2) + " MB")
try {
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe)
    Write-Host ("版本：" + $vi.FileVersion + " / 产品：" + $vi.ProductName)
} catch { }

# ---------------- 2. 是否带"来自 Internet"标记（SmartScreen 的直接触发条件） ----------------
Section "Mark of the Web（下载标记）"
$zone = $null
try { $zone = Get-Content -Path $Exe -Stream Zone.Identifier -ErrorAction SilentlyContinue } catch { }
if ($zone) {
    Write-Host "带下载标记 → Windows SmartScreen 会拦（与是否病毒无关）" -ForegroundColor Yellow
    $zone | ForEach-Object { Write-Host ("  " + $_) }
    Write-Host "  处理：Unblock-File '$Exe'    （或 AppForge.exe --unblock）" -ForegroundColor Yellow
}
else {
    Write-Host "没有下载标记 → SmartScreen 的「已保护你的电脑」不应该出现。" -ForegroundColor Green
    Write-Host "  如果仍然弹风险提示，看下面「安全软件」一节：多半是第三方安全软件，不是 Windows。"
}

# ---------------- 3. 数字签名 ----------------
Section "数字签名"
try {
    $sig = Get-AuthenticodeSignature -FilePath $Exe
    Write-Host ("状态：" + $sig.Status)
    if ($sig.SignerCertificate) {
        Write-Host ("签名者：" + $sig.SignerCertificate.Subject)
        Write-Host ("有效期至：" + $sig.SignerCertificate.NotAfter)
    }
    else {
        Write-Host "未签名 —— 想彻底不弹 SmartScreen 只能买证书签名（推荐 EV：签名即生效）" -ForegroundColor Yellow
        Write-Host "  本仓库已带工具：.\scripts\sign.ps1 -Thumbprint <证书指纹>" -ForegroundColor Gray
    }
}
catch { Write-Host ("读取签名失败：" + $_.Exception.Message) }

# ---------------- 4. Windows 侧的两个开关 ----------------
Section "Windows 侧开关"
try {
    $s = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy" -Name VerifiedAndReputablePolicyState -ErrorAction SilentlyContinue
    $sac = if ($null -eq $s) { "未知" } else { $s.VerifiedAndReputablePolicyState }
    $sacText = switch ($sac) { 0 { "关闭（不会因此拦人）" } 1 { "强制（未签名/无信誉的程序会被直接拦，只能签名或关闭）" } 2 { "评估模式" } default { "未知" } }
    Write-Host ("Smart App Control：" + $sac + " —— " + $sacText) -ForegroundColor $(if ($sac -eq 1) { "Yellow" } else { "Gray" })
}
catch { Write-Host "Smart App Control：读取失败" }

try {
    $p = Get-MpPreference -ErrorAction Stop
    Write-Host ("Defender 受控文件夹访问：" + $p.EnableControlledFolderAccess + "（1 = 开启，会拦未受信任程序写文件）")
    $folders = $p.ControlledFolderAccessProtectedFolders
    if ($folders) { Write-Host ("  受保护目录：" + ($folders -join "; ")) }
}
catch { Write-Host "Defender 偏好读取失败（可能不是管理员）" }

try {
    $ah = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\AppHost" -Name EnableWebContentEvaluation -ErrorAction SilentlyContinue
    if ($null -eq $ah) {
        Write-Host "SmartScreen「检查应用和文件」：默认开启（未显式改过）" -ForegroundColor Yellow
    }
    else {
        Write-Host ("SmartScreen「检查应用和文件」：EnableWebContentEvaluation=" + $ah.EnableWebContentEvaluation + "（0 = 已关闭）")
    }
    Write-Host "  想在本机关掉它（仅本机，会降低整体防护）：" -ForegroundColor Gray
    Write-Host "  设置 → 隐私和安全性 → Windows 安全中心 → 应用和浏览器控制 → 基于声誉的保护 → 设置 → 关闭「检查应用和文件」" -ForegroundColor Gray
}
catch { Write-Host "SmartScreen 开关读取失败" }

# ---------------- 5. 第三方安全软件（这才是最常见的元凶） ----------------
Section "安全软件"
$known = @(
    @{ Name = "火绒"; Proc = @("HipsTray", "HipsDaemon", "HipsMain"); Path = @("$env:ProgramFiles\Huorong", "${env:ProgramFiles(x86)}\Huorong") },
    @{ Name = "360 安全卫士"; Proc = @("360tray", "ZhuDongFangYu", "360rp"); Path = @("$env:ProgramFiles\360", "${env:ProgramFiles(x86)}\360") },
    @{ Name = "腾讯电脑管家"; Proc = @("QQPCTray", "QQPCRTP"); Path = @("$env:ProgramFiles\Tencent\QQPCMgr") },
    @{ Name = "联想电脑管家"; Proc = @("wsctrl11", "usysdiag", "LPMgrTray"); Path = @("$env:ProgramFiles\Lenovo\PCManager", "${env:ProgramFiles(x86)}\Lenovo\PCManager") }
)

$found = $false
foreach ($p in $known) {
    $running = @()
    foreach ($n in $p.Proc) {
        $proc = Get-Process -Name $n -ErrorAction SilentlyContinue
        if ($proc) { $running += $n }
    }
    $installed = @()
    foreach ($path in $p.Path) {
        if ($path -and (Test-Path $path)) { $installed += $path }
    }
    if ($running.Count -gt 0 -or $installed.Count -gt 0) {
        $found = $true
        $state = if ($running.Count -gt 0) { "运行中（" + ($running -join ", ") + "）" } else { "已安装，未运行" }
        Write-Host ($p.Name + "：" + $state) -ForegroundColor Yellow
        if ($installed.Count -gt 0) { Write-Host ("  安装位置：" + ($installed -join "; ")) -ForegroundColor Gray }
    }
}
if (-not $found) { Write-Host "未发现已知的第三方安全软件（只靠 Windows Defender）" -ForegroundColor Green }

Write-Host ""
Write-Host "火绒/360/联想管家 这类软件默认会："
Write-Host "  · 拦未签名程序的「创建文件」（表现为 AppData 下写不进去）"
Write-Host "  · 拦「创建资源管理器进程」（表现为打开目录失败、退出码 0xC0000142）"
Write-Host "  · 自己弹一个「发现风险程序」的框（长得像 Windows 的提示，但不是）"
Write-Host "处理：在该软件的「信任区 / 白名单 / 自定义防护」里把 AppForge.exe（或整个目录）加进去。" -ForegroundColor Yellow

# ---------------- 6. 能不能创建进程 / 叫起资源管理器 ----------------
Section "进程与外壳能力（--shell-diag）"
Push-Location $root
try {
    $tmp = Join-Path $env:TEMP ("appforge-shelldiag-" + [Guid]::NewGuid().ToString("N") + ".json")
    & $Exe --shell-diag 2>&1 | Out-File -FilePath $tmp -Encoding UTF8
    $raw = Get-Content -Path $tmp -Raw -Encoding UTF8
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue

    $sd = $null
    try { $sd = $raw | ConvertFrom-Json } catch { }
    if ($sd) {
        Write-Host ("用户：" + $sd.user + "   管理员：" + $sd.elevated + "   会话：" + $sd.sessionId)
        Write-Host ("能创建进程（cmd 探针）：" + $sd.canSpawnProcess + "（退出码 " + $sd.probeExitCode + "，期望 7）") -ForegroundColor $(if ($sd.canSpawnProcess) { "Green" } else { "Red" })
        Write-Host ("能启动 explorer.exe：" + $sd.canSpawnExplorer + "（退出码 " + $sd.explorerExitCode + "）") -ForegroundColor $(if ($sd.canSpawnExplorer) { "Green" } else { "Red" })
        Write-Host ("shell32 打开目录接口：" + $sd.shellApiOk + "  " + $sd.shellApiError)

        Write-Host ""
        if (-not $sd.canSpawnProcess) {
            Write-Host "结论：本进程被**禁止创建任何进程**（HIPS/EDR 拦死）→ 只能把 AppForge.exe 加入安全软件白名单。" -ForegroundColor Yellow
        }
        elseif (-not $sd.canSpawnExplorer) {
            Write-Host "结论：普通进程（cmd）能起，**只有 explorer.exe 起不来**（0xC0000142）→ 有针对资源管理器的封堵。" -ForegroundColor Yellow
            Write-Host "  · 若把 AppForge 加白后仍不行，检查：组策略「不运行指定的 Windows 应用程序」、AppLocker/SRP 规则、安全软件的「启动防护」" -ForegroundColor Gray
            Write-Host "  · 手动对照：在 cmd 里执行  explorer.exe `"C:\`"  —— 能弹出窗口就说明系统本身没问题，是拦我们这个调用方" -ForegroundColor Gray
        }
        elseif ($sd.shellApiOk) {
            Write-Host "结论：进程创建、explorer.exe 和 shell32 打开目录接口都正常。" -ForegroundColor Green
        }
        else {
            Write-Host "结论：进程和 explorer 都能起来，但 shell32 接口被拒（权限/完整性级别不匹配）→「打开目录」应能靠 explorer 那一级成功。" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "--shell-diag 输出解析失败：" -ForegroundColor Yellow
        Write-Host $raw
    }
}
finally { Pop-Location }

# ---------------- 7. 数据目录可写性 ----------------
Section "数据目录"
Push-Location $root
try {    # 先落盘再读，避免管道把 UTF-8 当 GBK 解码
    $tmp = Join-Path $env:TEMP ("appforge-check-" + [Guid]::NewGuid().ToString("N") + ".json")
    & $Exe --check 2>&1 | Out-File -FilePath $tmp -Encoding UTF8
    $out = Get-Content -Path $tmp -Raw -Encoding UTF8
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue

    $obj = $null
    try { $obj = $out | ConvertFrom-Json } catch { }
    if ($obj) {
        Write-Host ("实际使用：" + $obj.dataDir)
        Write-Host ("便携模式：" + $obj.portable)
        Write-Host "候选位置探测："
        foreach ($line in $obj.dataProbe) { Write-Host ("  " + $line) }
    }
    else {
        Write-Host "--check 输出解析失败，原始输出：" -ForegroundColor Yellow
        Write-Host $out
    }
}
finally { Pop-Location }

Write-Host ""
Write-Host "以上信息可整段粘贴给开发者定位问题。" -ForegroundColor Green

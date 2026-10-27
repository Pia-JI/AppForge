# AppForge 代码签名脚本（SmartScreen / 杀软误判的唯一根治手段）
#
# 背景：Windows 对"未签名 + 无信誉"的程序会弹「已保护你的电脑 / 发布者未知」（SmartScreen），
#       火绒/360 也会对"未签名的自解压 exe"提高拦截等级。这不是程序缺陷，是 Windows 的机制：
#       检查发生在我们的代码运行之前，程序自己无法阻止第一次拦截。
#       能真正消除的只有两条路：① 代码签名（OV/EV 证书）；② 累积下载信誉（时间长、不可控）。
#
# 用法（在仓库根目录运行）：
#   # A. 已经有证书（推荐）：用证书指纹签名
#   .\scripts\sign.ps1 -Thumbprint "A1B2C3..." -Path .\dist\AppForge.exe
#
#   # B. 有 .pfx 文件
#   .\scripts\sign.ps1 -Pfx .\cert\appforge.pfx -PfxPassword "***"
#
#   # C. 只想在本机装个自签名证书做验证（能去掉"发布者未知"，但仅本机信任，
#   #    换机器/发给别人仍会提示；正式分发必须用 CA 签发的证书）
#   .\scripts\sign.ps1 -CreateSelfSigned
#
#   # D. 只清 Internet 标记（不签名，最省事，但只对"已经下载到本机"的文件有效）
#   .\scripts\sign.ps1 -UnblockOnly
#
# 关于证书类型：
#   · OV（组织验证）：几百~两千/年，SmartScreen 需要累积一段时间的下载量才有信誉
#   · EV（扩展验证）：三四千/年，**签名即立刻有信誉**，不弹 SmartScreen —— 想彻底消除就选它
#   · 自签名：只在本机受信任，适合自用/内测，不能解决分发时的提示

param(
    [string]$Path = "",
    [string]$Thumbprint = "",
    [string]$Pfx = "",
    [string]$PfxPassword = "",
    [switch]$CreateSelfSigned,
    [switch]$UnblockOnly,
    [switch]$NoTimestamp,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# ---------------- 目标文件 ----------------
if ([string]::IsNullOrWhiteSpace($Path)) {
    $candidates = @()
    $distExe = Join-Path $root "dist\AppForge.exe"
    $portableExe = Join-Path $root "dist\AppForge-portable\AppForge.exe"
    if (Test-Path $distExe) { $candidates += $distExe }
    if (Test-Path $portableExe) { $candidates += $portableExe }
    if ($candidates.Count -eq 0) {
        throw "找不到要签名的 exe。先运行 .\scripts\pack.ps1 或 .\scripts\pack-portable.ps1，或用 -Path 指定文件。"
    }
} else {
    $candidates = @((Resolve-Path $Path).Path)
}

foreach ($f in $candidates) {
    if (-not (Test-Path $f)) { throw "文件不存在：$f" }
}

# ---------------- D. 只清 Internet 标记 ----------------
if ($UnblockOnly) {
    $cleared = 0
    foreach ($f in $candidates) {
        $dir = Split-Path -Parent $f
        # -Include 只有配合 -Recurse（或路径以 \* 结尾）才生效；之前这里会静默扫不到文件。
        Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.exe', '.dll', '.config') } |
            ForEach-Object {
            try { Unblock-File -Path $_.FullName -ErrorAction Stop; $cleared++ } catch { }
        }
        try { Unblock-File -Path $f -ErrorAction Stop } catch { }
    }
    Write-Host "已清除 $cleared 个文件的「来自 Internet」标记（SmartScreen 只在带该标记时才会拦）" -ForegroundColor Green
    foreach ($f in $candidates) {
        $sig = Get-AuthenticodeSignature -FilePath $f
        Write-Host ("  {0} → 签名状态：{1}" -f (Split-Path -Leaf $f), $sig.Status)
    }
    return
}

# ---------------- 找 signtool.exe ----------------
function Find-SignTool {
    $candidates = @()
    $roots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\8.1\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\8.1\bin"
    )
    foreach ($r in $roots) {
        if (Test-Path $r) {
            $candidates += Get-ChildItem -Path $r -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending |
                Select-Object -ExpandProperty FullName
        }
    }
    if ($candidates.Count -gt 0) { return $candidates[0] }
    return $null
}

$signtool = Find-SignTool
if (-not $signtool) {
    Write-Host "未找到 signtool.exe，改用 PowerShell 内置 Set-AuthenticodeSignature（无需安装 Windows SDK）。" -ForegroundColor Yellow
} else {
    Write-Host "signtool：$signtool" -ForegroundColor DarkGray
}

# ---------------- C. 生成自签名证书（仅本机可信） ----------------
if ($CreateSelfSigned) {
    # 自签名证书没有公共信任链，时间戳服务器没有意义；离线/断网时还会让签名失败。
    $NoTimestamp = $true
    $subject = "CN=AppForge 造物台, O=AppForge, C=CN"
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

    if (-not $cert) {
        Write-Host "== 生成本机自签名代码签名证书（3 年）==" -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $subject `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -NotAfter (Get-Date).AddYears(3) `
            -KeyUsage DigitalSignature
    }
    $Thumbprint = $cert.Thumbprint

    # 只有把证书放进"受信任的根"和"受信任的发布者"，本机才会认为签名有效
    $stores = @("Root", "TrustedPublisher")
    foreach ($store in $stores) {
        $target = "Cert:\CurrentUser\$store"
        $exists = Get-ChildItem $target | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
        if (-not $exists) {
            $certBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
            $tmp = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllBytes($tmp, $certBytes)
            try {
                Import-Certificate -FilePath $tmp -CertStoreLocation $target | Out-Null
                Write-Host "  已导入 Cert:\CurrentUser\$store" -ForegroundColor DarkGray
            } finally {
                Remove-Item $tmp -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Write-Host "自签名证书指纹：$Thumbprint" -ForegroundColor Yellow
    Write-Host "提示：自签名只在本机被信任。发给别人仍会提示「发布者未知」，正式分发请用 CA 证书。" -ForegroundColor Yellow
}

# ---------------- A/B. 真正签名 ----------------
if ([string]::IsNullOrWhiteSpace($Thumbprint) -and [string]::IsNullOrWhiteSpace($Pfx)) {
    throw "请指定签名证书：-Thumbprint（本机证书指纹）/ -Pfx（pfx 文件）/ -CreateSelfSigned（生成自签名）/ -UnblockOnly（只清标记）。"
}

# 自签名证书（Subject == Issuer）没有公共信任链，不打时间戳，避免断网/时间戳服务异常导致签名失败。
if (-not $NoTimestamp) {
    $probeCert = $null
    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        foreach ($loc in @("CurrentUser", "LocalMachine")) {
            $probeCert = Get-Item "Cert:\$loc\My\$Thumbprint" -ErrorAction SilentlyContinue
            if ($probeCert) { break }
        }
    } elseif (Test-Path $Pfx) {
        try { $probeCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2((Resolve-Path $Pfx).Path, $PfxPassword) } catch { }
    }
    if ($probeCert -and $probeCert.Subject -eq $probeCert.Issuer) {
        $NoTimestamp = $true
        Write-Host "检测到自签名证书：跳过时间戳（仅本机信任）。" -ForegroundColor Yellow
    }
    if ($probeCert -and -not $probeCert.HasPrivateKey) { $probeCert = $null }
}

$signArgs = @("sign", "/fd", "SHA256")
if (-not $NoTimestamp) { $signArgs += @("/td", "SHA256", "/tr", $TimestampUrl) }
$signArgs += "/v"
if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
    # 证书可能装在 CurrentUser 或 LocalMachine：只有 LocalMachine 才需要 /sm
    $store = $null
    foreach ($loc in @("CurrentUser", "LocalMachine")) {
        $found = Get-ChildItem "Cert:\$loc\My" -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $Thumbprint }
        if ($found) { $store = $loc; break }
    }
    $signArgs += @("/sha1", $Thumbprint)
    if ($store -eq "LocalMachine") { $signArgs += "/sm" }
} else {
    if (-not (Test-Path $Pfx)) { throw "pfx 文件不存在：$Pfx" }
    $signArgs += @("/f", (Resolve-Path $Pfx).Path)
    if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) { $signArgs += @("/p", $PfxPassword) }
}

foreach ($f in $candidates) {
    Write-Host "== 签名 $f ==" -ForegroundColor Cyan
    if ($signtool) {
        & $signtool @signArgs $f
        if ($LASTEXITCODE -ne 0) { throw "签名失败（signtool 退出码 $LASTEXITCODE）：$f" }
    } else {
        $certObj = $null
        if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
            foreach ($loc in @("CurrentUser", "LocalMachine")) {
                $foundCert = Get-Item "Cert:\$loc\My\$Thumbprint" -ErrorAction SilentlyContinue
                if ($foundCert) { $certObj = $foundCert; break }
            }
            if (-not $certObj) { throw "找不到证书：$Thumbprint" }
        } else {
            if (-not (Test-Path $Pfx)) { throw "pfx 文件不存在：$Pfx" }
            $certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
                (Resolve-Path $Pfx).Path, $PfxPassword)
        }

        $authParams = @{
            FilePath = $f
            Certificate = $certObj
            HashAlgorithm = "SHA256"
        }
        if (-not $NoTimestamp) { $authParams.TimestampServer = $TimestampUrl }
        $sig = Set-AuthenticodeSignature @authParams
        if ($sig.Status -ne "Valid") {
            throw "签名失败：$($sig.Status) $($sig.StatusMessage)（文件：$f）"
        }
    }

    $sig = Get-AuthenticodeSignature -FilePath $f
    $color = if ($sig.Status -eq "Valid") { "Green" } else { "Yellow" }
    $signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "(none)" }
    Write-Host ("  签名状态：{0}  签名者：{1}" -f $sig.Status, $signer) -ForegroundColor $color

    # 签名会改写文件，顺手清一次 Internet 标记（否则首次运行仍会被 SmartScreen 拦）
    try { Unblock-File -Path $f -ErrorAction Stop } catch { }
}

Write-Host ""
Write-Host "完成。若仍是「发布者未知」：" -ForegroundColor Green
Write-Host "  · 用 EV 证书签名后不需要累积信誉，立刻不弹；OV 证书要等下载量起来" -ForegroundColor Gray
Write-Host "  · 已发布过的同名文件，Windows 会按文件哈希缓存提示，改代码重新签名即可" -ForegroundColor Gray
Write-Host "  · 杀软误判（火绒/360）：把 exe 加入信任区，或提交误报申诉" -ForegroundColor Gray

exit 0

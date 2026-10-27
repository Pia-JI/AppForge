# 把开发仓库导出成"脱敏开源版"并（可选）推送到 GitHub
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\export-public.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\export-public.ps1 -Push
#
# 做的事：
#   1) 只复制源码/文档/脚本（排除 dev-data、dist、bin、obj、.git、界面截图）
#   2) 按 scripts\export-local.json 做脱敏替换（该文件**不入库**，里面是本机/内部信息）
#   3) 目标目录若已是 git 仓库（含远端），按当前版本提交；-Push 时推送
#
# 新克隆一份而不是复用开发仓库的 .git，是为了让公开仓库的历史里
# 不含开发期的数据/密钥（哪怕后来删掉，历史里仍然可查）。
#
# 为什么替换表放在外部 json：这样开源仓库里不会出现"待脱敏的原文"本身。
param(
    [string]$Target = (Join-Path ([Environment]::GetFolderPath("Desktop")) "AppForge-造物台"),
    [switch]$Push
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props") -Encoding UTF8)).Project.PropertyGroup.Version

# ---- 脱敏替换表（本地文件，不入库）----
$localConfig = Join-Path $PSScriptRoot "export-local.json"
$repl = [ordered]@{}
if (Test-Path $localConfig) {
    $json = Get-Content $localConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($pair in $json.replace) { $repl[[string]$pair[0]] = [string]$pair[1] }
    Write-Host ("== 已加载脱敏替换表：{0} 条 ==" -f $repl.Count) -ForegroundColor Cyan
} else {
    Write-Host "⚠ 没找到 scripts\export-local.json，本次不做文本脱敏（只排除数据与产物）" -ForegroundColor Yellow
}

Write-Host "== 导出脱敏副本：$Target（版本 $version）==" -ForegroundColor Cyan
if (-not (Test-Path $Target)) { New-Item -ItemType Directory -Path $Target -Force | Out-Null }

# 保留 .git 与 LICENSE，其余清空，避免旧文件残留
Get-ChildItem $Target -Force | Where-Object { $_.Name -ne '.git' -and $_.Name -ne 'LICENSE' } |
    Remove-Item -Recurse -Force

$xd = @('dev-data', 'data', 'dist', 'bin', 'obj', '.git', '.vs', '.workbuddy',
        'node_modules', 'publish', 'screenshots', 'logs', 'payload', 'AppForge-payload')
$xf = @('*.user', '*.suo', '*.log', 'secrets.dat', 'index.json', '*.pdb', '*.pma',
        'export-local.json', 'appforge-write-test.tmp')
$args = @($root, $Target, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1') +
        ($xd | ForEach-Object { '/XD'; $_ }) + ($xf | ForEach-Object { '/XF'; $_ })
& robocopy @args | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 失败：$LASTEXITCODE" }

Write-Host "== 脱敏替换 ==" -ForegroundColor Cyan
$exts = @('.cs', '.js', '.mjs', '.md', '.json', '.ps1', '.cmd', '.txt', '.html', '.css',
          '.props', '.sln', '.csproj', '.xml', '.manifest', '.yml', '.yaml')
$changed = 0
foreach ($f in (Get-ChildItem $Target -Recurse -File -Force)) {
    if ($f.FullName -match '\\\.git\\') { continue }
    if (-not ($exts -contains $f.Extension -or $f.Name -eq '.gitignore')) { continue }

    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $orig = $text
    foreach ($k in $repl.Keys) { if ($text.Contains($k)) { $text = $text.Replace($k, $repl[$k]) } }
    if ($text -ne $orig) {
        [System.IO.File]::WriteAllText($f.FullName, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
        $changed++
    }
}
Write-Host "   改写 $changed 个文件"

# ---- 截图不随开源版发布（可能暴露本机数据与内部网关配置）→ 清掉引用，避免死链 ----
$noteReadme = "> 说明：界面截图未包含在开源发布版中（避免暴露本机数据与私有网关配置）。可自行运行程序查看，或在本地重新截图后补入 ``docs/screenshots/``。"
$noteDoc = "> 说明：本文档中的界面截图未包含在开源发布版中（避免暴露本机数据与私有网关配置）。"
$readmePath = Join-Path $Target "README.md"
if (Test-Path $readmePath) {
    $t = Get-Content $readmePath -Raw -Encoding UTF8
    if ($t -match 'screenshots/') {
        $t = [regex]::Replace($t, '(?m)^\s*!\[[^\]]*\]\([^)]*screenshots/[^)]*\)\s*\r?\n', '')
        if ($t -notmatch '截图未包含在开源发布版') {
            $idx = $t.IndexOf('---')
            if ($idx -ge 0) { $t = $t.Substring(0, $idx + 3) + "`r`n`r`n$noteReadme`r`n" + $t.Substring($idx + 3) }
        }
        Set-Content -Path $readmePath -Value $t -Encoding UTF8 -NoNewline
    }
}
foreach ($doc in (Get-ChildItem (Join-Path $Target "docs") -Filter *.md -ErrorAction SilentlyContinue)) {
    $t = Get-Content $doc.FullName -Raw -Encoding UTF8
    if ($t -match 'screenshots/') {
        $t = [regex]::Replace($t, '(?m)^\s*!\[[^\]]*\]\([^)]*screenshots/[^)]*\)\s*\r?\n', '')
        if ($t -notmatch '截图未包含在开源发布版') { $t = $t.TrimEnd() + "`r`n`r`n$noteDoc`r`n" }
        Set-Content -Path $doc.FullName -Value $t -Encoding UTF8 -NoNewline
    }
}

# ---- 复查：替换表里的原文若还出现，说明漏了 ----
$leaks = @()
foreach ($k in $repl.Keys) {
    $hits = Get-ChildItem $Target -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '\\\.git\\' } |
        Select-String -Pattern $k -SimpleMatch -ErrorAction SilentlyContinue
    foreach ($h in $hits) { $leaks += "$k ← $($h.Path):$($h.LineNumber)" }
}
if ($leaks.Count -gt 0) {
    Write-Host "⚠ 仍有可能敏感的串：" -ForegroundColor Yellow
    $leaks | Select-Object -First 10 | ForEach-Object { Write-Host "   $_" }
} else {
    Write-Host "   复查通过：替换表里的原文均已清理" -ForegroundColor Green
}

if (Test-Path (Join-Path $Target ".git")) {
    Write-Host "== 提交 ==" -ForegroundColor Cyan
    git -C $Target add -A
    $status = git -C $Target status --porcelain
    if ($status) {
        git -C $Target commit -q -m "chore(release): 同步 v$version（脱敏发布版）"
        Write-Host ("   已提交：" + (git -C $Target log --oneline -1))
    } else {
        Write-Host "   没有变更，跳过提交"
    }
    if ($Push) {
        git -C $Target push origin HEAD 2>&1 | Out-Null
        Write-Host "   已推送到 origin" -ForegroundColor Green
    }
}
Write-Host ""
Write-Host "✅ 脱敏副本就绪：$Target" -ForegroundColor Green

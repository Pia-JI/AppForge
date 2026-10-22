# 生成 AppForge 应用图标（build\AppForge.ico，多尺寸 PNG 压缩 ICO）
# 纯 System.Drawing 绘制，无第三方依赖
param(
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutFile) { $OutFile = Join-Path $root "build\AppForge.ico" }
$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

Add-Type -AssemblyName System.Drawing

function New-LogoPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
        $radius = [int]($size * 0.22)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($size - $d, 0, $d, $d, 270, 90)
        $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
        $path.AddArc(0, $size - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 37, 99, 235),
            [System.Drawing.Color]::FromArgb(255, 124, 58, 237),
            45.0)
        $g.FillPath($brush, $path)

        $fontSize = [float]($size * 0.42)
        $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $g.DrawString("AF", $font, $textBrush, (New-Object System.Drawing.RectangleF(0, 0, $size, $size)), $fmt)

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose()
        return ,$bytes
    } finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) { $pngs += , (New-LogoPng $s) }

# 组装 ICO：ICONDIR(6) + 每张 ICONDIRENTRY(16) + PNG 数据
$count = $sizes.Count
$headerSize = 6 + 16 * $count
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([UInt16]0)      # reserved
$writer.Write([UInt16]1)      # type = icon
$writer.Write([UInt16]$count)

$offset = $headerSize
for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $data = $pngs[$i]
    $writer.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # width
    $writer.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # height
    $writer.Write([Byte]0)                                      # color count
    $writer.Write([Byte]0)                                      # reserved
    $writer.Write([UInt16]1)                                    # planes
    $writer.Write([UInt16]32)                                   # bit count
    $writer.Write([UInt32]$data.Length)
    $writer.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $writer.Write([byte[]]$data) }

$writer.Flush()
[System.IO.File]::WriteAllBytes($OutFile, $stream.ToArray())
$writer.Close()
$stream.Close()

Write-Host ("✅ 图标已生成：{0}（{1} 个尺寸，{2:N1} KB）" -f $OutFile, $count, ((Get-Item $OutFile).Length / 1KB))

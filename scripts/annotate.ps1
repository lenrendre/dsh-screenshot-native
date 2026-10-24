param(
    [Parameter(Mandatory = $true)][string]$InFile,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [Parameter(Mandatory = $true)][string]$MarksFile
)

# Emit UTF-8 on stdout: the host decodes it as UTF-8, and a non-ASCII path (for example a
# Chinese user name) would otherwise arrive as "??" and turn into a bogus ENOENT host-side.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# Annotate an existing PNG. Marks are passed as a JSON array so the caller never has to fight
# PowerShell quoting. Coordinates are image pixels (top-left origin).
#
# Each mark: { type: rect|ellipse|arrow|label|mosaic, x, y, width, height, text?, color?,
#              thickness?, fontSize? }

Add-Type -AssemblyName System.Drawing

$raw = Get-Content -LiteralPath $MarksFile -Raw -Encoding UTF8
# ConvertFrom-Json hands back an Object[] for a JSON array, and wrapping that in @() yields an
# array holding one array — every $m would then be the whole list. Keep the array as-is.
$parsed = $raw | ConvertFrom-Json
if ($parsed -is [System.Array]) { $marks = $parsed } else { $marks = @($parsed) }

$img = [System.Drawing.Image]::FromFile($InFile)
$bmp = New-Object System.Drawing.Bitmap $img.Width, $img.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.DrawImage($img, 0, 0, $img.Width, $img.Height)

function ConvertTo-Color([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return [System.Drawing.Color]::FromArgb(255, 255, 59, 48) }
    $t = $text.TrimStart('#')
    if ($t.Length -eq 6) {
        $r = [Convert]::ToInt32($t.Substring(0, 2), 16)
        $gg = [Convert]::ToInt32($t.Substring(2, 2), 16)
        $b = [Convert]::ToInt32($t.Substring(4, 2), 16)
        return [System.Drawing.Color]::FromArgb(255, $r, $gg, $b)
    }
    return [System.Drawing.Color]::FromArgb(255, 255, 59, 48)
}

$applied = 0
foreach ($m in $marks) {
    $type = [string]$m.type
    $x = [int]$m.x; $y = [int]$m.y
    $w = [int]$m.width; $h = [int]$m.height
    $thickness = if ($null -ne $m.thickness) { [float]$m.thickness } else { 4.0 }
    $color = ConvertTo-Color ([string]$m.color)

    switch ($type) {
        'rect' {
            $pen = New-Object System.Drawing.Pen $color, $thickness
            $g.DrawRectangle($pen, $x, $y, $w, $h)
            $pen.Dispose()
            $applied++
        }
        'ellipse' {
            $pen = New-Object System.Drawing.Pen $color, $thickness
            $g.DrawEllipse($pen, $x, $y, $w, $h)
            $pen.Dispose()
            $applied++
        }
        'arrow' {
            $pen = New-Object System.Drawing.Pen $color, $thickness
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $x2 = $x + $w; $y2 = $y + $h
            $g.DrawLine($pen, $x, $y, $x2, $y2)
            # Head: two short strokes back from the tip, spread by a fixed angle.
            $len = [Math]::Sqrt([Math]::Pow($w, 2) + [Math]::Pow($h, 2))
            if ($len -gt 1) {
                $ux = $w / $len; $uy = $h / $len
                $head = [Math]::Max(10.0, [Math]::Min(26.0, $len * 0.28))
                foreach ($sign in @(1, -1)) {
                    $ang = [Math]::Atan2($uy, $ux) + $sign * 0.42
                    $hx = $x2 - [Math]::Cos($ang) * $head
                    $hy = $y2 - [Math]::Sin($ang) * $head
                    $g.DrawLine($pen, $x2, $y2, [int]$hx, [int]$hy)
                }
            }
            $pen.Dispose()
            $applied++
        }
        'label' {
            $text = [string]$m.text
            if ([string]::IsNullOrEmpty($text)) { continue }
            $size = if ($null -ne $m.fontSize) { [float]$m.fontSize } else { 24.0 }
            $font = New-Object System.Drawing.Font 'Microsoft YaHei', $size, ([System.Drawing.FontStyle]::Bold)
            $measure = $g.MeasureString($text, $font)
            $padX = 10; $padY = 6
            $boxW = [int]$measure.Width + 2 * $padX
            $boxH = [int]$measure.Height + 2 * $padY
            # A solid background keeps the text readable over any picture underneath.
            $bg = New-Object System.Drawing.SolidBrush $color
            $g.FillRectangle($bg, $x, $y, $boxW, $boxH)
            $fg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
            $g.DrawString($text, $font, $fg, ($x + $padX), ($y + $padY))
            $bg.Dispose(); $fg.Dispose(); $font.Dispose()
            $applied++
        }
        'mosaic' {
            if ($w -lt 2 -or $h -lt 2) { continue }
            $copy = New-Object System.Drawing.Bitmap $w, $h
            $cg = [System.Drawing.Graphics]::FromImage($copy)
            $cg.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), (New-Object System.Drawing.Rectangle $x, $y, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
            $cg.Dispose()
            $block = [Math]::Max(6, [int]([Math]::Max($w, $h) / 18))
            $small = New-Object System.Drawing.Bitmap ([Math]::Max(1, [int]($w / $block))), ([Math]::Max(1, [int]($h / $block)))
            $sg = [System.Drawing.Graphics]::FromImage($small)
            $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
            $sg.DrawImage($copy, 0, 0, $small.Width, $small.Height)
            $sg.Dispose()
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g.DrawImage($small, (New-Object System.Drawing.Rectangle $x, $y, $w, $h))
            $small.Dispose(); $copy.Dispose()
            $applied++
        }
        default { }
    }
}
$g.Dispose()
$img.Dispose()

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# Same envelope as the other helpers: the host unwraps `data`, so a flat object would reach it
# as {} and every field (including the mark count) would read as missing.
[ordered]@{
    status = 'ok'
    data   = [ordered]@{
        file  = $OutFile
        marks = $applied
        bytes = [int](Get-Item -LiteralPath $OutFile).Length
    }
} | ConvertTo-Json -Compress -Depth 4

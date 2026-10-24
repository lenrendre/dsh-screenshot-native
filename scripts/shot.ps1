# shot.ps1 — capture the screen (or one window / one region) to a PNG file.
#
# Output contract (single line, ASCII-safe JSON):
#   {"status":"ok","data":{"file":"<png path>","bytes":123,"x":..,"y":..,"width":..,"height":..}}
#   {"status":"error","error":"..."}
#
# The PNG is written to a file instead of being returned inline: a full-screen capture
# is ~10 MB of base64, which does not survive a command line. The caller reads the file.
#
# Notes:
#   * The process is made DPI-aware first, so every rectangle below is in real screen
#     pixels and matches what cua-driver reports in list_windows.
#   * All diagnostics are ASCII so the output survives any console codepage.
param(
  [ValidateSet('fullscreen', 'window', 'region')]
  [string]$Mode = 'fullscreen',
  [long]$Hwnd = 0,
  [int]$X = 0,
  [int]$Y = 0,
  [int]$W = 0,
  [int]$H = 0,
  [Parameter(Mandatory = $true)]
  [string]$OutFile
)

# Emit UTF-8 on stdout: the host decodes it as UTF-8, and a non-ASCII path (for example a
# Chinese user name) would otherwise arrive as "??" and turn into a bogus ENOENT host-side.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }


$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace SS -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

try {
  # DPI awareness must be set before any coordinate is read.
  [void][SS.Native]::SetProcessDPIAware()

  $v = [System.Windows.Forms.SystemInformation]::VirtualScreen

  switch ($Mode) {
    'fullscreen' {
      $srcX = $v.X; $srcY = $v.Y; $srcW = $v.Width; $srcH = $v.Height
    }
    'window' {
      if ($Hwnd -eq 0) { throw 'window mode requires -Hwnd' }
      $h = [IntPtr]$Hwnd
      if (-not [SS.Native]::IsWindowVisible($h)) { throw "window $Hwnd is not visible" }
      $r = New-Object SS.Native+RECT
      if (-not [SS.Native]::GetWindowRect($h, [ref]$r)) { throw "GetWindowRect failed for $Hwnd" }
      $srcX = $r.Left; $srcY = $r.Top
      $srcW = $r.Right - $r.Left; $srcH = $r.Bottom - $r.Top
    }
    'region' {
      $srcX = $X; $srcY = $Y; $srcW = $W; $srcH = $H
    }
  }

  if ($srcW -le 0 -or $srcH -le 0) { throw "invalid capture size ${srcW}x${srcH}" }

  $bmp = New-Object System.Drawing.Bitmap($srcW, $srcH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)

  if ($Mode -eq 'window') {
    # PrintWindow gives the window's own pixels even when it is partially covered.
    $hdc = $g.GetHdc()
    try {
      $ok = [SS.Native]::PrintWindow([IntPtr]$Hwnd, $hdc, 2)  # PW_RENDERFULLCONTENT
    } finally { $g.ReleaseHdc($hdc) }
    if (-not $ok) {
      # Fall back to a screen-area copy when the window refuses PrintWindow.
      $g.CopyFromScreen($srcX, $srcY, 0, 0, (New-Object System.Drawing.Size($srcW, $srcH)), [System.Drawing.CopyPixelOperation]::SourceCopy)
    }
  } else {
    $g.CopyFromScreen($srcX, $srcY, 0, 0, (New-Object System.Drawing.Size($srcW, $srcH)), [System.Drawing.CopyPixelOperation]::SourceCopy)
  }
  $g.Dispose()

  # Write the PNG to disk: a full-screen capture is far too large to pass inline.
  $dir = Split-Path -Parent $OutFile
  if ($dir -and -not (Test-Path $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
  $bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $bytes = (Get-Item $OutFile).Length
  $safePath = $OutFile -replace '\\', '\\'

  # Report the virtual-screen bounds alongside the image. These are in the SAME
  # coordinate space as the captured pixels, which is what makes "move the window
  # outside the capture" reliable: a caller must not derive those bounds from any
  # other source, because SetProcessDPIAware() can silently fail inside PowerShell
  # (it is ignored once the process owns a window), leaving that source at logical
  # rather than physical scale.
  Write-Output ('{"status":"ok","data":{"file":"' + $safePath + '","bytes":' + $bytes +
    ',"x":' + $srcX + ',"y":' + $srcY + ',"width":' + $srcW + ',"height":' + $srcH +
    ',"screen":{"x":' + $v.X + ',"y":' + $v.Y + ',"width":' + $v.Width + ',"height":' + $v.Height + '}}}')
}
catch {
  $err = [ordered]@{ status = 'error'; error = ($_.Exception.Message -replace '[^\x20-\x7E]', '?') }
  Write-Output ($err | ConvertTo-Json -Compress)
  exit 1
}

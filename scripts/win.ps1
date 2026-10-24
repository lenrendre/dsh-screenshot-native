# win.ps1 — enumerate top-level windows, and show/hide one window.
#
# Output contract (single line, ASCII-safe JSON, NON-ASCII escaped as \uXXXX):
#   {"status":"ok","data":{"windows":[{"hwnd":..,"pid":..,"x":..,"y":..,"width":..,"height":..,"visible":true,"minimized":false,"title":"..","process":".."}]}}
#   {"status":"ok","data":{"hwnd":..,"visible":true}}
#   {"status":"error","error":".."}
#
# JSON is assembled by hand so the \uXXXX escapes survive verbatim (ConvertTo-Json
# would double-escape them). The process is DPI-aware so every rectangle is in real
# screen pixels, matching what cua-driver reports in list_windows.
param(
  [ValidateSet('list', 'hide', 'show', 'rect')]
  [string]$Action = 'list',
  [long]$Hwnd = 0
)

# Emit UTF-8 on stdout: the host decodes it as UTF-8, and a non-ASCII path (for example a
# Chinese user name) would otherwise arrive as "??" and turn into a bogus ENOENT host-side.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }


$ErrorActionPreference = 'Stop'

# Quote one string for JSON, escaping every non-ASCII code unit as \uXXXX.
function Get-JsonString([string]$s) {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.Append('"')
  if (-not [string]::IsNullOrEmpty($s)) {
    foreach ($ch in $s.ToCharArray()) {
      $code = [int]$ch
      if ($ch -eq '"') { [void]$sb.Append('\"') }
      elseif ($ch -eq '\') { [void]$sb.Append('\\') }
      elseif ($code -ge 0x20 -and $code -le 0x7E) { [void]$sb.Append($ch) }
      elseif ($code -lt 0x20) { [void]$sb.Append(' ') }
      else { [void]$sb.Append('\u' + $code.ToString('x4')) }
    }
  }
  [void]$sb.Append('"')
  return $sb.ToString()
}

Add-Type -Namespace SW -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int val, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

function Get-WindowJsonList {
  $rows = New-Object System.Collections.Generic.List[string]

  # One process snapshot instead of a Get-Process call per window.
  #
  # The per-window form (`Get-Process -Id $pid`) costs a cmdlet invocation each time
  # and measured ~1.4s for ~44 windows on a machine where the whole enumeration took
  # 3.1s; taking the snapshot once and looking names up in a hashtable cuts that to
  # ~50ms. The delegate below can read this table — verified explicitly before relying
  # on it, because if it could not, every `process` field would come back empty and
  # findOwnWindow() (which matches on that field) would fail silently.
  $procNames = @{}
  foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
    $procNames[[int]$p.Id] = $p.ProcessName
  }

  $callback = [SW.Native+EnumWindowsProc] {
    param($hWnd, $lParam)

    $ex = [SW.Native]::GetWindowLong($hWnd, -20)   # GWL_EXSTYLE
    if (($ex -band 0x00000080) -ne 0) { return $true }   # WS_EX_TOOLWINDOW

    $len = [SW.Native]::GetWindowTextLength($hWnd)
    $sb = New-Object System.Text.StringBuilder ($len + 2)
    [void][SW.Native]::GetWindowText($hWnd, $sb, $sb.Capacity)
    $title = $sb.ToString()
    if ([string]::IsNullOrWhiteSpace($title)) { return $true }   # shell noise

    $r = New-Object SW.Native+RECT
    if (-not [SW.Native]::GetWindowRect($hWnd, [ref]$r)) { return $true }
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 1 -or $h -le 1) { return $true }

    $procId = 0
    [void][SW.Native]::GetWindowThreadProcessId($hWnd, [ref]$procId)
    # Looked up in the snapshot taken above; a miss (a process that exited, or one not
    # visible to this user) yields an empty name rather than throwing.
    $pname = ''
    if ($procNames.ContainsKey([int]$procId)) { $pname = $procNames[[int]$procId] }

    $vis = [bool][SW.Native]::IsWindowVisible($hWnd)
    $min = [bool][SW.Native]::IsIconic($hWnd)

    # DWM-cloaked windows must be skipped. A UWP app that the shell has put away keeps
    # its Win32 window object, and IsWindowVisible still reports true for it, but
    # nothing of it is on screen. Leaving those in makes them invisible snap targets:
    # an almost-fullscreen cloaked window would swallow every snap and outline empty
    # desktop. DWMWA_CLOAKED = 14; 0 means not cloaked.
    #
    # Note this is a delegate callback, not a loop: `continue` would end the whole
    # enumeration, so the filter has to exit with `return $true`.
    $cloaked = 0
    [void][SW.Native]::DwmGetWindowAttribute($hWnd, 14, [ref]$cloaked, 4)
    if ($cloaked -ne 0) { return $true }

    $visJson = if ($vis) { 'true' } else { 'false' }
    $minJson = if ($min) { 'true' } else { 'false' }

    $rows.Add('{"hwnd":' + [long]$hWnd +
      ',"pid":' + [int]$procId +
      ',"x":' + $r.Left + ',"y":' + $r.Top +
      ',"width":' + $w + ',"height":' + $h +
      ',"visible":' + $visJson + ',"minimized":' + $minJson +
      ',"cloaked":' + $cloaked +
      ',"title":' + (Get-JsonString $title) +
      ',"process":' + (Get-JsonString $pname) + '}')
    return $true
  }
  [void][SW.Native]::EnumWindows($callback, [IntPtr]::Zero)
  return ($rows -join ',')
}

try {
  [void][SW.Native]::SetProcessDPIAware()

  switch ($Action) {
    'list' {
      $body = Get-WindowJsonList
      Write-Output ('{"status":"ok","data":{"windows":[' + $body + ']}}')
    }
    'hide' {
      if ($Hwnd -eq 0) { throw 'hide requires -Hwnd' }
      [void][SW.Native]::ShowWindow([IntPtr]$Hwnd, 0)   # SW_HIDE
      Start-Sleep -Milliseconds 120
      $vis = [SW.Native]::IsWindowVisible([IntPtr]$Hwnd)
      $vj = if ($vis) { 'true' } else { 'false' }
      Write-Output ('{"status":"ok","data":{"hwnd":' + $Hwnd + ',"visible":' + $vj + '}}')
    }
    'show' {
      if ($Hwnd -eq 0) { throw 'show requires -Hwnd' }
      $h = [IntPtr]$Hwnd
      # A window hidden with SW_HIDE loses its activation, so SW_SHOW alone can
      # leave it visible-but-buried. Restore the normal frame, place it topmost
      # without stealing focus permanently, then re-assert visibility. Verified:
      # this is what keeps the window on screen after a hide-own capture.
      [void][SW.Native]::ShowWindow($h, 9)    # SW_RESTORE
      [void][SW.Native]::ShowWindow($h, 5)    # SW_SHOW
      [void][SW.Native]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0003)  # HWND_TOPMOST|NOSIZE|NOMOVE
      [void][SW.Native]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0003)  # HWND_NOTOPMOST
      [void][SW.Native]::SetForegroundWindow($h)
      [void][SW.Native]::ShowWindow($h, 5)
      Start-Sleep -Milliseconds 200
      $vis = [SW.Native]::IsWindowVisible($h)
      $vj = if ($vis) { 'true' } else { 'false' }
      Write-Output ('{"status":"ok","data":{"hwnd":' + $Hwnd + ',"visible":' + $vj + '}}')
    }
    'rect' {
      # Current screen rectangle of a window. Used to crop a window or region capture
      # out of a full-screen grab.
      if ($Hwnd -eq 0) { throw 'rect requires -Hwnd' }
      $r = New-Object SW.Native+RECT
      [void][SW.Native]::GetWindowRect([IntPtr]$Hwnd, [ref]$r)
      Write-Output ('{"status":"ok","data":{"hwnd":' + $Hwnd + ',"x":' + $r.Left + ',"y":' + $r.Top +
        ',"width":' + ($r.Right - $r.Left) + ',"height":' + ($r.Bottom - $r.Top) + '}}')
    }
  }
}
catch {
  $msg = $_.Exception.Message
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $msg.ToCharArray()) {
    $c = [int]$ch
    if ($c -ge 0x20 -and $c -le 0x7E) {
      if ($ch -eq '"' -or $ch -eq '\') { [void]$sb.Append('\') }
      [void]$sb.Append($ch)
    } else { [void]$sb.Append(' ') }
  }
  Write-Output ('{"status":"error","error":"' + $sb.ToString() + '"}')
  exit 1
}

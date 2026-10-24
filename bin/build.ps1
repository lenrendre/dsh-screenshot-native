# Build WeChatShot with the in-box .NET Framework compiler (no SDK, no download).
# ASCII only on purpose: cmd/pwsh codepage traps destroy non-ASCII in scripts.
#
# TRAP: inside an array literal the comma operator binds TIGHTER than '+', so
#   @('/opt:' + $a, $b)     parses as  '/opt:' + ($a, $b)   and swallows the rest.
# Every computed element below is therefore wrapped in parentheses.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if ([string]::IsNullOrEmpty($here)) { throw '$PSScriptRoot is empty; run this file directly' }

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw 'in-box csc.exe not found' }

$exe = Join-Path $here 'WeChatShot.exe'
$manifest = Join-Path $here 'app.manifest'
$source = Join-Path $here 'WeChatShot.cs'
if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }

Write-Output ('compiler: ' + $csc)
$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    ('/win32manifest:' + $manifest),
    ('/out:' + $exe),
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    $source
)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw ('compile failed, exit ' + $LASTEXITCODE) }

$info = Get-Item -LiteralPath $exe
Write-Output ('built : ' + $info.FullName + '  (' + $info.Length + ' bytes)')

# Verify the DPI manifest really landed inside the PE (a silent no-op manifest is
# indistinguishable from a blurry window at runtime).
$raw = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::ASCII.GetString($raw)
Write-Output ('manifest PerMonitorV2 embedded: ' + ($text -match 'PerMonitorV2'))

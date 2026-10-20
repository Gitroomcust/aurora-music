param(
    [switch]$Run,
    [switch]$ForceIcon
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$assets = Join-Path $root 'assets'
$icon = Join-Path $assets 'lumen.ico'
$manifest = Join-Path $src 'app.manifest'
$output = Join-Path $dist 'Lumen.exe'

if ($ForceIcon -or -not (Test-Path $icon)) {
    & (Join-Path $root 'tools\make-icon.ps1')
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw 'C# compiler for .NET Framework 4.x was not found.'
}

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'PresentationFramework.dll'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}

$wpf = Join-Path $framework 'WPF'
$references = @(
    (Join-Path $framework 'System.dll'),
    (Join-Path $framework 'System.Core.dll'),
    (Join-Path $framework 'System.Xml.dll'),
    (Join-Path $framework 'System.Drawing.dll'),
    (Join-Path $framework 'System.Windows.Forms.dll'),
    (Join-Path $wpf 'PresentationFramework.dll'),
    (Join-Path $wpf 'PresentationCore.dll'),
    (Join-Path $wpf 'WindowsBase.dll')
)

$systemXaml = Join-Path $framework 'System.Xaml.dll'
if (Test-Path $systemXaml) { $references += $systemXaml }

foreach ($reference in $references) {
    if (-not (Test-Path $reference)) {
        throw "Missing reference assembly: $reference"
    }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$compilerArgs = @(
    '/nologo',
    '/noconfig',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/debug:pdbonly',
    '/langversion:5',
    '/utf8output',
    ('/out:' + $output),
    ('/win32icon:' + $icon),
    ('/win32manifest:' + $manifest),
    ('/resource:' + (Join-Path $src 'Theme.xaml') + ',Theme.xaml'),
    ('/resource:' + (Join-Path $src 'Shell.xaml') + ',Shell.xaml'),
    ('/resource:' + (Join-Path $src 'Welcome.xaml') + ',Welcome.xaml'),
    ('/resource:' + (Join-Path $src 'LocalView.xaml') + ',LocalView.xaml'),
    ('/resource:' + (Join-Path $src 'CaptureView.xaml') + ',CaptureView.xaml'),
    ('/resource:' + (Join-Path $src 'Net\WebUi.html') + ',WebUi.html')
)

foreach ($reference in $references) {
    $compilerArgs += ('/reference:' + $reference)
}

$sources = Get-ChildItem -Path $src -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName }
foreach ($source in $sources) {
    $compilerArgs += $source
}

Write-Host 'Compiling Lumen...' -ForegroundColor Cyan
$outputText = & $csc @compilerArgs 2>&1
$exitCode = $LASTEXITCODE
if ($outputText) { $outputText | ForEach-Object { Write-Host $_ } }
if ($exitCode -ne 0) {
    throw "Compilation failed with exit code $exitCode."
}

if (-not (Test-Path $output)) {
    throw 'Compiler reported success, but Lumen.exe was not created.'
}

$readme = Join-Path $root 'README.md'
if (Test-Path $readme) {
    Copy-Item -Force $readme (Join-Path $dist 'README.md')
}
$license = Join-Path $root 'LICENSE'
if (Test-Path $license) {
    Copy-Item -Force $license (Join-Path $dist 'LICENSE')
}
$docs = Join-Path $root 'docs'
if (Test-Path $docs) {
    $docsDest = Join-Path $dist 'docs'
    if (Test-Path $docsDest) {
        Remove-Item $docsDest -Recurse -Force
    }
    Copy-Item -Force -Recurse $docs $docsDest
}

# The LAN server serves assets/lumen-256.png as /icon.png for the web player.
if (Test-Path $assets) {
    $assetsDest = Join-Path $dist 'assets'
    if (Test-Path $assetsDest) {
        Remove-Item $assetsDest -Recurse -Force
    }
    Copy-Item -Force -Recurse $assets $assetsDest
}

Remove-Item (Join-Path $dist 'Lumen.pdb') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dist 'lumen.log') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dist 'settings.ini') -Force -ErrorAction SilentlyContinue

$releaseZip = Join-Path $dist 'Lumen-1.0-win64.zip'
if (Test-Path $releaseZip) {
    Remove-Item $releaseZip -Force
}
$archiveItems = @(
    (Join-Path $dist 'Lumen.exe'),
    (Join-Path $dist 'README.md'),
    (Join-Path $dist 'LICENSE'),
    (Join-Path $dist 'docs'),
    (Join-Path $dist 'assets')
) | Where-Object { Test-Path $_ }
Compress-Archive -Path $archiveItems -DestinationPath $releaseZip -CompressionLevel Optimal

$size = (Get-Item $output).Length / 1MB
Write-Host ("Built {0} ({1:N2} MB)" -f $output, $size) -ForegroundColor Green

if ($Run) {
    Start-Process -FilePath $output
}

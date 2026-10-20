$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$androidDir = Join-Path $root 'android'
$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# Local Android toolchain. Set JAVA_HOME / ANDROID_HOME yourself to override.
$defaultJdk = "C:\Users\sasha\AppData\Local\Programs\jdk-17\jdk-17.0.20.1+1"
$defaultSdk = "C:\Users\sasha\AppData\Local\Android\Sdk"
if (-not $env:JAVA_HOME -and (Test-Path $defaultJdk)) { $env:JAVA_HOME = $defaultJdk }
if (-not $env:ANDROID_HOME -and (Test-Path $defaultSdk)) {
    $env:ANDROID_HOME = $defaultSdk
    $env:ANDROID_SDK_ROOT = $defaultSdk
}

# Sync latest WebUi.html to Android assets
Copy-Item (Join-Path $root 'src\Net\WebUi.html') (Join-Path $androidDir 'app\src\main\assets\index.html') -Force

$apkSource = Join-Path $androidDir 'app\build\outputs\apk\debug\app-debug.apk'
$apkDest = Join-Path $distDir 'AuroraMusic.apk'
# Never leave an old APK behind if this build fails.
Remove-Item $apkSource -Force -ErrorAction SilentlyContinue
Remove-Item $apkDest -Force -ErrorAction SilentlyContinue

$gradlew = Join-Path $androidDir 'gradlew.bat'
$localGradle = "C:\Users\sasha\AppData\Local\Programs\gradle\gradle-8.5\bin\gradle.bat"
if (Test-Path $gradlew) { $gradle = $gradlew }
elseif (Test-Path $localGradle) { $gradle = $localGradle }
else { $gradle = 'gradle' }

Write-Host "Building Aurora Music APK with Gradle..." -ForegroundColor Cyan
try {
    Push-Location $androidDir
    & $gradle assembleDebug
    if ($LASTEXITCODE -ne 0) {
        throw "Gradle build failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}

if (Test-Path $apkSource) {
    Copy-Item $apkSource $apkDest -Force
    $size = (Get-Item $apkDest).Length / 1MB
    Write-Host ("APK built successfully: {0} ({1:N2} MB)" -f $apkDest, $size) -ForegroundColor Green
} else {
    throw "APK was not generated."
}

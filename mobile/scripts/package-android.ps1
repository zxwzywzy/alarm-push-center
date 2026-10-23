param([switch]$SkipBuild, [string]$SigningDirectory = (Join-Path $env:LOCALAPPDATA 'AlarmPushCenterBuild/AndroidSigning'))
$ErrorActionPreference='Stop'
$mobileRoot=Split-Path -Parent $PSScriptRoot
$projectRoot=Split-Path -Parent $mobileRoot
$version=(Get-Content -LiteralPath (Join-Path $mobileRoot 'package.json') -Raw | ConvertFrom-Json).version
$signingRoot=[IO.Path]::GetFullPath($SigningDirectory)
$outputRoot=Join-Path $projectRoot "dist/Mobile-$version"
New-Item -ItemType Directory -Force -Path $signingRoot,$outputRoot | Out-Null
$signingKey=Join-Path $signingRoot 'alarmhub.keystore'
$signingPassword=Join-Path $signingRoot 'signing-password.txt'
if(!(Test-Path -LiteralPath $signingPassword)) {
    [IO.File]::WriteAllText($signingPassword,[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    icacls $signingRoot /inheritance:r /grant:r "$([Security.Principal.WindowsIdentity]::GetCurrent().Name):(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
}
$env:ALARMHUB_KEY_PASSWORD=[IO.File]::ReadAllText($signingPassword)
try {
    if(!(Test-Path -LiteralPath $signingKey)) {
        & "$env:JAVA_HOME/bin/keytool.exe" -genkeypair -keystore $signingKey -alias alarmhub -keyalg RSA -keysize 3072 -validity 3650 -dname 'CN=AlarmHub,OU=Mobile,O=Alarm Push Center,C=CN' -storepass:env ALARMHUB_KEY_PASSWORD -keypass:env ALARMHUB_KEY_PASSWORD
        if($LASTEXITCODE -ne 0){throw 'APK signing key generation failed'}
    }
    if(!$SkipBuild){
        Push-Location $mobileRoot
        try {npm run build; if($LASTEXITCODE -ne 0){throw 'Web build failed'}; npx cap sync android; if($LASTEXITCODE -ne 0){throw 'Native sync failed'}}finally{Pop-Location}
        Push-Location (Join-Path $mobileRoot 'android')
        try {./gradlew.bat assembleRelease --console=plain; if($LASTEXITCODE -ne 0){throw 'Android build failed'}}finally{Pop-Location}
    }
    $unsigned=Join-Path $mobileRoot 'android/app/build/outputs/apk/release/app-release-unsigned.apk'
    $signed=Join-Path $outputRoot "AlarmHub-$version-android.apk"
    & "$env:ANDROID_HOME/build-tools/36.0.0/apksigner.bat" sign --ks $signingKey --ks-key-alias alarmhub --ks-pass env:ALARMHUB_KEY_PASSWORD --out $signed $unsigned
    if($LASTEXITCODE -ne 0){throw 'APK signing failed'}
    & "$env:ANDROID_HOME/build-tools/36.0.0/apksigner.bat" verify --verbose $signed
    if($LASTEXITCODE -ne 0){throw 'APK signature verification failed'}
    $hash=(Get-FileHash -LiteralPath $signed -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$signed.sha256", "$hash  $([IO.Path]::GetFileName($signed))`n")
    Write-Host "APK: $signed"
    Write-Host "SHA256: $hash"
} finally {Remove-Item Env:ALARMHUB_KEY_PASSWORD -ErrorAction SilentlyContinue}

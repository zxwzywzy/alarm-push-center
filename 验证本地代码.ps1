param()

$ErrorActionPreference = 'Stop'

function Invoke-NativeCheck {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command 检查失败，退出码：$LASTEXITCODE"
    }
}

Push-Location $PSScriptRoot
try {
    Get-Command dotnet, node -ErrorAction Stop | Out-Null
    Invoke-NativeCheck 'dotnet' @('build', 'AlarmPushCenter.OpenWrt', '-c', 'Release')
    Invoke-NativeCheck 'dotnet' @('restore', 'TiaOpennessImportAlarmCollector/TiaOpennessImportAlarmCollector.csproj')
    Invoke-NativeCheck 'dotnet' @('build', 'AlarmPushCenter', '-c', 'Release')
    Invoke-NativeCheck 'node' @('--check', 'AlarmPushCenter.OpenWrt/wwwroot/app.js')
    Invoke-NativeCheck 'node' @('--check', 'AlarmPushCenter.OpenWrt/wwwroot/phone-ui.js')
    Invoke-NativeCheck 'node' @('--check', 'AlarmPushCenter.OpenWrt/wwwroot/workspace-ui.js')
    Invoke-NativeCheck 'node' @('--check', 'AlarmPushCenter.OpenWrt/wwwroot/updates.js')
    Invoke-NativeCheck 'node' @('--test', 'AlarmPushCenter.OpenWrt/tests/ui-core.test.cjs', 'AlarmPushCenter.OpenWrt/tests/web-flow.test.cjs', 'AlarmPushCenter.OpenWrt/tests/telemetry-ui.test.cjs', 'AlarmPushCenter.OpenWrt/tests/api.test.cjs')
    Invoke-NativeCheck 'dotnet' @('AlarmPushCenter/bin/Release/net10.0-windows/AlarmPushCenter.dll', '--self-test')
    Invoke-NativeCheck 'dotnet' @('run', '--project', 'AlarmPushCenter.OpenWrt/tests/security/SecurityChecks.csproj', '-c', 'Release')
    Invoke-NativeCheck 'dotnet' @('run', '--project', 'AlarmPushCenter.OpenWrt/tests/phone/PhoneChecks.csproj', '-c', 'Release')
    Invoke-NativeCheck 'npm' @('--prefix', 'mobile', 'test')
    Invoke-NativeCheck 'node' @('tools/check-docs.cjs')
    Invoke-NativeCheck 'node' @('--test', 'tools/update-catalog.test.cjs')
    Invoke-NativeCheck 'dotnet' @('run', '--project', 'AlarmPushCenter.OpenWrt/tests/updates/UpdateChecks.csproj', '-c', 'Release')
    Write-Host '本地代码验证全部通过。'
}
finally {
    Pop-Location
}

param(
    [string]$Project,
    [string]$Output,
    [switch]$Running,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$collectorDll = Join-Path $PSScriptRoot 'AlarmPushCenter.TiaCollector.dll'
if (-not (Test-Path -LiteralPath $collectorDll)) {
    [Console]::Error.WriteLine('ERROR|TIA Openness collector assembly was not found.')
    exit 1
}

try {
    $apiKey = 'HKLM:\SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48'
    $api = Get-ItemProperty -LiteralPath $apiKey
    $baseDll = $api.'Siemens.Engineering.Base'
    $apiFolder = Split-Path -Parent $baseDll
    $portalFolder = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $apiFolder))
    $runtimeFolder = Join-Path $portalFolder 'Bin\PublicAPI'
    [Reflection.Assembly]::LoadFrom((Join-Path $runtimeFolder 'Siemens.Engineering.ClientAdapter.Interfaces.dll')) | Out-Null
    [Reflection.Assembly]::LoadFrom((Join-Path $runtimeFolder 'Siemens.Engineering.Contract.dll')) | Out-Null
    [Reflection.Assembly]::LoadFrom($baseDll) | Out-Null
    [Reflection.Assembly]::LoadFrom((Join-Path $apiFolder 'Siemens.Engineering.Step7.dll')) | Out-Null
    [Reflection.Assembly]::LoadFrom($collectorDll) | Out-Null
    if ($Check) {
        [string[]]$collectorArgs = @('--check')
    }
    elseif ($Running) {
        [string[]]$collectorArgs = @('--running', '--output', $Output)
    }
    else {
        [string[]]$collectorArgs = @('--project', $Project, '--output', $Output)
    }
    $exitCode = [TiaOpennessImportAlarmCollector.Program]::Run($collectorArgs)
    exit $exitCode
}
catch {
    [Console]::Error.WriteLine('ERROR|' + $_.Exception.Message)
    exit 1
}

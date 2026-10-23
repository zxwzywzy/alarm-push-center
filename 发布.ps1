$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'AlarmPushCenter\AlarmPushCenter.csproj'
$outputPath = Join-Path $PSScriptRoot 'AlarmPushCenter\publish-buttons'
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release 编译失败。' }
dotnet run --project $projectPath -c Release --no-build -- --self-test
if ($LASTEXITCODE -ne 0) { throw '离线自检失败，未发布。' }
dotnet publish $projectPath -c Release --no-restore --self-contained false -o $outputPath
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Write-Host "发布完成：$outputPath"


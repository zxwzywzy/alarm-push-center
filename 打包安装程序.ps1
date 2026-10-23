param(
    [string]$Version = '1.4.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'AlarmPushCenter\AlarmPushCenter.csproj'
$publish = Join-Path $root ("artifacts\installer-publish\$Version-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$output = Join-Path $root "dist\Windows-$Version"
$script = Join-Path $root 'installer\AlarmPushCenter.iss'
$compilerCandidates = @(
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw '未找到 Inno Setup 6，请先安装后再打包。' }

New-Item -ItemType Directory -Path $publish -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '自包含发布失败。' }

# Run the published assembly with the console host so PowerShell waits for its exit code.
dotnet (Join-Path $publish 'AlarmPushCenter.dll') --self-test
if ($LASTEXITCODE -ne 0) { throw '离线自检失败，已停止生成安装包。' }

& $compiler "/DMyAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$output" $script
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败。' }

$installer = Join-Path $output "AlarmPushCenter-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "安装包未生成：$installer" }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$installer.sha256", "$hash  $([IO.Path]::GetFileName($installer))`n", (New-Object Text.UTF8Encoding($false)))
$guide = "# Windows $Version 安装与功能手册`n`n[安装、升级与数据位置](https://github.com/zxwzywzy/alarm-push-center/blob/v$Version/docs/INSTALLATION.md#windows-客户端)`n`n[全部功能目录](https://github.com/zxwzywzy/alarm-push-center/blob/v$Version/docs/README.md)`n`n安装目录 docs 附带完整手册；也可下载同一 Release 的离线文档 ZIP。`n"
[IO.File]::WriteAllText((Join-Path $output "Windows-$Version-Guide.md"), $guide, (New-Object Text.UTF8Encoding($false)))
Copy-Item -LiteralPath (Join-Path $root 'DISCLAIMER.txt') -Destination $output -Force
Write-Host "安装包：$installer"
Write-Host "SHA256：$hash"

[CmdletBinding()]
param(
    [string]$Version = "1.7.0",
    [string]$Release = "1"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "AlarmPushCenter.OpenWrt\AlarmPushCenter.OpenWrt.csproj"
$work = Join-Path $root "artifacts\openwrt-package"
$publish = Join-Path $work "publish"
$data = Join-Path $work "data"
$control = Join-Path $work "control"
$output = Join-Path $root "dist\alarm-push-center_${Version}-${Release}_aarch64_cortex-a53.ipk"

$workspacePath = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
$workPath = [IO.Path]::GetFullPath($work)
if (-not $workPath.StartsWith($workspacePath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "打包临时目录必须位于项目目录内。"
}
Remove-Item -LiteralPath $workPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish,$data,$control,(Split-Path $output) | Out-Null

dotnet publish $project -c Release -r linux-musl-arm64 --self-contained true -o $publish `
    -p:Version=$Version -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "OpenWrt ARM64 发布失败。" }

$usrBin = Join-Path $data "usr\bin"
$etcInit = Join-Path $data "etc\init.d"
$etcConfig = Join-Path $data "etc\alarm-push-center"
New-Item -ItemType Directory -Force -Path $usrBin,$etcInit,$etcConfig | Out-Null
Copy-Item -LiteralPath (Join-Path $publish "alarm-push-center") -Destination $usrBin
Copy-Item -LiteralPath (Join-Path $publish "libSystem.IO.Ports.Native.so") -Destination $usrBin
Copy-Item -LiteralPath (Join-Path $publish "alarm-push-center.staticwebassets.endpoints.json") -Destination $usrBin
Copy-Item -LiteralPath (Join-Path $publish "wwwroot") -Destination $usrBin -Recurse
$htmlPath = Join-Path $usrBin 'wwwroot/index.html'
$html = [IO.File]::ReadAllText($htmlPath)
$html = [regex]::Replace($html, '(?<asset>/[^"''<>?]+\.(?:js|css))(?:\?v=[^"''<>]*)?', ('${asset}?v=' + $Version))
[IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $root "openwrt\files\alarm-push-center.init") -Destination (Join-Path $etcInit "alarm-push-center")
$libexec = Join-Path $data 'usr/libexec'
New-Item -ItemType Directory -Force -Path $libexec | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'openwrt/files/alarm-push-center-upgrade') -Destination $libexec
Copy-Item -LiteralPath (Join-Path $root "openwrt\files\config.json") -Destination $etcConfig
Copy-Item -LiteralPath (Join-Path $root "openwrt\files\console-settings.json") -Destination $etcConfig
Copy-Item -LiteralPath (Join-Path $root "openwrt\files\mobile-devices.json") -Destination $etcConfig
[IO.File]::WriteAllText((Join-Path $etcConfig "phone-gateway.json"), '{"Enabled":false,"Address":"http://192.168.0.1:2333"}', [Text.UTF8Encoding]::new($false))
foreach ($luciDirectory in @("usr", "www")) {
    Copy-Item -LiteralPath (Join-Path $root "openwrt\luci\$luciDirectory") -Destination $data -Recurse -Force
}

Copy-Item -Path (Join-Path $root "openwrt\control\*") -Destination $control
$controlFile = Join-Path $control "control"
(Get-Content -LiteralPath $controlFile -Raw).Replace("Version: 1.0.0-1", "Version: $Version-$Release") | Set-Content -LiteralPath $controlFile -NoNewline

# OpenWrt shell scripts and metadata must use Unix LF endings; CRLF breaks the init-script shebang.
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
foreach ($unixFile in @(
    (Join-Path $etcInit "alarm-push-center"),
    (Join-Path $libexec "alarm-push-center-upgrade"),
    (Join-Path $control "control"),
    (Join-Path $control "conffiles"),
    (Join-Path $control "postinst"),
    (Join-Path $control "prerm")
)) {
    $unixText = [IO.File]::ReadAllText($unixFile).Replace("`r`n", "`n")
    [IO.File]::WriteAllText($unixFile, $unixText, $utf8NoBom)
}

function Write-TarGzip([string]$Source, [string]$Destination, [string[]]$Executables) {
    $file = [System.IO.File]::Create($Destination)
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionLevel]::SmallestSize, $true)
        try {
            $writer = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Ustar, $true)
            try {
                $items = Get-ChildItem -LiteralPath $Source -Recurse | Sort-Object { $_.FullName.Length }
                foreach ($item in $items) {
                    $relativeName = [System.IO.Path]::GetRelativePath($Source, $item.FullName).Replace('\','/')
                    $name = "./$relativeName"
                    $kind = if ($item.PSIsContainer) { [System.Formats.Tar.TarEntryType]::Directory } else { [System.Formats.Tar.TarEntryType]::RegularFile }
                    $entry = [System.Formats.Tar.UstarTarEntry]::new($kind, $name)
                    $entry.Uid = 0; $entry.Gid = 0; $entry.UserName = "root"; $entry.GroupName = "root"
                    $entry.ModificationTime = [DateTimeOffset]::UtcNow
                    if ($item.PSIsContainer) { $entry.Mode = [Convert]::ToInt32("755",8) }
                    else {
                        $entry.Mode = if ($Executables -contains $relativeName) { [Convert]::ToInt32("755",8) } else { [Convert]::ToInt32("644",8) }
                        $entry.DataStream = [System.IO.File]::OpenRead($item.FullName)
                    }
                    try { $writer.WriteEntry($entry) } finally { if ($entry.DataStream) { $entry.DataStream.Dispose() } }
                }
            } finally { $writer.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $file.Dispose() }
}

$controlTar = Join-Path $work "control.tar.gz"
$dataTar = Join-Path $work "data.tar.gz"
Write-TarGzip $control $controlTar @("postinst","prerm")
Write-TarGzip $data $dataTar @("usr/bin/alarm-push-center","etc/init.d/alarm-push-center","usr/libexec/alarm-push-center-upgrade")

# OpenWrt opkg expects a gzip-compressed tar container, rather than Debian's ar container.
$container = Join-Path $work "container"
New-Item -ItemType Directory -Force -Path $container | Out-Null
[IO.File]::WriteAllText((Join-Path $container "debian-binary"), "2.0`n", $utf8NoBom)
Copy-Item -LiteralPath $controlTar,$dataTar -Destination $container
Write-TarGzip $container $output @()

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($output + ".sha256"), "$hash  $([System.IO.Path]::GetFileName($output))`n", $utf8NoBom)
$installedBytes = (Get-ChildItem -LiteralPath $data -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host "OpenWrt IPK 已生成：$output"
Write-Host "IPK 大小：$((Get-Item $output).Length) 字节；安装后约：$installedBytes 字节"
Write-Host "SHA256：$hash"

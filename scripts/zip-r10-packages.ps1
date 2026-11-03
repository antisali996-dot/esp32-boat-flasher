param([string]$Stage = 'work\r10-stage', [string]$OutDir = 'dist-r10-numberfix')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
Add-Type -AssemblyName System.IO.Compression.FileSystem

$stagePath = Join-Path $root $Stage
$outPath = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$verifyRoot = Join-Path $root 'work\r10-verify'
if (Test-Path $verifyRoot) { Remove-Item -LiteralPath $verifyRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$results = @()
foreach ($dir in Get-ChildItem -LiteralPath $stagePath -Directory) {
    $name = $dir.Name
    $zip = Join-Path $outPath ($name + '.zip')
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    $staging = Join-Path $root ('work\r10-zip-' + $name)
    if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Copy-Item -LiteralPath $dir.FullName -Destination (Join-Path $staging $name) -Recurse -Force
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -LiteralPath $staging -Recurse -Force

    # 解压验收
    $out = Join-Path $verifyRoot $name
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $out)
    $inner = Join-Path $out $name

    $proc = Start-Process -FilePath (Join-Path $inner 'ESP32小船烧录工具.exe') -ArgumentList '--self-check' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "$name 解压后自检失败" }

    $manifest = [System.IO.File]::ReadAllText((Join-Path $inner '文件校验.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $bad = 0
    foreach ($prop in $manifest.PSObject.Properties) {
        $f = Join-Path $inner $prop.Name.Replace('/', '\')
        if (-not (Test-Path $f)) { Write-Host "缺少 $($prop.Name)"; $bad++; continue }
        if ((HashOf $f) -ne $prop.Value) { Write-Host "哈希不符 $($prop.Name)"; $bad++ }
    }
    if ($bad -gt 0) { throw "$name 有 $bad 个文件校验失败" }

    $suiteName = '590-20-r10'
    $catalog = [System.IO.File]::ReadAllText((Join-Path $inner ('固件库\' + $suiteName + '\固件\固件清单.json')), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($role in @('BOAT','REMOTE')) {
        $spec = $catalog.roles.$role
        $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes((Join-Path $inner ('固件库\' + $suiteName + '\固件\' + $spec.app))))
        if (-not $text.Contains($spec.fwid)) { throw "$name $role 镜像 FWID 与清单不一致" }
        if ($spec.fwid -notmatch '^JAYFW[BR]10-') { throw "$name $role FWID 不是 r10" }
    }
    if ($catalog.version -ne '20261103-590rpm-r10' -or $catalog.target_rpm -ne 590 -or $catalog.overspeed_rpm -ne 600) {
        throw "$name 固件参数不符"
    }

    # r10 快照清单是 4 列：sha256、字节数、相对路径、来源绝对路径。
    # firmware/ 七个镜像不复制进源码快照目录（它们在固件库里，镜像哈希.txt 已单独核验），这里跳过。
    $snapDir = Join-Path $inner '源码快照'
    $snapBad = 0
    $snapCount = 0
    foreach ($line in (Get-Content -LiteralPath (Join-Path $snapDir 'SHA256SUMS.txt') -Encoding UTF8)) {
        if ($line.StartsWith('#')) { continue }
        $parts = $line -split '\s+'
        if ($parts.Count -lt 3) { continue }
        if ($parts[2] -like 'firmware/*') { continue }
        $snapCount++
        $f = Join-Path $snapDir $parts[2].Replace('/', '\')
        if (-not (Test-Path $f)) { $snapBad++; Write-Host "快照缺文件 $($parts[2])"; continue }
        if ((HashOf $f) -ne $parts[0]) { $snapBad++; Write-Host "快照哈希不符 $($parts[2])" }
    }
    if ($snapCount -eq 0) { throw "$name 源码快照清单为空" }
    if ($snapBad -gt 0) { throw "$name 源码快照有 $snapBad 项不符" }

    $preset = [System.IO.File]::ReadAllText((Join-Path $inner 'preset.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($name -like '*A-*') {
        if ($preset.kind -ne 'general') { throw "$name 应为通用包" }
    } else {
        if ($preset.kind -ne 'category' -or -not $preset.boat_mac -or -not $preset.remote_mac) { throw "$name 预设不完整" }
        if ($preset.boat_fwid -ne $catalog.roles.BOAT.fwid) { throw "$name 预设小船 FWID 不符" }
        if ($preset.remote_fwid -ne $catalog.roles.REMOTE.fwid) { throw "$name 预设遥控器 FWID 不符" }
    }
    if ($preset.id -notlike 'r10-*') { throw "$name preset id 不是 r10" }
    if (-not (Test-Path (Join-Path $inner '现场诊断-使用说明.txt'))) { throw "$name 缺少诊断说明" }

    $results += [pscustomobject]@{
        Package = $name
        Bytes = (Get-Item -LiteralPath $zip).Length
        SHA256 = (HashOf $zip)
        Files = $manifest.PSObject.Properties.Name.Count
        Preset = $(if ($preset.kind -eq 'category') { "小船 $($preset.boat_mac) / 遥控器 $($preset.remote_mac)" } else { '通用（无 MAC 限制）' })
    }
}
$results | Format-List | Out-String | Write-Host
Write-Host "全部验收通过：$($results.Count) 个包"

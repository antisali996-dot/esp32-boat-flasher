param([string]$Stage = 'work\r11-stage', [string]$OutDir = 'dist-r11-round5fix')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
Add-Type -AssemblyName System.IO.Compression.FileSystem

$stagePath = Join-Path $root $Stage
$outPath = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$verifyRoot = Join-Path $root 'work\r11-verify'
if (Test-Path $verifyRoot) {
    # 解压目录里可能残留运行中的 exe；先尝试清理，失败则换一个新目录名。
    try { Remove-Item -LiteralPath $verifyRoot -Recurse -Force -ErrorAction Stop }
    catch { $verifyRoot = Join-Path $root ('work\r11-verify-' + (Get-Date -Format 'HHmmss')) }
}
New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$boat11 = 'JAYFWB11-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr11-osoff'
$remote11 = 'JAYFWR11-S20-A70-110-U1278-1500-1722-D1750-2150'
$boat10 = 'JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared'
$remote10 = 'JAYFWR10-S20-A70-110-U1278-1500-1722-D1750-2150'

$results = @()
foreach ($dir in Get-ChildItem -LiteralPath $stagePath -Directory) {
    $name = $dir.Name
    $zip = Join-Path $outPath ($name + '.zip')
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    $staging = Join-Path $root ('work\r11-zip-' + $name)
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

    # 两套固件都在，且 FWID 与清单一致
    $c11 = [System.IO.File]::ReadAllText((Join-Path $inner '固件库\590-20-r11\固件\固件清单.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $c10 = [System.IO.File]::ReadAllText((Join-Path $inner '固件库\590-20-r10\固件\固件清单.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($c11.version -ne '20261103-590rpm-r11') { throw "$name r11 版本不符" }
    if ($c10.version -ne '20261103-590rpm-r10') { throw "$name r10 版本不符" }
    if ($c11.roles.BOAT.fwid -ne $boat11) { throw "$name r11 小船 FWID 不符" }
    if ($c11.roles.REMOTE.fwid -ne $remote11) { throw "$name r11 遥控器 FWID 不符" }
    if ($c10.roles.BOAT.fwid -ne $boat10) { throw "$name r10 小船 FWID 不符" }
    if ($c11.overspeed_lockout -ne 'off') { throw "$name r11 未声明锁存关闭" }

    foreach ($pair in @(@('590-20-r11', $boat11, 'boat-app.bin'), @('590-20-r11', $remote11, 'remote-app.bin'),
                        @('590-20-r10', $boat10, 'boat-app.bin'), @('590-20-r10', $remote10, 'remote-app.bin'))) {
        $p = Join-Path $inner ('固件库\' + $pair[0] + '\固件\' + $pair[2])
        $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($p))
        if (-not $text.Contains($pair[1])) { throw "$name $($pair[0]) $($pair[2]) 镜像 FWID 与清单不一致" }
    }

    # 源码快照
    $snapRoot = Join-Path $inner '源码快照'
    foreach ($ver in @('r11','r10')) {
        $sd = Join-Path $snapRoot $ver
        $sums = Join-Path $sd 'SHA256SUMS.txt'
        if (-not (Test-Path $sums)) { throw "$name 缺少 $ver 源码快照清单" }
        $snapBad = 0
        $snapCount = 0
        foreach ($line in (Get-Content -LiteralPath $sums -Encoding UTF8)) {
            if ($line.StartsWith('#')) { continue }
            $parts = $line -split '\s+'
            if ($parts.Count -lt 3) { continue }
            if ($parts[2] -like 'firmware/*') { continue }
            $snapCount++
            $f = Join-Path $sd $parts[2].Replace('/', '\')
            if (-not (Test-Path $f)) { $snapBad++; continue }
            if ((HashOf $f) -ne $parts[0]) { $snapBad++ }
        }
        if ($snapCount -eq 0) { throw "$name $ver 源码快照清单为空" }
        if ($snapBad -gt 0) { throw "$name $ver 源码快照有 $snapBad 项不符" }
    }

    $preset = [System.IO.File]::ReadAllText((Join-Path $inner 'preset.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($name -like '*A-*') {
        if ($preset.kind -ne 'general') { throw "$name 应为通用包" }
    } else {
        if ($preset.kind -ne 'category' -or -not $preset.boat_mac -or -not $preset.remote_mac) { throw "$name 预设不完整" }
        if ($preset.boat_fwid -ne $boat11) { throw "$name 预设小船 FWID 未指向 r11" }
        if ($preset.remote_fwid -ne $remote11) { throw "$name 预设遥控器 FWID 未指向 r11" }
    }
    if ($preset.suite_id -ne '590-20-r11') { throw "$name preset 未指向 r11 套装" }
    if ($preset.id -notlike 'r11-*') { throw "$name preset id 不是 r11" }
    if (-not (Test-Path (Join-Path $inner '现场诊断-使用说明.txt'))) { throw "$name 缺少诊断说明" }
    if (-not (Test-Path (Join-Path $inner '测试记录\测试结果.txt'))) { throw "$name 缺少测试记录" }
    if (-not (Test-Path (Join-Path $inner '固件套装格式规范.md'))) { throw "$name 缺少固件套装格式规范" }

    # 无设备自检：不启动 GUI（GUI 会枚举串口），只用 --self-check 覆盖的静态校验
    $results += [pscustomobject]@{
        Package = $name
        Bytes = (Get-Item -LiteralPath $zip).Length
        SHA256 = (HashOf $zip)
        Files = $manifest.PSObject.Properties.Name.Count
        Preset = $(if ($preset.kind -eq 'category') { "r11 小船 $($preset.boat_mac) / 遥控器 $($preset.remote_mac)" } else { 'r11 通用（无 MAC 限制）' })
    }
}
$results | Format-List | Out-String | Write-Host
Write-Host "全部验收通过：$($results.Count) 个包"
Write-Host "解压验收目录：$verifyRoot"

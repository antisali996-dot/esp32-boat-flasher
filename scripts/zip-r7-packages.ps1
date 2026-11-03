param([string]$DistDir = 'dist-r7')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
Add-Type -AssemblyName System.IO.Compression.FileSystem

$distRoot = Join-Path $root $DistDir
$zips = @()
foreach ($dir in Get-ChildItem -LiteralPath $distRoot -Directory) {
    $zip = Join-Path $root ($DistDir + '\' + $dir.Name + '.zip')
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    $staging = Join-Path $root ('work\zipstage-' + $dir.Name)
    if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Copy-Item -LiteralPath $dir.FullName -Destination (Join-Path $staging $dir.Name) -Recurse -Force
    foreach ($d in @('记录','配置')) {
        $p = Join-Path $staging ($dir.Name + '\' + $d)
        if (-not (Test-Path $p)) { New-Item -ItemType Directory -Force -Path $p | Out-Null }
        $keep = Join-Path $p '说明.txt'
        if (-not (Test-Path $keep)) {
            Set-Content -LiteralPath $keep -Encoding UTF8 -Value '此目录由工具在运行时写入，可以随时清空，不影响工具运行。'
        }
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -LiteralPath $staging -Recurse -Force
    $zips += $zip
}

# 解压验收
$verifyRoot = Join-Path $root ('work\verify-r7')
if (Test-Path $verifyRoot) { Remove-Item -LiteralPath $verifyRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null

$report = @()
foreach ($zip in $zips) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($zip)
    $out = Join-Path $verifyRoot $name
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $out)
    $inner = Join-Path $out $name

    # 1) 自检
    $proc = Start-Process -FilePath (Join-Path $inner 'ESP32小船烧录工具.exe') -ArgumentList '--self-check' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "$name 解压后自检失败" }
    $selfReport = [System.IO.File]::ReadAllText((Join-Path $inner '自检报告.txt'), [System.Text.Encoding]::UTF8)
    if ($selfReport -notmatch '结论：自检通过') { throw "$name 自检报告未通过" }

    # 2) 逐文件哈希
    $manifest = [System.IO.File]::ReadAllText((Join-Path $inner '文件校验.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $bad = 0
    foreach ($prop in $manifest.PSObject.Properties) {
        $f = Join-Path $inner $prop.Name.Replace('/', '\')
        if (-not (Test-Path $f)) { Write-Host "缺少 $($prop.Name)"; $bad++; continue }
        if ((Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant() -ne $prop.Value) {
            Write-Host "哈希不符 $($prop.Name)"; $bad++
        }
    }
    if ($bad -gt 0) { throw "$name 有 $bad 个文件校验失败" }

    # 3) 镜像 FWID 与清单一致
    $catalog = [System.IO.File]::ReadAllText((Join-Path $inner '固件库\590-20-r7\固件\固件清单.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($role in @('BOAT','REMOTE')) {
        $spec = $catalog.roles.$role
        $appPath = Join-Path $inner ('固件库\590-20-r7\固件\' + $spec.app)
        $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($appPath))
        if (-not $text.Contains($spec.fwid)) { throw "$name $role 镜像与清单 FWID 不一致" }
        if ($spec.fwid -notmatch '^JAYFW[BR]7-') { throw "$name $role FWID 不是 r7：$($spec.fwid)" }
    }
    if ($catalog.version -ne '20261103-590rpm-r7') { throw "$name 版本不是 r7" }
    if ($catalog.target_rpm -ne 590 -or $catalog.overspeed_rpm -ne 600) { throw "$name 转速参数不符" }

    # 4) 源码快照与快照清单哈希一致
    $snapDir = Join-Path $inner '源码快照'
    $sums = Get-Content -LiteralPath (Join-Path $snapDir 'SHA256SUMS.txt') -Encoding UTF8
    $snapBad = 0
    foreach ($line in $sums) {
        if ($line.StartsWith('#')) { continue }
        $parts = $line -split '\s+'
        if ($parts.Count -lt 3) { continue }
        $h = $parts[0]; $rel = $parts[2]
        $f = Join-Path $snapDir $rel.Replace('/', '\')
        if (-not (Test-Path $f)) { Write-Host "源码快照缺少 $rel"; $snapBad++; continue }
        if ((Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant() -ne $h) {
            Write-Host "源码快照哈希不符 $rel"; $snapBad++
        }
    }
    if ($snapBad -gt 0) { throw "$name 源码快照有 $snapBad 项不符" }

    # 5) preset 与包类型一致
    $presetPath = Join-Path $inner 'preset.json'
    $preset = [System.IO.File]::ReadAllText($presetPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($name -like '*A-*') {
        if ($preset.kind -ne 'general') { throw "$name 应为通用包" }
    } else {
        if ($preset.kind -ne 'category') { throw "$name 应为分类包" }
        if (-not $preset.boat_mac -or -not $preset.remote_mac) { throw "$name 预设缺少 MAC" }
        if ($preset.boat_fwid -ne $catalog.roles.BOAT.fwid) { throw "$name 预设小船 FWID 不符" }
        if ($preset.remote_fwid -ne $catalog.roles.REMOTE.fwid) { throw "$name 预设遥控器 FWID 不符" }
    }

    $size = (Get-Item -LiteralPath $zip).Length
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $report += [pscustomobject]@{
        Package = $name
        Bytes = $size
        SHA256 = $hash
        Files = $manifest.PSObject.Properties.Name.Count
        Preset = $(if ($preset.kind -eq 'category') { "小船 $($preset.boat_mac) / 遥控器 $($preset.remote_mac)" } else { '通用（无 MAC 限制）' })
    }
}

$report | Format-List | Out-String | Write-Host
Write-Host "全部验收通过：$($report.Count) 个包"

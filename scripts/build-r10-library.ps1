param([string]$SnapDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if ([string]::IsNullOrEmpty($SnapDir)) { $SnapDir = (Get-Content -LiteralPath 'work\r10-snap.txt' -Raw).Trim() }
$snap = Join-Path $root $SnapDir
if (-not (Test-Path $snap)) { throw "缺少 r10 快照：$snap" }

$boatFwid = 'JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared'
$remoteFwid = 'JAYFWR10-S20-A70-110-U1278-1500-1722-D1750-2150'
$version = '20261103-590rpm-r10'
$suiteId = '590-20-r10'

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$libRoot = Join-Path $root 'work\r10-library'
if (Test-Path $libRoot) { Remove-Item -LiteralPath $libRoot -Recurse -Force }
$suite = Join-Path $libRoot $suiteId
$fw = Join-Path $suite '固件'
$tools = Join-Path $suite '工具'
New-Item -ItemType Directory -Force -Path $fw | Out-Null
New-Item -ItemType Directory -Force -Path $tools | Out-Null

# 只用快照里的镜像，不碰固件工程当前目录
$map = @{
    'boat-app.bin' = 'firmware/boat-app.bin'
    'boat-bootloader.bin' = 'firmware/boat-bootloader.bin'
    'boat-partitions.bin' = 'firmware/boat-partitions.bin'
    'remote-app.bin' = 'firmware/remote-app.bin'
    'remote-bootloader.bin' = 'firmware/remote-bootloader.bin'
    'remote-partitions.bin' = 'firmware/remote-partitions.bin'
    'boot_app0.bin' = 'firmware/boot_app0.bin'
}
foreach ($k in $map.Keys) {
    Copy-Item -LiteralPath (Join-Path $snap $map[$k]) -Destination (Join-Path $fw $k) -Force
}

# 从镜像反读 FWID，与预期核对
function Get-Fwid([string]$path) {
    $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($path))
    $m = [regex]::Match($text, 'JAYFW[BR]\d+-[A-Za-z0-9\-]+')
    if (-not $m.Success) { throw "镜像中没有 FWID：$path" }
    return $m.Value
}
$boatActual = Get-Fwid (Join-Path $fw 'boat-app.bin')
$remoteActual = Get-Fwid (Join-Path $fw 'remote-app.bin')
if ($boatActual -ne $boatFwid) { throw "小船 FWID 不符：$boatActual" }
if ($remoteActual -ne $remoteFwid) { throw "遥控器 FWID 不符：$remoteActual" }

$manifest = [ordered]@{
    schema = 10
    version = $version
    steer_deg = 20
    angle_min = 70
    angle_max = 110
    center_us = 1500
    pulse_min_us = 1278
    pulse_max_us = 1722
    pair_protocol = 2
    target_rpm = 590
    overspeed_rpm = 600
    diag_capability = [ordered]@{ BOAT = @('COUNT', 'FAULT'); REMOTE = @() }
    note = '写入时保留 NVS 配对区；只有固件身份 FWID 与本清单一致才算最新；遥控器不支持 COUNT/FAULT'
    roles = [ordered]@{
        BOAT = [ordered]@{
            fwid = $boatFwid; version = $version; steer_deg = 20
            app = 'boat-app.bin'; app_offset = 65536
            bootloader = 'boat-bootloader.bin'; bootloader_offset = 4096
            partitions = 'boat-partitions.bin'; partitions_offset = 32768
            boot_app0 = 'boot_app0.bin'; boot_app0_offset = 57344
        }
        REMOTE = [ordered]@{
            fwid = $remoteFwid; version = $version; steer_deg = 20
            app = 'remote-app.bin'; app_offset = 65536
            bootloader = 'remote-bootloader.bin'; bootloader_offset = 4096
            partitions = 'remote-partitions.bin'; partitions_offset = 32768
            boot_app0 = 'boot_app0.bin'; boot_app0_offset = 57344
        }
    }
}
$manifestPath = Join-Path $fw '固件清单.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$fwWork = 'D:\1OPENAIworkspace\esp32-boat-debug'
Copy-Item -LiteralPath (Join-Path $fwWork 'release-kit\ESP32-保姆烧录工具-左右20度\工具\esptool.exe') -Destination (Join-Path $tools 'esptool.exe') -Force
Copy-Item -LiteralPath (Join-Path $fwWork 'release-kit\ESP32-保姆烧录工具-左右20度\工具\GPL-2.0.txt') -Destination (Join-Path $tools 'GPL-2.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $fwWork 'release-kit\ESP32-保姆烧录工具-左右20度\工具\NOTICE.txt') -Destination (Join-Path $tools 'NOTICE.txt') -Force

$import = [ordered]@{
    id = $suiteId
    version = $version
    target_rpm = 590
    overspeed_rpm = 600
    steer_deg = 20
    boat_fwid = $boatFwid
    remote_fwid = $remoteFwid
    source = 'coordinator-approved r10 snapshot: ' + $SnapDir
    manifest_sha256 = (HashOf $manifestPath).ToUpperInvariant()
}
$import | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $suite 'import.json') -Encoding UTF8

# 编译记录（引用快照来源，不重新编译）
$buildRec = @"
r10 固件来源：主协调端已确认的发布产物
快照目录：$SnapDir
镜像来源：
  boat-app.bin            <- work/build-r10rel-boat/boat.ino.bin
  boat-bootloader.bin     <- work/build-r10rel-boat/boat.ino.bootloader.bin
  boat-partitions.bin     <- work/build-r10rel-boat/boat.ino.partitions.bin
  remote-app.bin          <- work/build-r10rel-remote/remote.ino.bin
  remote-bootloader.bin   <- work/build-r10rel-remote/remote.ino.bootloader.bin
  remote-partitions.bin   <- work/build-r10rel-remote/remote.ino.partitions.bin
  boot_app0.bin           <- arduino-data esp32 3.0.2 partitions

主协调端确认：两端身份文件一致、仅改指定三个宏、新 BIN 大小/SHA256/内嵌 FWID 与报告一致、
-DNDEBUG 复跑 13383 项通过。

镜像实测：
  boat-app.bin   $(Get-Item (Join-Path $fw 'boat-app.bin')).Length 字节  SHA256 $(HashOf (Join-Path $fw 'boat-app.bin'))
  remote-app.bin $(Get-Item (Join-Path $fw 'remote-app.bin')).Length 字节  SHA256 $(HashOf (Join-Path $fw 'remote-app.bin'))
FWID（从镜像反读）：
  小船   $boatActual
  遥控器 $remoteActual
工具链（原构建）：arduino-cli 1.5.1 / esp32:esp32 3.0.2 / ESP32Servo 3.2.1 / U8g2 2.36.19
本工具包不重新编译固件，仅封装已批准产物。
"@
Set-Content -LiteralPath (Join-Path $suite '编译记录.txt') -Value $buildRec -Encoding UTF8

Write-Host "固件库：$suite"
Write-Host "小船 FWID：$boatActual"
Write-Host "遥控器 FWID：$remoteActual"

param(
    [string]$Stamp = '',
    [string]$DistDir = 'dist-r7'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if ([string]::IsNullOrEmpty($Stamp)) { $Stamp = (Get-Content -LiteralPath 'work\r7-stamp.txt' -Raw).Trim().Split('=')[1] }
$snap = Join-Path $root ("work\r7-source-" + $Stamp)
$build = Join-Path $root 'work\r7-build'
$fwWork = 'D:\1OPENAIworkspace\esp32-boat-debug'
if (-not (Test-Path $snap)) { throw "缺少 r7 源码快照：$snap" }
if (-not (Test-Path $build)) { throw "缺少 r7 构建产物：$build" }

$boatFwid = 'JAYFWB7-S20-A70-110-U1278-1500-1722-R590-OS600-Cr7-pendingerr-port'
$remoteFwid = 'JAYFWR7-S20-A70-110-U1278-1500-1722-D1750-2150'
$version = '20261103-590rpm-r7'
$suiteId = '590-20-r7'

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# ---- 1. 建立 r7 固件库目录（工具运行时结构）----
$suiteName = '590-20-r7'
$suiteRoot = Join-Path $root ('work\r7-library\' + $suiteName)
if (Test-Path (Join-Path $root 'work\r7-library')) { Remove-Item -LiteralPath (Join-Path $root 'work\r7-library') -Recurse -Force }
$fwDir = Join-Path $suiteRoot '固件'
$toolDir = Join-Path $suiteRoot '工具'
New-Item -ItemType Directory -Force -Path $fwDir | Out-Null
New-Item -ItemType Directory -Force -Path $toolDir | Out-Null

Copy-Item -LiteralPath (Join-Path $build 'build-boat\boat.ino.bin') -Destination (Join-Path $fwDir 'boat-app.bin') -Force
Copy-Item -LiteralPath (Join-Path $build 'build-boat\boat.ino.bootloader.bin') -Destination (Join-Path $fwDir 'boat-bootloader.bin') -Force
Copy-Item -LiteralPath (Join-Path $build 'build-boat\boat.ino.partitions.bin') -Destination (Join-Path $fwDir 'boat-partitions.bin') -Force
Copy-Item -LiteralPath (Join-Path $build 'build-remote\remote.ino.bin') -Destination (Join-Path $fwDir 'remote-app.bin') -Force
Copy-Item -LiteralPath (Join-Path $build 'build-remote\remote.ino.bootloader.bin') -Destination (Join-Path $fwDir 'remote-bootloader.bin') -Force
Copy-Item -LiteralPath (Join-Path $build 'build-remote\remote.ino.partitions.bin') -Destination (Join-Path $fwDir 'remote-partitions.bin') -Force
Copy-Item -LiteralPath (Join-Path $fwWork 'tools\arduino-data\packages\esp32\hardware\esp32\3.0.2\tools\partitions\boot_app0.bin') -Destination (Join-Path $fwDir 'boot_app0.bin') -Force

# 从实际镜像反读 FWID，不抄说明里的字符串
function Get-Fwid([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    $m = [regex]::Match($text, 'JAYFW[BR]\d+-[A-Za-z0-9\-]+')
    if (-not $m.Success) { throw "镜像中没有 FWID：$path" }
    return $m.Value
}
$boatActual = Get-Fwid (Join-Path $fwDir 'boat-app.bin')
$remoteActual = Get-Fwid (Join-Path $fwDir 'remote-app.bin')
if ($boatActual -ne $boatFwid) { throw "小船镜像 FWID 不符：$boatActual" }
if ($remoteActual -ne $remoteFwid) { throw "遥控器镜像 FWID 不符：$remoteActual" }

$manifest = [ordered]@{
    schema = 7
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
    note = '写入时保留 NVS 配对区；只有固件身份 FWID 与本清单一致才算最新'
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
$manifestPath = Join-Path $fwDir '固件清单.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $fwWork 'release-kit\ESP32-保姆烧录工具-左右20度\工具\esptool.exe') -Destination (Join-Path $toolDir 'esptool.exe') -Force
Copy-Item -LiteralPath (Join-Path $fwWork 'release-kit\ESP32-保姆烧录工具-左右20度\工具\GPL-2.0.txt') -Destination (Join-Path $toolDir 'GPL-2.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $fwWork 'release-kit\ESP32-保姆烧录工具-左右20度\工具\NOTICE.txt') -Destination (Join-Path $toolDir 'NOTICE.txt') -Force

$import = [ordered]@{
    id = $suiteId
    version = $version
    target_rpm = 590
    overspeed_rpm = 600
    steer_deg = 20
    boat_fwid = $boatFwid
    remote_fwid = $remoteFwid
    source = 'r7 snapshot build'
    manifest_sha256 = (HashOf $manifestPath).ToUpperInvariant()
}
$import | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $suiteRoot 'import.json') -Encoding UTF8

# ---- 2. 三类包 ----
$packages = @(
    @{ Name = 'A-通用保姆包'; Kind = 'general'; Title = '通用包'; PresetId = 'r7-general';
       BoatMac = ''; RemoteMac = '' },
    @{ Name = 'B-旧小船分类包'; Kind = 'category'; Title = '旧小船分类包'; PresetId = 'r7-old-boat-F07294';
       BoatMac = '24:62:AB:F0:72:94'; RemoteMac = '24:62:AB:EF:CF:84' },
    @{ Name = 'C-新小船分类包'; Kind = 'category'; Title = '新小船分类包'; PresetId = 'r7-new-boat-F13664';
       BoatMac = '24:62:AB:F1:36:64'; RemoteMac = '24:62:AB:EF:CF:84' }
)

$distRoot = Join-Path $root $DistDir
if (Test-Path $distRoot) { Remove-Item -LiteralPath $distRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

$summary = @()
foreach ($pkg in $packages) {
    $name = 'ESP32-r7-590-20-' + $pkg.Name
    $pkgDir = Join-Path $distRoot $name
    New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null

    Copy-Item -LiteralPath (Join-Path $root 'work\build\ESP32小船烧录工具.exe') -Destination (Join-Path $pkgDir 'ESP32小船烧录工具.exe') -Force
    Copy-Item -LiteralPath (Join-Path $root 'work\r7-library') -Destination (Join-Path $pkgDir '固件库') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\启动工具.cmd') -Destination (Join-Path $pkgDir '双击打开烧录工具.cmd') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\r7-使用说明.txt') -Destination (Join-Path $pkgDir '先看这里-使用说明.txt') -Force

    # preset.json
    $preset = [ordered]@{
        id = $pkg.PresetId
        kind = $pkg.Kind
        title = $pkg.Title
        suite_id = $suiteId
        boat_mac = $pkg.BoatMac
        remote_mac = $pkg.RemoteMac
        boat_fwid = $boatFwid
        remote_fwid = $remoteFwid
        note = '分类配置只用于核对芯片地址；不修改固件源码、不改芯片出厂 MAC'
    }
    $preset | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $pkgDir 'preset.json') -Encoding UTF8

    # 源码快照（实际编译输入）
    $srcDst = Join-Path $pkgDir '源码快照'
    New-Item -ItemType Directory -Force -Path $srcDst | Out-Null
    Copy-Item -LiteralPath (Join-Path $snap 'boat') -Destination (Join-Path $srcDst 'boat') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'remote') -Destination (Join-Path $srcDst 'remote') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'tests') -Destination (Join-Path $srcDst 'tests') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'SHA256SUMS.txt') -Destination $srcDst -Force

    # 构建记录
    $buildRec = Join-Path $pkgDir '构建记录'
    New-Item -ItemType Directory -Force -Path $buildRec | Out-Null
    Copy-Item -LiteralPath (Join-Path $build 'logs\compile-boat.log') -Destination (Join-Path $buildRec 'boat-编译记录.txt') -Force
    Copy-Item -LiteralPath (Join-Path $build 'logs\compile-remote.log') -Destination (Join-Path $buildRec 'remote-编译记录.txt') -Force

    $fwHead = (& git -C $fwWork rev-parse HEAD).Trim()
    $toolHead = (& git rev-parse HEAD).Trim()
    $info = @"
r7 构建信息
=============
快照目录     ：work\r7-source-$Stamp
固件工程 HEAD：$fwHead
固件工程状态 ：含未提交改动（快照包含未提交文件，见源码快照/SHA256SUMS.txt）
工具工程 HEAD：$toolHead

编译命令（工作区外只读输入，产物写在工具工程 work\r7-build）：
  arduino-cli compile --fqbn esp32:esp32:esp32 --build-path work\r7-build\build-boat   --libraries <固件工程>/libraries work\r7-source-$Stamp\boat
  arduino-cli compile --fqbn esp32:esp32:esp32 --build-path work\r7-build\build-remote --libraries <固件工程>/libraries work\r7-source-$Stamp\remote
退出码：boat=0，remote=0

工具链：
  arduino-cli 1.5.1 (Commit 01f3d4f2b, 2026-06-05)
  esp32:esp32 3.0.2
  ESP32Servo 3.2.1, U8g2 2.36.19
  g++ (MinGW-W64 16.2.0) 用于固件逻辑回归

规模：
  boat   ：Sketch 943765 字节（72%），RAM 46520 字节（14%）
  remote ：Sketch 912073 字节（69%），RAM 43996 字节（13%）

FWID（从镜像反读）：
  小船   ：$boatActual
  遥控器 ：$remoteActual
"@
    Set-Content -LiteralPath (Join-Path $buildRec '构建信息.txt') -Value $info -Encoding UTF8

    # 镜像哈希表
    $img = @()
    foreach ($f in @('boat-app.bin','boat-bootloader.bin','boat-partitions.bin','remote-app.bin','remote-bootloader.bin','remote-partitions.bin','boot_app0.bin','固件清单.json')) {
        $full = Join-Path $pkgDir ('固件库\' + $suiteName + '\固件\' + $f)
        $img += ('{0}  {1}  {2}' -f (HashOf $full), (Get-Item -LiteralPath $full).Length, $f)
    }
    $img | Set-Content -LiteralPath (Join-Path $buildRec '镜像哈希.txt') -Encoding UTF8

    # 包内整体校验
    $checks = [ordered]@{}
    Get-ChildItem -LiteralPath $pkgDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($pkgDir.Length + 1).Replace('\','/')
        if ($rel -eq '文件校验.json') { return }
        $checks[$rel] = HashOf $_.FullName
    }
    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $pkgDir '文件校验.json') -Encoding UTF8

    # 自检（GUI 子系统用 Start-Process -Wait）
    $proc = Start-Process -FilePath (Join-Path $pkgDir 'ESP32小船烧录工具.exe') -ArgumentList '--self-check' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "$name 包内自检失败（$($proc.ExitCode)）" }

    $summary += [pscustomobject]@{ Package = $name; Dir = $pkgDir; Files = $checks.Count }
}

$summary | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "固件库：$suiteRoot"
Write-Host "交付目录：$distRoot"

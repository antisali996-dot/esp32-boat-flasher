param([string]$SnapDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if ([string]::IsNullOrEmpty($SnapDir)) { $SnapDir = (Get-Content -LiteralPath 'work\r10-snap.txt' -Raw).Trim() }
$snap = Join-Path $root $SnapDir
if (-not (Test-Path $snap)) { throw "缺少 r10 快照：$snap" }
$suiteName = '590-20-r10'
$lib  = Join-Path $root 'work\r10-library'
$suite = Join-Path $lib $suiteName
$exe  = Join-Path $root 'work\build\ESP32小船烧录工具.exe'
foreach ($p in @($snap, $lib, $exe, (Join-Path $suite '固件\固件清单.json'))) { if (-not (Test-Path $p)) { throw "缺少依赖：$p" } }

$catalog = [System.IO.File]::ReadAllText((Join-Path $suite '固件\固件清单.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$boatFwid = $catalog.roles.BOAT.fwid
$remoteFwid = $catalog.roles.REMOTE.fwid
if ($catalog.version -ne '20261103-590rpm-r10') { throw '固件库不是 r10' }
if ($boatFwid -notlike 'JAYFWB10-*' -or $remoteFwid -notlike 'JAYFWR10-*') { throw 'FWID 不是 r10' }

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$packages = @(
    @{ Name = 'A-通用保姆包'; Kind = 'general'; Title = '通用包'; PresetId = 'r10-general'; BoatMac = ''; RemoteMac = '' },
    @{ Name = 'B-旧小船分类包'; Kind = 'category'; Title = '旧小船分类包'; PresetId = 'r10-old-boat-F07294';
       BoatMac = '24:62:AB:F0:72:94'; RemoteMac = '24:62:AB:EF:CF:84' },
    @{ Name = 'C-新小船分类包'; Kind = 'category'; Title = '新小船分类包'; PresetId = 'r10-new-boat-F13664';
       BoatMac = '24:62:AB:F1:36:64'; RemoteMac = '24:62:AB:EF:CF:84' }
)

$stage = Join-Path $root 'work\r10-stage'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$made = @()
foreach ($pkg in $packages) {
    $name = 'ESP32-r10-590-20-字段校验修复版-' + $pkg.Name
    $dir = Join-Path $stage $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    Copy-Item -LiteralPath $exe -Destination (Join-Path $dir 'ESP32小船烧录工具.exe') -Force
    Copy-Item -LiteralPath $lib -Destination (Join-Path $dir '固件库') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\启动工具.cmd') -Destination (Join-Path $dir '双击打开烧录工具.cmd') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\r10-使用说明.txt') -Destination (Join-Path $dir '先看这里-使用说明.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\现场诊断-使用说明.txt') -Destination (Join-Path $dir '现场诊断-使用说明.txt') -Force
    # 本轮修复摘要：让包内说明准确记录改了什么、什么没验证。
    @"
本包为 r10 字段校验修复版（工具端本轮重新构建）。
本轮修复：FAULT 的 rpm/integral 等数字字段改为严格校验（类型、范围、定点两位小数、
InvariantCulture）；任一字段非法时整行判“解析失败”，保留原文与失败原因，
不进入关联、不补齐/覆盖/解除已有故障组。
上一版“故障关联修复版”因该 P2 未通过复审，已保留原处并标注，不再交付。
本包固件未重新编译、未替换，仍是已批准的 r10 镜像。
修复内容：FAULT 关联不再把 boot 为空/占位值判为完整；同键同类型内容冲突时锁定“冲突”；
两行经正式采集路径形成真实关联组，不再停在“待适配”。
证据状态：待适配 / 设备分段未确认 / 启动分段未确认 / 不完整（含缺哪一行）/ 完整 / 冲突 / 无故障报告。
导出新增 FAULT关联.csv、FAULT关联状态变化.csv，均进入文件校验清单与 SHA256。
本包固件未重新编译、未替换，仍是已批准的 r10 镜像（哈希见 构建记录\镜像哈希.txt）。
未验证：未连接任何真实设备；超速、舵机微动、反复停启三个现场问题仍无实机验证结论。
"@ | Set-Content -LiteralPath (Join-Path $dir '本轮修复说明.txt') -Encoding UTF8

    $preset = [ordered]@{
        id = $pkg.PresetId
        kind = $pkg.Kind
        title = $pkg.Title
        suite_id = $suiteName
        boat_mac = $pkg.BoatMac
        remote_mac = $pkg.RemoteMac
        boat_fwid = $boatFwid
        remote_fwid = $remoteFwid
        note = '分类配置只用于核对芯片地址；不修改固件源码、不改芯片出厂 MAC'
    }
    $preset | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dir 'preset.json') -Encoding UTF8

    # 源码快照（已批准的 r10 输入，非本工具重新编译）
    $srcDst = Join-Path $dir '源码快照'
    New-Item -ItemType Directory -Force -Path $srcDst | Out-Null
    Copy-Item -LiteralPath (Join-Path $snap 'boat') -Destination (Join-Path $srcDst 'boat') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'remote') -Destination (Join-Path $srcDst 'remote') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'tests') -Destination (Join-Path $srcDst 'tests') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'SHA256SUMS.txt') -Destination $srcDst -Force

    # 构建记录：来源说明 + 镜像哈希（r10 由主协调端批准产物封装，不重编译）
    $buildDst = Join-Path $dir '构建记录'
    New-Item -ItemType Directory -Force -Path $buildDst | Out-Null
    $img = @()
    foreach ($f in @('boat-app.bin','boat-bootloader.bin','boat-partitions.bin','remote-app.bin','remote-bootloader.bin','remote-partitions.bin','boot_app0.bin','固件清单.json')) {
        $full = Join-Path $dir ('固件库\' + $suiteName + '\固件\' + $f)
        $img += ('{0}  {1}  {2}' -f (HashOf $full), (Get-Item -LiteralPath $full).Length, $f)
    }
    $img | Set-Content -LiteralPath (Join-Path $buildDst '镜像哈希.txt') -Encoding UTF8
    $snapHash = Get-Content -LiteralPath (Join-Path $snap 'SHA256SUMS.txt') -Raw
    @"
r10 固件来源：主协调端批准的发布产物
快照目录：$SnapDir
镜像来源：
  boat-app.bin            <- work/build-r10rel-boat/boat.ino.bin
  boat-bootloader.bin     <- work/build-r10rel-boat/boat.ino.bootloader.bin
  boat-partitions.bin     <- work/build-r10rel-boat/boat.ino.partitions.bin
  remote-app.bin          <- work/build-r10rel-remote/remote.ino.bin
  remote-bootloader.bin   <- work/build-r10rel-remote/remote.ino.bootloader.bin
  remote-partitions.bin   <- work/build-r10rel-remote/remote.ino.partitions.bin
  boot_app0.bin           <- arduino-data esp32 3.0.2 partitions

主协调端确认：两端身份文件一致、与备份相比仅改指定三个宏、
新 BIN 大小/SHA256/内嵌 FWID 与报告一致、-DNDEBUG 复跑 13383 项通过。

小船 FWID：$boatFwid
遥控器 FWID：$remoteFwid
版本：$($catalog.version)
目标转速 590 / 超速阈值 600（软件阈值）/ 左右各 20°

本包工具新增：r10 COUNT/FAULT 接口适配（64 位 COUNT、CURRENT/PREVIOUS 配对、
按角色白名单、UNSUPPORTED_COMMAND 停用查询）、重连边界与串口锁迁移修复。
诊断页不调用 esptool，不发送烧录/擦除/BIND/参数修改命令。

快照 SHA256SUMS：
$snapHash
"@ | Set-Content -LiteralPath (Join-Path $buildDst '来源说明.txt') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $suite '编译记录.txt') -Destination (Join-Path $buildDst 'r10-封装记录.txt') -Force

    # 运行期目录
    foreach ($d in @('记录','配置','诊断记录')) {
        $p = Join-Path $dir $d
        New-Item -ItemType Directory -Force -Path $p | Out-Null
        Set-Content -LiteralPath (Join-Path $p '说明.txt') -Encoding UTF8 `
            -Value '此目录由工具在运行时写入，可以随时清空，不影响工具运行。'
    }

    # 自检
    $proc = Start-Process -FilePath (Join-Path $dir 'ESP32小船烧录工具.exe') -ArgumentList '--self-check' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "$name 自检失败（$($proc.ExitCode)）" }
    $report = [System.IO.File]::ReadAllText((Join-Path $dir '自检报告.txt'), [System.Text.Encoding]::UTF8)
    if ($report -notmatch '结论：自检通过') { throw "$name 自检报告未通过" }
    if ($report -notmatch [regex]::Escape($boatFwid)) { throw "$name 自检报告没有小船 r10 FWID" }
    if ($report -notmatch [regex]::Escape($remoteFwid)) { throw "$name 自检报告没有遥控器 r10 FWID" }
    if ($report -notmatch '分区：') { throw "$name 自检报告没有分区信息" }

    # 哈希清单。自检报告内含绝对路径，解压重跑会变，因此与 r7 一致排除在外；
    # 自检结果由打包时与解压验收时两次比对"结论：自检通过"来保证。
    $checks = [ordered]@{}
    Get-ChildItem -LiteralPath $dir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($dir.Length + 1).Replace('\','/')
        if ($rel -eq '文件校验.json' -or $rel -eq '自检报告.txt') { return }
        $checks[$rel] = HashOf $_.FullName
    }
    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dir '文件校验.json') -Encoding UTF8

    $made += $dir
}

$made | ForEach-Object { Write-Host ("已组装：" + $_) }

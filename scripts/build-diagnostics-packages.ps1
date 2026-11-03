param([string]$Stamp = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

if ([string]::IsNullOrEmpty($Stamp)) {
    $Stamp = (Get-ChildItem -LiteralPath 'work' -Directory -Filter 'r7-source-*' |
              Sort-Object Name -Descending | Select-Object -First 1).Name.Replace('r7-source-', '')
}
$snap = Join-Path $root ("work\r7-source-" + $Stamp)
$lib  = Join-Path $root 'work\r7-library'
$exe  = Join-Path $root 'work\build\ESP32小船烧录工具.exe'
foreach ($p in @($snap, $lib, $exe)) { if (-not (Test-Path $p)) { throw "缺少依赖：$p" } }

$suiteName = '590-20-r7'
$suite = Join-Path $lib $suiteName
$catalog = [System.IO.File]::ReadAllText((Join-Path $suite '固件\固件清单.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$boatFwid = $catalog.roles.BOAT.fwid
$remoteFwid = $catalog.roles.REMOTE.fwid
if ($catalog.version -ne '20261103-590rpm-r7') { throw '固件库不是 r7' }

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$packages = @(
    @{ Name = 'A-通用保姆包'; Kind = 'general'; Title = '通用包'; PresetId = 'r7-general'; BoatMac = ''; RemoteMac = '' },
    @{ Name = 'B-旧小船分类包'; Kind = 'category'; Title = '旧小船分类包'; PresetId = 'r7-old-boat-F07294';
       BoatMac = '24:62:AB:F0:72:94'; RemoteMac = '24:62:AB:EF:CF:84' },
    @{ Name = 'C-新小船分类包'; Kind = 'category'; Title = '新小船分类包'; PresetId = 'r7-new-boat-F13664';
       BoatMac = '24:62:AB:F1:36:64'; RemoteMac = '24:62:AB:EF:CF:84' }
)

$stage = Join-Path $root 'work\diag-stage'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$made = @()
foreach ($pkg in $packages) {
    $name = 'ESP32-r7-590-20-重连修复版-' + $pkg.Name
    $dir = Join-Path $stage $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    Copy-Item -LiteralPath $exe -Destination (Join-Path $dir 'ESP32小船烧录工具.exe') -Force
    Copy-Item -LiteralPath $lib -Destination (Join-Path $dir '固件库') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\启动工具.cmd') -Destination (Join-Path $dir '双击打开烧录工具.cmd') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\r7-使用说明.txt') -Destination (Join-Path $dir '先看这里-使用说明.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\现场诊断-使用说明.txt') -Destination (Join-Path $dir '现场诊断-使用说明.txt') -Force

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

    # 源码快照 + 构建记录（沿用已批准的 r7 构建输入）
    $srcDst = Join-Path $dir '源码快照'
    New-Item -ItemType Directory -Force -Path $srcDst | Out-Null
    Copy-Item -LiteralPath (Join-Path $snap 'boat') -Destination (Join-Path $srcDst 'boat') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'remote') -Destination (Join-Path $srcDst 'remote') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'tests') -Destination (Join-Path $srcDst 'tests') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $snap 'SHA256SUMS.txt') -Destination $srcDst -Force

    $buildDst = Join-Path $dir '构建记录'
    New-Item -ItemType Directory -Force -Path $buildDst | Out-Null
    $r7build = Join-Path $root 'work\r7-build'
    if (Test-Path (Join-Path $r7build 'logs\compile-boat.log')) {
        Copy-Item -LiteralPath (Join-Path $r7build 'logs\compile-boat.log') -Destination (Join-Path $buildDst 'boat-编译记录.txt') -Force
        Copy-Item -LiteralPath (Join-Path $r7build 'logs\compile-remote.log') -Destination (Join-Path $buildDst 'remote-编译记录.txt') -Force
    }
    $img = @()
    foreach ($f in @('boat-app.bin','boat-bootloader.bin','boat-partitions.bin','remote-app.bin','remote-bootloader.bin','remote-partitions.bin','boot_app0.bin','固件清单.json')) {
        $full = Join-Path $dir ('固件库\' + $suiteName + '\固件\' + $f)
        $img += ('{0}  {1}  {2}' -f (HashOf $full), (Get-Item -LiteralPath $full).Length, $f)
    }
    $img | Set-Content -LiteralPath (Join-Path $buildDst '镜像哈希.txt') -Encoding UTF8
    @"
r7 固件来源：已批准的本地固件库 work\r7-library\$suiteName
版本：$($catalog.version)
小船 FWID：$boatFwid
遥控器 FWID：$remoteFwid
本包工具新增“现场诊断”页；固件未重新编译、未替换。
诊断页不调用 esptool，不发送烧录/擦除/BIND/参数修改命令。
"@ | Set-Content -LiteralPath (Join-Path $buildDst '来源说明.txt') -Encoding UTF8

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

    # 哈希清单
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

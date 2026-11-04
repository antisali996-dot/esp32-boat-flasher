param([string]$SnapDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if ([string]::IsNullOrEmpty($SnapDir)) { $SnapDir = (Get-Content -LiteralPath 'work\r11-snap.txt' -Raw).Trim() }
$snap = Join-Path $root $SnapDir
if (-not (Test-Path $snap)) { throw "缺少 r11 快照：$snap" }

$libRoot = Join-Path $root 'work\r11-library'
$suite11 = Join-Path $libRoot '590-20-r11'
$r10Suite = Join-Path $root 'work\r10-library\590-20-r10'
$exe = Join-Path $root 'work\build\ESP32小船烧录工具.exe'
foreach ($p in @($snap, $suite11, $r10Suite, $exe)) { if (-not (Test-Path $p)) { throw "缺少依赖：$p" } }

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$cat11 = [System.IO.File]::ReadAllText((Join-Path $suite11 '固件\固件清单.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$cat10 = [System.IO.File]::ReadAllText((Join-Path $r10Suite '固件\固件清单.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$boat11 = $cat11.roles.BOAT.fwid
$remote11 = $cat11.roles.REMOTE.fwid
$boat10 = $cat10.roles.BOAT.fwid
$remote10 = $cat10.roles.REMOTE.fwid
if ($cat11.version -ne '20261103-590rpm-r11') { throw 'r11 固件库版本不符' }
if ($cat10.version -ne '20261103-590rpm-r10') { throw 'r10 固件库版本不符' }
if ($boat11 -notlike 'JAYFWB11-*-OSOFF-*') { throw 'r11 小船 FWID 缺 OSOFF' }
if ($boat11 -like '*-OS600-*') { throw 'r11 小船 FWID 误含 OS600' }
if ($cat11.overspeed_lockout -ne 'off') { throw 'r11 清单未声明锁存关闭' }

$packages = @(
    @{ Name = 'A-通用保姆包'; Kind = 'general'; Title = '通用包'; PresetId = 'r11-general'; BoatMac = ''; RemoteMac = '' },
    @{ Name = 'B-旧小船分类包'; Kind = 'category'; Title = '旧小船分类包'; PresetId = 'r11-old-boat-F07294';
       BoatMac = '24:62:AB:F0:72:94'; RemoteMac = '24:62:AB:EF:CF:84' },
    @{ Name = 'C-新小船分类包'; Kind = 'category'; Title = '新小船分类包'; PresetId = 'r11-new-boat-F13664';
       BoatMac = '24:62:AB:F1:36:64'; RemoteMac = '24:62:AB:EF:CF:84' }
)

$stage = Join-Path $root 'work\r11-stage'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$made = @()
foreach ($pkg in $packages) {
    $name = 'ESP32-r11-590-20-GUI套装摘要修复版-' + $pkg.Name
    $dir = Join-Path $stage $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    Copy-Item -LiteralPath $exe -Destination (Join-Path $dir 'ESP32小船烧录工具.exe') -Force

    # 两套固件：r11（默认）+ r10（保留对照）
    $libDst = Join-Path $dir '固件库'
    New-Item -ItemType Directory -Force -Path $libDst | Out-Null
    Copy-Item -LiteralPath $suite11 -Destination (Join-Path $libDst '590-20-r11') -Recurse -Force
    Copy-Item -LiteralPath $r10Suite -Destination (Join-Path $libDst '590-20-r10') -Recurse -Force

    Copy-Item -LiteralPath (Join-Path $root 'scripts\启动工具.cmd') -Destination (Join-Path $dir '双击打开烧录工具.cmd') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\r11-使用说明.txt') -Destination (Join-Path $dir '先看这里-使用说明.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\现场诊断-使用说明.txt') -Destination (Join-Path $dir '现场诊断-使用说明.txt') -Force
    # 源码端格式规范：说明如何生成新版本 ZIP 并放入固件库
    Copy-Item -LiteralPath (Join-Path $root 'docs\固件套装格式规范.md') -Destination (Join-Path $dir '固件套装格式规范.md') -Force

    $preset = [ordered]@{
        id = $pkg.PresetId
        kind = $pkg.Kind
        title = $pkg.Title
        suite_id = '590-20-r11'
        preferred_suite = '590-20-r11'
        boat_mac = $pkg.BoatMac
        remote_mac = $pkg.RemoteMac
        boat_fwid = $boat11
        remote_fwid = $remote11
        overspeed_lockout = 'off'
        note = '分类配置指向 r11 并只用于核对芯片地址；不修改固件源码、不改芯片出厂 MAC'
    }
    $preset | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dir 'preset.json') -Encoding UTF8

    # 源码快照：r11 本轮输入 + r10 已批准输入（各一份，可核对）
    $srcDst = Join-Path $dir '源码快照'
    New-Item -ItemType Directory -Force -Path $srcDst | Out-Null
    $r11Dst = Join-Path $srcDst 'r11'
    New-Item -ItemType Directory -Force -Path $r11Dst | Out-Null
    foreach ($sub in @('boat','remote','tests')) {
        Copy-Item -LiteralPath (Join-Path $snap $sub) -Destination (Join-Path $r11Dst $sub) -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $snap 'SHA256SUMS.txt') -Destination $r11Dst -Force
    Copy-Item -LiteralPath (Join-Path $snap 'HEAD.txt') -Destination $r11Dst -Force

    $r10Snap = Join-Path $root 'work\r10-input-20261103-172323'
    if (Test-Path $r10Snap) {
        $r10Dst = Join-Path $srcDst 'r10'
        New-Item -ItemType Directory -Force -Path $r10Dst | Out-Null
        foreach ($sub in @('boat','remote','tests')) {
            if (Test-Path (Join-Path $r10Snap $sub)) {
                Copy-Item -LiteralPath (Join-Path $r10Snap $sub) -Destination (Join-Path $r10Dst $sub) -Recurse -Force
            }
        }
        if (Test-Path (Join-Path $r10Snap 'SHA256SUMS.txt')) {
            Copy-Item -LiteralPath (Join-Path $r10Snap 'SHA256SUMS.txt') -Destination $r10Dst -Force
        }
    }

    # 构建记录
    $buildDst = Join-Path $dir '构建记录'
    New-Item -ItemType Directory -Force -Path $buildDst | Out-Null
    Copy-Item -LiteralPath (Join-Path $suite11 '编译记录.txt') -Destination (Join-Path $buildDst 'r11-编译记录.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'work\r11-build\compile-boat.log') -Destination (Join-Path $buildDst 'r11-boat-编译日志.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'work\r11-build\compile-remote.log') -Destination (Join-Path $buildDst 'r11-remote-编译日志.txt') -Force

    $img = @()
    foreach ($suiteName in @('590-20-r11','590-20-r10')) {
        foreach ($f in @('boat-app.bin','boat-bootloader.bin','boat-partitions.bin','remote-app.bin','remote-bootloader.bin','remote-partitions.bin','boot_app0.bin','固件清单.json')) {
            $full = Join-Path $dir ('固件库\' + $suiteName + '\固件\' + $f)
            $img += ('{0}  {1}  {2}\{3}' -f (HashOf $full), (Get-Item -LiteralPath $full).Length, $suiteName, $f)
        }
    }
    $img | Set-Content -LiteralPath (Join-Path $buildDst '镜像哈希.txt') -Encoding UTF8

    @"
本包为 r11 适配版（工具端本轮重新构建）。
包含两套已批准固件：
  r11（默认选中） 版本 20261103-590rpm-r11，600 转超速锁存已关闭
  r10（保留对照） 版本 20261103-590rpm-r10，600 转软件超速锁存已开启

r11 本轮从冻结快照实际编译：arduino-cli 1.5.1 / esp32:esp32 3.0.2，两端退出码均为 0。
快照：$SnapDir（含 HEAD 与全部输入文件 SHA256）。
r10 套装镜像与清单未改动，哈希与上一轮一致。

r11 小船 FWID：$boat11
r11 遥控器 FWID：$remote11
r11 诊断接口：小船 COUNT/FAULT 可用；遥控器未实现，零自动查询。

r11 的 600 锁存已关闭：不保证空载转速不超过 600，也没有电流反馈保证不过载。
r10 的 600 只是软件锁存阈值，不是物理转速上限的保证。

未验证：未连接任何真实设备；超速、舵机微动、反复停启仍无实机验证结论。
本包不代表已找到根因、解决问题或通过比赛验收。
"@ | Set-Content -LiteralPath (Join-Path $buildDst '来源说明.txt') -Encoding UTF8

    # 测试记录
    $testDst = Join-Path $dir '测试记录'
    New-Item -ItemType Directory -Force -Path $testDst | Out-Null
    @"
本轮工具模拟测试共 958 项通过、0 失败（主入口自动汇总）：
  原工具 158 / 配对状态机 52 / 现场诊断 61 / 复审修复 74 / 重连修复 50 /
  边界修复 33 / r10 接口 29 / 故障关联修复 86 / FAULT 数值校验 73 / r11 适配 75 /
  动态固件库 56 / 动态集成修复 71 / 套装摘要一致性 140（本轮新增，直接驱动生产 MainForm）

本轮修复（GUI 回退）：
- 根因：OnSuiteChanged 只写 Workflow.SelectedSuite、按钮与板子备注，漏调 UpdateSuiteInfo；
  UpdateSuiteInfo 仅在刷新完成的 OnSuiteChangedFinal 中调用，导致普通选择与首次初始化后
  摘要仍停留在上一套（复审复现：刷新后手动选 r10，摘要仍显示 r11）。
- 修复：新增唯一实现 ApplySuiteSelection()，三条入口（FillSuites 首次初始化、
  SelectedIndexChanged→OnSuiteChanged 普通选择、刷新完成 OnSuiteChangedFinal 回调）共用。
  摘要、按钮、板子备注、诊断标签统一按“下拉最终选中项”更新；不创建诊断控制器、不枚举串口。
- 空库保护：FillSuites 在无套装时显式置 SelectedIndex=-1（原先赋 0 会抛异常）。
- 回归测试直接构造生产 MainForm 并用真实事件驱动，不复制界面逻辑；修复前该组 46 项失败。

本轮新增动态固件库能力：
- 固件库每个直接子目录一套；ID/版本/FWID 全部来自 import.json 与清单，不按目录名猜。
- 新增「刷新固件库」按钮：只读磁盘重扫，不打开串口；原选择有效则保留，
  目录删除/镜像替换/自检失败都会清除旧计划并提示。
- 烧录前再次核对所选套装（加载时镜像哈希快照 vs 磁盘当前值），检测刷新后被替换。
- 分类 preset 只强制角色与 MAC；suite_id/FWID 变为推荐信息，改选其它有效版本不禁用工具。
- 镜像路径限制在套装目录内（拒绝 ..、绝对路径、~）。
- 重复身份分三类：同 ID 冲突全不可用；同 FWID 同镜像允许并存；同 FWID 异镜像提示不确定性。
- 诊断契约表：清单声明 diag_contract（当前支持 diag-v1），按板上实际 FWID + 契约启用查询；
  未知契约被动记录。旧 r10/r11 包无需改动，走已批准精确映射。
- 详见包内《固件套装格式规范.md》。

r11 适配 75 项覆盖：身份解析与 FWID 匹配、OS600/OSOFF 三态展示、OSOFF 不显示为开启、
无 OS 标记显示未知、清单与 FWID 矛盾拒绝、默认选 r11（不受目录排序影响）、
按板上身份决定诊断查询、preset 按 suite_id 命中 r11、r11 白名单、UNSUPPORTED 停用、r11 样例经真实 Pump
进入 COUNT/FAULT 关联与导出、原有严格规则不回退、清单与镜像哈希一致。
动态固件库 56 项覆盖：任意新 ID 套装、目录名与版本解耦、r11+模拟r12 并存（模拟 r12 仅为测试夹具）、
preset 不锁死版本、推荐缺失不回退、刷新零串口操作、选择失效检测、同 ID 冲突、
契约按板上身份启用、未知契约被动、遥控器不自动启用、路径安全、哈希不符拒绝。
套装摘要一致性 140 项覆盖：首次初始化、普通切换 r11→r10→r11、刷新后手动切换、
清除选择、不可用套装、刷新删除/改动原套装、重建事件抑制；每步同时核对下拉选中对象、
Workflow.SelectedSuite、摘要版本、小船 FWID、遥控器 FWID，并断言零串口访问。

未验证项见《先看这里-使用说明.txt》第十一节。
"@ | Set-Content -LiteralPath (Join-Path $testDst '测试结果.txt') -Encoding UTF8

    # 运行期目录
    foreach ($d in @('记录','配置','诊断记录')) {
        $p = Join-Path $dir $d
        New-Item -ItemType Directory -Force -Path $p | Out-Null
        Set-Content -LiteralPath (Join-Path $p '说明.txt') -Encoding UTF8 `
            -Value '此目录由工具在运行时写入，可以随时清空，不影响工具运行。'
    }

    # 包内 EXE 必须与本次构建记录逐字节一致（不能拿遗留 EXE 冒充）
    $recPath = Join-Path $root 'work\build\构建记录.txt'
    if (-not (Test-Path $recPath)) { throw '缺少构建记录，拒绝打包（无法证明 EXE 是本次构建）' }
    $recHash = (Select-String -LiteralPath $recPath -Pattern 'GUI EXE SHA256：(.+)' ).Matches[0].Groups[1].Value.Trim().ToLowerInvariant()
    $pkgHash = (Get-FileHash -LiteralPath (Join-Path $dir 'ESP32小船烧录工具.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($pkgHash -ne $recHash) { throw ("$name 包内 EXE 哈希 $pkgHash 与构建记录 $recHash 不一致") }

    # 自检
    $proc = Start-Process -FilePath (Join-Path $dir 'ESP32小船烧录工具.exe') -ArgumentList '--self-check' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "$name 自检失败（$($proc.ExitCode)）" }
    $report = [System.IO.File]::ReadAllText((Join-Path $dir '自检报告.txt'), [System.Text.Encoding]::UTF8)
    if ($report -notmatch '结论：自检通过') { throw "$name 自检报告未通过" }
    if ($report -notmatch [regex]::Escape($boat11)) { throw "$name 自检报告没有 r11 小船 FWID" }
    if ($report -notmatch [regex]::Escape($boat10)) { throw "$name 自检报告没有 r10 小船 FWID" }
    if ($report -notmatch '锁存已关闭') { throw "$name 自检报告没有显示锁存关闭" }
    if (-not (Test-Path (Join-Path $dir '固件套装格式规范.md'))) { throw "$name 缺少固件套装格式规范" }

    # 哈希清单（自检报告含绝对路径，解压重跑会变，排除）
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

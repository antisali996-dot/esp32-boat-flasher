# 打包交付 ZIP：编译 → 从 firmware-library 组包 → 自检 → 生成 SHA256 → 压缩 → 解压验收
param(
    [string]$ZipPath = 'dist\ESP32小船烧录工具-580与590-20261103.zip',
    [string]$Version = '20261103'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Assert-WorkspacePath([string]$Target) {
    $base = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $full = [System.IO.Path]::GetFullPath($Target)
    if (-not $full.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase)) { throw '打包路径越出工具工作区，已停止。' }
    $cursor = $full
    while ($cursor -and $cursor.Length -ge $base.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw '打包路径含链接或重解析点，已停止。' }
        }
        $cursor = [System.IO.Path]::GetDirectoryName($cursor)
    }
}

Set-Location -LiteralPath $root

$stage = Join-Path $root 'work\dist-stage'
$pkgName = 'ESP32小船烧录工具-580与590'
$pkg = Join-Path $stage $pkgName

# 1) 编译
Write-Host '== 编译工具与模拟测试 =='
& (Join-Path $root 'scripts\build.ps1')
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

# 2) 组装包目录
Write-Host '== 组装交付目录 =='
Assert-WorkspacePath $stage
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'work\build\ESP32小船烧录工具.exe') -Destination (Join-Path $pkg 'ESP32小船烧录工具.exe') -Force

$libSource = Join-Path $root 'firmware-library'
if (-not (Test-Path $libSource)) { throw "缺少本地固件库：$libSource" }
$libDest = Join-Path $pkg '固件库'
New-Item -ItemType Directory -Force -Path $libDest | Out-Null
$suites = Get-ChildItem -LiteralPath $libSource -Directory
if ($suites.Count -eq 0) { throw '固件库为空，无法打包' }
foreach ($suite in $suites) {
    $dest = Join-Path $libDest $suite.Name
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -LiteralPath (Join-Path $suite.FullName 'import.json') -Destination $dest -Force
    Copy-Item -LiteralPath (Join-Path $suite.FullName '固件') -Destination $dest -Recurse -Force
    # 只取运行所需的烧录组件与许可证，不复制固件工程产物或记录。
    $toolDest = Join-Path $dest '工具'
    New-Item -ItemType Directory -Force -Path $toolDest | Out-Null
    foreach ($name in @('esptool.exe', 'GPL-2.0.txt', 'NOTICE.txt')) {
        $srcFile = Join-Path $suite.FullName ('工具\' + $name)
        if (Test-Path $srcFile) { Copy-Item -LiteralPath $srcFile -Destination $toolDest -Force }
    }
    # 只保留工具真正写入的分段镜像与清单；整片 merged.bin 不打包，
    # 避免用户误以为可以整片覆盖（那会连 NVS 配对区一起抹掉）。
    Get-ChildItem -LiteralPath (Join-Path $dest '固件') -File |
        Where-Object { $_.Name -like '*.merged.bin' -or $_.Name -notmatch '\.(bin|json|txt)$' } |
        Remove-Item -Force
}

# 3) 运行记录与配置目录（空目录 + 说明）
foreach ($d in @('记录', '配置')) {
    $p = Join-Path $pkg $d
    New-Item -ItemType Directory -Force -Path $p | Out-Null
    Set-Content -LiteralPath (Join-Path $p '说明.txt') -Encoding UTF8 `
        -Value '此目录由工具在运行时写入，可以随时清空，不影响工具运行。请不要把这里的备份发给他人。'
}

# 4) 中文说明与入口
Copy-Item -LiteralPath (Join-Path $root 'docs\使用说明.txt') -Destination (Join-Path $pkg '先看这里-使用说明.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'scripts\启动工具.cmd') -Destination (Join-Path $pkg '双击打开烧录工具.cmd') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\版本说明.txt') -Destination (Join-Path $pkg '版本说明.txt') -Force

# 5) 自检（GUI 子系统必须 Start-Process -Wait）
Write-Host '== 包内自检 =='
$exe = Join-Path $pkg 'ESP32小船烧录工具.exe'
$proc = Start-Process -FilePath $exe -ArgumentList '--self-check' -Wait -PassThru -WindowStyle Hidden
if ($proc.ExitCode -ne 0) { throw "包内自检失败（退出码 $($proc.ExitCode)），见 自检报告.txt" }
$report = [System.IO.File]::ReadAllText((Join-Path $pkg '自检报告.txt'), [System.Text.Encoding]::UTF8)
if ($report -notmatch '结论：自检通过') { throw '自检报告未通过' }
if ($report -notmatch '可用套装数：2 / 2') { throw '自检报告里可用套装数不是 2 / 2' }

# 6) SHA256 清单（不含自身）
Write-Host '== 生成文件校验 =='
$checksums = [ordered]@{}
Get-ChildItem -LiteralPath $pkg -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($pkg.Length + 1).Replace('\', '/')
    if ($rel -eq '文件校验.json') { return }
    # 自检报告里含运行目录路径，每次解压都不同，不能作为可复现的校验对象。
    if ($rel -eq '自检报告.txt') { return }
    if ($rel -like '记录/*' -or $rel -like '配置/*') { return }
    $checksums[$rel] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$checksums | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $pkg '文件校验.json') -Encoding UTF8

# 7) 压缩
Write-Host '== 生成 ZIP =='
$zipFull = Join-Path $root $ZipPath
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipFull) | Out-Null
Assert-WorkspacePath $zipFull
if (Test-Path $zipFull) { Remove-Item -LiteralPath $zipFull -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipFull,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

# 8) 解压验收
Write-Host '== 解压验收 =='
$verify = Join-Path $root 'work\dist-verify'
Assert-WorkspacePath $verify
if (Test-Path $verify) { Remove-Item -LiteralPath $verify -Recurse -Force }
[System.IO.Compression.ZipFile]::ExtractToDirectory($zipFull, $verify)
$extracted = Join-Path $verify $pkgName
$proc2 = Start-Process -FilePath (Join-Path $extracted 'ESP32小船烧录工具.exe') -ArgumentList '--self-check' -Wait -PassThru -WindowStyle Hidden
if ($proc2.ExitCode -ne 0) { throw "解压后自检失败（退出码 $($proc2.ExitCode)）" }

$manifest = [System.IO.File]::ReadAllText((Join-Path $extracted '文件校验.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$bad = 0
foreach ($prop in $manifest.PSObject.Properties) {
    $file = Join-Path $extracted $prop.Name.Replace('/', '\')
    if (-not (Test-Path $file)) { Write-Host "缺少文件：$($prop.Name)"; $bad++; continue }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $prop.Value) { Write-Host "校验不符：$($prop.Name)"; $bad++ }
}
if ($bad -gt 0) { throw "解压验收失败：$bad 个文件有问题" }
Write-Host "文件校验：$($manifest.PSObject.Properties.Name.Count) 个文件全部一致"

# 9) 镜像一致性：包内清单声明的 FWID 必须真的出现在对应应用镜像里
Write-Host '== 镜像与清单一致性 =='
foreach ($suite in Get-ChildItem -LiteralPath (Join-Path $extracted '固件库') -Directory) {
    $manifestPath = Join-Path $suite.FullName '固件\固件清单.json'
    $catalog = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($role in @('BOAT', 'REMOTE')) {
        $spec = $catalog.roles.$role
        $appPath = Join-Path $suite.FullName ('固件\' + $spec.app)
        $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($appPath))
        if (-not $text.Contains($spec.fwid)) { throw "镜像与清单不一致：$($suite.Name) $role" }
    }
    Write-Host ("  " + $suite.Name + "：" + $catalog.version + "，" + $catalog.target_rpm + " 转，镜像 FWID 与清单一致")
}

$size = (Get-Item -LiteralPath $zipFull).Length
Write-Host ''
Write-Host "ZIP：$zipFull"
Write-Host ("大小：{0:N2} MB" -f ($size / 1MB))

# 编译图形工具与模拟测试（使用 Windows 自带 .NET Framework 编译器，最终用户无需安装运行时）
param(
    [string]$Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$tests = Join-Path $root 'tests'
$out = Join-Path $root 'work\build'
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not (Test-Path $Csc)) { throw "找不到 .NET Framework 编译器：$Csc" }

$shared = @(
    (Join-Path $src 'Model.cs'),
    (Join-Path $src 'Json.cs'),
    (Join-Path $src 'FirmwareCatalog.cs'),
    (Join-Path $src 'PartitionLayout.cs'),
    (Join-Path $src 'FirmwareLibrary.cs'),
    (Join-Path $src 'PairProtocol.cs'),
    (Join-Path $src 'PresetConfig.cs'),
    (Join-Path $src 'Diagnostics\DiagModels.cs'),
    (Join-Path $src 'Diagnostics\StreamingLineAssembler.cs'),
    (Join-Path $src 'Diagnostics\CaptureSession.cs'),
    (Join-Path $src 'Diagnostics\DiagnosticParsers.cs'),
    (Join-Path $src 'Diagnostics\R10FaultAdapter.cs'),
    (Join-Path $src 'Diagnostics\DiagnosticsController.cs'),
    (Join-Path $src 'Diagnostics\DiagButtonActions.cs'),
    (Join-Path $src 'Diagnostics\PortLockRegistry.cs'),
    (Join-Path $src 'Diagnostics\DiagnosticsExporter.cs'),
    (Join-Path $src 'Diagnostics\SerialDiagChannel.cs'),
    (Join-Path $src 'RoleStore.cs'),
    (Join-Path $src 'Transport.cs'),
    (Join-Path $src 'Workflow.cs')
)

$guiSources = $shared + @(
    (Join-Path $src 'HardwareTransport.cs'),
    (Join-Path $src 'MainForm.cs'),
    (Join-Path $src 'Program.cs')
)

$testSources = $shared + @(
    (Join-Path $src 'MainForm.cs'),
    (Join-Path $tests 'SimulatedTransport.cs'),
    (Join-Path $tests 'SimulatedTests.cs'),
    (Join-Path $tests 'ScriptedChannel.cs'),
    (Join-Path $tests 'PairProtocolTests.cs'),
    (Join-Path $tests 'DiagnosticsTests.cs'),
    (Join-Path $tests 'DiagnosticsReviewTests.cs'),
    (Join-Path $tests 'ReconnectFixTests.cs'),
    (Join-Path $tests 'BoundaryFixTests.cs'),
    (Join-Path $tests 'R10InterfaceTests.cs'),
    (Join-Path $tests 'R10FaultFixTests.cs'),
    (Join-Path $tests 'FaultNumberTests.cs'),
    (Join-Path $tests 'R11AdaptationTests.cs'),
    (Join-Path $tests 'DynamicLibraryTests.cs'),
    (Join-Path $tests 'DynamicIntegrationFixTests.cs'),
    (Join-Path $tests 'SuiteSelectionGuiTests.cs'),
    (Join-Path $tests 'TestMain.cs')
)

$guiExe = Join-Path $out 'ESP32小船烧录工具.exe'
$testExe = Join-Path $out '模拟测试.exe'

# 编译前把旧构建产物移到历史目录，避免后续步骤误用遗留 EXE。
# 只处理本脚本生成的两个产物（GUI + 模拟测试），不动用户记录、固件或其它文件。
$buildStart = Get-Date
$history = Join-Path $root (Join-Path 'work\build-history' $buildStart.ToString('yyyyMMdd-HHmmss'))
foreach ($exe in @($guiExe, $testExe)) {
    if (Test-Path $exe) {
        if (-not (Test-Path $history)) { New-Item -ItemType Directory -Force -Path $history | Out-Null }
        Move-Item -LiteralPath $exe -Destination (Join-Path $history (Split-Path -Leaf $exe)) -Force
    }
}

Write-Host '编译图形工具……'
& $Csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    /out:$guiExe /reference:System.dll /reference:System.Core.dll `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Management.dll /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll @guiSources
if ($LASTEXITCODE -ne 0) {
    # GUI 编译失败必须立即终止：不能继续编译测试/打包/自检，也不能拿旧 EXE 冒充本次产物。
    Add-Content -LiteralPath (Join-Path $out '构建记录.txt') -Value (
        (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  GUI 编译失败，退出码 ' + $LASTEXITCODE)
    throw ('图形工具编译失败，退出码 ' + $LASTEXITCODE + '（已终止，未生成新 EXE）')
}
if (-not (Test-Path $guiExe)) { throw 'GUI 编译退出码为 0 但未生成 EXE，视为失败' }

Write-Host '编译模拟测试……'
& $Csc /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 `
    /out:$testExe /reference:System.dll /reference:System.Core.dll `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll @testSources
if ($LASTEXITCODE -ne 0) { throw ('模拟测试编译失败，退出码 ' + $LASTEXITCODE) }
if (-not (Test-Path $testExe)) { throw '模拟测试编译退出码为 0 但未生成 EXE，视为失败' }

$buildEnd = Get-Date
function HashOfFile([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# 构建记录：源文件 SHA256、起止时间、编译命令、退出码、新 EXE 哈希。
# 打包脚本据此核对包内 EXE 与本次构建逐字节一致。
$rec = New-Object System.Collections.Generic.List[string]
$rec.Add('构建记录（scripts/build.ps1 自动生成）')
$rec.Add('构建开始：' + $buildStart.ToString('yyyy-MM-dd HH:mm:ss'))
$rec.Add('构建结束：' + $buildEnd.ToString('yyyy-MM-dd HH:mm:ss'))
$rec.Add('编译器：' + $Csc)
$rec.Add('GUI 编译退出码：0')
$rec.Add('模拟测试编译退出码：0')
$rec.Add('GUI EXE：' + $guiExe)
$rec.Add('GUI EXE SHA256：' + (HashOfFile $guiExe))
$rec.Add('GUI EXE 字节数：' + (Get-Item -LiteralPath $guiExe).Length)
$rec.Add('测试 EXE：' + $testExe)
$rec.Add('测试 EXE SHA256：' + (HashOfFile $testExe))
$rec.Add('旧产物归档：' + $history)
$rec.Add('')
$rec.Add('源文件 SHA256（GUI 输入）：')
foreach ($f in $guiSources) { $rec.Add('  ' + (HashOfFile $f) + '  ' + $f) }
$rec.Add('')
$rec.Add('源文件 SHA256（仅测试输入）：')
foreach ($f in $testSources) { if ($guiSources -notcontains $f) { $rec.Add('  ' + (HashOfFile $f) + '  ' + $f) } }
$rec | Set-Content -LiteralPath (Join-Path $out '构建记录.txt') -Encoding UTF8

Write-Host ('图形工具：' + $guiExe)
Write-Host ('模拟测试：' + $testExe)
Write-Host ('构建记录：' + (Join-Path $out '构建记录.txt'))

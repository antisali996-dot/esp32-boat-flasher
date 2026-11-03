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
    (Join-Path $tests 'SimulatedTransport.cs'),
    (Join-Path $tests 'SimulatedTests.cs'),
    (Join-Path $tests 'TestMain.cs')
)

$guiExe = Join-Path $out 'ESP32小船烧录工具.exe'
$testExe = Join-Path $out '模拟测试.exe'

Write-Host '编译图形工具……'
& $Csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    /out:$guiExe /reference:System.dll /reference:System.Core.dll `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Management.dll @guiSources
if ($LASTEXITCODE -ne 0) { throw '图形工具编译失败' }

Write-Host '编译模拟测试……'
& $Csc /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 `
    /out:$testExe /reference:System.dll /reference:System.Core.dll @testSources
if ($LASTEXITCODE -ne 0) { throw '模拟测试编译失败' }

Write-Host ('图形工具：' + $guiExe)
Write-Host ('模拟测试：' + $testExe)

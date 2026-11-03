param([Parameter(Mandatory=$true)][string]$SourcePackage,[Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Id)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$source=(Resolve-Path -LiteralPath $SourcePackage).Path
$firmware=Join-Path $source '固件'
$manifestPath=Join-Path $firmware '固件清单.json'
$catalog=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach($role in @('BOAT','REMOTE')){
 $spec=$catalog.roles.$role
 if(!$spec -or !$spec.fwid){throw '发布清单缺少角色或FWID。'}
 foreach($key in @('app','bootloader','partitions','boot_app0')){
  $rel=[string]$spec.$key
  if([string]::IsNullOrEmpty($rel) -or [System.IO.Path]::IsPathRooted($rel) -or $rel.Contains('..')){throw '发布清单路径无效。'}
  if(!(Test-Path -LiteralPath (Join-Path $firmware $rel) -PathType Leaf)){throw ('缺少镜像：'+$rel)}
 }
}
$dest=Join-Path (Join-Path $root 'firmware-library') $Id
if(Test-Path -LiteralPath $dest){throw '该导入ID已经存在，请使用新的版本ID，不自动覆盖。'}
New-Item -ItemType Directory -Path $dest -ErrorAction Stop | Out-Null
Copy-Item -LiteralPath $firmware -Destination (Join-Path $dest '固件') -Recurse
$tools=Join-Path $source '工具'
if(Test-Path -LiteralPath $tools){
 New-Item -ItemType Directory -Path (Join-Path $dest '工具') | Out-Null
 foreach($name in @('esptool.exe','GPL-2.0.txt','NOTICE.txt')){
  $f=Join-Path $tools $name
  if(Test-Path -LiteralPath $f){Copy-Item -LiteralPath $f -Destination (Join-Path (Join-Path $dest '工具') $name)}
 }
}
$record=[ordered]@{id=$Id;version=$catalog.version;target_rpm=$catalog.target_rpm;steer_deg=$catalog.steer_deg;boat_fwid=$catalog.roles.BOAT.fwid;remote_fwid=$catalog.roles.REMOTE.fwid;source=$source;manifest_sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dest 'import.json') -Encoding UTF8
Write-Host ('已导入发布固件：'+$dest)
Write-Host '只复制发布包，不改源码、不编译固件、不访问设备。'

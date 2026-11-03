# ESP32小船烧录工具（独立工作区）

工具源码和模拟测试已从固件工程独立复制。本工程仅维护Windows C# WinForms上位机。

- 工具工作区：D:\1OPENAIworkspace\esp32-boat-flasher
- 固件工作区：D:\1OPENAIworkspace\esp32-boat-debug
- src：工具源码；tests：模拟测试；scripts：工具构建和导入脚本。
- firmware-library：本地导入发布固件，不上传Git；work：构建产物及临时记录。

## 构建与模拟测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
& '.\work\build\模拟测试.exe'
```

使用Windows自带.NET Framework编译器；模拟测试使用假板子，不访问真实串口。测试中的580参数是合成测试夹具，不限制发布包只能用580。

固件源码仍由原固件工程编译、校准并发布。本工具只导入清单和预编译镜像，不在工具仓库内编译固件。源工程tools/gui保留为迁移前历史副本，后续工具修改只在本目录维护。

## 当前状态与下一步

这是独立迁移基线，尚未实现多固件包选择。下一步增加“选择固件套装”功能，支持580/20°、590/20°，烧录前显示板上版本与将写版本；配对不切换固件，不强制备份。

迁移来源及源码校验见docs/migration.json。未连接、复位或烧录真实设备。

## 导入固件发布包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/import-package.ps1 -SourcePackage "发布包目录" -Id "590-20-r4"
```

只复制发布包的清单、镜像及烧录运行组件到本地firmware-library，完全不调用固件工程编译。现有GUI仍是单包版本，库中多包选择是下一步开发任务，不能把导入成功称为选择功能已完成。

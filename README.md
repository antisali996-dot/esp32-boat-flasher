# ESP32小船烧录工具（独立工作区）

工具源码和模拟测试已从固件工程独立复制。本工程仅维护 Windows C# WinForms 上位机。

- 工具工作区：D:\1OPENAIworkspace\esp32-boat-flasher
- 固件工作区：D:\1OPENAIworkspace\esp32-boat-debug（本工程不修改、不复制其 boat/remote 源码，也不调用 Arduino 编译）
- src：工具源码；tests：模拟测试；scripts：构建、导入、打包脚本；docs：中文说明与验证记录。
- firmware-library：本地导入的发布固件（不上传 Git）；work：构建产物及临时记录；dist：打包出的 ZIP。

## 构建、测试与打包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
& '.\work\build\模拟测试.exe'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

使用 Windows 自带 .NET Framework 编译器；模拟测试使用假板子，不访问真实串口。
`package.ps1` 会编译、从 firmware-library 组包、跑包内自检、生成 SHA256、打 ZIP，然后解压再验一遍
（逐文件 SHA256 + 逐套核对镜像里的 FWID）。

## 固件套装选择（本轮实现）

界面顶部“固件套装”下拉框列出 firmware-library 里导入的每个发布包：

- 580 转 / 20°（`580-20-r3`，版本 `20261103-20deg-r3`）
- 590 转 / 20°（`590-20-r4`，版本 `20261103-590rpm-r4`，含 600 转超速保护）

规则：

- 转速、角度、版本、FWID、镜像路径、写入地址、分区表全部来自该套装自己的 `固件清单.json`，
  不按文件名或目录名推断；`import.json` 与清单不一致时该套装判为不可用。
- 每套只用自己的镜像和 esptool，两套不会混用；同一角色在两个套装里 FWID 重复、或小船与遥控器指向
  同一应用镜像，都会在加载时被标记。
- 烧录确认框同时列出【板上现在】与【将写入】的角色、目标转速、转向角度、FWID。
  跨套装会列出两套名称；目标转速低于板上时单独弹出降级警告，取消则不写入。
- 板上固件与所选套装一致时提示“无需重复烧录”，不写入；勾选“即使已匹配也重刷”才会重写。
- 配对与套装无关：只改两端保存的对端地址，不切固件、不重刷。是否允许配对只看
  配对协议版本与转向契约（角度/范围）是否一致，590 小船配同一 20° 遥控器是允许的组合。
- 配对时已经指向正确对端的一端跳过写入与重启；部分失败会说明完成到 1/2 或 0/2，
  重试只处理尚未完成的一端。
- 串口只用于选择连接，身份按芯片 MAC 核对；同一串口、同一 MAC、确认后换插都会被拒绝。
- 三个主按钮保持不变（烧录当前板、配对两块板、备份原固件）；烧录前备份默认不勾选；
  写入强制 `--verify`，并必须看到 `Hash of data verified` 且重启读回 FWID 才算成功。
- 分段写入 bootloader/分区表/OTA 数据/应用，不写 0x9000 起的 NVS。分区表在加载和烧录前解析，
  若 NVS 会被覆盖或应用镜像越界，该套装判为不可用并拒绝写入，不会声称保留成功。

## 导入新的发布包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/import-package.ps1 -SourcePackage "发布包目录" -Id "590-20-r4"
```

只复制发布包的清单、镜像及烧录运行组件到本地 firmware-library，不调用固件工程编译。
导入后重新运行 `package.ps1` 即可把新套装打进交付 ZIP。

## 验证状态

- 模拟测试 138 项全部通过（见 `tests/sim-test-record.txt`），全部走模拟接口。
- 用本地真实发布包做的包内自检通过：两套都可用，分区表解析出 NVS 0x9000，镜像 FWID 与清单一致。
- GUI 以无设备模式（`BOATTOOL_NO_AUTOSCAN=1`）启动并检查过布局。
- 未连接、未探测、未复位、未读取、未烧录任何真实设备。驱动兼容性、配对断电保持、
  真实无线通信与跨套装实机烧录都未验证。详见 `docs/验证记录.md`。

迁移来源及拆分时的源码校验见 `docs/migration.json` 与 `docs/迁移验证记录.md`。

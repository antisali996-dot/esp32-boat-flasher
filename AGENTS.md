# 工具工程边界

本目录仅维护Windows图形烧录工具、工具测试、导入固件与打包脚本。
固件源码在 D:\1OPENAIworkspace\esp32-boat-debug（GitHub: antisali996-dot/esp32-boat-control），不要在本工程复制维护 boat/remote 源码，不从本工程调用Arduino编译。
工具通过显式导入的发布固件包衔接，保留FWID、角色、角度、目标转速与校验清单。
固件包和esptool运行组件仅作为本地依赖，放firmware-library；不提交原Flash备份、运行记录或用户配对记忆。
临时产物放work。未经明确授权，不连接/探测/复位真实串口，不读取/擦写真实芯片，也不启动可操作设备的界面。
不要因为工具包版本选择而顺带修改调速、舵机或无线控制代码。

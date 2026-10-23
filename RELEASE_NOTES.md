# Windows 1.4.0 · 保留配置更新

- 新增顶部“版本更新”，启动时与每 6 小时检查正式 Windows 安装包；可关闭自动检查。
- 下载核对文件大小与 SHA256，空闲时通过 Inno Setup 覆盖安装，自动备份配置并保留安装路径、自定义配置路径和采集启停状态。
- 报警、待发通知或电话操作期间暂缓；源码预览只提供发布页入口，不覆盖开发目录。
- 同步三端更新手册与按平台发布目录。旧版需要先安装一次 1.4.0，后续使用应用内更新。
- Windows / OpenWrt 零警告构建，133 条核心断言、10 项桌面电话检查及 29 项更新专项检查通过；未进行现场 PLC 写入或真实拨号。实际验证范围见[更新验证记录](docs/UPDATE_VALIDATION.md)。

[安装与升级](docs/INSTALLATION.md#windows-客户端) · [更新机制](docs/AUTO_UPDATES.md) · [全部功能](docs/README.md)

---

# Windows 1.3.0 · 全新工作区

- 顶部导航、独立参数字段、紧凑总览与真实曲线；长 NodeId、密码与设备切换更易使用。
- 保留 OPC UA 数组/ProDiag、工业协议、规则字典、机器人 PLC 动作和随身 WiFi 电话逻辑。
- Inno Setup 安装包附完整功能手册；源码、安装文件和现场资料分开整理。
- 本地检查：130 条核心断言、10 项桌面电话检查，以及共享电话与网页回归。未连接真实 PLC 或拨打现场电话。

[安装](docs/INSTALLATION.md#windows-客户端) · [全部功能](docs/README.md) · [历次更新](CHANGELOG.md)

---
# 工业报警推送中心 Windows 1.2.0

Windows 客户端新增直接连接 UFI-TOOLS 随身 WiFi 的电话提醒，不需要通过 OpenWrt 插件转发。

- “采集源”按设备配置，使用“采集连接 / 电话提醒”页签；滚动时设备选择框保持可见。
- 每台设备独立设置报警持续时间、接听号码与开关；统一管理随身 WiFi 地址、登录口令、AT 槽位及呼叫限制。
- 检查连接不拨号，测试拨号核对号码后确认，支持取消和挂断；报警恢复、采集断线、停止服务会取消等待。
- 与 OpenWrt 共用电话协议实现，保留 SIM 检查、单次提醒和全局间隔保护。
- 同步运行趋势图、用户提供的应用图标与免责声明入口，并在 Inno Setup 安装器展示免责声明。

安装包：`AlarmPushCenter-Setup-1.2.0-win-x64.exe`。安装后在“采集源 → 电话提醒”配置，完整说明见 [Windows 电话提醒](docs/WINDOWS_PHONE.md)。

验证：Windows 编译与 123 条离线断言、10 项桌面电话链路检查通过；OpenWrt 编译、API 回归及 41 项电话检查通过。电话检查使用模拟接口，未在此次更新中拨打现场号码。电脑必须保持运行并能访问支持语音的随身 WiFi；当前不含语音播报。

---

# 工业报警推送中心 1.0.0（历史版本）

首个 GitHub 安装版，面向 Windows x64。

## 主要功能

- OPC UA 报警码数组订阅与 ProDiag Alarms & Conditions 事件订阅
- Siemens S7、Modbus TCP/RTU、MQTT、HTTP、TCP、UDP、串口、ADS、EtherNet/IP 采集
- Telegram、钉钉、企业微信机器人报警推送与按钮交互
- OPC UA、S7 与 Modbus PLC 写入，执行前二次确认
- TIA Portal Openness 扫描 ProDiag AlarmId 与 PLC 报警文本列表
- 按设备分页查看历史报警
- 中文现代化客户端界面

## 安装说明

下载并运行 `AlarmPushCenter-Setup-1.0.0-win-x64.exe`。安装包为当前用户安装，不需要管理员权限，且已包含 .NET 运行时。

配置保存在 `%LOCALAPPDATA%\AlarmPushCenter\config.json`。升级或卸载程序不会删除该配置。

安装程序当前未进行商业代码签名，Windows 首次运行时可能显示信誉提示。请使用同一 Release 附带的 SHA256 文件核对下载完整性。

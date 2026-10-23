# 工业报警推送中心

连接 PLC，将报警码、ProDiag 事件匹配为可读文本，发送机器人通知，并按需进行按钮交互和超时电话提醒。

Windows 客户端、OpenWrt 插件和安卓 / iPhone App 统一维护。**普通用户直接下载安装包，无需编译源码。**

## 分类下载

| 平台 | 最新版本与安装包 | 安装说明 |
| --- | --- | --- |
| Windows 10 / 11 x64 | [1.4.0 · Inno Setup EXE](https://github.com/zxwzywzy/alarm-push-center/releases/tag/v1.4.0) | [Windows 安装与升级](docs/INSTALLATION.md#windows-客户端) |
| Cudy TR3000 v1 · OpenWrt 24.10 ARM64 | [1.7.0 · IPK](https://github.com/zxwzywzy/alarm-push-center/releases/tag/openwrt-v1.7.0) | [OpenWrt 安装与开关](docs/INSTALLATION.md#openwrt-插件) |
| Android 8.0+ | [1.2.0 · APK](https://github.com/zxwzywzy/alarm-push-center/releases/tag/mobile-v1.2.0) | [安卓安装](docs/INSTALLATION.md#安卓-app) |
| iPhone · iOS 15+ | [1.2.0 · 未签名 IPA，需自签](https://github.com/zxwzywzy/alarm-push-center/releases/tag/mobile-v1.2.0) | [iPhone 自签安装](docs/INSTALLATION.md#iphone-app-与自签) |

各发布页提供 SHA256 校验。OpenWrt IPK 面向 `aarch64_cortex-a53`，不适用于任意路由器。

## 界面

![OpenWrt 运行总览，演示数据](docs/images/current/web-overview.png)

[Windows 总览](docs/images/current/windows-overview.png) · [网页登录](docs/images/current/web-login.png) · [设备工作区](docs/images/current/web-device.png) · [手机报警页](docs/images/current/mobile-alarm.png)

## 功能

- **采集**：OPC UA 动态报警数组与 ProDiag A&C，S7、Modbus、MQTT、HTTP/TCP/UDP JSON、串口与 ADS。
- **每台设备独立配置**：连接、规则、文本、电话条件与历史集中在设备工作区，支持草稿连接检查。
- **报警文本库**：CSV/TSV 导入、全文检索、跨页勾选；Windows 可用 TIA V21 Openness 导出 ProDiag 与文本列表。
- **通知与交互**：Telegram、钉钉、企业微信、飞书、Webhook；支持可选报警按钮与管理员确认后 PLC 写入。
- **运行总览**：真实心跳、连接时长、报警/采集/进程资源曲线及有限历史记录。
- **电话提醒**：Windows 和 OpenWrt 可直接连接支持拨号的 UFI-TOOLS 随身 WiFi，按设备设置持续时间和号码。
- **OpenWrt 与手机**：LuCI 入口、启动开关、改密与邮箱找回；手机独立扫码授权插件，另行登录路由器监控系统与网络。

## 完整功能手册

**[从这里开始：安装与全部功能说明](docs/README.md)**

[设备与工作区](docs/CONFIGURATION.md) · [全部采集协议](docs/PROTOCOLS.md) · [报警与文本库](docs/ALARMS.md) · [机器人与 PLC 按钮](docs/NOTIFICATIONS.md) · [电话提醒](docs/PHONE.md) · [手机连接](docs/MOBILE.md) · [密码、邮箱和备份](docs/SECURITY.md) · [问题排查](docs/TROUBLESHOOTING.md)

每次发版记录见 **[CHANGELOG](CHANGELOG.md)**，具体包与验证范围见对应 Release。[本次界面验证](docs/UI_REDESIGN_TESTS.md)。

新增[保留配置更新](docs/AUTO_UPDATES.md)：Windows 提示后一键更新，OpenWrt 可配置维护时段自动安装，安卓使用系统安装确认，iPhone 下载校验后自签覆盖。首次需装一次包含更新入口的新版本。

## 源码

`AlarmPushCenter` 为 Windows 与共享核心，`AlarmPushCenter.OpenWrt` 为网页服务，`openwrt` 为 LuCI/安装脚本，`mobile` 为安卓/iOS 工程，`installer` 为 Inno Setup。构建方法见[安装指南末尾](docs/INSTALLATION.md#开发者构建)。

仓库不包含现场配置、历史数据或私密凭据。手机当前为前台监控，不提供后台系统通知；EtherNet/IP 尚未实现实际驱动。

安装使用前请阅读[免责声明](DISCLAIMER.txt)。软件是辅助监控工具，不能替代现场硬件联锁和必要报警措施。

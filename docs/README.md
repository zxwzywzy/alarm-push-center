# 安装与功能手册

适用版本：Windows **1.4.0**、OpenWrt **1.7.0**、手机 App **1.2.0**。旧版界面名称可能不同，升级前请先备份配置。截图来自隔离演示环境，不代表现场设备状态。

## 第一次使用

1. [按平台安装：Windows / OpenWrt / 安卓 / iPhone](INSTALLATION.md)。
2. [了解页面和配置流程](CONFIGURATION.md)，先用 Mock 模拟设备走通报警。
3. [选择采集协议、填写 PLC 地址](PROTOCOLS.md)。
4. [设置报警匹配、导入文本、查看历史](ALARMS.md)。
5. [配置通知机器人和按钮动作](NOTIFICATIONS.md)。
6. 需要超时拨号时，再配置[电话提醒](PHONE.md)。

## 每个功能去哪里找

| 功能 | 详细说明 |
| --- | --- |
| 运行总览、实时心跳、连接时长、采集/报警/资源曲线 | [工作区与运行流程](CONFIGURATION.md)、[统计口径](RUNTIME_TRENDS.md) |
| 每台设备独立配置、一键检查、草稿、保存、启停 | [设备工作区](CONFIGURATION.md#设备工作区) |
| OPC UA 数组、ProDiag A&C、S7、Modbus、MQTT、JSON、串口、ADS | [协议参数与实例](PROTOCOLS.md) |
| 报警规则、级别、冷却、模板、文本库、CSV 导入、批量删除 | [报警与文本库](ALARMS.md) |
| TIA Openness、AlarmId、文本列表与占位符 | [报警与文本库](ALARMS.md#tia-openness-导入)、[Windows 技术细节](CLIENT_GUIDE.md) |
| Telegram、钉钉、企业微信、飞书、Webhook | [通知目标](NOTIFICATIONS.md) |
| 菜单、状态查询、历史报警、管理员、PLC 写入与脉冲 | [交互按钮与控制](NOTIFICATIONS.md#机器人交互) |
| UFI-TOOLS、原始口令、设备超时拨号、检测与真实测试 | [电话提醒完整流程](PHONE.md) |
| 插件开机开关、管理密码、SMTP 邮箱找回、配置备份 | [系统与安全](SECURITY.md) |
| 手机扫码、域名连接、路由器监控、撤销授权、自签 IPA | [手机安装](INSTALLATION.md#安卓-app)、[手机功能](MOBILE.md) |
| PLC 仿真离线、推送失败、卡片不显示、口令不对 | [排查手册](TROUBLESHOOTING.md) |
| 每次版本更新 | [更新记录](../CHANGELOG.md) |
| 自动检查、保留配置升级、维护时段、回退与发布流程 | [三端更新机制](AUTO_UPDATES.md) |
| 更新专项测试与 TR3000 实际升级范围 | [更新验证记录](UPDATE_VALIDATION.md) |

## 平台能力差异

| 功能 | Windows | OpenWrt 网页 | 安卓 / iPhone |
| --- | --- | --- | --- |
| 本机运行采集、规则和推送 | 是 | 是，在路由器后台运行 | 否，连接已有插件 |
| 协议、规则、文本库、通知与按钮完整配置 | 是 | 是 | 当前提供设备参数编辑，完整配置用网页 |
| TIA Openness 扫描项目 | 是，需本机 TIA V21 | 导入 Windows 导出的 CSV | 无 |
| 电话提醒 | 直接连接随身 WiFi | 路由器连接随身 WiFi | 当前没有直接拨号功能 |
| 运行状态、趋势、按设备查看历史 | 是 | 是 | 是 |
| OpenWrt 整机状态、网络流量、无线与终端记录 | 无 | 插件页展示自身采集进程指标 | 路由器页通过 ubus 读取 |
| 账号管理与邮箱找回 | 使用 Windows 本机账户保护配置 | 独立网页管理密码 | 使用插件配对授权 |

手机 App 的报警配对与路由器登录互相独立；安装手机 App 不会在手机本机运行 PLC 采集。安装包不包含真实账号、现场地址、拨号号码或路由器凭据。

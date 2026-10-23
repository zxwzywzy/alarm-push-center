# Cudy TR3000 OpenWrt 版本

1.7.0 包含连接随身 WiFi 时的报警超时电话提醒、每台设备独立保存与一键检查，并修复 UFI-TOOLS 正确口令被拒绝的问题。使用方法见 [电话提醒与设备工作区](../docs/电话提醒与设备工作区.md)。

适用于 OpenWrt `mediatek/filogic`、软件包架构 `aarch64_cortex-a53` 的 Cudy TR3000 v1。后台为自包含的 .NET 10 musl ARM64 服务，不依赖 Python。

## 安装

当前发布版本为 **1.7.0-1**，从 [GitHub Releases](https://github.com/zxwzywzy/alarm-push-center/releases/tag/openwrt-v1.7.0) 下载 IPK 和对应 SHA256 文件。已在 Cudy TR3000 / OpenWrt 24.10.4 从 1.5.1 升级至 1.7.0，原有 12 个配置文件校验一致，保留手机授权、开机开关和采集停止状态；未进行现场 PLC 写入或真实拨号。详见[验证记录](../docs/UPDATE_VALIDATION.md)和[安装与升级指南](../docs/INSTALLATION.md)。

将 IPK 上传到路由器后执行：

```sh
opkg update
sha256sum -c alarm-push-center_1.7.0-1_aarch64_cortex-a53.ipk.sha256
opkg install ./alarm-push-center_1.7.0-1_aarch64_cortex-a53.ipk
```

安装结束时终端会显示随机生成的首次管理密码。也可以通过 SSH 查看：

```sh
cat /etc/alarm-push-center/web-password
```

首次安装默认不随路由器开机运行，也不自动采集。刷新 LuCI，在 **服务 → 报警推送中心** 点击“启动插件”，再打开配置台；也可在启动后独立访问 `http://路由器IP:5080`。

“系统设置 → 修改管理密码”可以更换本配置台密码；需要填写当前密码，新密码至少 8 位，修改后所有会话退出。该操作不更改 OpenWrt root 密码。

配置保存在 `/etc/alarm-push-center/config.json`，卸载或升级时由 opkg 按配置文件处理。

### 从旧版本升级

先备份 `/etc/alarm-push-center`，记录插件开机启动偏好与采集状态，然后停止插件，安装新 IPK，再按原来的运行需要手动启动。升级会保留已有的管理密码、设备、机器人和电话网关配置；如果 opkg 提示配置文件冲突，保留现场文件。服务重启后网页会话需要重新登录，电话计时重新开始。

LuCI 的“开机启动插件”控制网页后台服务；配置台“系统设置”的采集开关与“服务启动后自动采集”控制 PLC 采集。这两层设置分别生效，详见 [当前版本指南](../docs/INSTALLATION.md#开机启动与采集开关)。

## 服务命令

```sh
/etc/init.d/alarm-push-center status
/etc/init.d/alarm-push-center start
/etc/init.d/alarm-push-center stop
logread -e alarm-push-center
```

支持 OPC UA 报警码数组、OPC UA ProDiag A&C、S7、Modbus TCP/RTU、MQTT、HTTP/TCP/UDP JSON、串口文本与 ADS。网页端提供设备状态、连接测试、报警文本 CSV 导入、通知测试、机器人交互配置和最近报警分页。

## 存储说明

TR3000 有不同闪存布局。安装前建议用 `df -h /overlay` 确认有足够空间容纳安装后的文件与依赖（建议至少预留 100 MB，具体以包和固件实际占用为准）。128 MB 版本如果剩余空间不足，应使用外置存储扩容 overlay，或使用 OpenWrt U-Boot 扩展布局。

## 源码打包

需要 .NET 10 SDK 和 PowerShell 7，在仓库根目录执行：

```powershell
.\打包OpenWrt程序.ps1 -Version 1.7.0
```

输出文件位于 `dist/`，包括 IPK 和 SHA256 校验文件。IPK 已包含 LuCI 入口，安装后不需要另装前端包。

## 配置界面

![设备工作区（演示数据）](../docs/images/current/web-device.png)

每台设备的连接、规则、电话提醒、历史在同一个工作区管理。“一键检查此设备”只检查参数和连接，不保存、不发消息、不拨号；“保存此设备”只提交该设备及所属规则。顶栏“保存并应用”提交所有页面的草稿。

电话提醒需要先在“系统设置”保存 UFI-TOOLS 地址和原始登录口令，再在当前设备的“电话提醒”中填写持续秒数与接听号码。检测不会呼叫，真实测试电话有单独的确认步骤。详见 [电话提醒配置](../docs/电话提醒与设备工作区.md)。

## 钉钉企业应用机器人

OpenWrt 1.1.1 支持企业应用机器人主动发送报警和测试消息，无需群 Webhook。在“通知与交互 → 通知通道”填写应用 Client ID、Client Secret，并选择接收方式：

- **个人**：填写企业成员 userId，每行一个；不要填写手机号或 unionId。接收人需在应用可见范围内。
- **群聊**：填写 openConversationId，机器人需已加入该群。也可保存配置、开启命令接收，再在群内 @机器人发送 `/status`，自动记录群 ID 后刷新页面。

应用需要开通企业内机器人发送消息权限。需要报警按钮时，在机器人交互页启用按钮并填写互动卡片模板 ID。测试按钮直接使用当前表单内容；正式报警仍使用“保存并应用”后的配置。

接口说明：[个人通知](https://open.dingtalk.com/document/orgapp/chatbots-send-one-on-one-chat-messages-in-batches)、[群通知](https://open.dingtalk.com/document/orgapp/the-robot-sends-a-group-message)。

### 仿真 OPC UA 连接排查

电脑能够连接仿真 PLC，但路由器无法连接时，先检查 PLCSIM Advanced 的外部网卡设置。连接路由器 LAN 口的有线网卡应启用 **S7-PLCSIM Advanced Virtual Switch**，并在仿真器中选择相应的 TCP/IP 通信网卡。路由器与仿真 PLC 需处于可达的网络中。

OpenWrt 1.1.2 增加 `libopenssl-legacy` 包依赖，修复 OPC UA SDK 创建客户端 PKCS#12 证书时缺少 RC2 导致的 `digital envelope routines::unsupported`。安装时先运行 `opkg update`，升级包会自动安装依赖，不需要修改 OPC UA 安全策略。

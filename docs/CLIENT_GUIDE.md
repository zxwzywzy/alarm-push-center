# Windows 客户端使用指南

Windows 客户端提供工业协议采集、报警字典与机器人交互配置。OpenWrt 安装方法见 [OpenWrt 使用说明](../openwrt/README.md)。

## 安装程序

普通用户可从 [Windows 1.4.0 发布页](https://github.com/zxwzywzy/alarm-push-center/releases/tag/v1.4.0) 下载 `AlarmPushCenter-Setup-1.4.0-win-x64.exe`。安装包由 Inno Setup 生成，包含 Windows x64 自包含运行时，目标电脑无需预装 .NET 10，也不需要管理员权限。

- 程序默认安装到 `%LOCALAPPDATA%\Programs\AlarmPushCenter`。
- 可写配置保存在 `%LOCALAPPDATA%\AlarmPushCenter\config.json`，升级和卸载程序不会删除现场配置。
- 安装器可选择创建桌面快捷方式，开始菜单始终创建“工业报警推送中心”入口。
- 首次运行不会携带开发电脑上的 PLC 地址、机器人令牌、会话 ID 或报警历史。

开发者安装 Inno Setup 6/7 后，可运行：

```powershell
.\打包安装程序.ps1 -Version 1.4.0
```

脚本会发布 `win-x64` 自包含程序、执行离线自检、生成 `dist\AlarmPushCenter-Setup-<版本>-win-x64.exe`，并同时生成 SHA256 校验文件。

## 启动

双击根目录的 `启动.cmd`。它优先运行最新发布版，并明确使用根目录的 `config.json`。

从源码运行：

```powershell
dotnet run --project .\AlarmPushCenter\AlarmPushCenter.csproj -- --config .\config.json
```

直接运行框架依赖发布文件需要 .NET 10 Desktop Runtime；GitHub Release 中的安装版已包含运行时。开发和编译需要 .NET 10 SDK。程序不会自动启动监控；首次验证请使用 Mock 仿真。

## 配置流程

1. 在“设备配置”顶部新增、选择或删除设备，填写名称、协议、轮询间隔与连接参数。
2. 在“报警规则”顶部管理多条规则，并选择每条规则所属的设备。“匹配与消息 / 报警文本库”使用页签分区；文本库按 AlarmId 覆盖设备原始文本，可手工导入，也可直接扫描 TIA ProDiag。
3. “运行记录”统一放置实时日志和历史报警。历史报警要求先选择当前设备再查询，只展示该设备最近 20 条记录并按每页 10 条翻页；清理设备历史需要连续两次确认。机器人中的历史报警也先显示当前已连接设备，选择设备后再分页返回记录。
4. 在“通知与交互”统一管理 Telegram、钉钉、企业微信、飞书或自定义 Webhook。“通知通道”和“互动机器人”使用页签切换，并共同编辑当前通知目标。
5. “互动机器人”内部再分为“连接与对话”和“按钮与 PLC 动作”，配置管理员、命令、按钮、回复和 PLC 动作；本地预览不会发送消息或写入设备。
6. 点击“保存配置”。切换条目会保留当前编辑内容，但只有保存后才写入文件。删除设备前需先删除或重新关联其规则。
7. 按需点击“测试当前目标”，确认通知正常后启动服务。测试按钮会实际发送消息。

运行期间保存的新配置，在停止并重新启动监控后生效。关闭窗口前请保存修改。

## 采集与报警行为

- 每台设备独立后台轮询，按自己的 `poll_seconds` 运行；采集连接失败会在后续轮询重建。
- 连接和读取支持 `timeout` 参数，单位秒，默认 5，范围 0.1～120。同步工业驱动的停止速度受驱动自身超时影响。
- 报警按每条规则的条件从不匹配变为匹配时触发。激活状态下报警码变化也会触发新报警。
- 冷却期间抑制重复触发，被抑制的事件不延长冷却窗口。
- 恢复消息使用此前激活的报警码写入运行日志，不向通知渠道发送。
- 每个通知目的地拥有独立后台队列，发送慢或失败不会阻塞采集；失败最多尝试 3 次，重试间隔 5 秒。
- 每个目的地最多排队 1000 条，满队列会记录错误。队列仅保存在内存中，停止或退出会取消未发送消息；需连续监控时请保持程序运行。网络超时重试可能产生重复消息。
- 界面日志保留最近约 25 万字符。

消息模板支持 `{source_name}`、`{source_protocol}`、`{source_id}`、`{rule_name}`、`{severity}`、`{code}`、`{text}`、`{time}` 及设备返回的自定义字段（例如 `{value}`）。内置字段不会被设备 JSON 同名字段覆盖。

## 机器人交互与 PLC 控制

电话提醒独立于机器人通道。Windows 1.4.0 可在“设备配置 → 电话提醒”直接配置随身 WiFi 拨号，详见 [连接、测试及超时提醒步骤](WINDOWS_PHONE.md)。

“通知目标”负责报警推送，“机器人交互”负责接收消息和按钮操作。同一个 Telegram、企业微信或钉钉目标可以只启用其中一种，也可以同时启用。交互服务随主服务启动和停止。

每个动作可配置命令、按钮标题和以下行为：

| 行为 | 用途 |
| --- | --- |
| 自定义回复 | 返回可配置文本，可用 `{user}`、`{args}`、`{time}`、`{status}` |
| 查询运行状态 | 返回启用设备数、规则数和查询时间 |
| 查看历史报警 | 先选择已连接设备，再查看最近 20 条报警与恢复记录，每页 10 条 |
| 显示菜单 | 重新展示按钮；超过六项时自动分页 |
| 打开链接 | 展示 HTTP/HTTPS 链接按钮 |
| 写入 PLC | 控制 OPC UA 节点、S7 位、Modbus 线圈/寄存器或 Mock 仿真源 |

PLC 操作仅允许管理员执行，并且必须同时启用“允许 PLC 控制”。用户点击动作后，机器人先显示设备、地址、值和脉冲时间，再生成与当前用户和会话绑定的 60 秒一次性确认按钮。确认事件只能消费一次；重复的平台事件也不会再次执行。OPC UA、S7 和 Modbus 的监控读取、机器人写入使用同一设备锁；已连接的监控适配器会复用现有连接。OPC UA 动作需填写可写节点 ID、服务器节点对应的数据类型、写入值，以及使用脉冲时的恢复值。

完整按钮菜单只在 `/start`、`/menu` 和翻页时显示。报警推送是否附带交互按钮由“报警推送显示操作按钮”配置决定；普通回复、设备状态、历史报警和 PLC 执行结果只显示“返回菜单”。设备状态来自实时采集心跳，包含在线设备数、连接持续时间、最后心跳和当前有效报警数。历史记录保存在配置目录的 `alarm-history.jsonl`，超过 5MB 时保留最近 5000 条。

平台接入：

- **Telegram**：在通知目标填写 Bot Token；在机器人页启用交互并填写数字用户 ID。程序使用 `getUpdates` 长轮询，不能与该机器人的 Webhook 同时运行。按钮直接使用 Telegram 内联键盘。
- **企业微信**：创建支持长连接的智能机器人，在机器人页填写 Bot ID 和 Secret。程序连接企业微信长连接入口，接收文本、进入会话和模板卡片事件，并返回按钮交互卡片。
- **钉钉**：创建企业内部应用机器人并启用 Stream 模式，在机器人页填写 Client ID、Client Secret。普通文本回复可使用会话 Webhook；要使用真正的互动按钮，还需填写互动卡片模板 ID，并按 [钉钉互动卡片模板说明](../钉钉互动卡片模板说明.md) 创建模板。

允许交互的用户 ID 每行一个。Telegram 使用数字用户 ID；企业微信使用 `userid`；钉钉优先使用员工 `staffId`。凭据和管理员名单保存在本地 `config.json`，部署时应限制该文件和备份文件的访问权限。

## 协议参数

| 协议 | 主要参数与行为 |
| --- | --- |
| Mock | `toggle_every` 按读取次数切换状态；`code`、`text` 设置演示内容 |
| Siemens S7 | `host`、`cpu_type`（默认 S71200）、`rack`、`slot`、`db_number`、`trigger_byte`、`trigger_bit`、`code_offset`、`text_offset`、`text_length`、`encoding`；报警码按大端 16 位整数读取，文本为原始定长字节，非 Siemens STRING 头格式 |
| Modbus TCP | `host`、`port`、`unit_id`、`table`、`address`、`active_value`；地址为协议零基地址 |
| Modbus RTU | `port`、`baudrate`、`parity`（N/E/O 或枚举名）、`stopbits`（1/1.5/2）、`bytesize`，其余同 TCP |
| MQTT | `host`、`port`、`topic`、`qos`、`username`、`password`、`expected_active`；读取最近收到的消息，重连后等待新消息，不将无数据当作恢复 |
| OPC UA（报警码数组） | `endpoint`、`active_count_node_id`、`alarm_code_list_node_id`、`username`、`password`；保留原有方式，在同一订阅中监视有效报警数量和 DInt 报警码数组，按有效长度输出多条报警；质量不良时报告采集错误；机器人动作支持 Boolean、整数、浮点数和 String 节点写入 |
| OPC UA ProDiag（A&C） | `endpoint`、`event_node_id`、`username`、`password`、`publishing_interval_ms`、`event_queue_size`、`condition_refresh`；直接订阅 S7-1500 的 `ProcessDiagnostics` 报警事件。事件节点留空时自动发现，连接后可同步当前未清除报警；使用 ProDiag 的报警 ID、文本、时间、优先级和激活/恢复状态 |
| HTTP JSON | `url`、`method`、`headers_json`，非 GET 请求可用 `body_json` 设置请求体 |
| TCP JSON | `host`、`port`、`request_text`、`delimiter`；默认换行结束，支持分包；分隔符为空时读取到对端关闭连接；`max_response_bytes` 默认 1048576 |
| UDP JSON | `host`、`port`、`request_text`；一个数据报作为一条 JSON 响应 |
| 串口文本 | `port`、`baudrate`、`request_text`、`active_text`；按行读取并匹配报警文本 |
| Beckhoff ADS | `net_id`、`port`、`symbol`；读取符号值 |

JSON 协议使用 `active_key`、`code_key`、`text_key` 指定顶层字段，默认分别为 `active`、`code`、`text`。TCP 请求与分隔符中的 `\n`、`\r` 会转换为换行和回车。

OPC UA 报警数组模式默认订阅 `ns=4;s=Alarm.ActiveAlarmCount`（Int）和 `ns=4;s=Alarm.AlarmCodeList`（DInt 数组）。程序只处理数组下标 `0` 到 `ActiveAlarmCount-1`，忽略重复编号和配置的无效编号；新增编号分别报警，编号从有效范围消失时分别恢复。本地报警文本库按编号覆盖默认文本。一条规则可以将“匹配报警码”留空以接收数组中的所有编号，也可以为不同严重级别建立多条规则并分别填写匹配编号。

OPC UA ProDiag 模式要求 S7-1500 固件支持 Alarms & Conditions，并在 TIA Portal 的 CPU OPC UA 设置中启用该功能。程序优先自动寻找 `ProcessDiagnostics` EventNotifier；若现场服务器的地址空间不同，可在 `event_node_id` 中手动填写节点 ID。`condition_refresh=是` 会在首次连接后请求服务器重新发送当前仍有效的条件报警。ProDiag 消息文本直接作为报警文本，本地报警文本库仍可按报警 ID 覆盖。

ProDiag 文本中的 `@4%...@`、`@5%...@`、`@6%...@` 是关联值占位符。客户端在同一 OPC UA 事件订阅中读取 `AssociatedValue_01` 到 `AssociatedValue_10`，并按 `%s`、`%d`、`%u`、`%X`、`%b`、`%f` 等格式替换。对于 `@4%t#AreaGeneral@` 这类 PLC 报警文本列表引用，Openness 导入时会按“PLC、FB、监控实例、文本列表”分组，把该实例内每个 AlarmId 的监控行序号保存为固定文本索引；报警发生时优先用这个固定索引查中文文本，避免不同 AlarmId 因共享 SD 值而错误匹配成同一条。只有旧数据没有固定索引时，才回退到事件中的 SD_4 数值。若两种值都不可用，占位符会保留以提示检查 TIA 组态。

### TIA Openness 导入 ProDiag 文本

“报警规则 → 报警文本库”提供两个 TIA 入口：

- **从 TIA ProDiag 导入**：选择 `.ap21` 或 `.zap21`，客户端通过本机 TIA Portal V21 Openness 打开并扫描项目。
- **读取已打开 TIA**：附加到当前已在 TIA Portal V21 中打开的项目，只读扫描，不保存也不关闭该项目。归档缺少 GSD/GSDML 支持包时，建议使用这个入口。

扫描器递归遍历全部设备、PLC、程序块组和编程语言为 `ProDiag` 的 FB，并调用西门子公开接口 `ExportProDIAGInfo()`。所有 FB 的结果会合并为一张 UTF-8 CSV，总表包含报警级别、AlarmId、实例名、详细文本、FB 名称、FB 编号、文本列表、文本索引、PLC 名称、项目名称、ProDiag 标识、优先级和原始字段。扫描器还会通过 `PlcAlarmTextListProvider.ExportToXlsx()` 遍历 PLC 报警文本列表，生成中文索引表并自动导入当前规则。扫描器会识别无 BOM 的 UTF-8 文件，优先选择带本地文本列表引用的报警模板，并排除 `Information text` 等非报警字段。导入后客户端优先按完整 AlarmId 匹配；若 OPC UA 服务器只返回 Siemens 二进制 Condition NodeId，则解析其中的 FB 编号和监控编号进行唯一匹配。消息模板可使用 `{instance_name}`。

报警文本库首列提供勾选框，“全选 / 取消全选”可一次选择全部记录；批量删除前会显示选中数量并要求确认。

使用前需安装 TIA Portal V21 与 TIA Portal Openness，并将当前 Windows 用户加入 `Siemens TIA Openness` 用户组。首次附加 TIA 时按西门子提示允许客户端访问。归档方式要求项目引用的 GSD/GSDML 支持包已安装，且 ProDiag FB 处于一致状态。

S7 设备需按现场规范配置非优化 DB 和远程访问权限。所有真实设备协议仍需在现场联调；离线检查不能替代硬件验证。

当前 S7 驱动按 `DB编号 + 字节偏移 + 位编号` 访问绝对地址，例如 `DB10.DBX20.3`。因此在 TIA Portal 中应关闭对应 DB 的“优化的块访问”，并按所用 CPU 开放 PUT/GET 或外部客户端访问权限。启用优化块访问后地址由符号布局管理，不能保证 `DBX/DBW/DBB` 绝对偏移有效。

## 明确的功能边界

- EtherNet/IP 尚未接入实际 CIP 驱动。旧配置可保留，但启用该源时程序会阻止启动并解释原因。
- 机器人支持配置命令、按钮、回复和 PLC 写入；不提供通过聊天远程修改报警规则或管理员名单。检测到旧 Python 版 PLC 指令时会要求在新交互编辑器中重新确认设备与地址，避免将旧格式误当成普通回复。
- OPC UA 同时提供报警码数组订阅与 ProDiag Alarms & Conditions 事件订阅，两种模式独立配置，旧配置不会自动转换。
- 当前配置以版本 7（兼容旧版本） 的 `sources`、`rules`、`targets` 结构为基础。

## 配置保存

使用 `--config 路径` 可明确指定配置。未指定时优先使用可执行文件旁的配置，其次使用工作目录配置。

保存采用同目录临时文件与原子替换，已有配置保留一份 `config.json.bak`。程序不再截断超过 30 条的规则，也不会自动将无效规则关联到其他设备。配置包含凭据，请按实际需要限制文件访问权限；不要将真实配置或备份公开。

## 验证与发布

```powershell
dotnet run --project .\AlarmPushCenter\AlarmPushCenter.csproj -- --self-test
powershell -ExecutionPolicy Bypass -File .\发布.ps1
```

离线自检只使用临时配置、模拟适配器与本地回环网络，不访问实际设备或发送外部消息。覆盖多项编辑、机器人配置保存、原子保存、规则保留、TCP 分包/长度限制、UDP 超时、设备隔离与重连、规则换码与恢复、冷却、通知失败重试、命令鉴权、一次性 PLC 确认、事件防重放、菜单分页和企业平台卡片数据。

`发布.ps1` 先运行 Release 编译及自检，再更新 `AlarmPushCenter/publish-buttons`。发布不会覆盖根目录的现场配置文件。

## 目录

- `AlarmPushCenter/`：C# 项目和源码
- `AlarmPushCenter/publish-buttons/`：推荐使用的发布文件
- `config.json`：现有现场配置
- `启动.cmd`：使用根目录配置启动
- `发布.ps1`：编译、自检和发布

# PLC 与数据采集协议

[返回手册](README.md) · [设备工作区](CONFIGURATION.md#设备工作区) · [报警匹配](ALARMS.md)

## 公共设置与网络

每台设备填写名称、协议、启用开关、采集处理间隔（秒）。`timeout` 为连接/读取超时，默认 5 秒，可配置 0.1～120 秒。轮询失败会报告错误并在后续重连；不要靠极短轮询修复网络不通。

示例 PLC 为 `10.52.18.10 / 255.255.255.0` 时，采集端必须有到 `10.52.18.0/24` 的路由。同网段可给采集端使用一个确认未占用的地址，例如 `10.52.18.254`，且不能与已有网络冲突。实际填写前由现场确认地址。USB 随身 WiFi 上网与 PLC 有线网络可以同时存在：PLC 网口负责工业网，随身 WiFi 负责外网/电话；不要随意把默认路由指向没有互联网的 PLC 网口。

在 OpenWrt 运行时，`127.0.0.1` 指路由器自身；Windows 客户端运行时才指电脑自身。PLC 仿真在电脑上时，路由器不能使用 `opc.tcp://127.0.0.1:4840` 访问它。

## Mock：无设备测试

`toggle_every` 默认 8，表示经过多少次读取切换状态；`code` 默认 `1001`；`text` 为演示报警文本。适合检查规则、历史、曲线和按钮。Mock 同样可能通过已启用的目标发送真实通知或触发电话，所以离线演示时关闭真实通知与电话。

## OPC UA：动态报警码数组

这是保留的原有采集方式，适合 PLC 同时提供数量与数组。

| 参数 | 示例 / 默认 | 用途 |
| --- | --- | --- |
| `endpoint` | `opc.tcp://10.52.18.10:4840` | PLC OPC UA 服务地址 |
| `active_count_node_id` | `ns=4;s=Alarm.ActiveAlarmCount` | 有效报警数量，整数 |
| `alarm_code_list_node_id` | `ns=4;s=Alarm.AlarmCodeList` | DInt 等整数数组 |
| `username` / `password` | 空 | 服务器允许时使用匿名，否则填写账号 |
| `publishing_interval_ms` | 250 | 订阅发布周期，毫秒 |
| `sampling_interval_ms` | 250 | 节点采样周期，毫秒 |
| `subscription_coalesce_ms` | 50 | 数量/数组更新合并等待窗口 |
| `max_alarm_count` | 1000 | 有效数量上限，防止异常数据 |
| `inactive_code` | 0 | 忽略的无效编号 |
| `text` | 未配置的 OPC UA 报警码 | 未匹配字典时的回退文本 |

1. 在 PLC 开启 OPC UA 服务，确保两个节点可读，复制真实 NodeId。`ns` 的数字与字符串必须对应当前服务器，不能照搬其他项目。
2. 客户端选 OPC UA，填写 endpoint 和两个节点；保存并测试连接。
3. 同一订阅监视两个节点，读取 `ActiveAlarmCount=N` 后只处理数组 `0..N-1`。例如数量 2、数组 `[1001,1002,0,0]`，会分别形成 1001 和 1002。
4. 在规则中将“匹配报警码”留空可接收全部有效编号；要分级或分规则，再指定编号。
5. 在本地字典给 1001、1002 配中文说明。新增编号分别触发；从有效列表消失的编号分别恢复。重复编号和无效编号不重复生成同一报警。

PLC 写数组时尽量让数量与数组保持一致；客户端合并窗口不能代替 PLC 端一致性设计。节点读数质量差时会报告错误，不能用旧数值当成功心跳。

## OPC UA ProDiag：A&C 事件

适合提供 Alarms & Conditions 的 S7-1500 项目。它和数组模式是**两个独立协议选项**，不会自动把一个模式的配置转换成另一个。

| 参数 | 默认 / 说明 |
| --- | --- |
| `endpoint` | OPC UA 地址 |
| `event_node_id` | 留空自动寻找 `ProcessDiagnostics`；现场不同时手动指定 EventNotifier |
| `username` / `password` | OPC UA 身份 |
| `publishing_interval_ms` | 250 毫秒 |
| `event_queue_size` | 1000，服务器事件队列长度请求 |
| `condition_refresh` | 是；连接后请求当前仍有效条件 |

1. 核对 CPU、固件与 TIA 项目支持 A&C，并启用相应服务器功能。
2. 选 ProDiag 协议，填 endpoint，优先让事件节点自动发现。
3. 用一条已知测试报警验证 AlarmId、Message、Priority、Time、激活/恢复状态。
4. 若服务器文本已完整，不必强行用本地字典覆盖；需要实例中文文本时从 TIA 导出字典再导入。
5. `@5%s@`、`@6%...@` 属于关联值模板；`@4%t#AreaGeneral@` 属于文本列表引用，需要正确的实例、列表和索引关系。[匹配说明](ALARMS.md#tia-openness-导入)中有排查顺序。

连接成功但没有事件，不能只看 endpoint：还要核对事件节点、PLC 报警功能、报警是否实际触发，以及是否允许读取事件。

## Siemens S7：绝对地址

| 参数 | 默认 / 含义 |
| --- | --- |
| `host` | PLC IP |
| `cpu_type` | 未填默认 S71200；按现场 CPU 类型选择 |
| `rack` / `slot` | 默认 0 / 1，按 CPU 实际机架与槽位调整 |
| `db_number` | 默认 10 |
| `trigger_byte` / `trigger_bit` | 默认 0 / 0，触发 BOOL 的字节与位，位范围 0～7 |
| `code_offset` | 默认 2，报警码的字节偏移，大端 16 位整数 |
| `text_offset` / `text_length` | 默认 4 / 32，文本起始偏移和定长字节数 |
| `encoding` | 默认 utf-8，必须对应 PLC 实际编码 |

例如 `DB10.DBX20.3` 对应 DB=10、触发字节=20、位=3。**需要偏移量**。当前驱动采用绝对地址，目标 DB 应关闭“优化的块访问”，并按 CPU 安全设置允许对应远程访问/PUT-GET。优化 DB 的符号布局不保证固定偏移，不能把 OPC UA 符号 NodeId 填入 S7 偏移框。

文本按原始定长字节读取，不会自动跳过 Siemens STRING 的长度头；代码为 DInt 时也不能直接当默认 16 位 code 读取。此类数据布局更适合调整 PLC 接口或使用 OPC UA 数组。

## Modbus TCP / RTU

公共参数：`unit_id`（默认 1）、`table`（coil / discrete_input / holding_register / input_register）、`address`（协议零基地址，默认 0）、`active_value`（默认 1）、`code` 和 `text`（此点激活时的固定编号与文本）。界面选择项以当前下拉为准。

- TCP：`host`、`port`（默认 502）。
- RTU：`port`（Windows 如 COM3，OpenWrt 如实际 `/dev/ttyUSB0`）、`baudrate`（9600）、`parity`（N/E/O）、`stopbits`（1/1.5/2）、`bytesize`（8）。OpenWrt 需具备对应 USB/串口驱动与访问权限。
- 厂商手册中的 40001 往往表示第一个保持寄存器，协议地址可能为 0；必须按设备文档换算，不能直接把展示编号当线上的地址。
- 报警激活值须与设备返回值一致；机器人写入只能选择可写的线圈/保持寄存器，不能写离散输入或输入寄存器。

## MQTT

填写 `host`、`port`（1883）、`topic`（默认 `factory/alarm`）、`qos`（0/1/2）、`username`、`password`。消息使用 JSON，例如：

```json
{"active":true,"code":"1001","text":"电机过载","value":86}
```

`active_key` / `code_key` / `text_key` 默认是 active/code/text；`expected_active` 默认 true。读取最近收到的消息，重连后等待新数据，无消息不会被当作恢复。Broker 地址应从实际采集端可达。

## HTTP / TCP / UDP JSON

三者使用与 MQTT 相同的顶层 JSON 字段配置。需要返回激活状态、编号和文本；不会自动解析任意多层 JSON 路径。

| 协议 | 参数与收包方式 |
| --- | --- |
| HTTP JSON | `url`、`method`（GET）、`headers_json`（JSON 对象）；非 GET 可用 `body_json` 设置请求体 |
| TCP JSON | `host`、`port`（9000）、`request_text`、`delimiter`（默认换行）；支持分包，空分隔符表示读到对端关闭；`max_response_bytes` 默认 1048576 |
| UDP JSON | `host`、`port`（9001）、`request_text`；一个数据报作为一条响应 |

TCP 请求与分隔符中的字面 `\n` / `\r` 会转为换行 / 回车。JSON 要使用英文双引号；多余尾逗号、错误 Content-Type 或服务返回 HTML 都可能导致解析失败。

## 串口文本 / Beckhoff ADS

- 串口文本：`port`、`baudrate`、`request_text`、`active_text`（默认 ALARM）、固定 `code` / `text`。按行读取，核对设备换行格式与串口权限。
- ADS：`net_id`（AMS Net ID）、`port`（默认 851）、`symbol`（默认 `MAIN.bAlarm`）、固定 `code` / `text`；同时核对 TwinCAT 路由和符号访问权限。
- EtherNet/IP：目前没有实际 CIP 驱动，只保留旧配置兼容；启用时会阻止启动并解释原因，不可作为已支持协议投产。

真实 PLC、证书信任、安全策略、串口与工业网络都需要现场验证。测试连接只说明当前连接检查结果，不能证明所有规则、写入或外部通知都正确。

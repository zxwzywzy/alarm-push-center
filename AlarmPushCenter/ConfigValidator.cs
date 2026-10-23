namespace AlarmPushCenter;

public static class ConfigValidator
{
    public static void Validate(AppConfig config, bool forMonitoring = false)
    {
        Unique(config.Sources.Select(x => x.Id), "采集源");
        Unique(config.Rules.Select(x => x.Id), "报警规则");
        Unique(config.Targets.Select(x => x.Id), "通知目标");
        foreach (var source in config.Sources)
        {
            ValidatePhoneReminder(source.PhoneReminder ?? new());
            if (!double.IsFinite(source.PollSeconds) || source.PollSeconds is < 0.2 or > 3600)
                throw new InvalidOperationException($"[{source.Name}] 轮询间隔必须在 0.2～3600 秒之间。");
            var timeout = source.Params.GetDouble("timeout", 5);
            if (!double.IsFinite(timeout) || timeout is < 0.1 or > 120)
                throw new InvalidOperationException($"[{source.Name}] timeout 必须在 0.1～120 秒之间。");
            if (!source.Enabled) continue;
            if (!ConfigDefaults.Protocols.Contains(source.Protocol, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"[{source.Name}] 未知采集协议：{source.Protocol}");
            if (forMonitoring && source.Protocol == "ethernet_ip")
                throw new InvalidOperationException($"[{source.Name}] EtherNet/IP 驱动尚未接入。请禁用该源，或使用设备提供的 OPC UA / Modbus 接口。");
            if (source.Protocol == "modbus_rtu")
            {
                _ = ModbusSource.ParseParity(source.Params.Get("parity", "N"));
                _ = ModbusSource.ParseStopBits(source.Params.Get("stopbits", "1"));
            }
            if (source.Protocol is "opcua" or "opcua_prodiag")
            {
                if (!Uri.TryCreate(source.Params.Get("endpoint"), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "opc.tcp")
                    throw new InvalidOperationException($"[{source.Name}] OPC UA 服务地址必须使用 opc.tcp://。");
                if (source.Protocol == "opcua" && (string.IsNullOrWhiteSpace(source.Params.Get("active_count_node_id")) || string.IsNullOrWhiteSpace(source.Params.Get("alarm_code_list_node_id"))))
                    throw new InvalidOperationException($"[{source.Name}] 请配置有效报警数量节点和报警码数组节点。");
                if (source.Params.GetInt("publishing_interval_ms", 250) is < 50 or > 60000 || (source.Protocol == "opcua" && source.Params.GetInt("sampling_interval_ms", 250) is < 50 or > 60000))
                    throw new InvalidOperationException($"[{source.Name}] OPC UA 发布和采样间隔必须在 50～60000 毫秒之间。");
                if (source.Protocol == "opcua" && source.Params.GetInt("max_alarm_count", 1000) is < 1 or > 10000)
                    throw new InvalidOperationException($"[{source.Name}] OPC UA 最大报警数量必须在 1～10000 之间。");
                if (source.Protocol == "opcua_prodiag" && source.Params.GetInt("event_queue_size", 1000) is < 10 or > 10000)
                    throw new InvalidOperationException($"[{source.Name}] ProDiag 事件队列必须在 10～10000 之间。");
                if (source.Protocol == "opcua_prodiag" && !string.IsNullOrWhiteSpace(source.Params.Get("event_node_id")))
                    try { _ = Opc.UaFx.OpcNodeId.Parse(source.Params.Get("event_node_id")); }
                    catch (Exception ex) { throw new InvalidOperationException($"[{source.Name}] ProDiag 事件节点 ID 格式无效。", ex); }
            }
        }
        foreach (var rule in config.Rules)
        {
            if (!config.Sources.Any(x => x.Id == rule.SourceId))
                throw new InvalidOperationException($"[{rule.Name}] 引用的采集源不存在。");
            if (!double.IsFinite(rule.CooldownSeconds) || rule.CooldownSeconds is < 0 or > 86400)
                throw new InvalidOperationException($"[{rule.Name}] 冷却时间必须在 0～86400 秒之间。");
        }
        foreach (var target in config.Targets.Where(x => x.Enabled))
        {
            if (!ConfigDefaults.Channels.Contains(target.Channel, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"[{target.Name}] 未知通知渠道。");
            if (target.Channel == "telegram")
            {
                if (!BotInteraction.ValidTelegramToken(target.Token) || target.Recipients.Count == 0)
                    throw new InvalidOperationException($"[{target.Name}] 请填写有效的 Telegram 机器人令牌（数字ID:密钥）和至少一个会话 ID。");
            }
            else if (target.Channel == "dingtalk" && (!string.IsNullOrWhiteSpace(target.Params.Get("bot_client_id")) || !string.IsNullOrWhiteSpace(target.Params.Get("bot_client_secret"))))
            {
                if (string.IsNullOrWhiteSpace(target.Params.Get("bot_client_id")) || string.IsNullOrWhiteSpace(target.Params.Get("bot_client_secret")))
                    throw new InvalidOperationException($"[{target.Name}] 企业应用机器人需要同时填写 Client ID 和 Client Secret。");
                if (target.Params.Get("ding_message_target") == "users" && !target.Recipients.Any(x => !string.IsNullOrWhiteSpace(x)))
                    throw new InvalidOperationException($"[{target.Name}] 请填写接收通知的企业成员 userId（每行一个）。");
            }
            else if (!Uri.TryCreate(target.WebhookUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"[{target.Name}] Webhook 地址必须是有效的 HTTP/HTTPS URL。");
        }
        foreach (var target in config.Targets)
        {
            BotInteraction.Validate(target);
            if (target.Channel == "dingtalk" && BotInteraction.ShowButtonsOnAlarm(target))
            {
                if (!BotInteraction.Enabled(target))
                    throw new InvalidOperationException($"[{target.Name}] 钉钉报警按钮需要同时启用机器人交互。");
                if (string.IsNullOrWhiteSpace(target.Params.Get("card_template_id")))
                    throw new InvalidOperationException($"[{target.Name}] 请填写钉钉互动卡片模板 ID。目标群会话 ID 可由机器人收到群消息后自动记录。");
            }
            if (!BotInteraction.Enabled(target)) continue;
            foreach (var command in BotInteraction.Commands(target).Where(x => x.Action == "plc_write"))
            {
                var source = config.Sources.FirstOrDefault(x => x.Id == command.SourceId);
                if (source is null || source.Protocol is not ("mock" or "s7" or "modbus_tcp" or "modbus_rtu" or "opcua" or "opcua_prodiag"))
                    throw new InvalidOperationException($"[{command.Command}] 请选择 Mock、S7、Modbus 或 OPC UA 设备。");
                if (source.Protocol == "s7" && (command.WriteType != "bit" || command.Address is < 0 or > 65535 || command.DbNumber is < 1 or > 65535 || command.Bit is < 0 or > 7 || command.Value is < 0 or > 1))
                    throw new InvalidOperationException($"[{command.Command}] S7 仅支持 DB 位写入，位值必须为 0/1。");
                if (source.Protocol is "modbus_tcp" or "modbus_rtu" && (command.WriteType is not ("bit" or "register") || command.Address is < 0 or > 65535 || command.Value is < 0 or > 65535 || (command.WriteType == "bit" && command.Value > 1) || (command.WriteType == "register" && command.PulseMs > 0)))
                    throw new InvalidOperationException($"[{command.Command}] Modbus 位值为 0/1、寄存器值为 0～65535，寄存器不支持脉冲。");
                if (source.Protocol is "opcua" or "opcua_prodiag") OpcUaSource.ValidateWriteCommand(command);
            }
        }
        var identities = config.Targets.Where(BotInteraction.Enabled).Select(x => x.Channel + ":" + (x.Channel == "telegram" ? x.Token : x.Params.Get("bot_client_id")));
        if (identities.Distinct().Count() != identities.Count()) throw new InvalidOperationException("同一个机器人只能启用一个交互接收配置。");
    }

    private static void Unique(IEnumerable<string> ids, string label)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || !seen.Add(id)))
            throw new InvalidOperationException($"{label} ID 为空或重复，请检查配置文件。");
    }

    public static void ValidatePhoneReminder(PhoneReminder reminder)
    {
        if (!reminder.Enabled) return;
        if (reminder.DelaySeconds is < 1 or > 86400)
            throw new InvalidOperationException("电话提醒延时必须在 1～86400 秒之间。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(reminder.Number ?? "", @"^\+?[0-9]{7,15}$"))
            throw new InvalidOperationException("请填写 7～15 位接听号码，可使用 + 国际区号。");
    }
}

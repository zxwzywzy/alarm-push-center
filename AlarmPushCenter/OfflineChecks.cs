using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AlarmPushCenter;

public static class OfflineChecks
{
    private static int _passed;
    public static void RunUiChecks()
    {
        ApplicationConfiguration.Initialize();
        var folder = Path.Combine(Path.GetTempPath(), "alarm-ui-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigStore(Path.Combine(folder, "config.json"));
            var config = new AppConfig
            {
                Sources = [Source("first"), Source("second")],
                Rules = [new AlarmRule { Id = "one", SourceId = "first" }, new AlarmRule { Id = "two", SourceId = "second" }],
                Targets = [new NotificationTarget { Id = "one" }, new NotificationTarget { Id = "two" }]
            }.Normalize();
            config.Targets[1].Params["command_defs"] = JsonValues.Element(BotInteraction.Serialize([
                new BotCommandDefinition { Command = "/status", Button = "设备状态", Action = "status", Reply = "{status}" }
            ]));
            store.Save(config);
            using var form = new MainForm(store.Path);
            var updateScript = MainForm.DesktopUpdateScript("C:\\test folder\\setup.exe", "C:\\operator's folder\\config.json", "C:\\test folder\\app", 123456789);
            Assert(updateScript.Contains("operator''s folder"), "更新脚本必须正确转义自定义配置路径中的单引号");
            Assert(updateScript.Contains("--config") && updateScript.Contains("/DIR=\"C:\\test folder\\app\""), "更新后必须继续使用原安装与配置目录");
            var scriptPath = Path.Combine(folder,"update-script.ps1"); File.WriteAllText(scriptPath,updateScript);
            using (var parser = new Process {StartInfo=new("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}})
            {
                foreach(var argument in new[]{"-NoProfile","-Command","$errors=$null;$tokens=$null;[System.Management.Automation.Language.Parser]::ParseFile('"+scriptPath.Replace("'","''")+"',[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count){exit 1}"}) parser.StartInfo.ArgumentList.Add(argument);
                parser.Start(); parser.WaitForExit(); Assert(parser.ExitCode==0,"更新辅助脚本必须能被 Windows PowerShell 正确解析");
            }
            T Field<T>(string name) => (T)typeof(MainForm).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Size = form.MinimumSize;
            form.Show(); form.PerformLayout(); Application.DoEvents();
            var navigation = Field<List<Button>>("_navigationButtons");
            Assert(navigation.All(button => button.Width > 0 && button.Right <= button.Parent!.ClientSize.Width), "最小窗口宽度下所有导航入口必须完整显示");
            navigation[3].PerformClick(); Application.DoEvents();
            Assert(Field<Label>("_pageTitle").Text == "通知与交互" && ((WorkspaceNavigationButton)navigation[3]).IsActive, "点击导航应同步页面标题与选中状态");
            navigation[0].PerformClick();
            Assert(navigation.Cast<WorkspaceNavigationButton>().Count(button => button.IsActive) == 1, "顶栏仅能有一个当前工作区");
            form.Hide();
            var sourceGrid = Field<DataGridView>("_sourceParams");
            Assert(sourceGrid.Rows.Cast<DataGridViewRow>().Any(x => Convert.ToString(x.Cells[0].Value)?.Contains("仿真切换间隔") == true), "协议参数应显示中文名称");
            var connectionGrid = Field<DataGridView>("_connectionGrid");
            Assert(connectionGrid.Columns.Count == 5 && connectionGrid.Columns[2].HeaderText == "连接状态", "概览应显示设备连接心跳表");
            Assert(Field<List<Panel>>("_pages").Count == 5 && Field<List<Button>>("_navigationButtons").Count == 5, "通知与交互、日志与历史应分别合并为统一工作区");
            Assert(Field<TabControl>("_messageTabs").TabPages.Count == 2, "消息中心应使用通知通道与互动机器人双页签");
            Field<TextBox>("_sourceName").Text = "编辑后的设备一";
            Field<CheckBox>("_phoneEnabled").Checked = true;
            Field<TextBox>("_phoneNumber").Text = "13800138000";
            Field<NumericUpDown>("_phoneDelay").Value = 120;
            Field<ComboBox>("_sourceSelector").SelectedIndex = 1;
            Assert(Field<TextBox>("_sourceName").Text == "second", "界面可切换到第二台设备");
            Assert(!Field<CheckBox>("_phoneEnabled").Checked && Field<TextBox>("_phoneNumber").Text == "", "第二台设备不会继承第一台设备的电话提醒");
            Field<TextBox>("_phoneNumber").Text = "13900139000";
            Field<NumericUpDown>("_phoneDelay").Value = 600;
            Assert(Field<TabControl>("_sourceTabs").TabPages[1].Text == "电话提醒", "当前设备应有独立的电话提醒配置页");
            Field<TextBox>("_sourceName").Text = "编辑后的设备二";
            Field<ComboBox>("_ruleSelector").SelectedIndex = 1;
            Field<ComboBox>("_targetSelector").SelectedIndex = 1;
            Field<ComboBox>("_botTarget").SelectedIndex = 1;
            Field<TextBox>("_token").Text = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcd";
            Field<CheckBox>("_botEnabled").Checked = true;
            Field<CheckBox>("_botAllowWrite").Checked = true;
            Field<CheckBox>("_botAlarmButtons").Checked = true;
            Field<TextBox>("_botAdmins").Text = "1001";
            Field<TextBox>("_botWelcome").Text = "欢迎 {user}";
            var saved = (bool)typeof(MainForm).GetMethod("SaveFromUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, [true])!;
            var loaded = store.Load();
            Assert(saved && loaded.Sources[0].Name == "编辑后的设备一" && loaded.Sources[1].Name == "编辑后的设备二", "切换项目不丢失编辑内容");
            Assert(loaded.Sources[0].PhoneReminder is { Enabled: true, DelaySeconds: 120, Number: "13800138000" }
                && loaded.Sources[1].PhoneReminder is { Enabled: false, DelaySeconds: 600, Number: "13900139000" }, "保存必须保留各设备独立的号码、时间和提醒开关");
            Assert(loaded.Sources.All(x => x.Params.ContainsKey("toggle_every")), "中文参数名称不能改写内部配置键");
            Assert(loaded.Rules[0].SourceId == "first" && loaded.Rules[1].SourceId == "second", "切换设备不能改写规则关联");
            Assert(BotInteraction.Enabled(loaded.Targets[1]) && loaded.Targets[1].Params.Get("allow_plc_write") == "是" && loaded.Targets[1].Params.Get("admin_ids") == "1001", "机器人权限配置应写入当前通知目标");
            Assert(BotInteraction.ShowButtonsOnAlarm(loaded.Targets[1]), "报警推送按钮开关应保存到当前通知目标");
            Assert(loaded.Targets[1].Params.Get("welcome_text") == "欢迎 {user}", "机器人对话内容应完整保存");
            Field<ComboBox>("_sourceSelector").SelectedIndex = 0;
            Field<ComboBox>("_protocol").SelectedIndex = Array.IndexOf(ConfigDefaults.Protocols.ToArray(), "opcua");
            var parameterHost = Field<TableLayoutPanel>("_sourceParameterHost");
            TextBox Parameter(string key) => (TextBox)parameterHost.Controls.Find("source-param-" + key, true).Single();
            const string countNode = "ns=3;s=\"DB Alarm\".\"AlarmCount\"";
            Parameter("endpoint").Text = "opc.tcp://192.0.2.10:4840";
            Parameter("active_count_node_id").Text = countNode;
            Parameter("alarm_code_list_node_id").Text = "ns=3;s=\"DB Alarm\".\"AlarmCodes\"";
            Assert(Parameter("password").UseSystemPasswordChar, "普通参数表单必须遮挡密码");
            Field<ComboBox>("_sourceSelector").SelectedIndex = 1;
            Assert(Field<AppConfig>("_config").Sources[0].Params.Get("active_count_node_id") == countNode,
                "参数表单编辑后切换设备，带引号 NodeId 应完整保留在草稿中");
            Field<ComboBox>("_sourceSelector").SelectedIndex = 0;
            Assert(Parameter("active_count_node_id").Text == countNode && Field<AppConfig>("_config").Sources[1].Protocol == "mock",
                "返回设备时恢复独立参数，且不改变另一台设备协议");
            var persisted = (bool)typeof(MainForm).GetMethod("SaveFromUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, [true])!;
            Assert(persisted && store.Load().Sources[0].Params.Get("active_count_node_id") == countNode,
                "保存配置后带引号 NodeId 应完整写入文件");
        }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
    public static async Task RunAsync()
    {
        await DesktopPhoneChecks.RunAsync();
        CheckConfig();
        CheckRenderingAndValidation();
        await CheckFramesAsync();
        await CheckNetworkAsync();
        await CheckIsolationAsync();
        await CheckRulesAsync();
        await CheckRetriesAsync();
        await CheckBotCommandsAsync();
        await CheckDingApplicationAsync();
        Console.WriteLine($"offline checks passed: {_passed} assertions");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("检查失败：" + message);
        _passed++;
    }

    private static SourceConfig Source(string id = "source") => new()
    {
        Id = id, Name = id, Protocol = "mock", PollSeconds = 0.2,
        Params = new() { ["toggle_every"] = JsonValues.Element("1") }
    };
    private static Signal Signal(bool active, string code = "1001", string mode = "alarm") => new(active, code, "高温",
        new Dictionary<string, object?> { ["value"] = active ? 1 : 0, ["mode"] = mode, ["text"] = "设备原文" });

    private static void CheckConfig()
    {
        Assert(ProductDisclaimer.Text.Contains("责任限制") && ProductDisclaimer.Text.Contains("法律允许"), "发行程序必须内置完整免责声明");
        var folder = Path.Combine(Path.GetTempPath(), "alarm-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigStore(Path.Combine(folder, "config.json"));
            var source = Source();
            var config = new AppConfig { Sources = [source], Rules = Enumerable.Range(0, 45).Select(i =>
                new AlarmRule { Id = "rule" + i, SourceId = source.Id, Name = "rule" + i }).ToList() }.Normalize();
            store.Save(config);
            Assert(store.Load().Rules.Count == 45, "45 条规则必须完整保存");
            config.Sources[0].Name = "第二版";
            store.Save(config);
            Assert(new ConfigStore(store.Path + ".bak").Load().Sources[0].Name == "source", "原子保存保留上一版备份");
            var before = File.ReadAllText(store.Path);
            config.Sources[0].PollSeconds = -1;
            try { store.Save(config); Assert(false, "非法配置应拒绝保存"); }
            catch (InvalidOperationException) { }
            Assert(File.ReadAllText(store.Path) == before, "验证失败不能覆盖配置");
            config.Sources[0].PollSeconds = 0.2;
            config.Rules[0].SourceId = "missing";
            try { store.Save(config); Assert(false, "孤立规则必须报错"); }
            catch (InvalidOperationException) { }
            Assert(File.ReadAllText(store.Path) == before, "不能把孤立规则静默转给其他设备");
            var history = new AlarmHistoryStore(store.Path);
            history.Record(new AlarmHistoryEntry(DateTime.Now, "alarm", "设备A", "1001", "电机过载", "warning"));
            Assert(history.FormatRecent().Contains("1001") && history.FormatRecent().Contains("电机过载"), "历史报警应持久保存并可供机器人查询");
            var historyPath = Path.Combine(folder, "alarm-history.jsonl");
            var latest = new AlarmHistoryEntry(DateTime.Now.AddMinutes(1), "alarm", "设备B", "2001", "温度过高", "warning");
            history.Record(latest);
            history.Record(latest with { Time = DateTime.Now.AddMinutes(-1), Code = "2000" });
            Assert(history.ReadRecent("", 1)[0].Code == "2001" && history.FormatRecent(1).Contains("2001"), "历史按时间排序，不能把最后写入的旧记录当成最新记录");
            File.AppendAllText(historyPath, "not-json\n");
            new AlarmHistoryStore(store.Path).Record(latest with { Code = "3001", Time = latest.Time.AddSeconds(1) });
            Assert(history.ReadRecent("", 1)[0].Code == "3001" && history.ReadRecent("设备B").Count == 3, "外部写入应刷新缓存，损坏行不能影响有效记录");
            Assert(history.DeleteForSource("设备A") == 1 && history.ReadRecent("设备A").Count == 0, "按设备删除后缓存必须刷新");
            Assert(history.DeleteAll() == 3 && history.ReadRecent("").Count == 0 && new FileInfo(historyPath).Length == 0, "清空历史应覆盖所有设备并删除损坏行");
            history.Record(latest);
            Assert(history.ReadRecent("").Count == 1, "清空历史后应仍可追加并读取新报警");
        }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }

    private static void CheckRenderingAndValidation()
    {
        var source = Source();
        var rule = new AlarmRule { SourceId = source.Id, AlarmTexts = [new AlarmText { Code = "1001", InstanceName = "电机_1", Text = "自定义高温" }] };
        Assert(MessageRenderer.Render("{source_name}-{code}-{instance_name}-{text}-{value}", source, Signal(true), rule) == "source-1001-电机_1-自定义高温-1", "报警文本库应提供实例名且 JSON 字段不能覆盖文本");
        Assert(ModbusSource.ParseParity("N") == Parity.None && ModbusSource.ParseParity("Even") == Parity.Even, "串口校验位兼容");
        Assert(ModbusSource.ParseStopBits("1.5") == StopBits.OnePointFive && ModbusSource.ParseStopBits("2") == StopBits.Two, "串口停止位兼容");
        var opc = Source("opc");
        opc.Protocol = "opcua";
        opc.Params = ConfigDefaults.MergeSourceParams("opcua", new Dictionary<string, JsonElement>());
        var opcSignals = OpcUaSource.BuildSignals(opc, 3, new[] { 1001, 1002, 1002, 9999 });
        Assert(opcSignals.Select(x => x.Code).SequenceEqual(["1001", "1002"]) && opcSignals.All(x => Equals(x.Values["active_count"], 3)), "OPC UA 按有效数量截取数组并去重报警码");
        var structuredSignals = OpcUaSource.BuildSignals(opc, 2, new[] { new byte[] { 0, 0, 3, 233, 1 }, new byte[] { 0, 0, 3, 234, 1 } });
        Assert(structuredSignals.Select(x => x.Code).SequenceEqual(["1001", "1002"]), "OPC UA 应解析 Siemens 结构数组中的 DInt 报警码");
        Assert(Equals(OpcUaSource.ParseWriteValue("Boolean", "1"), true) && Equals(OpcUaSource.ParseWriteValue("Int32", "-12"), -12), "OPC UA 写入值应按配置的数据类型转换");
        var opcWrite = new BotCommandDefinition { Command = "/opc_reset", Action = "plc_write", SourceId = opc.Id, OpcNodeId = "ns=3;s=\"Control_DB\".\"Reset\"", OpcDataType = "Boolean", OpcValue = "true", OpcResetValue = "false", PulseMs = 500 };
        OpcUaSource.ValidateWriteCommand(opcWrite); _passed++;
        Assert(OpcUaSource.BuildSignals(opc, 0, Array.Empty<int>()).Count == 0, "OPC UA 有效报警数量为零时应返回空报警集合");
        var proDiag = OpcUaProDiagSource.BuildSignal("4711", "驱动故障", true, 700, "ProcessDiagnostics", "MotorAlarm", DateTime.UtcNow, false, true, "event-1");
        Assert(proDiag.Active && proDiag.Code == "4711" && proDiag.Text == "驱动故障" && Equals(proDiag.Values["severity"], 700), "OPC UA ProDiag 事件应转换为带编号、文本和优先级的报警信号");
        Assert(OpcUaProDiagSource.TryDecodeSimaticConditionNodeId("ns=3;b=AQACAJIDDoo=", out var fbNumber, out var supervisionId) && fbNumber == 914 && supervisionId == "0001", "应从 Siemens ProDiag 二进制 Condition NodeId 解析 FB 编号和监控编号");
        var proDiagLibrary = new AlarmRule
        {
            SourceId = opc.Id,
            AlarmTexts = [new AlarmText { Code = "00460001", FbNumber = "914", InstanceName = "Opmode01PD", Text = "错误的 TIM DEL 设置" }]
        };
        var binaryProDiag = OpcUaProDiagSource.BuildSignal("ns=3;b=AQACAJIDDoo=", "服务器原始文本", true, 700, "ProcessDiagnostics", "MotorAlarm", DateTime.UtcNow, false, true, "event-2");
        Assert(MessageRenderer.ResolveAlarmCode(proDiagLibrary, binaryProDiag) == "00460001" && MessageRenderer.ResolveAlarmText(proDiagLibrary, binaryProDiag) == "错误的 TIM DEL 设置", "ProDiag 应按 FB 编号和监控编号匹配 Openness AlarmId 与中文文本");
        var dynamicProDiag = OpcUaProDiagSource.BuildSignal("00460002", "设备 @5%s@，计数 @6%4d@，温度 @7%6.1f@", true, 500, "ProcessDiagnostics", "DynamicAlarm", DateTime.UtcNow, false, true, "event-3",
            new Dictionary<int, object?> { [5] = "M01", [6] = 12, [7] = 23.45 });
        Assert(dynamicProDiag.Text == "设备 M01，计数 0012，温度 23.4" && Equals(dynamicProDiag.Values["associated_value_5"], "M01"), $"ProDiag 应订阅并格式化 SD_1 到 SD_10 关联值（实际：{dynamicProDiag.Text}）");
        var dynamicRule = new AlarmRule { SourceId = opc.Id, AlarmTexts = [new AlarmText { Code = "00460002", Text = "设备 @5%s@ 报警，当前值 @6%u@" }] };
        Assert(MessageRenderer.ResolveAlarmText(dynamicRule, dynamicProDiag) == "设备 M01 报警，当前值 12", "本地中文报警模板也应使用 OPC UA 关联值替换占位符");
        Assert(OpcUaProDiagSource.DescribeAssociatedValuePlaceholders("@5%s@:@6%u@") == "〈运行时关联值 SD_5〉:〈运行时关联值 SD_6〉", "配置界面应以可读名称展示关联值占位符");
        var textListRule = new AlarmRule
        {
            SourceId = opc.Id,
            AlarmTexts = [new AlarmText { Code = "00380FD1", Text = "区域：@4%t#AreaGeneral@", TextListName = "AreaGeneral", TextListIndex = "1" }],
            AlarmTextLists =
            [
                new AlarmTextListEntry { PlcName = "PLC1", ListName = "AreaGeneral", From = "1", To = "1", Text = "VFD1_Q1未反馈" },
                new AlarmTextListEntry { PlcName = "PLC1", ListName = "AreaGeneral", From = "5", To = "5", Text = "上一PLC急停按下" },
                new AlarmTextListEntry { PlcName = "PLC1", ListName = "AREAGENERAL", From = "5", To = "5", Text = "不应选中" }
            ]
        };
        var textListSignal = OpcUaProDiagSource.BuildSignal("00380FD1", "原始文本", true, 500, "ProcessDiagnostics", "TextListAlarm", DateTime.UtcNow, false, true, "event-4",
            new Dictionary<int, object?> { [4] = (ushort)5 });
        Assert(MessageRenderer.ResolveAlarmText(textListRule, textListSignal) == "区域：VFD1_Q1未反馈", "ProDiag 应优先使用 AlarmId 对应的固定监控行索引，不能误用共享运行时关联值");

        var dynamicTextListRule = new AlarmRule
        {
            AlarmTexts = [new AlarmText { Code = "00380FD1", Text = "区域：@4%t#AreaGeneral@" }],
            AlarmTextLists = textListRule.AlarmTextLists
        };
        Assert(MessageRenderer.ResolveAlarmText(dynamicTextListRule, textListSignal) == "区域：上一PLC急停按下", "没有固定映射时仍应支持按运行时关联值解析动态文本列表");
        foreach (var (channel, json) in new[] { ("telegram", "{\"ok\":true}"), ("wecom", "{\"errcode\":0}"), ("dingtalk", "{\"errcode\":0}"), ("feishu", "{\"code\":0}") })
        {
            using var doc = JsonDocument.Parse(json);
            Notifier.ValidateResponse(channel, doc.RootElement);
            _passed++;
        }
        using var failure = JsonDocument.Parse("{\"errcode\":40014}");
        var rejected = false;
        try { Notifier.ValidateResponse("wecom", failure.RootElement); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "HTTP 200 业务失败不能报告成功");
    }

    private static async Task CheckFramesAsync()
    {
        var payload = "{\"active\":true,\"text\":\"" + new string('温', 4000) + "\"}";
        await using var fragmented = new FragmentStream(Encoding.UTF8.GetBytes(payload + "\r\nextra"), 7);
        var frame = await TcpJsonSource.ReadFrameAsync(fragmented, "\r\n", 20000, CancellationToken.None);
        Assert(frame == payload, "超过 8192 字节及多字节中文分包应完整读取");
        await using var eof = new FragmentStream(Encoding.UTF8.GetBytes("{}"), 1);
        Assert(await TcpJsonSource.ReadFrameAsync(eof, "", 10, CancellationToken.None) == "{}", "无分隔符时按 EOF 收包");
        await using var missing = new FragmentStream(Encoding.UTF8.GetBytes("{}"), 1);
        await ThrowsAsync<IOException>(() => TcpJsonSource.ReadFrameAsync(missing, "\n", 10, CancellationToken.None), "缺少分隔符应报错");
        await using var oversized = new FragmentStream(Encoding.UTF8.GetBytes("1234567890\n"), 3);
        await ThrowsAsync<IOException>(() => TcpJsonSource.ReadFrameAsync(oversized, "\n", 5, CancellationToken.None), "响应上限应生效");
    }

    private static async Task CheckNetworkAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = Source("tcp");
        source.Protocol = "tcp_json";
        source.Params = new() { ["host"] = JsonValues.Element("127.0.0.1"), ["port"] = JsonValues.Element(((IPEndPoint)listener.LocalEndpoint).Port.ToString()), ["timeout"] = JsonValues.Element("0.3") };
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"active\":"), deadline.Token);
            await Task.Delay(35, deadline.Token);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("true,\"code\":\"42\"}\n"), deadline.Token);
        });
        await using (var adapter = new TcpJsonSource(source))
        {
            var signal = await adapter.ReadAsync(deadline.Token);
            Assert(signal.Active && signal.Code == "42", "真实 TCP 分包端到端读取");
        }
        await server;
        using var silentServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        source.Protocol = "udp_json";
        source.Params["port"] = JsonValues.Element(((IPEndPoint)silentServer.Client.LocalEndPoint!).Port.ToString());
        await using var udp = new UdpJsonSource(source);
        var watch = Stopwatch.StartNew();
        await ThrowsAsync<OperationCanceledException>(() => udp.ReadAsync(deadline.Token), "UDP 无响应必须超时");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(2), "UDP 请求超时不依赖停止监控");
    }

    private static async Task CheckIsolationAsync()
    {
        var slow = Source("slow"); slow.PollSeconds = 0.6;
        var fast = Source("fast");
        var counts = new ConcurrentDictionary<string, int>();
        var events = new ConcurrentQueue<RuntimeEvent>();
        var initialized = 0;
        var config = new AppConfig { Sources = [slow, fast], Rules = [new AlarmRule { Id = "fastRule", SourceId = fast.Id }], Targets = [] };
        await using var monitor = new AlarmMonitor(config, events.Enqueue, source =>
        {
            if (source.Id == "slow")
            {
                Thread.Sleep(300);
                if (Interlocked.Increment(ref initialized) == 1) throw new IOException("模拟初次连接失败");
            }
            return new ScriptedSource(source, index => { counts.AddOrUpdate(source.Id, 1, (_, n) => n + 1); return Signal(index % 2 == 0); });
        });
        var watch = Stopwatch.StartNew();
        monitor.Start();
        Assert(watch.ElapsedMilliseconds < 150, "Start 不在调用线程连接设备");
        await Task.Delay(1450);
        var liveStatus = monitor.StatusText();
        var snapshots = monitor.ConnectionSnapshots();
        var metrics = monitor.Metrics();
        Assert(metrics.Reads >= 7 && metrics.FailedReads == 1 && metrics.ReadMicroseconds >= 300000, "趋势采集计数应包含真实成功、失败与连接耗时");
        var telemetry = new RuntimeTelemetry();
        telemetry.Sample(monitor, 2);
        var sample = telemetry.Read().Single();
        Assert(sample.Connected == 2 && sample.MemoryMb > 0 && sample.CpuPercent is >= 0 and <= 100 && sample.ReadsPerSecond >= 0,
            "趋势应提供实际连接数、进程内存和有界 CPU 数据");
        telemetry.Sample(monitor, 2);
        Assert(telemetry.Read().Count == 1, "多个界面刷新不能产生重复趋势采样");
        Assert(snapshots["fast"].Connected && snapshots["fast"].ConnectedSince.HasValue && snapshots["fast"].LastSuccess.HasValue, "网页诊断应提供真实连接时间和成功采集心跳");
        await monitor.StopAsync();
        Assert(counts.GetValueOrDefault("fast") >= 6, "慢设备和初始化失败不阻塞快速设备");
        Assert(initialized >= 2 && counts.GetValueOrDefault("slow") >= 1, "初始化失败应自动重连");
        Assert(counts.GetValueOrDefault("slow") < counts.GetValueOrDefault("fast"), "每台设备使用独立轮询间隔");
        Assert(events.Any(x => x.SourceId == "fast" && x.Level == "connected") && events.Any(x => x.SourceId == "fast" && x.Level == "heartbeat"), "首次成功采集应产生连接成功和心跳事件");
        Assert(events.Any(x => x.SourceId == "slow" && x.Level == "disconnected"), "连接失败应产生断线状态事件");
        Assert(liveStatus.Contains("当前连接：2/2 台") && liveStatus.Contains("已连接"), "机器人设备状态应包含实时连接数和连接时长");
    }

    private static async Task CheckRulesAsync()
    {
        var source = Source();
        var events = new ConcurrentQueue<RuntimeEvent>();
        Signal[] sequence = [Signal(true, "A"), Signal(true, "B"), Signal(false, "0"), Signal(true, "B", "idle"), Signal(true, "B"), Signal(false)];
        var rule = new AlarmRule { Id = "rule", SourceId = source.Id, MatchKey = "mode", MatchValue = "alarm", MessageTemplate = "alarm:{code}", RecoveryTemplate = "recovery:{code}" };
        await using (var monitor = new AlarmMonitor(new AppConfig { Sources = [source], Rules = [rule] }, events.Enqueue,
            s => new ScriptedSource(s, i => sequence[Math.Min(i, sequence.Length - 1)])))
        {
            monitor.Start();
            rule.MessageTemplate = "外部修改不应影响运行";
            await Task.Delay(1250);
            await monitor.StopAsync();
        }
        Assert(events.Count(x => x.Message == "alarm:A") == 1 && events.Count(x => x.Message == "alarm:B") == 2, "持续激活时换码和规则条件上升沿均触发");
        Assert(events.Count(x => x.Message == "recovery:A") == 1 && events.Count(x => x.Message == "recovery:B") == 2, "恢复日志使用此前激活的规则和报警码");
        events.Clear();
        var cooldownRule = new AlarmRule { Id = "cooldown", SourceId = source.Id, CooldownSeconds = 0.7, MessageTemplate = "cooldown-alarm" };
        await using var cooldown = new AlarmMonitor(new AppConfig { Sources = [source], Rules = [cooldownRule] }, events.Enqueue,
            s => new ScriptedSource(s, i => Signal(i is 0 or 2 or 4)));
        cooldown.Start();
        await Task.Delay(1100);
        await cooldown.StopAsync();
        Assert(events.Count(x => x.Message == "cooldown-alarm") == 2, "被冷却抑制的报警不能延长冷却窗口");

        events.Clear();
        var arrayRule = new AlarmRule
        {
            Id = "array", SourceId = source.Id, MessageTemplate = "array-alarm:{code}:{text}", RecoveryTemplate = "array-recovery:{code}",
            AlarmTexts = [new AlarmText { Code = "1001", Text = "电机过载" }, new AlarmText { Code = "1002", Text = "温度过高" }]
        };
        IReadOnlyList<Signal>[] batches =
        [
            [Signal(true, "1001"), Signal(true, "1002")],
            [Signal(true, "1001"), Signal(true, "1002")],
            [Signal(true, "1002")],
            []
        ];
        await using var arrayMonitor = new AlarmMonitor(new AppConfig { Sources = [source], Rules = [arrayRule], Targets = [] }, events.Enqueue,
            s => new ScriptedBatchSource(s, index => batches[Math.Min(index, batches.Length - 1)]));
        arrayMonitor.Start();
        await Task.Delay(750);
        await arrayMonitor.StopAsync();
        Assert(events.Count(x => x.Message.StartsWith("array-alarm:")) == 2 && events.Any(x => x.Message == "array-alarm:1001:电机过载") && events.Any(x => x.Message == "array-alarm:1002:温度过高"), "同一 OPC UA 数组中的多个报警码应分别匹配本地文本并触发");
        Assert(events.Count(x => x.Message.StartsWith("array-recovery:")) == 2, "报警码从 OPC UA 有效数组消失时应分别恢复");
    }

    private static async Task CheckRetriesAsync()
    {
        var source = Source();
        var target = new NotificationTarget { Id = "good", Enabled = true, Channel = "custom_webhook" };
        var failed = new NotificationTarget { Id = "retry", Enabled = true, Channel = "custom_webhook" };
        var counts = new ConcurrentDictionary<string, int>();
        var polls = 0;
        await using var monitor = new AlarmMonitor(new AppConfig
        {
            Sources = [source], Rules = [new AlarmRule { SourceId = source.Id }], Targets = [target, failed]
        }, _ => { }, s => new ScriptedSource(s, _ => { Interlocked.Increment(ref polls); return Signal(true); }),
        t => new RecordingNotifier(async token =>
        {
            var attempt = counts.AddOrUpdate(t.Id, 1, (_, n) => n + 1);
            if (t.Id == "retry" && attempt == 1) throw new IOException("模拟发送失败");
            await Task.Delay(700, token);
            return 1;
        }));
        monitor.Start();
        await Task.Delay(6000);
        await monitor.StopAsync();
        Assert(counts.GetValueOrDefault("good") == 1 && counts.GetValueOrDefault("retry") == 2, "仅重试失败的通知目标");
        Assert(polls >= 25, "慢通知及重试不能阻塞采集");
    }

    private static async Task CheckBotCommandsAsync()
    {
        Assert(BotInteraction.ValidTelegramToken("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcd"), "Telegram 完整令牌格式应通过校验");
        Assert(!BotInteraction.ValidTelegramToken("ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcd"), "缺少机器人 ID 和冒号的 Telegram 令牌必须被拒绝");
        var source = Source("plc");
        var commands = new[]
        {
            new BotCommandDefinition { Command = "/status", Button = "状态", Action = "status", Reply = "你好 {user}\n{status}" },
            new BotCommandDefinition { Command = "/history", Button = "历史报警", Action = "history" },
            new BotCommandDefinition { Command = "/reset", Button = "复位", Action = "plc_write", SourceId = source.Id, PulseMs = 200, Reply = "复位成功" }
        };
        var target = new NotificationTarget { Channel = "telegram", Token = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcd", Params = ConfigDefaults.MergeTargetParams("telegram", null) };
        target.Params["receive_enabled"] = JsonValues.Element("是");
        target.Params["allow_plc_write"] = JsonValues.Element("是");
        target.Params["admin_ids"] = JsonValues.Element("1001");
        target.Params["command_defs"] = JsonValues.Element(BotInteraction.Serialize(commands));
        Assert(!BotInteraction.ShowButtonsOnAlarm(target), "报警推送按钮默认应关闭并允许按目标配置");
        target.Params["alarm_show_buttons"] = JsonValues.Element("是");
        Assert(BotInteraction.ShowButtonsOnAlarm(target) && BotInteraction.Keyboard(target) is not null, "开启后报警推送应生成已配置按钮菜单");
        BotInteraction.Validate(target);
        Assert(BotInteraction.Commands(target).Count == 3, "机器人命令配置应可往返序列化");
        var writes = 0;
        var engine = new BotCommandEngine(target, [source], () => "2 台设备在线", (_, _, _) => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, () => "最近报警：1001 电机过载");
        Assert(await engine.ExecuteAsync("999", "访客", "chat", "/reset", "event-0", CancellationToken.None) is null && writes == 0, "未授权用户不能执行命令");
        var status = await engine.ExecuteAsync("1001", "值班员", "chat", "/status", "event-1", CancellationToken.None);
        Assert(status?.Text.Contains("2 台设备在线") == true && status.Buttons.Count == 1 && status.Buttons[0].Command == "/menu", "状态命令应渲染动态状态且只保留返回菜单");
        var historyReply = await engine.ExecuteAsync("1001", "值班员", "chat", "/history", "event-history", CancellationToken.None);
        Assert(historyReply?.Text.Contains("1001 电机过载") == true && historyReply.Buttons.Count == 1, "历史报警动作应返回记录且不重复完整菜单");
        var stagedHistory = new BotCommandEngine(target, [source], () => "在线", historyPage: (name, page) =>
            new BotHistoryPage($"{name} 第{page + 1}页，每页10条", page, 2, 20), connectedSources: () => new HashSet<string> { source.Id });
        var deviceChoice = await stagedHistory.ExecuteAsync("1001", "值班员", "chat", "/history", "event-history-choice", CancellationToken.None);
        Assert(deviceChoice?.Text.Contains("选择") == true && deviceChoice.Buttons.Any(x => x.Command.StartsWith("/history_device " + source.Id)), "历史报警应先显示当前连接设备供用户二次选择");
        var historyPage = await stagedHistory.ExecuteAsync("1001", "值班员", "chat", $"/history_device {source.Id} 0", "event-history-page", CancellationToken.None);
        Assert(historyPage?.Text.Contains("每页10条") == true && historyPage.Buttons.Any(x => x.Text == "下一页"), "设备历史报警应按每页10条、最多20条分页显示");
        var confirmation = await engine.ExecuteAsync("1001", "值班员", "chat", "/reset", "event-2", CancellationToken.None);
        Assert(confirmation?.Buttons.Count == 2 && writes == 0, "PLC 命令必须等待二次确认");
        var confirmCommand = confirmation!.Buttons.First(x => x.Text == "确认执行").Command;
        var executed = await engine.ExecuteAsync("1001", "值班员", "chat", confirmCommand, "event-3", CancellationToken.None);
        Assert(executed?.Text == "复位成功" && writes == 1, "有效确认执行一次 PLC 写入");
        var replay = await engine.ExecuteAsync("1001", "值班员", "chat", confirmCommand, "event-4", CancellationToken.None);
        Assert(replay?.Text.Contains("失效") == true && writes == 1, "一次性确认不能重复执行");
        Assert(await engine.ExecuteAsync("1001", "值班员", "chat", "/status", "event-1", CancellationToken.None) is null, "相同平台事件不能重复处理");
        var pageCommands = Enumerable.Range(0, 9).Select(i => new BotCommandDefinition { Command = "/c" + i, Button = "动作" + i }).ToArray();
        target.Params["command_defs"] = JsonValues.Element(BotInteraction.Serialize(pageCommands));
        var paged = new BotCommandEngine(target, [source], () => "ok");
        Assert(paged.Menu().Any(x => x.Text == "下一页") && paged.Menu(2).Any(x => x.Text == "上一页"), "超过六个按钮时应自动分页");
        var dingPayload = BotJson.Parse("{\"cardPrivateData\":{\"command\":\"/reset abc\"}}");
        Assert(EnterpriseBotReceiver.FindCommand(dingPayload) == "/reset abc", "钉钉卡片回调应读取命令参数");
        Assert(EnterpriseBotReceiver.DingCardData(confirmation).Count == 14, "钉钉卡片应生成六组按钮变量");
        Assert(ConfigDefaults.MergeTargetParams("dingtalk", null).ContainsKey("ding_conversation_id"), "钉钉目标应提供报警卡片群会话配置");
        DingCardRegistry.Register("alarm-card-test", new DingCardContext("group-1", "", "2", DateTime.UtcNow));
        Assert(DingCardRegistry.TryGet("alarm-card-test", out var cardContext) && cardContext.ChatId == "group-1", "报警卡片回调应能关联原始群会话");
    }

    private static async Task CheckDingApplicationAsync()
    {
        var target = new NotificationTarget { Channel = "dingtalk", Enabled = true, Params = new() };
        target.Params["bot_client_id"] = JsonValues.Element("offline-app-" + Guid.NewGuid());
        target.Params["bot_client_secret"] = JsonValues.Element("offline-secret");
        var calls = new List<string>();
        var failSend = false;
        var invalidUser = false;
        using var client = new HttpClient(new ScriptedHttpHandler(async (request, ct) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri.AbsolutePath.EndsWith("accessToken"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"accessToken\":\"offline-token\",\"expireIn\":7200}") };
            Assert(request.Headers.GetValues("x-acs-dingtalk-access-token").Single() == "offline-token", "企业机器人发送应携带应用访问令牌");
            if (request.RequestUri.AbsolutePath.EndsWith("createAndDeliver"))
            {
                Assert(body.RootElement.GetProperty("openSpaceId").GetString()!.StartsWith("dtv1.card//IM_ROBOT.user-") && body.RootElement.TryGetProperty("imRobotOpenDeliverModel", out _) && !body.RootElement.TryGetProperty("imGroupOpenDeliverModel", out _), "个人报警卡片应使用个人投递空间");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("batchSend"))
                Assert(body.RootElement.GetProperty("userIds").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["user-a", "user-b"]) && !body.RootElement.TryGetProperty("openConversationId", out _), "个人通知应使用企业 userId 且不要求群 ID");
            else Assert(body.RootElement.GetProperty("openConversationId").GetString() == "offline-group" && body.RootElement.GetProperty("msgKey").GetString() == "sampleText", "企业机器人应向指定群发送文本模板");
            using var content = JsonDocument.Parse(body.RootElement.GetProperty("msgParam").GetString()!);
            Assert(content.RootElement.GetProperty("content").GetString()!.StartsWith("离线测试"), "企业机器人中文内容应保留在序列化模板参数中");
            return new HttpResponseMessage(failSend ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(invalidUser ? "{\"processQueryKey\":\"offline-message\",\"invalidStaffIdList\":[\"user-a\"]}" : "{\"processQueryKey\":\"offline-message\"}") };
        }));
        var notifier = new DingTalkNotifier(target, client);
        await ThrowsAsync<InvalidOperationException>(() => notifier.SendAsync("离线测试", default), "缺少目标群 ID 时应提示配置而非调用网络");
        Assert(calls.Count == 0, "缺少群 ID 时不能发送消息或请求令牌");
        target.Params["ding_conversation_id"] = JsonValues.Element("offline-group");
        Assert(await notifier.SendAsync("离线测试", default) == 1, "企业机器人无需 Webhook 即可发送文本");
        target.Params["alarm_show_buttons"] = JsonValues.Element("是");
        Assert(await notifier.SendAsync("离线测试", default) == 1, "无卡片模板时企业机器人应使用应用文本通道");
        Assert(calls.Count(x => x.EndsWith("accessToken")) == 1, "同一应用凭据应缓存令牌");
        target.Params["bot_client_secret"] = JsonValues.Element("changed-secret");
        await notifier.SendAsync("离线测试", default);
        Assert(calls.Count(x => x.EndsWith("accessToken")) == 2, "修改密钥后应重新获取令牌");
        failSend = true;
        await ThrowsAsync<IOException>(() => notifier.SendAsync("离线测试", default), "企业机器人发送失败不能报告成功");
        failSend = false;
        target.Params["ding_message_target"] = JsonValues.Element("users");
        target.Params["ding_conversation_id"] = JsonValues.Element("");
        target.Recipients = ["user-a", "user-b", "user-a"];
        Assert(await notifier.SendAsync("离线测试", default) == 2, "个人通知应按去重后的 userId 数量返回结果");
        invalidUser = true;
        await ThrowsAsync<IOException>(() => notifier.SendAsync("离线测试", default), "HTTP 200 返回无效用户时不能报告全部成功");
        target.Params["card_template_id"] = JsonValues.Element("offline-template");
        Assert(await notifier.SendAsync("离线测试", default) == 2 && calls.Count(x => x.EndsWith("createAndDeliver")) == 2, "个人报警卡片应分别投递给去重后的接收人员");
        var config = new AppConfig().Normalize();
        config.Targets = [target];
        target.Params["alarm_show_buttons"] = JsonValues.Element("否");
        ConfigValidator.Validate(config);
        _passed++;
    }

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        var caught = false;
        try { await action(); } catch (T) { caught = true; }
        Assert(caught, message);
    }
    private sealed class ScriptedSource(SourceConfig config, Func<int, Signal> read) : SourceAdapter(config)
    {
        private int _index;
        public override Task<Signal> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(read(_index++));
    }
    private sealed class ScriptedBatchSource(SourceConfig config, Func<int, IReadOnlyList<Signal>> read) : SourceAdapter(config)
    {
        private int _index;
        public override Task<Signal> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(read(_index++).FirstOrDefault() ?? new Signal(false, "0", "", new Dictionary<string, object?>()));
        public override Task<IReadOnlyList<Signal>> ReadSignalsAsync(CancellationToken cancellationToken) => Task.FromResult(read(_index++));
    }
    private sealed class RecordingNotifier(Func<CancellationToken, Task<int>> send) : INotifier
    {
        public Task<int> SendAsync(string message, CancellationToken cancellationToken) => send(cancellationToken);
    }
    private sealed class FragmentStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}

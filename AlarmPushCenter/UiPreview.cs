using System.Drawing.Imaging;
using System.Reflection;

namespace AlarmPushCenter;

public static class UiPreview
{
    public static void Render(string folder, bool liveTelemetry = false)
    {
        Directory.CreateDirectory(folder);
        ApplicationConfiguration.Initialize();
        var configPath = Path.Combine(folder, "preview-config.json");
        var source = ConfigDefaults.NewSource(); source.Name = "一号车间 · 装配产线"; source.Protocol = "opcua"; source.Params = ConfigDefaults.MergeSourceParams("opcua", null);
        var second = ConfigDefaults.NewSource(); second.Name = "二号车间 · 包装产线";
        source.PhoneReminder = new() { Enabled = true, DelaySeconds = 120, Number = "13800138000" };
        if (liveTelemetry)
        {
            source.Protocol = "mock"; source.Enabled = true; source.PollSeconds = .2;
            source.Params = ConfigDefaults.MergeSourceParams("mock", null);
            source.Params["toggle_every"] = JsonValues.Element("8");
            second.Enabled = false;
        }
        var target = ConfigDefaults.NewTarget(); target.Name = "生产值班机器人";
        target.Params["welcome_text"] = JsonValues.Element("你好，值班员。今天也一起守护产线平稳运行。\n请选择需要的操作：");
        target.Params["command_defs"] = JsonValues.Element(BotInteraction.Serialize([
            new BotCommandDefinition { Command = "/status", Button = "查看设备状态", Action = "status", Reply = "设备运行状态\n{status}" },
            new BotCommandDefinition { Command = "/reset", Button = "报警复位", Action = "plc_write", SourceId = source.Id, Reply = "复位指令已完成。", PulseMs = 500 },
            new BotCommandDefinition { Command = "/contact", Button = "联系值班员", Reply = "如需协助，请联系当班维护人员。" },
            new BotCommandDefinition { Command = "/menu", Button = "返回主菜单", Action = "menu", Reply = "请选择操作。" }
        ]));
        new ConfigStore(configPath).Save(new AppConfig { Sources = [source, second], Rules = [ConfigDefaults.NewRule(source.Id), ConfigDefaults.NewRule(second.Id)], Targets = liveTelemetry ? [] : [target] });
        using var form = new MainForm(configPath) { Size = new Size(1440, 960), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Opacity = 0 };
        form.Show(); Application.DoEvents();
        form.PerformLayout();
        if (liveTelemetry)
        {
            var toggle = typeof(MainForm).GetMethod("ToggleMonitorAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var start = (Task)toggle.Invoke(form, null)!;
            while (!start.IsCompleted) { Application.DoEvents(); Thread.Sleep(20); }
            start.GetAwaiter().GetResult();
            var until = DateTime.UtcNow.AddSeconds(31);
            while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(30); }
        }
        var show = typeof(MainForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (page, name, messageTab) in new[] { (0, "overview", -1), (1, "devices", -1), (2, "rules", -1), (3, "message-delivery", 0), (3, "message-bot", 1), (4, "runtime", -1) })
        {
            show.Invoke(form, [page]); form.PerformLayout(); Application.DoEvents();
            if (messageTab >= 0)
            {
                var tabs = (TabControl)typeof(MainForm).GetField("_messageTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                tabs.SelectedIndex = messageTab;
                form.PerformLayout(); Application.DoEvents();
            }
            form.Refresh();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
            if (page == 1)
            {
                var deviceTabs = (TabControl)typeof(MainForm).GetField("_sourceTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                deviceTabs.SelectedIndex = 1; form.PerformLayout(); Application.DoEvents(); form.Refresh();
                using var phoneBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(phoneBitmap, new Rectangle(Point.Empty, form.Size));
                phoneBitmap.Save(Path.Combine(folder, "desktop-phone.png"), ImageFormat.Png);
                form.BeginInvoke(() =>
                {
                    var gateway = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Owner == form);
                    if (gateway is null) return;
                    gateway.PerformLayout(); gateway.Refresh();
                    using var gatewayBitmap = new Bitmap(gateway.Width, gateway.Height);
                    gateway.DrawToBitmap(gatewayBitmap, new Rectangle(Point.Empty, gateway.Size));
                    gatewayBitmap.Save(Path.Combine(folder, "desktop-phone-gateway.png"), ImageFormat.Png);
                    gateway.Close();
                });
                typeof(MainForm).GetMethod("ShowPhoneGatewaySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                deviceTabs.SelectedIndex = 0;
            }
            if (liveTelemetry && page == 0)
            {
                var trend = (TelemetryTrendControl)typeof(MainForm).GetField("_alarmTrend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                using var chart = new Bitmap(trend.Parent!.Width, trend.Parent.Height);
                trend.Parent.DrawToBitmap(chart, new Rectangle(Point.Empty, chart.Size));
                chart.Save(Path.Combine(folder, "desktop-trends.png"), ImageFormat.Png);
            }
        }
        form.Size = form.MinimumSize; show.Invoke(form, [0]); form.PerformLayout(); Application.DoEvents(); form.Refresh();
        using (var compact = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(compact, new Rectangle(Point.Empty, form.Size));
            compact.Save(Path.Combine(folder, "overview-1120.png"), ImageFormat.Png);
        }
        if (liveTelemetry)
        {
            var stop = (Task)typeof(MainForm).GetMethod("ToggleMonitorAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
            while (!stop.IsCompleted) { Application.DoEvents(); Thread.Sleep(20); }
            stop.GetAwaiter().GetResult();
        }
        File.Delete(configPath);
        if (File.Exists(configPath + ".bak")) File.Delete(configPath + ".bak");
    }
}

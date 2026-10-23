using System.Text;
using Opc.UaFx;
using Opc.UaFx.Client;

namespace AlarmPushCenter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (args.Length == 2 && args[0] == "--render-preview") { UiPreview.Render(Path.GetFullPath(args[1])); return; }
        if (args.Length == 2 && args[0] == "--render-trend-preview") { UiPreview.Render(Path.GetFullPath(args[1]), true); return; }
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            OfflineChecks.RunUiChecks();
            SynchronizationContext.SetSynchronizationContext(null);
            OfflineChecks.RunAsync().GetAwaiter().GetResult();
            return;
        }
        var configArgument = Array.FindIndex(args, x => x == "--config");
        if (configArgument >= 0 && configArgument + 1 >= args.Length)
            throw new ArgumentException("--config 后需要配置文件路径。");
        var workingConfig = Path.Combine(Environment.CurrentDirectory, "config.json");
        var applicationConfig = Path.Combine(AppContext.BaseDirectory, "config.json");
        var configPath = configArgument >= 0 ? Path.GetFullPath(args[configArgument + 1])
            : File.Exists(applicationConfig) ? applicationConfig : File.Exists(workingConfig) ? workingConfig : applicationConfig;
        var probeArgument = Array.FindIndex(args, x => x == "--probe-source");
        if (probeArgument >= 0)
        {
            var sourceId = probeArgument + 1 < args.Length && !args[probeArgument + 1].StartsWith("--") ? args[probeArgument + 1] : "";
            ProbeSourceAsync(configPath, sourceId).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--browse-opc", StringComparer.OrdinalIgnoreCase))
        {
            BrowseOpc(configPath);
            return;
        }
        if (args.Contains("--inspect-opc", StringComparer.OrdinalIgnoreCase))
        {
            InspectOpc(configPath);
            return;
        }
        ApplicationConfiguration.Initialize();
        try { Application.Run(new MainForm(configPath)); }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}\n配置路径：{configPath}\n原配置未被覆盖。", "工业报警推送中心", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    private static async Task ProbeSourceAsync(string configPath, string sourceId)
    {
        var config = new ConfigStore(configPath).Load();
        var source = sourceId.Length == 0
            ? config.Sources.FirstOrDefault(x => x.Protocol == "opcua") ?? config.Sources.First()
            : config.Sources.FirstOrDefault(x => x.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"未找到采集源：{sourceId}");
        await using var adapter = SourceFactory.Create(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var signals = await adapter.ReadSignalsAsync(timeout.Token);
        Console.WriteLine($"采集连接成功：{source.Name} / {source.Protocol}，当前有效报警 {signals.Count} 条。");
    }

    private static void BrowseOpc(string configPath)
    {
        var source = new ConfigStore(configPath).Load().Sources.First(x => x.Protocol == "opcua");
        using var client = new OpcClient(source.Params.Get("endpoint"));
        if (!string.IsNullOrWhiteSpace(source.Params.Get("username")))
            client.Security.UserIdentity = new OpcClientIdentity(source.Params.Get("username"), source.Params.Get("password"));
        client.Connect();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        BrowseNode(client.BrowseNode(OpcNodeId.Parse("i=85")), "Objects", 0, visited, ref count);
        Console.WriteLine($"浏览完成，共检查 {count} 个节点。以上仅显示名称或路径中包含 Alarm/报警/Code/Count 的节点。");
    }

    private static void InspectOpc(string configPath)
    {
        var source = new ConfigStore(configPath).Load().Sources.First(x => x.Protocol == "opcua");
        using var client = new OpcClient(source.Params.Get("endpoint"));
        client.Connect();
        var value = client.ReadNode(OpcNodeId.Parse(source.Params.Get("alarm_code_list_node_id")));
        DumpValue(value.Value, 0);
    }

    private static void DumpValue(object? value, int depth)
    {
        if (value is null || depth > 3) return;
        Console.WriteLine($"{new string(' ', depth * 2)}类型={value.GetType().FullName} 值={value}");
        if (value is System.Collections.IEnumerable items and not string)
        {
            var index = 0;
            foreach (var item in items) { Console.WriteLine($"元素[{index++}]"); DumpValue(item, depth + 1); if (index >= 2) break; }
        }
        foreach (var property in value.GetType().GetProperties().Where(x => x.GetIndexParameters().Length == 0).Take(12))
        {
            try { Console.WriteLine($"{new string(' ', depth * 2 + 2)}属性 {property.Name}={property.GetValue(value)}"); } catch { }
        }
    }

    private static void BrowseNode(OpcNodeInfo node, string path, int depth, HashSet<string> visited, ref int count)
    {
        if (depth > 8 || count >= 10000 || !visited.Add(node.NodeId.ToString())) return;
        count++;
        var current = path + "/" + node.DisplayName;
        if (current.Contains("alarm", StringComparison.OrdinalIgnoreCase) || current.Contains("报警", StringComparison.OrdinalIgnoreCase)
            || current.Contains("code", StringComparison.OrdinalIgnoreCase) || current.Contains("count", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"{node.NodeId}\t{current}");
        foreach (var child in node.Children()) BrowseNode(child, current, depth + 1, visited, ref count);
    }
}

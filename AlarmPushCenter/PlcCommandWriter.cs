using System.Collections.Concurrent;

namespace AlarmPushCenter;

public static class PlcCommandWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly ConcurrentDictionary<string, ISourceAdapter> Adapters = new();
    internal static SemaphoreSlim Gate(string id) => Gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    internal static void Register(string id, ISourceAdapter adapter) => Adapters[id] = adapter;
    internal static void Unregister(string id) => Adapters.TryRemove(id, out _);
    public static async Task WriteAsync(SourceConfig source, BotCommandDefinition command, CancellationToken token)
    {
        var gate = Gate(source.Id);
        await gate.WaitAsync(token);
        try
        {
            if (source.Protocol == "mock") { await Task.Delay(50, token); return; }
            if (source.Protocol is "opcua" or "opcua_prodiag")
            {
                if (Adapters.TryGetValue(source.Id, out var opcLive) && opcLive is OpcUaSource connectedOpc)
                { await connectedOpc.WriteCommandAsync(command, token); return; }
                await using var opc = new OpcUaSource(source);
                await opc.WriteCommandAsync(command, token);
                return;
            }
            if (source.Protocol is "modbus_tcp" or "modbus_rtu")
            {
                if (Adapters.TryGetValue(source.Id, out var live) && live is ModbusSource connected)
                { await connected.WriteCommandAsync(command, token); return; }
                await using var adapter = new ModbusSource(source);
                await adapter.WriteCommandAsync(command, token);
                return;
            }
            if (source.Protocol != "s7" || command.WriteType != "bit") throw new InvalidOperationException("当前控制支持 OPC UA 节点、S7 位、Modbus 线圈/寄存器写入以及 Mock 仿真。");
            if (Adapters.TryGetValue(source.Id, out var s7Live) && s7Live is S7Source connectedS7)
            { await connectedS7.WriteCommandAsync(command, token); return; }
            await using var s7 = new S7Source(source);
            await s7.WriteCommandAsync(command, token);
        }
        finally { gate.Release(); }
    }
}

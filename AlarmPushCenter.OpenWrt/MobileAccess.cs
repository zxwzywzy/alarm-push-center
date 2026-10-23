using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlarmPushCenter;

// Pairing codes are short-lived and one-use; persistent records contain only token hashes.
public sealed class MobileAccess
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _codes = [];
    private readonly string _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("ALARM_CONFIG") ?? "/etc/alarm-push-center/config.json"))!, "mobile-devices.json");
    private List<MobileDevice> _devices = [];
    public MobileAccess()
    {
        try
        {
            if (File.Exists(_path))
                _devices = (JsonSerializer.Deserialize<List<MobileDevice>>(File.ReadAllText(_path)) ?? [])
                    .Where(x => x.TokenHash.Length == 64 && x.TokenHash.All(Uri.IsHexDigit)).Take(20).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        { Console.Error.WriteLine("手机授权记录无法读取，已禁用旧授权，请重新配对。"); }
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public object CreateCode()
    {
        lock (_gate)
        {
            foreach (var expired in _codes.Where(x => x.Value <= DateTime.UtcNow).Select(x => x.Key).ToArray()) _codes.Remove(expired);
            if (_codes.Count >= 10) throw new InvalidOperationException("连接码过多，请稍后重试。");
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var expires = DateTime.UtcNow.AddMinutes(2);
            _codes.Add(Hash(code), expires);
            return new { code, expires_at = expires };
        }
    }
    public object Claim(string code, string name)
    {
        lock (_gate)
        {
            if (code.Length != 48 || !_codes.TryGetValue(Hash(code), out var expires) || expires <= DateTime.UtcNow)
                throw new InvalidOperationException("连接二维码无效或已过期，请在管理页面重新生成。");
            _codes.Remove(Hash(code));
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var label = new string((name ?? "手机").Where(c => !char.IsControl(c)).Take(60).ToArray());
            var device = new MobileDevice(Guid.NewGuid().ToString("N"), label, Hash(token), DateTime.UtcNow, DateTime.UtcNow.AddDays(30));
            var previous = _devices.ToList();
            _devices.RemoveAll(x => x.Expires <= DateTime.UtcNow);
            if (_devices.Count >= 20) throw new InvalidOperationException("最多允许 20 台手机，请先撤销旧设备。");
            _devices.Add(device);
            try { Save(); } catch { _devices = previous; throw; }
            return new { token, device_id = device.Id, expires_at = device.Expires };
        }
    }
    public bool IsAuthorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length != 71) return false;
        var hash = Hash(header[7..]);
        lock (_gate) return _devices.Any(x => x.Expires > DateTime.UtcNow && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(x.TokenHash), Convert.FromHexString(hash)));
    }
    public object Devices() { lock (_gate) return _devices.Select(x => new { x.Id, x.Name, paired_at = x.Created, expires_at = x.Expires }).ToArray(); }
    public void Revoke(string id)
    {
        lock (_gate)
        {
            var previous = _devices.ToList();
            _devices.RemoveAll(x => x.Id == id);
            try { Save(); } catch { _devices = previous; throw; }
        }
    }
    public void RevokeAll()
    {
        lock (_gate) { _codes.Clear(); _devices.Clear(); Save(); }
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_devices));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private sealed record MobileDevice(string Id, string Name, string TokenHash, DateTime Created, DateTime Expires);
}

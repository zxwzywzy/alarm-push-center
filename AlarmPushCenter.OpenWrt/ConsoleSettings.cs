using System.Net.Mail;
using System.Text.Json;

namespace AlarmPushCenter;

public sealed record ConsoleOptions
{
    public bool StartCollectionOnLaunch { get; init; }
    public string RecoveryEmail { get; init; } = "";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public string SmtpUser { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    public string SmtpFrom { get; init; } = "";
    public bool RecoveryEnabled { get; init; }
}

public sealed class ConsoleSettings
{
    private readonly object _gate = new();
    private readonly string _path;
    private ConsoleOptions _value;
    public ConsoleSettings()
    {
        var config = Environment.GetEnvironmentVariable("ALARM_CONFIG") ?? "/etc/alarm-push-center/config.json";
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!, "console-settings.json");
        _value = File.Exists(_path) ? JsonSerializer.Deserialize<ConsoleOptions>(File.ReadAllText(_path)) ?? new() : new();
    }
    public ConsoleOptions Value { get { lock (_gate) return _value; } }
    public object PublicView() => Value with { SmtpPassword = "" };
    public void Save(ConsoleOptions input)
    {
        lock (_gate)
        {
            input = input with { SmtpPassword = string.IsNullOrEmpty(input.SmtpPassword) ? _value.SmtpPassword : input.SmtpPassword };
            if (input.RecoveryEnabled)
            {
                if (!MailAddress.TryCreate(input.RecoveryEmail, out _) || !MailAddress.TryCreate(input.SmtpFrom, out _) ||
                    string.IsNullOrWhiteSpace(input.SmtpHost) || input.SmtpPort is < 1 or > 65535 || input.SmtpPort == 465 ||
                    string.IsNullOrWhiteSpace(input.SmtpUser) || string.IsNullOrEmpty(input.SmtpPassword))
                    throw new InvalidOperationException("请填写有效邮箱、发件人和 SMTP 参数；使用支持 STARTTLS 的端口（通常 587），不支持 465 隐式 TLS。");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(input));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, _path, true);
                _value = input;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

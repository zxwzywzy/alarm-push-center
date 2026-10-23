using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace AlarmPushCenter;

public interface IRecoveryEmailSender
{
    Task SendAsync(ConsoleOptions settings, string code, CancellationToken token);
}

public sealed class SmtpRecoveryEmailSender : IRecoveryEmailSender
{
    public async Task SendAsync(ConsoleOptions value, string code, CancellationToken token)
    {
        using var client = new SmtpClient(value.SmtpHost, value.SmtpPort)
        {
            EnableSsl = true, Credentials = new NetworkCredential(value.SmtpUser, value.SmtpPassword)
        };
        using var message = new MailMessage(value.SmtpFrom, value.RecoveryEmail,
            "报警推送中心 · 管理密码找回", $"您的验证码：{code}\n10 分钟内有效，只可使用一次。\n如果不是您发起的请求，请忽略此邮件。");
        await client.SendMailAsync(message, token);
    }
}

public sealed class EmailRecovery(ConsoleSettings settings, WebSecurity security, IRecoveryEmailSender sender)
{
    private readonly object _gate = new();
    private byte[]? _codeHash;
    private DateTime _expires;
    private DateTime _lastRequest;
    private int _attempts;
    private string _email = "";
    private string _requestId = "";
    public object Status()
    {
        var value = settings.Value;
        var email = value.RecoveryEmail;
        var at = email.IndexOf('@');
        return new { enabled = value.RecoveryEnabled, email = at > 0 ? email[..1] + "***" + email[at..] : "" };
    }
    public void Invalidate()
    {
        lock (_gate) { _codeHash = null; _requestId = ""; }
    }
    public async Task<object> RequestAsync(CancellationToken token)
    {
        var value = settings.Value;
        if (!value.RecoveryEnabled) throw new InvalidOperationException("尚未启用邮箱找回，请通过 OpenWrt 后台查看管理密码文件。");
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        string requestId;
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastRequest < TimeSpan.FromMinutes(1))
                throw new InvalidOperationException("验证码已发送，请等待 60 秒后再试。");
            _lastRequest = DateTime.UtcNow;
            _codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
            _expires = DateTime.UtcNow.AddMinutes(10);
            _attempts = 0;
            _email = value.RecoveryEmail;
            requestId = _requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await sender.SendAsync(value, code, timeout.Token);
            return new { request_id = requestId, message = "验证码已发送，请检查绑定邮箱（含垃圾邮件）。" };
        }
        catch
        {
            lock (_gate) { if (_requestId == requestId) _codeHash = null; }
            throw new InvalidOperationException("验证码邮件发送失败，请检查网络、SMTP 参数和邮箱授权码。");
        }
    }
    public void Reset(HttpContext context, string requestId, string code, string password)
    {
        lock (_gate)
        {
            if (!settings.Value.RecoveryEnabled || settings.Value.RecoveryEmail != _email ||
                _codeHash is null || _expires <= DateTime.UtcNow || _requestId != requestId)
                throw new InvalidOperationException("验证码已失效，请重新获取。");
            if (++_attempts > 5) { _codeHash = null; throw new InvalidOperationException("尝试次数过多，请重新获取验证码。"); }
            if (!CryptographicOperations.FixedTimeEquals(_codeHash, SHA256.HashData(Encoding.UTF8.GetBytes(code ?? ""))))
                throw new InvalidOperationException("验证码不正确。");
            security.ResetPassword(context, password);
            _codeHash = null;
        }
    }
}

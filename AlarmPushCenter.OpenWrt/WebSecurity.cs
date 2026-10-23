using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AlarmPushCenter;

public sealed class WebSecurity
{
    private string _password;
    private readonly string _passwordPath;
    private readonly object _passwordGate = new();
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private const string CookieName = "alarm_session";

    public WebSecurity()
    {
        var path = _passwordPath = Environment.GetEnvironmentVariable("ALARM_PASSWORD_FILE") ?? "/etc/alarm-push-center/web-password";
        if (File.Exists(path)) _password = File.ReadAllText(path).Trim();
        else
        {
            _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, _password + Environment.NewLine);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console.WriteLine($"首次启动管理密码：{_password}（已保存到 {path}）");
        }
    }

    public bool Login(HttpContext context, string password)
    {
        lock (_passwordGate)
        {
        if (!FixedEquals(password, _password)) return false;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _sessions[token] = DateTime.UtcNow.AddHours(12);
        context.Response.Cookies.Append(CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, MaxAge = TimeSpan.FromHours(12), Path = "/" });
        return true;
        }
    }

    public void ChangePassword(HttpContext context, string currentPassword, string newPassword)
    {
        lock (_passwordGate)
        {
            if (!IsAuthorized(context)) throw new InvalidOperationException("请先登录管理页面。");
            if (!FixedEquals(currentPassword, _password)) throw new InvalidOperationException("当前密码不正确。");
            ResetPassword(context, newPassword);
        }
    }

    public void VerifyPassword(string password)
    {
        lock (_passwordGate)
            if (!FixedEquals(password, _password)) throw new InvalidOperationException("当前管理密码不正确。");
    }

    public void ResetPassword(HttpContext context, string newPassword)
    {
        lock (_passwordGate)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length is < 8 or > 128 ||
                newPassword.Any(char.IsControl) || newPassword != newPassword.Trim())
                throw new InvalidOperationException("新密码需要 8～128 个字符，不能包含控制字符或首尾空格。");
            if (FixedEquals(newPassword, _password)) throw new InvalidOperationException("新密码不能与当前密码相同。");
            var temporaryPath = _passwordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, newPassword + Environment.NewLine);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporaryPath, _passwordPath, true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            _password = newPassword;
            _sessions.Clear();
            context.RequestServices?.GetService<MobileAccess>()?.RevokeAll();
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
        }
    }

    public bool IsAuthorized(HttpContext context)
        => IsWebAuthorized(context) || (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
            && context.RequestServices?.GetService<MobileAccess>()?.IsAuthorized(context) == true);

    public bool IsWebAuthorized(HttpContext context)
    {
        foreach (var expired in _sessions.Where(x => x.Value <= DateTime.UtcNow).ToArray()) _sessions.TryRemove(expired.Key, out _);
        return context.Request.Cookies.TryGetValue(CookieName, out var token) && _sessions.TryGetValue(token, out var expires) && expires > DateTime.UtcNow;
    }

    public void Logout(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token)) _sessions.TryRemove(token, out _);
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left ?? ""));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right ?? ""));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

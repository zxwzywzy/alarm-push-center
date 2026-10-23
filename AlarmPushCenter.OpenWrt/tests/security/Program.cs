using AlarmPushCenter;
using Microsoft.AspNetCore.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

var directory = Path.Combine(Path.GetTempPath(), "alarm-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
Environment.SetEnvironmentVariable("ALARM_CONFIG", Path.Combine(directory, "config.json"));
Environment.SetEnvironmentVariable("ALARM_PASSWORD_FILE", Path.Combine(directory, "password"));
File.WriteAllText(Path.Combine(directory, "password"), "old-test-password");
var passed = 0;
try
{
    var settings = new ConsoleSettings();
    var security = new WebSecurity();
    var sender = new TestSender();
    var recovery = new EmailRecovery(settings, security, sender);
    settings.Save(new ConsoleOptions { RecoveryEnabled = true, RecoveryEmail = "test@example.com", SmtpHost = "smtp.example.com",
        SmtpUser = "test", SmtpPassword = "secret", SmtpFrom = "test@example.com" });
    void Field(string name, object value) => typeof(EmailRecovery).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(recovery, value);
    void Issue(DateTime expires)
    {
        Field("_codeHash", SHA256.HashData(Encoding.UTF8.GetBytes("123456")));
        Field("_expires", expires); Field("_requestId", "test-request"); Field("_email", "test@example.com"); Field("_attempts", 0);
    }
    void Assert(bool value) { if (!value) throw new Exception("邮箱找回验证失败"); passed++; }
    void Reject(Action action) { try { action(); throw new Exception("应该拒绝本次重置"); } catch (InvalidOperationException) { passed++; } }
    var context = new DefaultHttpContext();
    Issue(DateTime.UtcNow.AddMinutes(-1));
    Reject(() => recovery.Reset(context, "test-request", "123456", "new-test-password"));
    Issue(DateTime.UtcNow.AddMinutes(10));
    Reject(() => recovery.Reset(context, "wrong-request", "123456", "new-test-password"));
    for (var i = 0; i < 5; i++) Reject(() => recovery.Reset(context, "test-request", "000000", "new-test-password"));
    Reject(() => recovery.Reset(context, "test-request", "123456", "new-test-password"));
    Issue(DateTime.UtcNow.AddMinutes(10));
    Reject(() => recovery.Reset(context, "test-request", "123456", "short"));
    recovery.Reset(context, "test-request", "123456", "new-test-password");
    Assert(File.ReadAllText(Path.Combine(directory, "password")).Trim() == "new-test-password");
    Reject(() => recovery.Reset(context, "test-request", "123456", "another-password"));
    Assert(!security.Login(new DefaultHttpContext(), "old-test-password"));
    Assert(security.Login(new DefaultHttpContext(), "new-test-password"));
    Issue(DateTime.UtcNow.AddMinutes(10)); recovery.Invalidate();
    Reject(() => recovery.Reset(context, "test-request", "123456", "another-password"));
    Assert(settings.Value.StartCollectionOnLaunch == false);
    Assert(((ConsoleOptions)settings.PublicView()).SmtpPassword == "");
    var requested = System.Text.Json.JsonSerializer.SerializeToElement(await recovery.RequestAsync(CancellationToken.None));
    var issuedRequest = requested.GetProperty("request_id").GetString()!;
    Assert(sender.Email == "test@example.com" && sender.Code.Length == 6);
    try { await recovery.RequestAsync(CancellationToken.None); throw new Exception("重复发送应被限制"); }
    catch (InvalidOperationException) { passed++; }
    recovery.Reset(context, issuedRequest, sender.Code, "emailed-test-password");
    Assert(security.Login(new DefaultHttpContext(), "emailed-test-password"));
    Reject(() => recovery.Reset(context, issuedRequest, sender.Code, "repeat-password"));
    Field("_lastRequest", DateTime.MinValue);
    sender.Fail = true;
    try { await recovery.RequestAsync(CancellationToken.None); throw new Exception("发送失败应报错"); }
    catch (InvalidOperationException) { passed++; }
    Assert(typeof(EmailRecovery).GetField("_codeHash", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recovery) is null);
    Console.WriteLine($"recovery checks passed: {passed}");
}
finally { Directory.Delete(directory, true); }

sealed class TestSender : IRecoveryEmailSender
{
    public string Code = "";
    public string Email = "";
    public bool Fail;
    public Task SendAsync(ConsoleOptions settings, string code, CancellationToken token)
    {
        Code = code; Email = settings.RecoveryEmail;
        return Fail ? Task.FromException(new IOException("simulated mail failure")) : Task.CompletedTask;
    }
}

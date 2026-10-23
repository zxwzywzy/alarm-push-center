using System.Text;
using AlarmPushCenter;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ALARM_LISTEN") ?? "http://0.0.0.0:5080");
builder.Services.AddSingleton<RuntimeHost>();
builder.Services.AddSingleton<AlarmCallTracker>();
builder.Services.AddSingleton<UfiGatewaySettings>();
builder.Services.AddSingleton(sp => new UfiPhoneService(sp.GetRequiredService<UfiGatewaySettings>(), sp.GetRequiredService<AlarmCallTracker>(), sp.GetRequiredService<RuntimeHost>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<UfiPhoneService>());
builder.Services.AddSingleton<WebSecurity>();
builder.Services.AddSingleton<MobileAccess>();
builder.Services.AddSingleton<ConsoleSettings>();
builder.Services.AddSingleton<EmailRecovery>();
builder.Services.AddSingleton<IRecoveryEmailSender, SmtpRecoveryEmailSender>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RuntimeHost>());
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();
app.MapGet("/disclaimer", () => Results.Text(ProductDisclaimer.Text, "text/plain; charset=utf-8"));
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    // Only the same router may embed the console (LuCI runs on a different port).
    var host = context.Request.Host.Host;
    var httpOrigin = new UriBuilder("http", host).Uri.GetLeftPart(UriPartial.Authority);
    var httpsOrigin = new UriBuilder("https", host).Uri.GetLeftPart(UriPartial.Authority);
    context.Response.Headers.ContentSecurityPolicy = $"frame-ancestors 'self' {httpOrigin} {httpsOrigin}";
    if (context.Request.Path == "/" || context.Request.Path == "/index.html" || context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context =>
{
    context.Context.Response.Headers.CacheControl = context.File.Name == "index.html" ? "no-store" : "public,max-age=3600";
}});

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/api/auth") || context.Request.Path == "/api/health")
    { await next(); return; }
    var security = context.RequestServices.GetRequiredService<WebSecurity>();
    if (!security.IsAuthorized(context)) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "请先登录管理页面。" }); return; }
    if (!security.IsWebAuthorized(context))
    {
        var path = context.Request.Path;
        var allowed = (HttpMethods.IsGet(context.Request.Method) && (path == "/api/overview" || path == "/api/history" || path.StartsWithSegments("/api/mobile/sources")))
            || (HttpMethods.IsPut(context.Request.Method) && path.StartsWithSegments("/api/mobile/sources"))
            || (HttpMethods.IsPost(context.Request.Method) && (path == "/api/runtime/start" || path == "/api/runtime/stop"))
            || path.StartsWithSegments("/api/mobile/pairing") || path.StartsWithSegments("/api/mobile/devices");
        if (!allowed) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "手机授权仅允许报警查看、采集设备配置和采集启停。" }); return; }
    }
    await next();
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", product = "工业报警推送中心 OpenWrt", version = AppUpdateClient.CurrentVersion }));
app.MapGet("/api/updates", (UpdateService updates) => Results.Ok(updates.PublicView()));
app.MapPost("/api/updates/check", async (UpdateService updates, CancellationToken token) => Results.Ok(await updates.CheckAsync(token)));
app.MapPut("/api/updates/settings", (UpdateSettingsRequest body, UpdateService updates, WebSecurity security) =>
{
    try { security.VerifyPassword(body.CurrentPassword); updates.SavePreferences(body.Preferences); return Results.Ok(updates.PublicView()); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/updates/apply", async (ApplyUpdateRequest body, UpdateService updates, WebSecurity security, CancellationToken token) =>
{
    try { security.VerifyPassword(body.CurrentPassword); return Results.Ok(await updates.ApplyAsync(body.Version, token)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex is InvalidOperationException ? ex.Message : "更新下载或校验失败，原服务保持运行。" }); }
});
app.MapGet("/api/auth/status", (HttpContext context, WebSecurity security) => Results.Ok(new { authenticated = security.IsAuthorized(context) }));
app.MapPost("/api/auth/mobile/claim", (MobileClaim body, MobileAccess mobile) =>
{
    try { return Results.Ok(mobile.Claim(body.Code ?? "", body.Name ?? "手机")); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/mobile/pairing", (HttpContext context, WebSecurity security, MobileAccess mobile) =>
{
    if (!security.IsWebAuthorized(context)) return Results.Unauthorized();
    try { return Results.Ok(mobile.CreateCode()); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/mobile/devices", (HttpContext context, WebSecurity security, MobileAccess mobile) =>
    security.IsWebAuthorized(context) ? Results.Ok(mobile.Devices()) : Results.Unauthorized());
app.MapDelete("/api/mobile/devices/{id}", (string id, HttpContext context, WebSecurity security, MobileAccess mobile) =>
{
    if (!security.IsWebAuthorized(context)) return Results.Unauthorized();
    mobile.Revoke(id); return Results.Ok(new { ok = true });
});
app.MapPost("/api/auth/login", async (HttpContext context, WebSecurity security) =>
{
    var body = await context.Request.ReadFromJsonAsync<LoginRequest>();
    return body is not null && security.Login(context, body.Password) ? Results.Ok(new { ok = true }) : Results.Json(new { error = "管理密码不正确。" }, statusCode: 401);
});
app.MapPost("/api/auth/logout", (HttpContext context, WebSecurity security) => { security.Logout(context); return Results.Ok(new { ok = true }); });
app.MapGet("/api/auth/recovery", (EmailRecovery recovery) => Results.Ok(recovery.Status()));
app.MapPost("/api/auth/recovery/request", async (EmailRecovery recovery, CancellationToken token) =>
{
    try { return Results.Ok(await recovery.RequestAsync(token)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/auth/recovery/reset", (HttpContext context, EmailRecovery recovery, RecoveryReset body) =>
{
    try { recovery.Reset(context, body.RequestId, body.Code, body.NewPassword); return Results.Ok(new { message = "密码已重置，请重新登录。" }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/settings", (ConsoleSettings settings) => Results.Ok(settings.PublicView()));
app.MapPut("/api/settings", (SettingsRequest body, WebSecurity security, ConsoleSettings settings, EmailRecovery recovery) =>
{
    try
    {
        security.VerifyPassword(body.CurrentPassword);
        settings.Save(body.Settings);
        recovery.Invalidate();
        return Results.Ok(new { message = "设置已保存。" });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/security/password", (HttpContext context, WebSecurity security, EmailRecovery recovery, PasswordChangeRequest body) =>
{
    try
    {
        security.ChangePassword(context, body.CurrentPassword, body.NewPassword);
        recovery.Invalidate();
        return Results.Ok(new { ok = true, message = "密码已修改，请使用新密码重新登录。" });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/overview", (RuntimeHost runtime) => Results.Ok(runtime.Overview()));
app.MapGet("/api/phone", (UfiGatewaySettings settings, UfiPhoneService phone) => Results.Ok(new { gateway = settings.PublicView(), status = phone.Status() }));
app.MapPut("/api/phone", (UfiGatewayOptions options, UfiGatewaySettings settings) =>
{
    try { settings.Save(options); return Results.Ok(new { message = "随身 WiFi 电话网关设置已保存。" }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/phone/probe", async (UfiPhoneService phone, CancellationToken token) =>
{
    try { return Results.Ok(await phone.ProbeAsync(token)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex is InvalidOperationException ? ex.Message : "未连接随身 WiFi，或电话接口暂不可用。" }); }
});
app.MapPost("/api/phone/test", async (SourceConfig source, UfiPhoneService phone, CancellationToken token) =>
{
    try { return Results.Ok(await phone.CallAsync(source, token)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex is InvalidOperationException ? ex.Message : "测试呼叫未完成，请检查随身 WiFi。" }); }
});
app.MapPut("/api/devices/{id}", async (string id, DeviceDraft draft, RuntimeHost runtime) =>
{
    try { await runtime.SaveDeviceAsync(id, draft); return Results.Ok(new { message = "仅当前设备与所属报警规则已保存并应用。" }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/test/device", async (DeviceDraft draft, RuntimeHost runtime, UfiPhoneService phone, CancellationToken token) =>
{
    var steps = new List<object>();
    try
    {
        if (draft.Source is null || draft.Rules is null || draft.Rules.Any(r => r.SourceId != draft.Source.Id)) throw new InvalidOperationException("请提交当前设备与其所属报警规则。");
        ConfigValidator.Validate(new AppConfig { Sources = [draft.Source], Rules = draft.Rules, Targets = [] }, true);
        steps.Add(new { name = "设备与报警规则", ok = true, message = $"已检查 {draft.Rules.Count} 条所属规则。" });
        try { await runtime.TestSourceAsync(draft.Source, token); steps.Add(new { name = "采集连接", ok = true, message = "当前填写的参数读取成功。" }); }
        catch (Exception ex) { steps.Add(new { name = "采集连接", ok = false, message = ex.GetBaseException().Message }); }
        if (draft.Source.PhoneReminder.Enabled)
        {
            try
            {
                if (!phone.GatewayEnabled) throw new InvalidOperationException("电话网关未启用，请到系统设置开启并保存。");
                await phone.ProbeAsync(token); steps.Add(new { name = "随身 WiFi", ok = true, message = "连接与 SIM 检查通过，未拨号。" });
            }
            catch (Exception ex) { steps.Add(new { name = "随身 WiFi", ok = false, message = ex is InvalidOperationException ? ex.Message : "随身 WiFi 不可达。" }); }
        }
        return Results.Ok(new { steps, message = "当前设备检查已完成，未保存配置、未发送消息或拨打电话。" });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/config", (RuntimeHost runtime) => Results.Ok(runtime.Config));
app.MapGet("/api/mobile/sources/{id}", (string id, RuntimeHost runtime) =>
{
    var source = ConfigStore.Clone(runtime.Config).Sources.FirstOrDefault(x => x.Id == id);
    return source is null ? Results.NotFound() : Results.Ok(source);
});
app.MapPut("/api/mobile/sources/{id}", async (string id, SourceConfig source, RuntimeHost runtime) =>
{
    try { await runtime.UpdateSourceAsync(id, source); return Results.Ok(new { message = "当前设备配置已保存。" }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/defaults", () => Results.Ok(ConfigDefaults.Protocols.ToDictionary(protocol => protocol,
    protocol => ConfigDefaults.MergeSourceParams(protocol, new Dictionary<string, System.Text.Json.JsonElement>()))));
app.MapPut("/api/config", async (AppConfig config, RuntimeHost runtime) =>
{
    try { await runtime.SaveAndRestartAsync(config); return Results.Ok(new { ok = true, message = "配置已保存；运行中的采集已重新加载，已停止的采集保持停止。" }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
app.MapPost("/api/runtime/{action}", async (string action, RuntimeHost runtime) =>
{
    try
    {
        if (action == "start") await runtime.StartRuntimeAsync();
        else if (action == "stop") await runtime.StopRuntimeAsync();
        else if (action == "restart") await runtime.RestartAsync();
        else return Results.NotFound();
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
app.MapPost("/api/test/source", async (SourceConfig source, RuntimeHost runtime, CancellationToken token) =>
{
    try { return Results.Ok(await runtime.TestSourceAsync(source, token)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
app.MapPost("/api/test/source/{id}", async (string id, RuntimeHost runtime, CancellationToken token) =>
{
    try { return Results.Ok(await runtime.TestSourceAsync(id, token)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
app.MapPost("/api/test/target", async (NotificationTarget target, RuntimeHost runtime, CancellationToken token) =>
{
    try { return Results.Ok(await runtime.TestTargetAsync(target, token)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
app.MapPost("/api/test/target/{id}", async (string id, RuntimeHost runtime, CancellationToken token) =>
{
    try { return Results.Ok(await runtime.TestTargetAsync(id, token)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
app.MapGet("/api/history", (string? source, int? page, int? pageSize, RuntimeHost runtime) => Results.Ok(runtime.History(source ?? "", page ?? 1, pageSize ?? 10)));
app.MapDelete("/api/history", (string? source, RuntimeHost runtime) => Results.Ok(new { deleted = runtime.DeleteHistory(source ?? "") }));
app.Map("/api/{**path}", () => Results.NotFound(new { error = "接口不存在。" }));
app.MapFallbackToFile("index.html");

await app.RunAsync();

internal sealed record LoginRequest(string Password);
internal sealed record MobileClaim(string? Code, string? Name);
internal sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
internal sealed record RecoveryReset(string RequestId, string Code, string NewPassword);
internal sealed record SettingsRequest(string CurrentPassword, ConsoleOptions Settings);

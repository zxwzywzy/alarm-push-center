using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AlarmPushCenter;

var count = 0;
void Assert(bool value, string name) { if (!value) throw new Exception(name); count++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception(name); }
var bytes = System.Text.Encoding.UTF8.GetBytes("isolated installer fixture");
var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var release = new AppRelease("1.10.0", "AlarmPushCenter-Setup-1.10.0-win-x64.exe", "https://github.com/zxwzywzy/alarm-push-center/releases/download/v1.10.0/AlarmPushCenter-Setup-1.10.0-win-x64.exe", sha, bytes.Length, "https://github.com/zxwzywzy/alarm-push-center/releases/tag/v1.10.0");
AppUpdateClient.Validate(release,"windows"); count++;
Reject(()=>AppUpdateClient.Validate(release with { Url="https://example.com/file.exe" },"windows"),"origin");
Reject(()=>AppUpdateClient.Validate(release with { Url=release.Url+"?redirect=bad" },"windows"),"query");
Reject(()=>AppUpdateClient.Validate(release with { FileName="../installer.exe" },"windows"),"path traversal");
Reject(()=>AppUpdateClient.Validate(release with { Sha256="bad" },"windows"),"digest missing");
Reject(()=>AppUpdateClient.Validate(release with { Size=300L*1024*1024+1 },"windows"),"oversize");
Reject(()=>AppUpdateClient.Validate(release with { Version="1.10.0-beta" },"windows"),"prerelease");
Reject(()=>AppUpdateClient.Validate(release,"android"),"platform confusion");
Reject(()=>AppUpdateClient.Validate(release with { ApiUrl="https://api.github.com/repos/other/project/releases/assets/1" },"windows"),"API repository confusion");
AppUpdateClient.Validate(release with { ApiUrl="https://api.github.com/repos/zxwzywzy/alarm-push-center/releases/assets/123" },"windows");count++;
var json = JsonSerializer.Serialize(new ReleaseCatalog(1,new() { ["windows"]=release }),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower});
using var client = new AppUpdateClient(new FixtureHandler(request=>
{
    if(request.RequestUri!.AbsolutePath.Contains("/contents/updates/")) return new(HttpStatusCode.OK) {Content=new StringContent(json)};
    if(request.RequestUri.Host=="github.com") {var redirect=new HttpResponseMessage(HttpStatusCode.Found);redirect.Headers.Location=new("https://release-assets.githubusercontent.com/download/file");return redirect;}
    return new(HttpStatusCode.OK) {Content=new ByteArrayContent(bytes)};
}));
Assert((await client.CheckAsync("windows","1.9.0")).Available,"numeric semver");
Assert(!(await client.CheckAsync("windows","1.10.0")).Available,"equal version");
Assert(!(await client.CheckAsync("windows","2.0.0")).Available,"no downgrade");
using var fallback = new AppUpdateClient(new FixtureHandler(request=>
{
    if(request.RequestUri!.AbsoluteUri!=AppUpdateClient.CatalogApiUrl)throw new HttpRequestException("isolated primary failure");
    return new(HttpStatusCode.OK){Content=new StringContent(json)};
}));
Assert((await fallback.CheckAsync("windows","1.9.0")).Available,"official API fallback without disabling TLS");
var folder=Path.Combine(Path.GetTempPath(),"alarm-update-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try
{
    var file=await client.DownloadAsync(release,"windows",folder);
    Assert(File.ReadAllBytes(file).SequenceEqual(bytes),"trusted redirect and download");
    Assert(await AppUpdateClient.VerifyAsync(file,release),"checksum");
    File.AppendAllText(file,"corrupt");Assert(!await AppUpdateClient.VerifyAsync(file,release),"tamper detection");
    await client.DownloadAsync(release,"windows",folder);Assert(await AppUpdateClient.VerifyAsync(file,release),"corrupt cache replaced");
    using var bad = new AppUpdateClient(new FixtureHandler(_=>{var r=new HttpResponseMessage(HttpStatusCode.Found);r.Headers.Location=new("http://127.0.0.1/internal");return r;}));
    try {await bad.DownloadAsync(release,"windows",Path.Combine(folder,"bad"));throw new Exception("unsafe redirect accepted");}catch(InvalidDataException){count++;}
    using var shortData = new AppUpdateClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent([1,2])}));
    try {await shortData.DownloadAsync(release,"windows",Path.Combine(folder,"short"));throw new Exception("truncated file accepted");}catch(InvalidDataException){count++;}
    Assert(!Directory.EnumerateFiles(folder,"*.partial",SearchOption.AllDirectories).Any(),"failed partial files removed");
    using var wrongHash = new AppUpdateClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}));
    try {await wrongHash.DownloadAsync(release with {Sha256=new string('0',64)},"windows",Path.Combine(folder,"hash"));throw new Exception("corrupt hash accepted");}catch(InvalidDataException){count++;}
    using var cancelled = new CancellationTokenSource();cancelled.Cancel();
    try {await client.DownloadAsync(release,"windows",Path.Combine(folder,"cancelled"),cancelled.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){count++;}
    var config=Path.Combine(folder,"config.json");File.WriteAllText(config,"{}");
    UpdateResumeState.Write(config,true);Assert(UpdateResumeState.Consume(config)==true,"resume running once");
    Assert(UpdateResumeState.Consume(config)==null,"resume consumed");
    UpdateResumeState.Write(config,false);Assert(UpdateResumeState.Consume(config)==false,"preserve stopped");
    Assert(UpdateService.IsMaintenanceHour(3,3,5),"maintenance start inclusive");
    Assert(!UpdateService.IsMaintenanceHour(5,3,5),"maintenance end exclusive");
    Assert(UpdateService.IsMaintenanceHour(23,22,2)&&UpdateService.IsMaintenanceHour(1,22,2)&&!UpdateService.IsMaintenanceHour(12,22,2),"overnight maintenance");
}
finally {Directory.Delete(folder,true);}
Console.WriteLine($"update checks passed: {count}");
sealed class FixtureHandler(Func<HttpRequestMessage,HttpResponseMessage> handle):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(handle(request));}
}

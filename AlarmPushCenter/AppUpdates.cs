using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UpdateChecks")]

namespace AlarmPushCenter;

public sealed record AppRelease(string Version, string FileName, string Url, string Sha256, long Size, string NotesUrl, string ApiUrl = "");
public sealed record ReleaseCatalog(int Schema, Dictionary<string, AppRelease> Platforms);
public sealed record UpdateStatus(string CurrentVersion, bool Available, AppRelease? Release, DateTime CheckedAt, string Message);

// Only publisher-owned HTTPS metadata and release assets are accepted. No credentials are sent.
public sealed class AppUpdateClient : IDisposable
{
    public const string Repository = "zxwzywzy/alarm-push-center";
    public const string DownloadsRepository = "zxwzywzy/alarm-push-center-downloads";
    public const string CatalogUrl = "https://raw.githubusercontent.com/" + Repository + "/main/updates/stable.json";
    public const string CatalogApiUrl = "https://api.github.com/repos/" + Repository + "/contents/updates/stable.json?ref=main";
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static string CurrentVersion => (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
    public AppUpdateClient() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal AppUpdateClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AlarmPushCenter-Updater/1.0");
        _http.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
    }

    public async Task<UpdateStatus> CheckAsync(string platform, string currentVersion, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var catalog = await ReadCatalogAsync(timeout.Token);
        if (catalog.Schema != 1 || catalog.Platforms is null || !catalog.Platforms.TryGetValue(platform, out var release))
            throw new InvalidDataException("当前平台暂无有效更新目录。");
        Validate(release, platform);
        var available = Version.Parse(release.Version) > Version.Parse(currentVersion);
        return new(currentVersion, available, release, DateTime.UtcNow, available ? $"发现新版本 {release.Version}" : "当前已是最新版本。");
    }

    private async Task<ReleaseCatalog> ReadCatalogAsync(CancellationToken token)
    {
        foreach (var address in new[] { "https://api.github.com/repos/" + DownloadsRepository + "/contents/updates/stable.json?ref=main", CatalogApiUrl, CatalogUrl })
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
                using var limited = new MemoryStream();
                await CopyBoundedAsync(stream, limited, 128 * 1024, attempt.Token);
                var catalog = JsonSerializer.Deserialize<ReleaseCatalog>(limited.ToArray(), Json);
                if (catalog?.Schema != 1 || catalog.Platforms is null) throw new InvalidDataException("更新目录格式不正确。");
                return catalog;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException) { }
        }
        throw new IOException("无法读取 GitHub 正式更新目录，请检查网络后重试。");
    }

    public static void Validate(AppRelease release, string platform)
    {
        if (release is null) throw new InvalidDataException("更新条目为空。");
        if (!Regex.IsMatch(release.Version ?? "", @"^\d+\.\d+\.\d+$") || !Version.TryParse(release.Version, out _))
            throw new InvalidDataException("更新版本格式无效。");
        var (name, tag) = platform switch
        {
            "windows" => ($"AlarmPushCenter-Setup-{release.Version}-win-x64.exe", $"v{release.Version}"),
            "openwrt" => ($"alarm-push-center_{release.Version}-1_aarch64_cortex-a53.ipk", $"openwrt-v{release.Version}"),
            "android" => ($"AlarmHub-{release.Version}-android.apk", $"mobile-v{release.Version}"),
            "ios" => ($"AlarmHub-{release.Version}-unsigned.ipa", $"mobile-v{release.Version}"),
            _ => throw new InvalidDataException("更新平台无效。")
        };
        var repository = new[] { Repository, DownloadsRepository }.FirstOrDefault(repo => release.Url == $"https://github.com/{repo}/releases/download/{tag}/{name}");
        if (release.FileName != name || repository is null || release.NotesUrl != $"https://github.com/{repository}/releases/tag/{tag}" ||
            !Regex.IsMatch(release.Sha256 ?? "", "^[a-fA-F0-9]{64}$") || release.Size is < 1 or > 300L * 1024 * 1024)
            throw new InvalidDataException("更新来源、文件名或校验信息无效。");
        if (!string.IsNullOrEmpty(release.ApiUrl) && !Regex.IsMatch(release.ApiUrl, "^https://api\\.github\\.com/repos/" + Regex.Escape(repository) + @"/releases/assets/[1-9]\d*$"))
            throw new InvalidDataException("更新资产 API 地址无效。");
    }

    public async Task<string> DownloadAsync(AppRelease release, string platform, string directory, CancellationToken token = default)
    {
        Validate(release, platform);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(Path.GetFullPath(directory), release.FileName);
        if (File.Exists(path) && await VerifyAsync(path, release, token)) return path;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var uri = new Uri(string.IsNullOrEmpty(release.ApiUrl) ? release.Url : release.ApiUrl);
            using var response = await GetAssetAsync(uri, token);
            if (response.Content.Headers.ContentLength is long size && size != release.Size) throw new InvalidDataException("更新文件长度不符。");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(input, output, release.Size, token);
            if (!await VerifyAsync(temp, release, token)) throw new InvalidDataException("更新文件 SHA256 校验失败，未执行安装。");
            File.Move(temp, path, true);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<HttpResponseMessage> GetAssetAsync(Uri uri, CancellationToken token)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("github.com" or "api.github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("更新下载跳转到不受信任地址。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("更新下载跳转缺少地址。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("更新下载跳转过多。");
    }

    internal static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[64 * 1024]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("更新数据超过允许大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
    public static async Task<bool> VerifyAsync(string path, AppRelease release, CancellationToken token = default)
    {
        if (new FileInfo(path).Length != release.Size) return false;
        await using var input = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(input, token);
        return Convert.ToHexString(hash).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() => _http.Dispose();
}

public static class UpdateResumeState
{
    public static string PathFor(string configPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, ".update-resume-once");
    public static void Write(string configPath, bool running)
    {
        var path = PathFor(configPath);
        File.WriteAllText(path, running ? "running" : "stopped");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public static bool? Consume(string configPath)
    {
        var path = PathFor(configPath);
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim(); File.Delete(path);
        return value == "running" ? true : value == "stopped" ? false : null;
    }
}

using AgentSpeed.Linux.Platform;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Linux.Services;

internal sealed class LinuxUpdater(LinuxApp app) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private readonly UpdateSource _source = UpdateSource.FromOverride(Environment.GetEnvironmentVariable(UpdateSource.FeedOverrideVariable), out _);
    private CancellationTokenSource? _request;
    public ReleaseInfo? Available { get; private set; }
    public event Action? Changed;
    public string? Status { get; private set; }
    public async Task CheckAsync(bool manual = true)
    {
        if (!app.Settings.Current.NetworkEnabled || _request is not null) return;
        _request = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var data = new MemoryStream(); await Fetch(_source.FeedUrl, data, ReleaseFeed.MaxFeedBytes, _request.Token);
            var release = ReleaseFeed.Parse(data.ToArray());
            Available = SemanticVersion.TryParse(app.Version, out var current)
                && UpdateSchedule.Evaluate(current, release, app.Settings.Current.SkippedVersion, manual) == UpdateOffer.Available
                && release?.FindAsset("AgentSpeed-Linux-x64.deb") is not null ? release : null;
            Status = Available?.DisplayVersion ?? "Up to date";
            app.Settings.Update(s => s with { LastUpdateCheck = DateTimeOffset.UtcNow });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UpdateException) { Status = "Update check failed"; }
        finally { _request.Dispose(); _request = null; Changed?.Invoke(); }
    }
    public async Task<string?> DownloadAsync()
    {
        if (!app.Settings.Current.NetworkEnabled || _request is not null || Available is not { } release
            || release.FindAsset("AgentSpeed-Linux-x64.deb") is not { } asset || release.FindAsset("SHA256SUMS.txt") is not { } sums) return null;
        if (asset.Size is <= 0 or > 300L * 1024 * 1024) { Status = "Download or verification failed"; return null; }
        _request = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var directory = Path.Combine(LinuxPaths.Cache, "updates", release.DisplayVersion);
        LinuxPaths.EnsurePrivate(directory);
        var final = Path.Combine(directory, asset.Name); var temp = final + ".partial";
        try
        {
            using var manifest = new MemoryStream(); await Fetch(sums.DownloadUrl, manifest, ChecksumManifest.MaxBytes, _request.Token);
            var expected = ChecksumManifest.Find(System.Text.Encoding.UTF8.GetString(manifest.ToArray()), asset.Name);
            if (expected is null) throw new InvalidDataException("Missing checksum.");
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                await Fetch(asset.DownloadUrl, file, 300L * 1024 * 1024, _request.Token);
            if (new FileInfo(temp).Length != asset.Size) throw new InvalidDataException("Asset size mismatch.");
            var actual = await ChecksumManifest.ComputeAsync(temp, _request.Token);
            if (!ChecksumManifest.Matches(expected, actual)) throw new InvalidDataException("Checksum mismatch.");
            File.Move(temp, final, true); Status = "Verified"; return final;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { Status = "Download or verification failed"; return null; }
        finally { File.Delete(temp); _request.Dispose(); _request = null; Changed?.Invoke(); }
    }
    private async Task Fetch(Uri uri, Stream output, long limit, CancellationToken token)
    {
        for (var redirects = 0; redirects <= UpdateSource.MaxRedirects; redirects++)
        {
            if (!_source.IsAllowed(uri)) throw new IOException("Disallowed update host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd($"AgentSpeed/{app.Version} (Linux)");
            if (uri == _source.FeedUrl)
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            { uri = _source.ResolveRedirect(uri, response.Headers.Location) ?? throw new IOException("Disallowed redirect."); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new IOException("Update response too large.");
            await using var body = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[81920]; long total = 0;
            for (int count; (count = await body.ReadAsync(buffer, token)) > 0;)
            { total += count; if (total > limit) throw new IOException("Update response too large."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
            return;
        }
        throw new IOException("Too many update redirects.");
    }
    public void Cancel() => _request?.Cancel();
    public void Skip()
    {
        if (Available is not { } release) return;
        app.Settings.Update(s => s with { SkippedVersion = release.Tag }); Available = null; Changed?.Invoke();
    }
    public void Dispose() { Cancel(); _http.Dispose(); }
}

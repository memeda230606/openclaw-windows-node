using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.SetupEngine;

internal sealed class OssDependencyManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string? Version { get; set; }
    public OssDependencyAsset? InstallCli { get; set; }
    public OssDependencyAsset? UbuntuWslRootfs { get; set; }
}

internal sealed class OssDependencyAsset
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
    public long? Size { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
}

internal sealed record InstallCliResolution(
    bool Success,
    string? Url,
    string Source,
    string? VerifiedScript,
    IReadOnlyDictionary<string, string>? Environment,
    string? ErrorMessage)
{
    public static InstallCliResolution Ok(string url, string source, string? verifiedScript = null) =>
        new(true, url, source, verifiedScript, null, null);

    public static InstallCliResolution Ok(
        string url,
        string source,
        string verifiedScript,
        IReadOnlyDictionary<string, string>? environment) =>
        new(true, url, source, verifiedScript, environment, null);

    public static InstallCliResolution Fail(string message) =>
        new(false, null, "oss-manifest", null, null, message);
}

internal sealed record WslRootfsResolution(
    bool Success,
    string Source,
    string? LocalPath,
    string? ErrorMessage)
{
    public static WslRootfsResolution Ok(string localPath, string source) =>
        new(true, source, localPath, null);

    public static WslRootfsResolution OfficialFallback(string source) =>
        new(true, source, null, null);

    public static WslRootfsResolution Fail(string message) =>
        new(false, "oss-manifest", null, message);
}

internal static class OssDependencyResolver
{
    internal static Func<Uri, CancellationToken, Task<byte[]>>? AssetDownloaderOverride { get; set; }
    internal static Func<Uri, string, CancellationToken, Task>? AssetFileDownloaderOverride { get; set; }

    public static async Task<InstallCliResolution> ResolveInstallCliAsync(
        SetupConfig config,
        SetupLogger logger,
        CancellationToken ct)
    {
        if (config.Gateway.InstallUrl != null)
            return InstallCliResolution.Ok(config.Gateway.InstallUrl, "gateway-config");

        if (!config.Oss.Enabled)
            return InstallCliResolution.Ok(GatewayLkgVersion.DefaultInstallUrl, "official-default");

        var manifestResult = await TryLoadManifestAsync(config.Oss, ct);
        if (!manifestResult.Success)
            return FallBackInstallCliOrFail(config.Oss, logger, manifestResult.ErrorMessage!);

        var manifest = manifestResult.Manifest;
        var asset = manifest.InstallCli;
        var installUrl = asset?.Url;
        if (string.IsNullOrWhiteSpace(installUrl))
            return FallBackInstallCliOrFail(config.Oss, logger, "OSS manifest does not define installCli.url.");

        if (!Uri.TryCreate(installUrl, UriKind.Absolute, out var parsedInstallUrl) ||
            !string.Equals(parsedInstallUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return FallBackInstallCliOrFail(config.Oss, logger, $"OSS installCli.url must be HTTPS: {installUrl}");
        }

        if (string.IsNullOrWhiteSpace(asset?.Sha256))
            return FallBackInstallCliOrFail(config.Oss, logger, "OSS manifest installCli.sha256 is required.");

        var scriptResult = await TryDownloadAndVerifyScriptAssetAsync(parsedInstallUrl, asset, ct);
        if (!scriptResult.Success)
            return FallBackInstallCliOrFail(config.Oss, logger, scriptResult.ErrorMessage!);

        var environmentResult = ValidateEnvironment(asset.Environment);
        if (!environmentResult.Success)
            return FallBackInstallCliOrFail(config.Oss, logger, environmentResult.ErrorMessage!);

        logger.Info("Using OSS dependency manifest for CLI installer", new
        {
            source = manifestResult.Source,
            manifest_version = manifest?.Version,
            install_cli_url = installUrl,
            install_cli_size = scriptResult.Size,
            install_cli_sha256 = scriptResult.Sha256,
            install_cli_environment = environmentResult.Environment?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [],
        });
        return InstallCliResolution.Ok(
            installUrl,
            "oss-manifest",
            scriptResult.Script!,
            environmentResult.Environment);
    }

    public static async Task<WslRootfsResolution> ResolveUbuntuWslRootfsAsync(
        SetupConfig config,
        string localDataDir,
        SetupLogger logger,
        CancellationToken ct)
    {
        var baseDistro = config.BaseDistro.Trim();
        if (!config.Oss.Enabled)
            return WslRootfsResolution.OfficialFallback("official-default");

        if (!string.Equals(baseDistro, "Ubuntu-24.04", StringComparison.OrdinalIgnoreCase))
        {
            return FallBackWslRootfsOrFail(
                config.Oss,
                logger,
                $"OSS rootfs mirror only supports BaseDistro Ubuntu-24.04, got {baseDistro}.");
        }

        var manifestResult = await TryLoadManifestAsync(config.Oss, ct);
        if (!manifestResult.Success)
            return FallBackWslRootfsOrFail(config.Oss, logger, manifestResult.ErrorMessage!);

        var manifest = manifestResult.Manifest;
        var asset = manifest.UbuntuWslRootfs;
        var rootfsUrl = asset?.Url;
        if (string.IsNullOrWhiteSpace(rootfsUrl))
            return FallBackWslRootfsOrFail(config.Oss, logger, "OSS manifest does not define ubuntuWslRootfs.url.");

        if (!Uri.TryCreate(rootfsUrl, UriKind.Absolute, out var parsedRootfsUrl) ||
            !string.Equals(parsedRootfsUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return FallBackWslRootfsOrFail(config.Oss, logger, $"OSS ubuntuWslRootfs.url must be HTTPS: {rootfsUrl}");
        }

        if (string.IsNullOrWhiteSpace(asset?.Sha256))
            return FallBackWslRootfsOrFail(config.Oss, logger, "OSS manifest ubuntuWslRootfs.sha256 is required.");

        if (!TryNormalizeSha256(asset.Sha256, out var expectedSha256))
            return FallBackWslRootfsOrFail(config.Oss, logger, "OSS manifest ubuntuWslRootfs.sha256 must be a 64-character hex SHA-256 digest.");

        var cacheDir = Path.Combine(localDataDir, "oss-cache", "ubuntu-wsl-rootfs");
        Directory.CreateDirectory(cacheDir);
        var fileName = SafeFileName(Path.GetFileName(parsedRootfsUrl.LocalPath), "ubuntu-24.04-wsl.rootfs.tar.gz");
        var localPath = Path.Combine(cacheDir, $"{expectedSha256[..12]}-{fileName}");

        var cached = await TryVerifyFileAsync(localPath, expectedSha256, asset.Size, ct);
        if (!cached.Success)
        {
            var tempPath = Path.Combine(cacheDir, $".{Guid.NewGuid():N}.download");
            try
            {
                await DownloadAssetFileAsync(parsedRootfsUrl, tempPath, ct);
                var downloaded = await TryVerifyFileAsync(tempPath, expectedSha256, asset.Size, ct);
                if (!downloaded.Success)
                    return FallBackWslRootfsOrFail(config.Oss, logger, downloaded.ErrorMessage!);

                if (File.Exists(localPath))
                    File.Delete(localPath);
                File.Move(tempPath, localPath);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                return FallBackWslRootfsOrFail(config.Oss, logger, $"Unable to download OSS ubuntuWslRootfs: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
            {
                return FallBackWslRootfsOrFail(config.Oss, logger, $"Unable to download OSS ubuntuWslRootfs: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        logger.Info("Using OSS dependency manifest for Ubuntu WSL rootfs", new
        {
            source = manifestResult.Source,
            manifest_version = manifest.Version,
            ubuntu_wsl_rootfs_url = rootfsUrl,
            ubuntu_wsl_rootfs_path = localPath,
            ubuntu_wsl_rootfs_sha256 = expectedSha256,
        });
        return WslRootfsResolution.Ok(localPath, "oss-manifest");
    }

    private static async Task<ScriptAssetDownloadResult> TryDownloadAndVerifyScriptAssetAsync(
        Uri uri,
        OssDependencyAsset asset,
        CancellationToken ct)
    {
        if (!TryNormalizeSha256(asset.Sha256, out var expectedSha256))
            return ScriptAssetDownloadResult.Fail("OSS manifest installCli.sha256 must be a 64-character hex SHA-256 digest.");

        byte[] bytes;
        try
        {
            bytes = AssetDownloaderOverride != null
                ? await AssetDownloaderOverride(uri, ct)
                : await DownloadAssetBytesAsync(uri, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return ScriptAssetDownloadResult.Fail($"Unable to download OSS installCli script: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return ScriptAssetDownloadResult.Fail($"Unable to download OSS installCli script: {ex.Message}");
        }

        if (asset.Size is { } expectedSize && bytes.LongLength != expectedSize)
        {
            return ScriptAssetDownloadResult.Fail(
                $"OSS installCli script size mismatch: expected {expectedSize}, got {bytes.LongLength}.");
        }

        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ScriptAssetDownloadResult.Fail(
                $"OSS installCli script SHA-256 mismatch: expected {expectedSha256}, got {actualSha256}.");
        }

        var script = Encoding.UTF8.GetString(bytes);
        return ScriptAssetDownloadResult.Ok(script, bytes.LongLength, actualSha256);
    }

    private static EnvironmentValidationResult ValidateEnvironment(Dictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return EnvironmentValidationResult.Ok(null);

        var validated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in environment)
        {
            if (!IsAllowedEnvironmentKey(key))
                return EnvironmentValidationResult.Fail($"OSS manifest installCli.environment contains an unsupported key: {key}");

            if (value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
                return EnvironmentValidationResult.Fail($"OSS manifest installCli.environment value for {key} cannot contain control line breaks.");

            if (value.Length > 4096)
                return EnvironmentValidationResult.Fail($"OSS manifest installCli.environment value for {key} is too long.");

            validated[key] = value;
        }

        return EnvironmentValidationResult.Ok(validated);
    }

    private static bool IsAllowedEnvironmentKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            return false;

        if (string.Equals(key, "WSLENV", StringComparison.Ordinal))
            return false;

        if (key[0] != '_' && !char.IsAsciiLetter(key[0]))
            return false;

        for (var i = 1; i < key.Length; i++)
        {
            var ch = key[i];
            if (ch != '_' && !char.IsAsciiLetterOrDigit(ch))
                return false;
        }

        return key.StartsWith("OPENCLAW_", StringComparison.Ordinal) ||
               key.StartsWith("NPM_CONFIG_", StringComparison.Ordinal) ||
               key.StartsWith("npm_config_", StringComparison.Ordinal) ||
               key.StartsWith("COREPACK_", StringComparison.Ordinal);
    }

    private static async Task<byte[]> DownloadAssetBytesAsync(Uri uri, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        return await client.GetByteArrayAsync(uri, ct);
    }

    private static async Task DownloadAssetFileAsync(Uri uri, string outputPath, CancellationToken ct)
    {
        if (AssetFileDownloaderOverride != null)
        {
            await AssetFileDownloaderOverride(uri, outputPath, ct);
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await using var input = await client.GetStreamAsync(uri, ct);
        await using var output = File.Create(outputPath);
        await input.CopyToAsync(output, ct);
    }

    private static async Task<FileVerifyResult> TryVerifyFileAsync(
        string path,
        string expectedSha256,
        long? expectedSize,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
                return FileVerifyResult.Fail("OSS ubuntuWslRootfs is not cached.");

            var info = new FileInfo(path);
            if (expectedSize is { } size && info.Length != size)
                return FileVerifyResult.Fail($"OSS ubuntuWslRootfs size mismatch: expected {size}, got {info.Length}.");

            await using var stream = File.OpenRead(path);
            var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return FileVerifyResult.Fail(
                    $"OSS ubuntuWslRootfs SHA-256 mismatch: expected {expectedSha256}, got {actualSha256}.");
            }

            return FileVerifyResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileVerifyResult.Fail($"Unable to verify OSS ubuntuWslRootfs: {ex.Message}");
        }
    }

    private static async Task<ManifestLoadResult> TryLoadManifestAsync(OssMirrorConfig config, CancellationToken ct)
    {
        var jsonResult = await TryLoadManifestJsonAsync(config, ct);
        if (!jsonResult.Success)
            return ManifestLoadResult.Fail(jsonResult.ErrorMessage!);

        try
        {
            var manifest = JsonSerializer.Deserialize<OssDependencyManifest>(
                jsonResult.Json!,
                SetupConfig.JsonOptions);
            return manifest is null
                ? ManifestLoadResult.Fail("OSS manifest JSON is empty.")
                : ManifestLoadResult.Ok(manifest, jsonResult.Source);
        }
        catch (JsonException ex)
        {
            return ManifestLoadResult.Fail($"OSS manifest JSON is invalid: {ex.Message}");
        }
    }

    private static async Task<ManifestJsonLoadResult> TryLoadManifestJsonAsync(OssMirrorConfig config, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.ManifestPath))
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(config.ManifestPath);
                var json = await File.ReadAllTextAsync(path, ct);
                return ManifestJsonLoadResult.Ok(json, "manifest-path");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return ManifestJsonLoadResult.Fail($"Unable to read OSS manifest path: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ManifestUrl))
        {
            if (!Uri.TryCreate(config.ManifestUrl, UriKind.Absolute, out var manifestUri) ||
                !string.Equals(manifestUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return ManifestJsonLoadResult.Fail($"OSS manifest URL must be HTTPS: {config.ManifestUrl}");
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var json = await client.GetStringAsync(manifestUri, ct);
                return ManifestJsonLoadResult.Ok(json, "manifest-url");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                return ManifestJsonLoadResult.Fail($"Unable to download OSS manifest: {ex.Message}");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                return ManifestJsonLoadResult.Fail($"Unable to download OSS manifest: {ex.Message}");
            }
        }

        return ManifestJsonLoadResult.Fail("OSS mirror is enabled but no manifest path or URL is configured.");
    }

    private static InstallCliResolution FallBackInstallCliOrFail(OssMirrorConfig config, SetupLogger logger, string reason)
    {
        if (!config.AllowOfficialFallback)
            return InstallCliResolution.Fail($"OSS dependency manifest failed and official fallback is disabled: {reason}");

        logger.Warn($"OSS dependency manifest unavailable; falling back to official installer URL. {reason}");
        return InstallCliResolution.Ok(GatewayLkgVersion.DefaultInstallUrl, "official-fallback");
    }

    private static WslRootfsResolution FallBackWslRootfsOrFail(OssMirrorConfig config, SetupLogger logger, string reason)
    {
        if (!config.AllowOfficialFallback)
            return WslRootfsResolution.Fail($"OSS dependency manifest failed and official fallback is disabled: {reason}");

        logger.Warn($"OSS ubuntuWslRootfs unavailable; falling back to wsl.exe web download. {reason}");
        return WslRootfsResolution.OfficialFallback("official-fallback");
    }

    private static string SafeFileName(string? candidate, string fallback)
    {
        var fileName = string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
        foreach (var ch in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(ch, '-');
        return fileName;
    }

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            candidate = candidate["sha256:".Length..].Trim();

        if (candidate.Length != 64)
            return false;

        foreach (var ch in candidate)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        normalized = candidate.ToLowerInvariant();
        return true;
    }

    private sealed record ManifestJsonLoadResult(bool Success, string? Json, string Source, string? ErrorMessage)
    {
        public static ManifestJsonLoadResult Ok(string json, string source) => new(true, json, source, null);
        public static ManifestJsonLoadResult Fail(string message) => new(false, null, "oss-manifest", message);
    }

    private sealed record ManifestLoadResult(bool Success, OssDependencyManifest Manifest, string Source, string? ErrorMessage)
    {
        public static ManifestLoadResult Ok(OssDependencyManifest manifest, string source) => new(true, manifest, source, null);
        public static ManifestLoadResult Fail(string message) => new(false, new OssDependencyManifest(), "oss-manifest", message);
    }

    private sealed record ScriptAssetDownloadResult(
        bool Success,
        string? Script,
        long Size,
        string? Sha256,
        string? ErrorMessage)
    {
        public static ScriptAssetDownloadResult Ok(string script, long size, string sha256) =>
            new(true, script, size, sha256, null);

        public static ScriptAssetDownloadResult Fail(string message) =>
            new(false, null, 0, null, message);
    }

    private sealed record FileVerifyResult(bool Success, string? ErrorMessage)
    {
        public static FileVerifyResult Ok() => new(true, null);
        public static FileVerifyResult Fail(string message) => new(false, message);
    }

    private sealed record EnvironmentValidationResult(
        bool Success,
        IReadOnlyDictionary<string, string>? Environment,
        string? ErrorMessage)
    {
        public static EnvironmentValidationResult Ok(IReadOnlyDictionary<string, string>? environment) =>
            new(true, environment, null);

        public static EnvironmentValidationResult Fail(string message) =>
            new(false, null, message);
    }
}

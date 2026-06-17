using System.Text.Json;

namespace OpenClaw.SetupEngine;

public sealed class LongwangModelsManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string? Version { get; set; }
    public string? DefaultModelId { get; set; }
    public List<LongwangModelConfig> Models { get; set; } = [];
}

public sealed record LongwangModelManifestApplyResult(
    bool Applied,
    string Source,
    string? Version,
    int ModelCount,
    string? ErrorMessage)
{
    public static LongwangModelManifestApplyResult Ok(string source, string? version, int modelCount) =>
        new(true, source, version, modelCount, null);

    public static LongwangModelManifestApplyResult Skipped(string reason) =>
        new(false, "skipped", null, 0, reason);

    public static LongwangModelManifestApplyResult Failed(string source, string message) =>
        new(false, source, null, 0, message);
}

public static class LongwangModelManifestResolver
{
    internal static Func<Uri, TimeSpan, CancellationToken, Task<string>>? ManifestDownloaderOverride { get; set; }

    public static async Task<LongwangModelManifestApplyResult> TryApplyAsync(
        ModelSetupConfig config,
        SetupLogger logger,
        CancellationToken ct)
    {
        if (!config.ModelsManifestEnabled)
            return LongwangModelManifestApplyResult.Skipped("Longwang model manifest is disabled.");

        if (!config.Enabled || !config.UseLongwang)
            return LongwangModelManifestApplyResult.Skipped("Longwang model setup is disabled.");

        var load = await TryLoadManifestJsonAsync(config, ct);
        if (!load.Success)
        {
            logger.Warn($"Longwang model manifest unavailable; using bundled model list. {load.ErrorMessage}");
            return LongwangModelManifestApplyResult.Failed(load.Source, load.ErrorMessage ?? "Unknown manifest load error.");
        }

        LongwangModelsManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LongwangModelsManifest>(load.Json!, SetupConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.Warn($"Longwang model manifest JSON is invalid; using bundled model list. {ex.Message}");
            return LongwangModelManifestApplyResult.Failed(load.Source, $"Invalid JSON: {ex.Message}");
        }

        var models = NormalizeModels(manifest?.Models);
        if (models.Count == 0)
        {
            logger.Warn("Longwang model manifest contains no valid models; using bundled model list.");
            return LongwangModelManifestApplyResult.Failed(load.Source, "Manifest contains no valid models.");
        }

        var configuredDefaultModelId = config.ModelId;
        config.Models = models;

        var selected = models.FirstOrDefault(model =>
            string.Equals(model.Id, configuredDefaultModelId, StringComparison.OrdinalIgnoreCase));
        if (selected != null)
        {
            config.SelectModel(selected);
        }
        else if (string.IsNullOrWhiteSpace(configuredDefaultModelId) &&
                 !string.IsNullOrWhiteSpace(manifest?.DefaultModelId))
        {
            selected = models.FirstOrDefault(model =>
                string.Equals(model.Id, manifest.DefaultModelId, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
                config.SelectModel(selected);
        }

        logger.Info("Loaded Longwang model manifest", new
        {
            source = load.Source,
            version = manifest?.Version,
            models = models.Count,
            default_model = config.ModelId
        });
        return LongwangModelManifestApplyResult.Ok(load.Source, manifest?.Version, models.Count);
    }

    private static async Task<ManifestJsonLoadResult> TryLoadManifestJsonAsync(ModelSetupConfig config, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.ModelsManifestPath))
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(config.ModelsManifestPath);
                var json = await File.ReadAllTextAsync(path, ct);
                return ManifestJsonLoadResult.Ok(json, "manifest-path");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return ManifestJsonLoadResult.Fail("manifest-path", $"Unable to read Longwang model manifest path: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(config.ModelsManifestUrl))
            return ManifestJsonLoadResult.Fail("manifest-url", "Longwang model manifest URL is empty.");

        if (!Uri.TryCreate(config.ModelsManifestUrl, UriKind.Absolute, out var manifestUri) ||
            !string.Equals(manifestUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestJsonLoadResult.Fail("manifest-url", $"Longwang model manifest URL must be HTTPS: {config.ModelsManifestUrl}");
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(config.ModelsManifestTimeoutSeconds, 1, 60));
            var downloader = ManifestDownloaderOverride ?? DownloadStringAsync;
            var json = await downloader(manifestUri, timeout, ct);
            return ManifestJsonLoadResult.Ok(json, "manifest-url");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return ManifestJsonLoadResult.Fail("manifest-url", $"Unable to download Longwang model manifest: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return ManifestJsonLoadResult.Fail("manifest-url", $"Unable to download Longwang model manifest: {ex.Message}");
        }
    }

    private static async Task<string> DownloadStringAsync(Uri manifestUri, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = timeout };
        return await client.GetStringAsync(manifestUri, ct);
    }

    private static List<LongwangModelConfig> NormalizeModels(IEnumerable<LongwangModelConfig>? models)
    {
        var result = new List<LongwangModelConfig>();
        if (models == null)
            return result;

        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
                continue;
            if (result.Any(existing => string.Equals(existing.Id, model.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            var id = model.Id.Trim();
            result.Add(new LongwangModelConfig
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(model.Name) ? id : model.Name.Trim(),
                Reasoning = model.Reasoning,
                Input = NormalizeInput(model.Input),
                ContextWindow = model.ContextWindow,
                ContextTokens = model.ContextTokens,
                MaxTokens = model.MaxTokens,
                ThinkingFormat = NormalizeOptionalString(model.ThinkingFormat),
                SupportsTools = model.SupportsTools,
                SupportsUsageInStreaming = model.SupportsUsageInStreaming,
                SupportsReasoningEffort = model.SupportsReasoningEffort,
                MaxTokensField = NormalizeOptionalString(model.MaxTokensField)
            });
        }

        return result;
    }

    private static string[] NormalizeInput(string[]? input)
    {
        if (input is not { Length: > 0 })
            return ["text"];

        var normalized = input
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0 ? normalized : ["text"];
    }

    private static string? NormalizeOptionalString(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed record ManifestJsonLoadResult(bool Success, string? Json, string Source, string? ErrorMessage)
    {
        public static ManifestJsonLoadResult Ok(string json, string source) => new(true, json, source, null);
        public static ManifestJsonLoadResult Fail(string source, string message) => new(false, null, source, message);
    }
}

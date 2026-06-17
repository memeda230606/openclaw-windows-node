using System.Text.Json;
using System.Runtime.Versioning;

namespace OpenClaw.SetupEngine.Tests;

public class SetupConfigTests : IDisposable
{
    private readonly string _tempDir;

    public SetupConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Defaults_AreReasonable()
    {
        var config = new SetupConfig();
        Assert.Equal("OpenClawGateway", config.DistroName);
        Assert.Equal(18789, config.GatewayPort);
        Assert.Equal("Ubuntu-24.04", config.BaseDistro);
        Assert.False(config.Headless);
        Assert.False(config.DryRun);
        Assert.Equal("trace", config.LogLevel);
        Assert.False(config.RollbackOnFailure);
        Assert.Equal("loopback", config.Gateway.Bind);
        Assert.Equal("none", config.Gateway.AuthMode);
        Assert.False(config.Oss.Enabled);
        Assert.Null(config.Oss.ManifestUrl);
        Assert.Null(config.Oss.ManifestPath);
        Assert.True(config.Oss.AllowOfficialFallback);
        Assert.False(config.SkipPermissions);
        Assert.False(config.SkipWizard);
        Assert.True(config.ModelSetup.Enabled);
        Assert.True(config.ModelSetup.UseLongwang);
        Assert.Equal("https://openclaw.ipk50.com/console", config.ModelSetup.ConsoleUrl);
        Assert.Equal("https://openclaw.ipk50.com/v1", config.ModelSetup.BaseUrl);
        Assert.Equal("longwang-qwen", config.ModelSetup.ProviderId);
        Assert.Equal("qwen3.7-plus", config.ModelSetup.ModelId);
        Assert.Equal("Qwen3.7 Plus", config.ModelSetup.ModelName);
        Assert.Equal("longwang-qwen/qwen3.7-plus", config.ModelSetup.DefaultModelRef);
        Assert.True(config.ModelSetup.ModelsManifestEnabled);
        Assert.Equal("https://openclaw.ipk50.com/manifests/longwang-models.json", config.ModelSetup.ModelsManifestUrl);
        Assert.Null(config.ModelSetup.ModelsManifestPath);
        Assert.Equal(8, config.ModelSetup.ModelsManifestTimeoutSeconds);
        Assert.True(config.ModelSetup.EffectiveModels.Count >= 15);
        Assert.Contains(config.ModelSetup.EffectiveModels, model => model.Id == "qwen3.7-max");
        Assert.Contains(config.ModelSetup.EffectiveModels, model => model.Id == "deepseek-v4-pro");
        Assert.Contains(config.ModelSetup.EffectiveModels, model => model.Id == "kimi-k2.6");
        Assert.Contains(config.ModelSetup.EffectiveModels, model => model.Id == "glm-5.2");
        Assert.Contains(config.ModelSetup.EffectiveModels, model => model.Id == "glm-5.1");
        Assert.Contains(config.ModelSetup.EffectiveModels, model => model.Id == "minimax-m3");
        var glm5v = Assert.Single(config.ModelSetup.EffectiveModels, model => model.Id == "glm-5v-turbo");
        Assert.Contains("file", glm5v.Input);
    }

    [Fact]
    public void ApplyUiDefaults_EnablesRollbackAndClearsHeadless()
    {
        var config = new SetupConfig
        {
            Headless = true,
            RollbackOnFailure = false
        };

        config.ApplyUiDefaults();

        Assert.False(config.Headless);
        Assert.True(config.RollbackOnFailure);
    }

    [Fact]
    public void ApplyUiDefaults_AllowsRollbackOptOut()
    {
        var config = new SetupConfig { RollbackOnFailure = true };

        config.ApplyUiDefaults(rollbackOnFailure: false);

        Assert.False(config.Headless);
        Assert.False(config.RollbackOnFailure);
    }

    [Fact]
    public void EffectiveGatewayUrl_UsesPort()
    {
        var config = new SetupConfig { GatewayPort = 9999 };
        Assert.Equal("ws://localhost:9999", config.EffectiveGatewayUrl);
    }

    [Fact]
    public void EffectiveGatewayUrl_PreferExplicitUrl()
    {
        var config = new SetupConfig { GatewayUrl = "ws://custom:1234" };
        Assert.Equal("ws://custom:1234", config.EffectiveGatewayUrl);
    }

    [Fact]
    public void LoadFromFile_ParsesJson()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """
        {
            "DistroName": "TestDistro",
            "GatewayPort": 12345,
            "Headless": true,
            // comment support
            "Gateway": {
                "Bind": "localhost",
                "ReloadMode": "cold"
            },
            "Oss": {
                "Enabled": true,
                "ManifestUrl": "https://oss.example.test/openclaw/dependencies.json",
                "AllowOfficialFallback": false
            }
        }
        """);

        var config = SetupConfig.LoadFromFile(path);
        Assert.Equal("TestDistro", config.DistroName);
        Assert.Equal(12345, config.GatewayPort);
        Assert.True(config.Headless);
        Assert.Equal("localhost", config.Gateway.Bind);
        Assert.Equal("cold", config.Gateway.ReloadMode);
        Assert.True(config.Oss.Enabled);
        Assert.Equal("https://oss.example.test/openclaw/dependencies.json", config.Oss.ManifestUrl);
        Assert.False(config.Oss.AllowOfficialFallback);
    }

    [Fact]
    public void FromEnvironment_OverridesDefaults()
    {
        // Set env vars temporarily
        var prevDistro = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_DISTRO");
        var prevPort = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PORT");
        var prevHeadless = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS");
        var prevOssEnabled = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_OSS_ENABLED");
        var prevOssManifestUrl = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_OSS_MANIFEST_URL");
        var prevOssFallback = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_OSS_ALLOW_OFFICIAL_FALLBACK");
        var prevModelEnabled = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_MODEL_ENABLED");
        var prevLongwangEnabled = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_ENABLED");
        var prevLongwangConsoleUrl = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_CONSOLE_URL");
        var prevLongwangBaseUrl = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_BASE_URL");
        var prevLongwangProviderId = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_PROVIDER_ID");
        var prevLongwangModelId = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODEL_ID");
        var prevLongwangModelName = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODEL_NAME");
        var prevLongwangModelsManifestEnabled = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_ENABLED");
        var prevLongwangModelsManifestUrl = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_URL");
        var prevLongwangModelsManifestPath = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_PATH");
        var prevLongwangModelsManifestTimeout = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_DISTRO", "EnvDistro");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", "9876");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS", "true");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_OSS_ENABLED", "true");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_OSS_MANIFEST_URL", "https://oss.example.test/openclaw/dependencies.json");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_OSS_ALLOW_OFFICIAL_FALLBACK", "false");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_MODEL_ENABLED", "false");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_ENABLED", "false");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_CONSOLE_URL", "https://console.example.test");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_BASE_URL", "https://api.example.test/v1");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_PROVIDER_ID", "custom-longwang");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODEL_ID", "qwen-test");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODEL_NAME", "Qwen Test");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_ENABLED", "false");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_URL", "https://models.example.test/longwang.json");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_PATH", @"C:\models\longwang.json");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_TIMEOUT_SECONDS", "12");

            var config = SetupConfig.FromEnvironment();
            Assert.Equal("EnvDistro", config.DistroName);
            Assert.Equal(9876, config.GatewayPort);
            Assert.True(config.Headless);
            Assert.True(config.Oss.Enabled);
            Assert.Equal("https://oss.example.test/openclaw/dependencies.json", config.Oss.ManifestUrl);
            Assert.False(config.Oss.AllowOfficialFallback);
            Assert.False(config.ModelSetup.Enabled);
            Assert.False(config.ModelSetup.UseLongwang);
            Assert.Equal("https://console.example.test", config.ModelSetup.ConsoleUrl);
            Assert.Equal("https://api.example.test/v1", config.ModelSetup.BaseUrl);
            Assert.Equal("custom-longwang", config.ModelSetup.ProviderId);
            Assert.Equal("qwen-test", config.ModelSetup.ModelId);
            Assert.Equal("Qwen Test", config.ModelSetup.ModelName);
            Assert.False(config.ModelSetup.ModelsManifestEnabled);
            Assert.Equal("https://models.example.test/longwang.json", config.ModelSetup.ModelsManifestUrl);
            Assert.Equal(@"C:\models\longwang.json", config.ModelSetup.ModelsManifestPath);
            Assert.Equal(12, config.ModelSetup.ModelsManifestTimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_DISTRO", prevDistro);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", prevPort);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS", prevHeadless);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_OSS_ENABLED", prevOssEnabled);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_OSS_MANIFEST_URL", prevOssManifestUrl);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_OSS_ALLOW_OFFICIAL_FALLBACK", prevOssFallback);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_MODEL_ENABLED", prevModelEnabled);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_ENABLED", prevLongwangEnabled);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_CONSOLE_URL", prevLongwangConsoleUrl);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_BASE_URL", prevLongwangBaseUrl);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_PROVIDER_ID", prevLongwangProviderId);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODEL_ID", prevLongwangModelId);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODEL_NAME", prevLongwangModelName);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_ENABLED", prevLongwangModelsManifestEnabled);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_URL", prevLongwangModelsManifestUrl);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_PATH", prevLongwangModelsManifestPath);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_LONGWANG_MODELS_MANIFEST_TIMEOUT_SECONDS", prevLongwangModelsManifestTimeout);
        }
    }

    [Fact]
    public async Task LongwangModelManifest_AppliesLocalManifestAndKeepsConfiguredDefault()
    {
        var manifestPath = Path.Combine(_tempDir, "longwang-models.json");
        File.WriteAllText(manifestPath, """
        {
          "schemaVersion": 1,
          "version": "test-20260617",
          "defaultModelId": "future-model",
          "models": [
            {
              "id": "future-model",
              "name": "Future Model",
              "reasoning": true,
              "input": ["TEXT", "image", "image", "file"],
              "contextWindow": 123456,
              "contextTokens": 120000,
              "maxTokens": 32000,
              "supportsTools": true,
              "supportsUsageInStreaming": true
            },
            {
              "id": "qwen3.7-plus",
              "name": "Qwen Manifest Plus",
              "reasoning": true,
              "input": ["text"],
              "contextWindow": 1000000,
              "contextTokens": 960000,
              "maxTokens": 65536,
              "thinkingFormat": "qwen",
              "supportsTools": true,
              "supportsUsageInStreaming": true
            }
          ]
        }
        """);

        var config = new SetupConfig();
        config.ModelSetup.ModelsManifestPath = manifestPath;
        using var logger = new SetupLogger(filePath: null);

        var result = await LongwangModelManifestResolver.TryApplyAsync(config.ModelSetup, logger, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("manifest-path", result.Source);
        Assert.Equal("test-20260617", result.Version);
        Assert.Equal(2, result.ModelCount);
        Assert.Equal("qwen3.7-plus", config.ModelSetup.ModelId);
        Assert.Equal("Qwen Manifest Plus", config.ModelSetup.ModelName);

        var future = Assert.Single(config.ModelSetup.EffectiveModels, model => model.Id == "future-model");
        Assert.Equal(["text", "image", "file"], future.Input);
    }

    [Fact]
    public async Task LongwangModelManifest_UnavailableManifestKeepsBundledModels()
    {
        var config = new SetupConfig();
        var bundledCount = config.ModelSetup.EffectiveModels.Count;
        config.ModelSetup.ModelsManifestPath = Path.Combine(_tempDir, "missing.json");
        using var logger = new SetupLogger(filePath: null);

        var result = await LongwangModelManifestResolver.TryApplyAsync(config.ModelSetup, logger, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("manifest-path", result.Source);
        Assert.Equal(bundledCount, config.ModelSetup.EffectiveModels.Count);
        Assert.Contains(config.ModelSetup.EffectiveModels, model => model.Id == "glm-5v-turbo");
    }

    [Fact]
    public void FromEnvironment_InvalidPort_KeepsDefault()
    {
        var prevPort = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PORT");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", "notanumber");
            var config = SetupConfig.FromEnvironment();
            Assert.Equal(18789, config.GatewayPort); // default
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", prevPort);
        }
    }

    [Fact]
    public void CapabilitiesConfig_DefaultsEnableExpectedCategories()
    {
        var caps = new CapabilitiesConfig();
        var enabled = caps.GetEnabledCapabilities();
        var categories = enabled.Select(c => c.Category).ToList();

        Assert.Contains("system", categories);
        Assert.Contains("canvas", categories);
        Assert.Contains("screen", categories);
        Assert.Contains("device", categories);
        Assert.Contains("tts", categories);
        Assert.Contains("stt", categories);
    }

    [Fact]
    public void CapabilitiesConfig_DefaultOrderMatchesTrayRegistrationOrder()
    {
        var caps = new CapabilitiesConfig();

        Assert.Equal(
            ["system", "canvas", "screen", "camera", "location", "tts", "stt", "device", "browser"],
            caps.GetEnabledCapabilities().Select(c => c.Category).ToArray());
    }

    [Fact]
    public void CapabilitiesConfig_GetEnabledCommandIds_FlattensEnabledCapabilities()
    {
        var caps = new CapabilitiesConfig
        {
            Camera = false,
            Stt = false
        };

        var commands = caps.GetEnabledCommandIds();

        Assert.Contains("system.notify", commands);
        Assert.Contains("tts.speak", commands);
        Assert.DoesNotContain("camera.snap", commands);
        Assert.DoesNotContain("stt.listen", commands);
        Assert.Equal(commands.Count, commands.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(commands.Order(StringComparer.OrdinalIgnoreCase), commands);
    }

    [Fact]
    public void CapabilitiesConfig_DisabledCategory_NotInList()
    {
        var caps = new CapabilitiesConfig { System = false, Canvas = false };
        var enabled = caps.GetEnabledCapabilities();
        var categories = enabled.Select(c => c.Category).ToList();

        Assert.DoesNotContain("system", categories);
        Assert.DoesNotContain("canvas", categories);
        Assert.Contains("screen", categories);
    }

    [Fact]
    public void TraySettingsConfig_MergesIntoFile_OverwritesSetupKeysAndPreservesUnknownKeys()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"CustomKey": "custom_value", "EnableNodeMode": false, "AutoStart": true, "NodeCameraEnabled": false}""");

        var traySettings = new TraySettingsConfig { EnableNodeMode = true, AutoStart = false };
        traySettings.MergeIntoSettingsFile(settingsPath);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.False(result.RootElement.GetProperty("AutoStart").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
        Assert.Equal("custom_value", result.RootElement.GetProperty("CustomKey").GetString());
    }

    [Fact]
    public void TraySettingsConfig_CorruptExistingFile_BacksUpAndThrows()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, "{not json");

        var ex = Assert.Throws<InvalidDataException>(() => new TraySettingsConfig().MergeIntoSettingsFile(settingsPath));

        Assert.Contains("settings.json is corrupt", ex.Message);
        Assert.Equal("{not json", File.ReadAllText(settingsPath));
        Assert.Single(Directory.EnumerateFiles(_tempDir, "settings.json.corrupt-*.bak"));
    }

    [Fact]
    public void TraySettingsConfig_CreatesNewFile_WhenMissing()
    {
        var settingsPath = Path.Combine(_tempDir, "newsettings", "settings.json");
        var traySettings = new TraySettingsConfig();
        traySettings.MergeIntoSettingsFile(settingsPath);

        Assert.True(File.Exists(settingsPath));
        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeTtsEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeSttEnabled").GetBoolean());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrayArtifactCleanup_ResetOnboardingSettings_PreservesNodeSettings_WhenGatewaysRemain()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"GatewayUrl": "ws://localhost:18789", "EnableNodeMode": true, "AutoStart": true}""");

        TrayArtifactCleanup.ResetOnboardingSettings(_tempDir, new SetupLogger(filePath: null), preserveNodeSettings: true);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(result.RootElement.TryGetProperty("GatewayUrl", out _));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(result.RootElement.GetProperty("AutoStart").GetBoolean());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrayArtifactCleanup_ResetOnboardingSettings_DisablesNodeSettings_WhenNoGatewaysRemain()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"GatewayUrl": "ws://localhost:18789", "EnableNodeMode": true, "AutoStart": true}""");

        TrayArtifactCleanup.ResetOnboardingSettings(_tempDir, new SetupLogger(filePath: null), preserveNodeSettings: false);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(result.RootElement.TryGetProperty("GatewayUrl", out _));
        Assert.False(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.False(result.RootElement.GetProperty("AutoStart").GetBoolean());
    }

    [Fact]
    public void WslConfig_Defaults()
    {
        var wsl = new WslConfig();
        Assert.Equal("openclaw", wsl.User);
        Assert.True(wsl.Systemd);
        Assert.False(wsl.Interop);
    }

    [Fact]
    public void PairingConfig_Defaults()
    {
        var pairing = new PairingConfig();
        Assert.Equal(60, pairing.TimeoutSeconds);
    }

    [Fact]
    public void StepResult_Ok_IsSuccess()
    {
        Assert.True(StepResult.Ok().IsSuccess);
        Assert.True(StepResult.Ok("msg").IsSuccess);
    }

    [Fact]
    public void StepResult_Skip_IsSuccess()
    {
        Assert.True(StepResult.Skip("reason").IsSuccess);
    }

    [Fact]
    public void StepResult_Fail_IsNotSuccess()
    {
        Assert.False(StepResult.Fail("err").IsSuccess);
    }

    [Fact]
    public void StepResult_Terminal_IsNotSuccess()
    {
        Assert.False(StepResult.Terminal("fatal").IsSuccess);
        Assert.Equal(StepOutcome.FailedTerminal, StepResult.Terminal("fatal").Outcome);
    }

    [Fact]
    public void StepResult_RebootRequired_IsNotSuccess()
    {
        Assert.False(StepResult.RebootRequired("restart").IsSuccess);
        Assert.Equal(StepOutcome.RebootRequired, StepResult.RebootRequired("restart").Outcome);
    }

    [Fact]
    public void PipelineResult_ExitCodes()
    {
        Assert.Equal(0, new PipelineResult(PipelineOutcome.Success).ExitCode);
        Assert.Equal(1, new PipelineResult(PipelineOutcome.Failed).ExitCode);
        Assert.Equal(3, new PipelineResult(PipelineOutcome.Cancelled).ExitCode);
        Assert.Equal(4, new PipelineResult(PipelineOutcome.RebootRequired).ExitCode);
    }
}

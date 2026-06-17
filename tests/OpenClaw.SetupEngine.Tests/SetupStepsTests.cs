using OpenClaw.Connection;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class SetupStepsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localTempDir;
    private readonly string? _prevDataDir;
    private readonly string? _prevLocalDataDir;

    public SetupStepsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"steps-test-{Guid.NewGuid():N}");
        _localTempDir = Path.Combine(Path.GetTempPath(), $"steps-local-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_localTempDir);
        _prevDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        _prevLocalDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _tempDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _localTempDir);
    }

    public void Dispose()
    {
        OssDependencyResolver.AssetDownloaderOverride = null;
        OssDependencyResolver.AssetFileDownloaderOverride = null;
        PreflightWslStep.EnableWslWindowsFeaturesOverride = null;
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _prevDataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _prevLocalDataDir);
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_localTempDir, recursive: true); } catch { }
    }

    private SetupContext CreateContext(SetupConfig? config = null, ICommandRunner? commands = null)
    {
        var cfg = config ?? new SetupConfig { CleanBeforeRun = true };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var journal = new TransactionJournal(filePath: null);
        return new SetupContext(cfg, logger, journal, commands ?? new CommandRunner(logger), CancellationToken.None);
    }

    // ─── CleanupStaleGatewayStep: Preserve non-local records ───

    [Fact]
    public async Task CleanupStaleGateway_RemovesLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a local gateway record
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "local-gw",
            Url = gatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
            SshTunnel = null,
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.Null(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesSshTunneledRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a gateway record with SSH tunnel (remote gateway using localhost)
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "tunneled-gw",
            Url = gatewayUrl,
            IsLocal = true,
            SshTunnel = new SshTunnelConfig("user", "remote.host", 18789, 18789),
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was NOT removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesNonLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a non-local gateway record
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "remote-gw",
            Url = gatewayUrl,
            IsLocal = false,
            SshTunnel = null,
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was NOT removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_DeletesIdentityDirectoryForLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "local-gw-with-identity",
            Url = gatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
        });
        registry.Save();

        // Create an identity directory
        var identityDir = registry.GetIdentityDirectory("local-gw-with-identity");
        Directory.CreateDirectory(identityDir);
        File.WriteAllText(Path.Combine(identityDir, "device-key.json"), "{}");

        var step = new CleanupStaleGatewayStep();
        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(Directory.Exists(identityDir));
    }

    [Fact]
    public async Task CleanupStaleGateway_SkippedWhenCleanBeforeRunFalse()
    {
        var ctx = CreateContext(new SetupConfig { CleanBeforeRun = false });

        var step = new CleanupStaleGatewayStep();
        Assert.True(step.CanSkip(ctx));
    }

    // ─── InstallCliStep: URL validation and quoting ───

    [Fact]
    public async Task PreflightPort_Loopback_SucceedsForAvailablePort()
    {
        var port = GetFreeTcpPort();
        var ctx = CreateContext(new SetupConfig { GatewayPort = port });

        var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task PreflightPort_Lan_FailsWhenAnyBindPortInUse()
    {
        var listener = new TcpListener(IPAddress.Any, 0)
        {
            ExclusiveAddressUse = true
        };
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ctx = CreateContext(new SetupConfig
            {
                GatewayPort = port,
                Gateway = new GatewayConfig { Bind = "lan" }
            });

            var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

            Assert.Equal(StepOutcome.Failed, result.Outcome);
            Assert.Contains("already in use", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task InstallCli_RejectsHttpUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "http://evil.com/install.sh" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_UsesDefaultWhenVersionMissing()
    {
        var command = InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli.sh", null);

        Assert.Equal("curl -fsSL --proto '=https' --tlsv1.2 'https://openclaw.ai/install-cli.sh' | bash", command);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_AppendsVersionWhenConfigured()
    {
        var command = InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli.sh", "2026.5.22");

        Assert.Equal("curl -fsSL --proto '=https' --tlsv1.2 'https://openclaw.ai/install-cli.sh' | bash -s -- --version '2026.5.22'", command);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_EscapesSingleQuotesInUrlAndVersion()
    {
        var command = InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli's.sh", "2026.5.22'a");

        Assert.Equal("curl -fsSL --proto '=https' --tlsv1.2 'https://openclaw.ai/install-cli'\\''s.sh' | bash -s -- --version '2026.5.22'\\''a'", command);
    }

    [Fact]
    public async Task PreflightWsl_FailsForUnsupportedDirectInstallVersion()
    {
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? Ok("WSL version: 2.3.0.0\n")
                : Ok());
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Update WSL", result.Message);
        Assert.Contains(WslInstallSupport.UpdateUrl, result.Message);
    }

    [Fact]
    public async Task PreflightWsl_UsesOssWslCoreWhenVersionCommandIsUnsupported()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "version": "test"
        }
        """);
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? new CommandResult(1, "", "Invalid command line option: --version", TimeSpan.Zero, TimedOut: false)
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        }, commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("wslCore.assets", result.Message);
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task CreateWslInstance_UsesDirectFreshInstallAndDoesNotExportBaseDistro()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--export"));
        Assert.DoesNotContain(commands.Calls, c =>
            c.Arguments is ["--terminate", "Ubuntu-24.04"] or ["--unregister", "Ubuntu-24.04"]);

        var installCall = Assert.Single(commands.Calls, c => c.Arguments.Contains("--install"));
        Assert.Contains("--distribution", installCall.Arguments);
        Assert.Contains("Ubuntu-24.04", installCall.Arguments);
        Assert.Contains("--name", installCall.Arguments);
        Assert.Contains("OpenClawGateway", installCall.Arguments);
        Assert.Contains("--location", installCall.Arguments);
        Assert.Contains(Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway"), installCall.Arguments);
        Assert.Contains("--web-download", installCall.Arguments);
    }

    [Fact]
    public async Task CreateWslInstance_UsesOssRootfsImportWhenManifestProvidesUbuntuRootfs()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        var rootfsBytes = Encoding.UTF8.GetBytes("rootfs tarball test payload");
        var rootfsSha256 = Convert.ToHexString(SHA256.HashData(rootfsBytes)).ToLowerInvariant();
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "version": "test",
            "ubuntuWslRootfs": {
                "url": "https://oss.example.test/openclaw/wsl/ubuntu.rootfs.tar.gz",
                "sha256": "__SHA256__",
                "size": __SIZE__
            }
        }
        """
            .Replace("__SHA256__", rootfsSha256, StringComparison.Ordinal)
            .Replace("__SIZE__", rootfsBytes.Length.ToString(), StringComparison.Ordinal));

        var imported = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(imported ? "OpenClawGateway\n" : "");
            if (args.Contains("--import"))
            {
                imported = true;
                return Ok("Importing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        }, commands);

        OssDependencyResolver.AssetFileDownloaderOverride = (uri, outputPath, _) =>
        {
            Assert.Equal("https://oss.example.test/openclaw/wsl/ubuntu.rootfs.tar.gz", uri.ToString());
            File.WriteAllBytes(outputPath, rootfsBytes);
            return Task.CompletedTask;
        };

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
        var importCall = Assert.Single(commands.Calls, c => c.Arguments.Contains("--import"));
        Assert.Contains("OpenClawGateway", importCall.Arguments);
        Assert.Contains(Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway"), importCall.Arguments);
        Assert.Contains("--version", importCall.Arguments);
        Assert.Contains("2", importCall.Arguments);
        Assert.Contains(importCall.Arguments, arg => arg.EndsWith("ubuntu.rootfs.tar.gz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateWslInstance_FailsWhenOssRootfsMissingAndFallbackDisabled()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "version": "test"
        }
        """);
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        }, commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("ubuntuWslRootfs.url", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--import"));
    }

    [Fact]
    public async Task CreateWslInstance_TranslatesHcsServiceNotAvailableInstallFailure()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok("");
            if (args.Contains("--install"))
                return new CommandResult(
                    -1,
                    "",
                    "Wsl/Service/RegisterDistro/CreateVm/HCS/HCS_E_SERVICE_NOT_AVAILABLE",
                    TimeSpan.Zero,
                    TimedOut: false);

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("虚拟机平台", result.Message);
        Assert.DoesNotContain("HCS_E_SERVICE_NOT_AVAILABLE", result.Message);
    }

    [Fact]
    public async Task ResolveWslCoreMsi_DownloadsArchitectureAssetFromOssManifest()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        var msiBytes = Encoding.UTF8.GetBytes("wsl msi test payload");
        var msiSha256 = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "version": "test",
            "wslCore": {
                "version": "2.7.3",
                "assets": {
                    "x64": {
                        "url": "https://oss.example.test/openclaw/wsl/core/wsl.x64.msi",
                        "sha256": "__SHA256__",
                        "size": __SIZE__
                    }
                }
            }
        }
        """
            .Replace("__SHA256__", msiSha256, StringComparison.Ordinal)
            .Replace("__SIZE__", msiBytes.Length.ToString(), StringComparison.Ordinal));
        var config = new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        };

        OssDependencyResolver.AssetFileDownloaderOverride = (uri, outputPath, _) =>
        {
            Assert.Equal("https://oss.example.test/openclaw/wsl/core/wsl.x64.msi", uri.ToString());
            File.WriteAllBytes(outputPath, msiBytes);
            return Task.CompletedTask;
        };

        var resolution = await OssDependencyResolver.ResolveWslCoreMsiAsync(
            config,
            _localTempDir,
            new SetupLogger(filePath: null, LogLevel.Trace),
            Architecture.X64,
            CancellationToken.None);

        Assert.True(resolution.Success, resolution.ErrorMessage);
        Assert.Equal("oss-manifest", resolution.Source);
        Assert.NotNull(resolution.LocalPath);
        Assert.True(File.Exists(resolution.LocalPath));
        Assert.EndsWith("wsl.x64.msi", resolution.LocalPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightWsl_FailsWhenOssWslCoreMissingAndFallbackDisabled()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "version": "test"
        }
        """);
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? new CommandResult(1, "", "WSL is not installed. Visit https://aka.ms/wslinstall", TimeSpan.Zero, TimedOut: false)
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        }, commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("wslCore.assets", result.Message);
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupAvoidsGlobalShutdownWhenUnregisterSucceeds()
    {
        var listCalls = 0;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return Ok(listCalls == 1 ? "" : "OpenClawGateway\n");
            }
            if (args.Contains("--install"))
                return Fail("download failed");
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]))
                return Ok();
            if (args.SequenceEqual(["--unregister", "OpenClawGateway"]))
                return Ok();

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupSkipsInstallPathDeleteWhenDistroStateIsUnknown()
    {
        var listCalls = 0;
        var installPath = "";
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return listCalls == 1 ? Ok("") : Fail("list failed");
            }
            if (args.Contains("--install"))
            {
                Directory.CreateDirectory(installPath);
                File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "partial");
                return Fail("download failed");
            }
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]))
                return Fail("terminate unavailable");
            if (args.SequenceEqual(["--unregister", "OpenClawGateway"]))
                return Fail("unregister unavailable");
            if (args.SequenceEqual(["--shutdown"]))
                return Ok();

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.Contains("could not confirm whether distro 'OpenClawGateway' is still registered", result.Message);
        Assert.Contains("skipped deleting app-owned install path", result.Message);
        Assert.True(File.Exists(Path.Combine(installPath, "ext4.vhdx")));
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupDeletesInstallPathWhenListFailsButDistroIsAlreadyGone()
    {
        var listCalls = 0;
        var installPath = "";
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return listCalls == 1 ? Ok("") : Fail("list failed");
            }
            if (args.Contains("--install"))
            {
                Directory.CreateDirectory(installPath);
                File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "partial");
                return Fail("download failed");
            }
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]) ||
                args.SequenceEqual(["--unregister", "OpenClawGateway"]))
            {
                return Fail("There is no distribution with the supplied name.");
            }

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.DoesNotContain("Partial app-owned distro cleanup also failed", result.Message);
        Assert.False(Directory.Exists(installPath));
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
    }

    [Fact]
    public async Task CreateWslInstance_FailsWhenTargetDistroStillExists()
    {
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("OpenClawGateway\n")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("still exists after cleanup", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task CreateWslInstance_FailsWhenInstallDirectoryIsDirty()
    {
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);
        var installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "stale");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("still contains files after cleanup", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task CreateWslInstance_RemovesStaleFileAtInstallPathBeforeInstalling()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        var installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
        File.WriteAllText(installPath, "stale");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(File.Exists(installPath));
        Assert.Contains(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public void WslInstallSupport_ParsesVersionAndVerboseDistroList()
    {
        Assert.True(WslInstallSupport.TryParseWslVersion("WSL version: 2.7.3.0", out var version));
        Assert.Equal(new Version(2, 7, 3, 0), version);
        Assert.True(WslInstallSupport.SupportsDirectNamedInstall(version));

        Assert.True(WslInstallSupport.TryGetDistroVersion(
            "  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n",
            "OpenClawGateway",
            out var distroVersion));
        Assert.Equal(2, distroVersion);
    }

    // Regression: wsl.exe emits UTF-16LE on some Windows builds, and localized
    // Windows changes the human-readable label around the stable WSL product token.
    [Theory]
    [InlineData("WSL version: 2.7.3.0", "2.7.3.0")]                       // English
    [InlineData("WSL-Version: 2.7.7.0", "2.7.7.0")]                       // German / NUL-stripped UTF-16
    [InlineData("WSL-Version: 2.7.7.0\nKernelversion: 6.18.26.1-1\nWSLg-Version: 1.0.73.2\nWindows-Version: 10.0.26300.8553", "2.7.7.0")]
    [InlineData("Versión de WSL: 2.7.3.0", "2.7.3.0")]                    // Spanish
    [InlineData("Versión de WSL: 2.7.3.0\nKernel: 5.15.0.1", "2.7.3.0")]  // Spanish with trailing lines
    [InlineData("WSL バージョン: 2.7.8.0", "2.7.8.0")]                    // Japanese-style label
    [InlineData("WSL版本: 2.7.9.0", "2.7.9.0")]                          // No separator after WSL
    public void WslInstallSupport_TryParseWslVersion_HandlesLocalizedAndHyphenatedLabels(string output, string expectedVersion)
    {
        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version),
            $"Expected TryParseWslVersion to succeed for: {output}");
        Assert.Equal(Version.Parse(expectedVersion), version);
        Assert.True(WslInstallSupport.SupportsDirectNamedInstall(version),
            $"Expected parsed version {version} to satisfy minimum install requirement");
    }

    // Mirrors microsoft/WSL localization/strings/*/Resources.resw MessagePackageVersions.
    [Theory]
    [InlineData("cs-CZ", "Verze WSL: 2.7.3.0")]
    [InlineData("da-DK", "WSL-version: 2.7.3.0")]
    [InlineData("de-DE", "WSL-Version: 2.7.3.0")]
    [InlineData("en-GB", "WSL version: 2.7.3.0")]
    [InlineData("en-US", "WSL version: 2.7.3.0")]
    [InlineData("es-ES", "Versión de WSL: 2.7.3.0")]
    [InlineData("fi-FI", "WSL-versio: 2.7.3.0")]
    [InlineData("fr-FR", "Version WSL : 2.7.3.0")]
    [InlineData("hu-HU", "WSL-verzió: 2.7.3.0")]
    [InlineData("it-IT", "Versione WSL: 2.7.3.0")]
    [InlineData("ja-JP", "WSL バージョン: 2.7.3.0")]
    [InlineData("ko-KR", "WSL 버전: 2.7.3.0")]
    [InlineData("nb-NO", "WSL-versjon: 2.7.3.0")]
    [InlineData("nl-NL", "WSL-versie: 2.7.3.0")]
    [InlineData("pl-PL", "Wersja podsystemu WSL: 2.7.3.0")]
    [InlineData("pt-BR", "Versão do WSL: 2.7.3.0")]
    [InlineData("pt-PT", "Versão WSL: 2.7.3.0")]
    [InlineData("ru-RU", "Версия WSL: 2.7.3.0")]
    [InlineData("sv-SE", "WSL-version: 2.7.3.0")]
    [InlineData("tr-TR", "WSL sürümü: 2.7.3.0")]
    [InlineData("zh-CN", "WSL 版本: 2.7.3.0")]
    [InlineData("zh-TW", "WSL 版本： 2.7.3.0")]
    public void WslInstallSupport_TryParseWslVersion_HandlesMicrosoftLocalizedPackageVersionLabels(
        string locale,
        string output)
    {
        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version),
            $"Expected TryParseWslVersion to succeed for {locale}: {output}");
        Assert.Equal(new Version(2, 7, 3, 0), version);
    }

    [Theory]
    [InlineData("WSL-Version: 2.7.7.0", "2.7.7.0")]
    [InlineData("Versión de WSL: 2.7.3.0", "2.7.3.0")]
    public void WslInstallSupport_TryParseWslVersion_NulStrippedUtf16_ParsesCorrectVersion(string raw, string expectedVersion)
    {
        // Simulate UTF-16LE NUL-byte injection then NUL-stripping.
        var utf16Encoded = string.Join("\0", raw.ToCharArray()) + "\0";
        var stripped = utf16Encoded.Replace("\0", "");
        Assert.True(WslInstallSupport.TryParseWslVersion(stripped, out var version),
            $"Expected TryParseWslVersion to succeed for NUL-stripped: {raw}");
        Assert.Equal(Version.Parse(expectedVersion), version);
    }

    [Fact]
    public void WslInstallSupport_TryParseWslVersion_IgnoresAdjacentWslAndWindowsVersionLines()
    {
        var output = "WSLg-Version: 1.0.73.2\n"
            + "Windows-Version: 10.0.26300.8553\n"
            + "Kernelversion: 6.18.26.1-1\n"
            + "WSL-Version: 2.7.7.0\n";

        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version));
        Assert.Equal(new Version(2, 7, 7, 0), version);
    }

    [Fact]
    public void WslInstallSupport_TryParseWslVersion_FailsWhenOnlyAdjacentComponentVersionsArePresent()
    {
        var output = "WSLg-Version: 1.0.73.2\n"
            + "Windows-Version: 10.0.26300.8553\n"
            + "Kernelversion: 6.18.26.1-1\n";

        Assert.False(WslInstallSupport.TryParseWslVersion(output, out _));
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsFirmwareVirtualizationOff()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL2 is unable to start since virtualization is not enabled on this machine. "
            + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
            + "and virtualization is turned on in your computer's firmware settings.",
            Architecture.X64,
            out var message));
        Assert.Contains("BIOS/UEFI", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("硬件虚拟化", message);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_UsesArm64WordingOnArm64()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL2 is unable to start since virtualization is not enabled on this machine. "
            + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
            + "and virtualization is turned on in your computer's firmware settings.",
            Architecture.Arm64,
            out var message));
        Assert.Contains("ARM64", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UEFI", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("虚拟化", message);
        // Must not name x86-specific extensions on ARM64.
        Assert.DoesNotContain("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AMD-V", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SVM", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsCanonical0x80370102Error()
    {
        // This is the actual error wsl.exe emits on modern Windows builds when
        // the Virtual Machine Platform / Hyper-V feature is disabled.
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL 2 requires an update to its kernel component.\n"
            + "For information please visit https://aka.ms/wsl2kernel\n"
            + "Error: 0x80370102 The virtual machine could not be started because a "
            + "required feature is not installed.",
            out var message));
        Assert.Contains("虚拟机平台", message);
        Assert.Contains("DISM", message);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsHcsServiceNotAvailableSymbol()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "Wsl/Service/RegisterDistro/CreateVm/HCS/HCS_E_SERVICE_NOT_AVAILABLE",
            out var message));

        Assert.Contains("虚拟机平台", message);
        Assert.Contains("重启 Windows", message);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsNoDistributionInstallHint()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "请运行 wsl.exe --install --no-distribution 后重试。https://aka.ms/enablevirtualization",
            out var message));

        Assert.Contains("适用于 Linux 的 Windows 子系统", message);
        Assert.Contains("DISM", message);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsNulSeparatedStatusHint()
    {
        var output = string.Join('\0', "wsl.exe --install --no-distribution".Select(c => c.ToString()));

        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(output, out var message));

        Assert.Contains("虚拟机平台", message);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_ReturnsFalseForHealthyStatus()
    {
        Assert.False(WslInstallSupport.TryGetEnvironmentIssue(
            "Default Distribution: OpenClawGateway\nDefault Version: 2\n",
            out var message));
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public async Task PreflightWsl_FailsTerminalWhenVirtualizationDisabledInFirmware()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok(
                    "WSL2 is unable to start since virtualization is not enabled on this machine. "
                    + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
                    + "and virtualization is turned on in your computer's firmware settings.");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("硬件虚拟化", result.Message);
        // Don't assert on "BIOS" / "UEFI" here -- the wording flexes by host
        // CPU architecture (this test runs on either x64 or Arm64 dev boxes).
    }

    [Fact]
    public async Task PreflightWsl_ReturnsRebootRequiredWhenWslFeaturesWereEnabled()
    {
        var featureEnableAttempted = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok(
                    "WSL 2 requires an update to its kernel component.\n"
                    + "Error: 0x80370102 The virtual machine could not be started because a "
                    + "required feature is not installed.");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);
        PreflightWslStep.EnableWslWindowsFeaturesOverride = (_, message, _) =>
        {
            featureEnableAttempted = true;
            Assert.Contains("虚拟机平台", message);
            return Task.FromResult(StepResult.RebootRequired("已启用 WSL Windows 功能，请重启 Windows 后继续安装。"));
        };

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.RebootRequired, result.Outcome);
        Assert.True(featureEnableAttempted);
        Assert.Contains("重启 Windows", result.Message);
    }

    [Fact]
    public async Task PreflightWsl_SucceedsWhenStatusOutputIsHealthy()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok("Default Distribution: OpenClawGateway\nDefault Version: 2\n");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("bad;user")]
    [InlineData("BadUser")]
    [InlineData("bad user")]
    [InlineData("bad$user")]
    public async Task ConfigureWsl_RejectsInvalidLinuxUserName(string user)
    {
        var ctx = CreateContext();
        ctx.Config.Wsl.User = user;
        ctx.DistroName = "test-distro";

        var step = new ConfigureWslInstanceStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid WSL user", result.Message);
    }

    [Fact]
    public void WslConfig_AcceptsValidLinuxUserName()
    {
        Assert.True(WslConfig.IsValidLinuxUserName("openclaw"));
        Assert.True(WslConfig.IsValidLinuxUserName("_openclaw"));
        Assert.True(WslConfig.IsValidLinuxUserName("openclaw-user_1"));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesUnmarkedLocalhostRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "external-localhost",
            Url = gatewayUrl,
            IsLocal = true,
            SshTunnel = null,
        });
        registry.Save();

        var result = await new CleanupStaleGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.GetById("external-localhost"));
    }

    [Fact]
    public async Task InstallCli_RejectsInvalidUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "not-a-url" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public async Task InstallCli_UsesOssManifestInstallUrlWhenEnabled()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        var scriptBytes = Encoding.UTF8.GetBytes("#!/usr/bin/env bash\necho installing openclaw\n");
        var scriptSha256 = Convert.ToHexString(SHA256.HashData(scriptBytes)).ToLowerInvariant();
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "version": "test",
            "installCli": {
                "url": "https://oss.example.test/openclaw/install-cli.sh",
                "sha256": "__SHA256__",
                "size": __SIZE__,
                "environment": {
                    "OPENCLAW_NODE_DIST_BASE_URL": "https://oss.example.test/node/dist",
                    "NPM_CONFIG_REGISTRY": "https://registry.example.test/"
                }
            }
        }
        """
            .Replace("__SHA256__", scriptSha256, StringComparison.Ordinal)
            .Replace("__SIZE__", scriptBytes.Length.ToString(), StringComparison.Ordinal));

        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => Ok("openclaw 2026.6.1"));
        var ctx = CreateContext(new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        }, commands);

        OssDependencyResolver.AssetDownloaderOverride = (uri, _) =>
        {
            Assert.Equal("https://oss.example.test/openclaw/install-cli.sh", uri.ToString());
            return Task.FromResult(scriptBytes);
        };
        try
        {
            var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotEmpty(commands.WslCalls);
            Assert.Equal("bash -s", commands.WslCalls[0].Command);
            Assert.Equal(Encoding.UTF8.GetString(scriptBytes), commands.WslCalls[0].StdinInput);
            Assert.NotNull(commands.WslCalls[0].Environment);
            Assert.Equal(
                "https://oss.example.test/node/dist",
                commands.WslCalls[0].Environment!["OPENCLAW_NODE_DIST_BASE_URL"]);
            Assert.Equal(
                "https://registry.example.test/",
                commands.WslCalls[0].Environment!["NPM_CONFIG_REGISTRY"]);
            Assert.DoesNotContain("https://oss.example.test/openclaw/install-cli.sh", commands.WslCalls[0].Command);
        }
        finally
        {
            OssDependencyResolver.AssetDownloaderOverride = null;
        }
    }

    [Fact]
    public async Task InstallCli_KeepsExplicitGatewayUrlPriorityOverOssManifest()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "installCli": {
                "url": "https://oss.example.test/openclaw/install-cli.sh"
            }
        }
        """);

        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => Ok("openclaw 2026.6.1"));
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "https://custom.example.test/install.sh" },
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        }, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("https://custom.example.test/install.sh", commands.WslCalls[0].Command);
        Assert.DoesNotContain("https://oss.example.test/openclaw/install-cli.sh", commands.WslCalls[0].Command);
    }

    [Fact]
    public async Task InstallCli_FailsWhenOssScriptChecksumMismatchesAndFallbackDisabled()
    {
        var manifestPath = Path.Combine(_tempDir, "oss-dependencies.json");
        File.WriteAllText(manifestPath, """
        {
            "schemaVersion": 1,
            "installCli": {
                "url": "https://oss.example.test/openclaw/install-cli.sh",
                "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
            }
        }
        """);

        var ctx = CreateContext(new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = manifestPath,
                AllowOfficialFallback = false
            }
        }, new FakeCommandRunner(_ => Ok(), (_, _, _) => Ok()));

        OssDependencyResolver.AssetDownloaderOverride = (_, _) =>
            Task.FromResult(Encoding.UTF8.GetBytes("#!/usr/bin/env bash\necho tampered\n"));
        try
        {
            var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

            Assert.Equal(StepOutcome.Failed, result.Outcome);
            Assert.Contains("SHA-256 mismatch", result.Message);
        }
        finally
        {
            OssDependencyResolver.AssetDownloaderOverride = null;
        }
    }

    [Fact]
    public async Task InstallCli_FailsWhenOssManifestInvalidAndFallbackDisabled()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Oss = new OssMirrorConfig
            {
                Enabled = true,
                ManifestPath = Path.Combine(_tempDir, "missing.json"),
                AllowOfficialFallback = false
            }
        }, new FakeCommandRunner(_ => Ok(), (_, _, _) => Ok()));

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("official fallback is disabled", result.Message);
    }

    [Theory]
    [InlineData("gateway.auth.token")]
    [InlineData("gateway_nodes-allowCommands")]
    [InlineData("a.b_c-1")]
    public void ConfigureGateway_AcceptsSafeExtraConfigKeys(string key)
    {
        Assert.True(ConfigureGatewayStep.IsSafeExtraConfigKey(key));
    }

    [Theory]
    [InlineData("bad key")]
    [InlineData("bad$key")]
    [InlineData("bad;key")]
    [InlineData("bad\nkey")]
    public void ConfigureGateway_RejectsUnsafeExtraConfigKeys(string key)
    {
        Assert.False(ConfigureGatewayStep.IsSafeExtraConfigKey(key));
    }

    [Fact]
    public void ConfigureGateway_DefaultsToLoopbackNoAuth()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'");

        Assert.Contains("openclaw config set gateway.auth.mode none", commands);
        Assert.Contains("openclaw config set gateway.auth.token \"$OPENCLAW_GATEWAY_TOKEN\"", commands);
    }

    [Fact]
    public void ConfigureGateway_AddsDevicePairPublicUrlForLoopbackGateway()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.config.publicUrl 'http://127.0.0.1:18789'",
            commands);
    }

    // Issue: device-pair plugin must be enabled, not just configured. Otherwise
    // OAuth providers (Codex, etc.) hang at scope-upgrade and never emit auth URLs.
    [Fact]
    public void ConfigureGateway_EnablesDevicePairPluginForLoopbackGateway()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_EnablesDevicePairPluginWhenPublicUrlOverridden()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "lan",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairPublicUrlKey] = "https://gateway.example.test",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_DoesNotEnableDevicePairWhenNoPublicUrlAvailable()
    {
        // LAN bind with no operator-supplied publicUrl: we don't know where the plugin
        // would be reachable, so don't enable it; preserves the prior behavior.
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "lan" },
            18789,
            "'[]'");

        Assert.DoesNotContain(
            "openclaw config set plugins.entries.device-pair.enabled",
            commands);
    }

    [Fact]
    public void ConfigureGateway_RespectsExplicitDevicePairEnabledOverride()
    {
        // If the operator explicitly sets the enabled flag via ExtraConfig, the
        // ExtraConfig loop writes it and we don't append a duplicate.
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairEnabledKey] = "false",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled 'false'",
            commands);
        Assert.DoesNotContain(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_DoesNotOverrideExplicitDevicePairPublicUrl()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairPublicUrlKey] = "https://gateway.example.test",
                },
            },
            18789,
            "'[]'");

        Assert.DoesNotContain("'http://127.0.0.1:18789'", commands);
        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.config.publicUrl 'https://gateway.example.test'",
            commands);
    }

    [Fact]
    public async Task ConfigureGateway_UsesExtendedTimeoutForWslConfig()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => Ok("GATEWAY_CONFIGURED"));
        var ctx = CreateContext(commands: commands);

        var result = await new ConfigureGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var wslCall = Assert.Single(commands.WslCalls);
        Assert.Equal(
            ConfigureGatewayStep.ComputeConfigurationTimeout(wslCall.Command),
            wslCall.Timeout);
        Assert.True(wslCall.Timeout >= ConfigureGatewayStep.MinConfigurationTimeout);
    }

    [Fact]
    public async Task ConfigureGateway_ReturnsTimeoutSpecificFailure()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, timeout) => new CommandResult(-1, "", "", timeout, TimedOut: true));
        var ctx = CreateContext(commands: commands);

        var result = await new ConfigureGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        var message = Assert.IsType<string>(result.Message);
        Assert.Contains("Gateway configuration timed out after", message);
        Assert.DoesNotContain("exit -1", message);
    }

    [Fact]
    public void ComputeConfigurationTimeout_ScalesWithConfigCommandCount()
    {
        // Each `openclaw config set` pays a cold Node start inside WSL. As more keys are
        // configured the budget must grow, otherwise the step silently regresses toward a
        // timeout (the failure mode the fixed 120s cap only partially closed).
        var fewCommands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "lan" },
            18789,
            "'[]'");
        var manyCommands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    ["gateway.extra.one"] = "1",
                    ["gateway.extra.two"] = "2",
                    ["gateway.extra.three"] = "3",
                    ["gateway.extra.four"] = "4",
                },
            },
            18789,
            "'[]'");

        var fewTimeout = ConfigureGatewayStep.ComputeConfigurationTimeout(fewCommands);
        var manyTimeout = ConfigureGatewayStep.ComputeConfigurationTimeout(manyCommands);

        Assert.True(
            manyTimeout > fewTimeout,
            $"Timeout should grow with config command count; few={fewTimeout}, many={manyTimeout}");
    }

    [Fact]
    public void ComputeConfigurationTimeout_NeverBelowFloor()
    {
        // A minimal config set must still receive the safety floor, never base + one.
        var timeout = ConfigureGatewayStep.ComputeConfigurationTimeout(
            "openclaw config set gateway.mode local");

        Assert.True(timeout >= ConfigureGatewayStep.MinConfigurationTimeout);
    }

    [Fact]
    public void ConfigureLongwangModel_BuildConfigCommands_WritesProviderAndDefaultWithoutSecret()
    {
        var model = new ModelSetupConfig
        {
            ApiKey = "sk-lw-v1-secret-for-test"
        };

        var commands = ConfigureLongwangModelStep.BuildConfigCommands(model);

        Assert.Contains("openclaw config set env.LONGWANG_API_KEY \"$LONGWANG_SETUP_API_KEY\" >/dev/null", commands);
        Assert.Contains("printf '%s\\n' \"$LONGWANG_SETUP_API_KEY\" | openclaw models auth paste-api-key --provider 'longwang-qwen' --profile-id 'longwang-qwen:manual' >/dev/null", commands);
        Assert.Contains("openclaw config set models.providers.longwang-qwen", commands);
        Assert.Contains("openclaw config set agents.defaults.models", commands);
        Assert.Contains("openclaw config set agents.defaults.model.primary 'longwang-qwen/qwen3.7-plus'", commands);
        Assert.Contains("openclaw models auth list --provider 'longwang-qwen' >/dev/null", commands);
        Assert.Contains("\"apiKey\":\"${LONGWANG_API_KEY}\"", commands);
        Assert.Contains("\"id\":\"qwen3.7-plus\"", commands);
        Assert.Contains("\"name\":\"Qwen3.7 Plus\"", commands);
        Assert.Contains("\"id\":\"qwen3.7-max\"", commands);
        Assert.Contains("\"name\":\"Qwen3.7 Max\"", commands);
        Assert.Contains("\"id\":\"deepseek-v4-pro\"", commands);
        Assert.Contains("\"thinkingFormat\":\"deepseek\"", commands);
        Assert.Contains("\"id\":\"kimi-k2.6\"", commands);
        Assert.Contains("\"id\":\"glm-5.2\"", commands);
        Assert.Contains("\"id\":\"glm-5.1\"", commands);
        Assert.Contains("\"id\":\"glm-5v-turbo\"", commands);
        Assert.Contains("\"input\":[\"text\",\"image\",\"video\"]", commands);
        Assert.DoesNotContain("\"file\"", commands);
        Assert.Contains("\"id\":\"minimax-m3\"", commands);
        Assert.Contains("\"longwang-qwen/qwen3.7-max\":{}", commands);
        Assert.Contains("\"longwang-qwen/deepseek-v4-pro\":{}", commands);
        Assert.Contains("\"longwang-qwen/glm-5v-turbo\":{}", commands);
        Assert.DoesNotContain("sk-lw-v1-secret-for-test", commands);
    }

    [Fact]
    public async Task ConfigureLongwangModel_PassesApiKeyViaWslEnvironment()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => Ok(ConfigureLongwangModelStep.SuccessMarker));
        var config = new SetupConfig
        {
            ModelSetup = new ModelSetupConfig
            {
                ApiKey = "sk-lw-v1-secret-for-test"
            }
        };
        var ctx = CreateContext(config, commands);

        var result = await new ConfigureLongwangModelStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var wslCall = Assert.Single(commands.WslCalls);
        Assert.NotNull(wslCall.Environment);
        Assert.Equal("sk-lw-v1-secret-for-test", wslCall.Environment![ConfigureLongwangModelStep.ApiKeyEnvironmentName]);
        Assert.DoesNotContain("sk-lw-v1-secret-for-test", wslCall.Command);
        Assert.True(wslCall.Timeout >= ConfigureLongwangModelStep.MinConfigurationTimeout);
    }

    [Fact]
    public async Task ConfigureLongwangModel_RequiresApiKey()
    {
        var ctx = CreateContext(new SetupConfig
        {
            ModelSetup = new ModelSetupConfig
            {
                UseLongwang = true,
                ApiKey = ""
            }
        }, new FakeCommandRunner(_ => Ok(), (_, _, _) => Ok()));

        var result = await new ConfigureLongwangModelStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("API key", result.Message);
    }

    [Theory]
    [InlineData("""{"bootstrapToken":"boot-token"}""", "boot-token", "bootstrapToken")]
    [InlineData("""{"setupCode":"setup-code"}""", "setup-code", "setupCode")]
    public void MintBootstrapToken_ReadsSupportedQrJsonShapes(string json, string expectedToken, string expectedSource)
    {
        var parsed = MintBootstrapTokenStep.TryReadBootstrapToken(json, out var token, out var source);

        Assert.True(parsed);
        Assert.Equal(expectedToken, token);
        Assert.Equal(expectedSource, source);
    }

    [Fact]
    public void MintBootstrapToken_RejectsQrJsonWithoutUsableBootstrapCredential()
    {
        var parsed = MintBootstrapTokenStep.TryReadBootstrapToken("""{"gatewayUrl":"ws://127.0.0.1:18789"}""", out var token, out var source);

        Assert.False(parsed);
        Assert.Null(token);
        Assert.Null(source);
    }

    [Fact]
    public async Task InstallCli_RejectsFtpUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "ftp://files.com/install.sh" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public void BuildReplacementSummary_NoExistingConfig_StatesNothingAffected()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: false,
            LocalGatewayId: null,
            LocalGatewayUrl: null,
            HasDistro: false,
            DistroName: null,
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("现有配置不会受到影响", summary);
    }

    [Fact]
    public void BuildReplacementSummary_LocalGatewayAndDistro_MentionsReplacement()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: true,
            DistroName: "OpenClaw",
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("WSL 发行版“OpenClaw”将被删除并重新创建", summary);
        Assert.Contains("本地网关记录将被替换", summary);
    }

    [Fact]
    public void BuildReplacementSummary_PreservedGateways_MentionsPreservation()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: false,
            DistroName: null,
            HasIdentityFiles: false,
            PreservedGatewayCount: 2,
            PreservedGatewayNames: ["Remote Gateway", "SSH Tunnel"]);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("不会受到影响", summary);
        Assert.Contains("Remote Gateway", summary);
        Assert.Contains("SSH Tunnel", summary);
    }

    [Fact]
    public void BuildReplacementSummary_IdentityFiles_MentionsRegeneration()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: false,
            DistroName: null,
            HasIdentityFiles: true,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("本地网关的设备身份文件将重新生成", summary);
    }

    [Fact]
    public void RedactTokens_RedactsThirtyTwoCharHexString()
    {
        const string token = "1234567890abcdef1234567890abcdef";

        var result = StartGatewayStep.RedactTokens(token);

        Assert.Equal("12345678…[REDACTED]", result);
    }

    [Fact]
    public void RedactTokens_DoesNotRedactShortHexString()
    {
        const string token = "1234567890abcdef1234567890abcde";

        var result = StartGatewayStep.RedactTokens(token);

        Assert.Equal(token, result);
    }

    [Fact]
    public void RedactTokens_LeavesNormalTextUnchanged()
    {
        const string text = "gateway started successfully";

        var result = StartGatewayStep.RedactTokens(text);

        Assert.Equal(text, result);
    }

    [Fact]
    public void RedactTokens_RedactsEmbeddedTokenOnly()
    {
        const string text = "token=1234567890abcdef1234567890abcdef status=ok";

        var result = StartGatewayStep.RedactTokens(text);

        Assert.Equal("token=12345678…[REDACTED] status=ok", result);
    }

    [Fact]
    public void TryGetExistingKeepalive_ReturnsFalseForCorruptMarker()
    {
        var markerPath = Path.Combine(_tempDir, "keepalive.json");
        File.WriteAllText(markerPath, "not json");

        var result = StartKeepaliveStep.TryGetExistingKeepalive(markerPath, "OpenClawGateway", out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void IsKeepaliveCommandLine_RequiresDistroAndSleepInfinity()
    {
        Assert.True(StartKeepaliveStep.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep infinity",
            "OpenClawGateway"));
        Assert.False(StartKeepaliveStep.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep 60",
            "OpenClawGateway"));
        Assert.False(StartKeepaliveStep.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OtherGateway -- sleep infinity",
            "OpenClawGateway"));
    }

    // ─── Bind validation ───

    [Fact]
    public async Task ConfigureGateway_RejectsInvalidBind()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { Bind = "0.0.0.0" }
        });
        ctx.DistroName = "test-distro";

        var step = new ConfigureGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid Gateway.Bind", result.Message);
    }

    [Theory]
    [InlineData("loopback")]
    [InlineData("lan")]
    public void ConfigureGateway_AcceptsValidBindValues(string bind)
    {
        var gw = new GatewayConfig { Bind = bind };
        Assert.True(gw.Bind is "loopback" or "lan");
    }

    // ─── Secure defaults ───

    [Fact]
    public void DefaultConfig_HasSecureDefaults()
    {
        var config = new SetupConfig();

        Assert.Equal("loopback", config.Gateway.Bind);
        Assert.True(config.Wsl.Systemd);
        Assert.False(config.Wsl.Interop);
        Assert.False(config.Wsl.AppendWindowsPath);
        Assert.False(config.Wsl.Automount);
        Assert.False(config.Wsl.MountFsTab);
    }

    [Fact]
    public void DefaultConfig_NoPairingScopeFields()
    {
        var props = typeof(PairingConfig).GetProperties();
        var scopeProps = props.Where(p => p.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(scopeProps);
    }

    private static CommandResult Ok(string stdout = "", string stderr = "")
        => new(0, stdout, stderr, TimeSpan.Zero, TimedOut: false);

    private static CommandResult Fail(string stderr = "")
        => new(1, "", stderr, TimeSpan.Zero, TimedOut: false);

    private sealed class FakeCommandRunner(
        Func<string[], CommandResult> run,
        Func<string, string, TimeSpan, CommandResult>? runInWsl = null) : ICommandRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];
        public List<(
            string DistroName,
            string Command,
            TimeSpan Timeout,
            IReadOnlyDictionary<string, string>? Environment,
            string? StdinInput)> WslCalls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(run(arguments));
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            string? stdinInput = null)
        {
            if (runInWsl == null)
                throw new NotSupportedException("RunInWslAsync is not expected in these tests.");

            WslCalls.Add((distroName, command, timeout, environment, stdinInput));
            return Task.FromResult(runInWsl(distroName, command, timeout));
        }
    }
}

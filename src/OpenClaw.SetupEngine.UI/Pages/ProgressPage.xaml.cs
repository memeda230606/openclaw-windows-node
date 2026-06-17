using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class ProgressPage : Page
{
    private SetupConfig? _config;
    private SetupPipeline? _pipeline;
    private SetupLogger? _logger;
    private CancellationTokenSource? _runCts;
    private readonly Dictionary<string, StepRow> _rows = new();
    private bool _logExpanded;
    private int _logLineCount;
    private bool _pipelineFinished;
    private const int MaxLogLines = 200;

    // Map pipeline step IDs to display groups (N:1)
    private static readonly (string GroupId, string DisplayName, string[] StepIds)[] StepGroups =
    [
        ("preflight", "检查系统", ["preflight-os", "preflight-wsl"]),
        ("cleanup", "移除现有网关", ["cleanup-distro", "cleanup-gateway"]),
        ("port", "检查网关端口", ["preflight-port"]),
        ("wsl-create", "安装全新的 WSL 网关", ["wsl-create"]),
        ("wsl-configure", "配置实例", ["wsl-configure", "validate-wsl-lockdown"]),
        ("install-cli", "安装 OpenClaw", ["install-cli"]),
        ("configure", "准备网关", ["configure-gateway", "install-service"]),
        ("start", "启动网关", ["start-gateway", "mint-token"]),
        ("pairing", "配对设备", ["pair-operator", "pair-node", "verify-e2e"]),
        ("finish", "完成安装", ["run-wizard", "start-keepalive"]),
    ];

    public ProgressPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPipeline();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        SubtitleText.Text = $"正在创建 {_config.DistroName} WSL 实例";

        BuildStepRows();
        StartPipeline();
    }

    private void BuildStepRows()
    {
        foreach (var (groupId, displayName, _) in StepGroups)
        {
            var row = new StepRow(displayName);
            _rows[groupId] = row;
            StepsPanel.Children.Add(row.Element);
        }
    }

    private void StartPipeline() =>
        AsyncEventHandlerGuard.Run(
            StartPipelineAsync,
            NullLogger.Instance,
            nameof(StartPipeline));

    private async Task StartPipelineAsync()
    {
        var config = _config!;
        if (_runCts != null)
            return;

        config.LogPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray", "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        _runCts = cts;

        try
        {
            _logger = new SetupLogger(config.LogPath,
                Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);

            _logger.LogEmitted += OnLogEmitted;

            var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
            using var journal = new TransactionJournal(journalPath);
            var commands = new CommandRunner(_logger);
            var ctx = new SetupContext(config, _logger, journal, commands, cts.Token);

            var steps = BuildSteps(config);
            _pipeline = new SetupPipeline(steps);
            _pipeline.StepProgress += OnStepProgress;

            var result = await Task.Run(() => _pipeline.RunAsync(ctx), cts.Token);
            sw.Stop();
            _pipelineFinished = true;

            if (result.Outcome == PipelineOutcome.RebootRequired)
            {
                await ShowRebootRequiredDialogAsync(config, result);
                return;
            }

            var success = result.Outcome == PipelineOutcome.Success;
            if (success)
            {
                if (config.ModelSetup.Enabled)
                {
                    if (_rows.TryGetValue("finish", out var finishRow))
                        finishRow.SetStatus(StepStatus.Running);
                    SubtitleText.Text = "正在打开模型设置...";
                    await Task.Delay(900);
                    finishRow?.SetStatus(StepStatus.Done);
                    SetupWindow.Active?.NavigateToModelSetup();
                }
                else if (!config.SkipWizard)
                {
                    if (_rows.TryGetValue("finish", out var finishRow))
                        finishRow.SetStatus(StepStatus.Running);
                    SubtitleText.Text = "正在打开网关设置...";
                    await Task.Delay(900);
                    finishRow?.SetStatus(StepStatus.Done);
                    SetupWindow.Active?.NavigateToWizard();
                }
                else if (config.SkipPermissions)
                    SetupWindow.Active?.NavigateToComplete(true, sw.Elapsed, config.LogPath);
                else
                    SetupWindow.Active?.NavigateToPermissions();
            }
            else
            {
                var errorMsg = result.Outcome == PipelineOutcome.Cancelled
                    ? "安装已取消。"
                    : result.FailedStepId != null
                        ? $"步骤“{result.FailedStepId}”失败：{result.Message}"
                        : result.Message;
                SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, errorMsg);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            _pipelineFinished = true;
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, "安装已取消。");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _pipelineFinished = true;
            _logger?.Error($"Setup UI pipeline failed: {ex.Message}");
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, $"安装进程异常：{ex.Message}");
        }
        finally
        {
            if (_logger != null)
                _logger.LogEmitted -= OnLogEmitted;
            if (_pipeline != null)
                _pipeline.StepProgress -= OnStepProgress;
            _logger?.Dispose();
            _logger = null;
            _pipeline = null;
            if (ReferenceEquals(_runCts, cts))
                _runCts = null;
        }
    }

    private void CancelPipeline()
    {
        if (!_pipelineFinished)
            _runCts?.Cancel();
    }

    private void OnStepProgress(object? sender, StepProgressEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Find which group this step belongs to
            var groupIndex = Array.FindIndex(StepGroups, g => g.StepIds.Contains(e.StepId));
            if (groupIndex < 0) return;

            var group = StepGroups[groupIndex];
            var row = _rows[group.GroupId];

            if (e.Outcome == null)
            {
                // Step started — mark all previous groups as done if still running
                for (int i = 0; i < groupIndex; i++)
                {
                    var prevRow = _rows[StepGroups[i].GroupId];
                    if (prevRow.Status == StepStatus.Running)
                        prevRow.SetStatus(StepStatus.Done);
                }

                // Mark this group as running
                if (row.Status != StepStatus.Done)
                    row.SetStatus(StepStatus.Running);
            }
            else if (e.Outcome == StepOutcome.RebootRequired)
            {
                row.SetStatus(StepStatus.RebootRequired);
            }
            else if (e.Outcome == StepOutcome.Failed || e.Outcome == StepOutcome.FailedTerminal)
            {
                row.SetStatus(StepStatus.Failed);
            }
            else
            {
                // Step succeeded/skipped — track it
                _completedSteps.Add(e.StepId);

                // If all steps in this group are done, mark group done
                if (group.StepIds.All(id => _completedSteps.Contains(id)))
                    row.SetStatus(StepStatus.Done);
            }
        });
    }

    private readonly HashSet<string> _completedSteps = new();

    private void OnLogEmitted(object? sender, LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var line = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}\n";
            _logLineCount++;
            if (_logLineCount > MaxLogLines)
            {
                // Trim old lines (simple: just keep appending; reset periodically)
                if (_logLineCount % MaxLogLines == 0)
                    LogText.Text = line;
                else
                    LogText.Text += line;
            }
            else
            {
                LogText.Text += line;
            }

            // Auto-scroll
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
        });
    }

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        _logExpanded = !_logExpanded;
        LogPanel.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        OpenLogButton.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        LogToggleButton.Content = _logExpanded ? "隐藏日志 ▼" : "显示日志 ▲";

        var isDark = ActualTheme == ElementTheme.Dark;
        LogPanel.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A)
            : Color.FromArgb(255, 0xF8, 0xF8, 0xF8));
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_config?.LogPath);
    }

    private async Task ShowRebootRequiredDialogAsync(SetupConfig config, PipelineResult result)
    {
        var reason = string.IsNullOrWhiteSpace(result.Message)
            ? "OpenClaw 需要重启 Windows 后继续安装。"
            : result.Message!;
        var autoResumeRegistered = true;

        try
        {
            SetupRebootCoordinator.RegisterContinueAfterReboot(reason, config.LogPath);
        }
        catch (Exception ex)
        {
            autoResumeRegistered = false;
            _logger?.Error($"Failed to register setup resume after reboot: {ex.Message}");
        }

        SubtitleText.Text = autoResumeRegistered
            ? "需要重启 Windows。重启并登录后会自动继续安装。"
            : "需要重启 Windows。自动继续注册失败，请重启后手动打开 OpenClaw 安装。";

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = reason,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = autoResumeRegistered
                ? "点击“立即重启”后，Windows 重启并登录完成时会自动回到当前安装流程。"
                : "点击“立即重启”后，如果没有自动回到安装流程，请手动打开 OpenClaw。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        });

        var dialog = new ContentDialog
        {
            Title = "需要重启 Windows",
            Content = content,
            PrimaryButtonText = "立即重启",
            CloseButtonText = "稍后重启",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
            return;

        try
        {
            SetupRebootCoordinator.RestartWindowsNow();
            SubtitleText.Text = "正在重启 Windows...";
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to restart Windows: {ex.Message}");
            await ShowRestartFailedDialogAsync(ex.Message);
        }
    }

    private async Task ShowRestartFailedDialogAsync(string error)
    {
        var dialog = new ContentDialog
        {
            Title = "无法立即重启",
            Content = new TextBlock
            {
                Text = $"请手动重启 Windows，然后继续安装。\n\n错误：{error}",
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static List<SetupStep> BuildSteps(SetupConfig config)
        => SetupStepFactory.BuildDefaultSteps()
            .Where(step => step is not RunGatewayWizardStep)
            .ToList();
}

// ─── Step Row UI Element ───

internal enum StepStatus { Idle, Running, Done, Failed, RebootRequired }

internal sealed class StepRow
{
    public FrameworkElement Element { get; }
    public StepStatus Status { get; private set; }

    private readonly TextBlock _label;
    private readonly ProgressRing _spinner;
    private readonly Border _idleBadge;
    private readonly Border _checkBadge;
    private readonly Border _errorBadge;
    private readonly Border _rebootBadge;

    public StepRow(string displayName)
    {
        _label = new TextBlock
        {
            Text = displayName,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _spinner = new ProgressRing
        {
            Width = 28, Height = 28,
            MinWidth = 28, MinHeight = 28,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };

        _idleBadge = CreateEmptyBadge();

        _checkBadge = CreateIconBadge("\uE73E", Color.FromArgb(255, 0x2B, 0xC3, 0x6F), Color.FromArgb(255, 255, 255, 255));
        _checkBadge.Visibility = Visibility.Collapsed;

        _errorBadge = CreateIconBadge("\uE711", Color.FromArgb(255, 0xE8, 0x11, 0x23), Color.FromArgb(255, 255, 255, 255));
        _errorBadge.Visibility = Visibility.Collapsed;

        _rebootBadge = CreateIconBadge("\uE823", Color.FromArgb(255, 0xF7, 0xA4, 0x00), Color.FromArgb(255, 255, 255, 255));
        _rebootBadge.Visibility = Visibility.Collapsed;

        var badgeContainer = new Grid
        {
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeContainer.Children.Add(_idleBadge);
        badgeContainer.Children.Add(_spinner);
        badgeContainer.Children.Add(_checkBadge);
        badgeContainer.Children.Add(_errorBadge);
        badgeContainer.Children.Add(_rebootBadge);

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(badgeContainer, 1);
        grid.Children.Add(_label);
        grid.Children.Add(badgeContainer);

        Element = grid;
    }

    public void SetStatus(StepStatus status)
    {
        Status = status;
        _spinner.IsActive = status == StepStatus.Running;
        _spinner.Visibility = status == StepStatus.Running ? Visibility.Visible : Visibility.Collapsed;
        _idleBadge.Visibility = status == StepStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
        _checkBadge.Visibility = status == StepStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        _errorBadge.Visibility = status == StepStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _rebootBadge.Visibility = status == StepStatus.RebootRequired ? Visibility.Visible : Visibility.Collapsed;
        _label.Opacity = status == StepStatus.Idle ? 0.72 : 1.0;
        _label.FontWeight = status is StepStatus.Running or StepStatus.RebootRequired
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private static Border CreateEmptyBadge()
    {
        // Use a theme-aware stroke brush so the pending-step ring is visible
        // in both light and dark mode. The previous hard-coded translucent
        // white was invisible against light backgrounds.
        var border = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
        };

        if (Application.Current.Resources.TryGetValue("ControlStrongStrokeColorDefaultBrush", out var brush)
            && brush is Brush themed)
        {
            border.BorderBrush = themed;
        }
        else
        {
            border.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 128, 128, 128));
        }

        return border;
    }

    private static Border CreateIconBadge(string glyph, Color background, Color foreground)
    {
        return new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(background),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Foreground = new SolidColorBrush(foreground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }
}

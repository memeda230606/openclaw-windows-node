using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;
using OpenClaw.SetupEngine.UI;
using Windows.System;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class ModelSetupPage : Page
{
    private SetupConfig? _config;
    private bool _isBusy;

    public ModelSetupPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        var model = _config.ModelSetup;

        UseLongwangCheck.IsChecked = model.UseLongwang;
        ConsoleUrlText.Text = model.ConsoleUrl;
        PopulateModelCombo(model);
        UpdateFormState();
    }

    private void UseLongwangCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_config != null)
            _config.ModelSetup.UseLongwang = UseLongwangCheck.IsChecked == true;

        UpdateFormState();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateFormState();
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedModel();
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        AsyncEventHandlerGuard.Run(
            OpenConsoleAsync,
            NullLogger.Instance,
            nameof(OpenConsole_Click),
            ex => ShowError($"无法打开控制台：{ex.Message}"));
    }

    private async Task OpenConsoleAsync()
    {
        var url = _config?.ModelSetup.ConsoleUrl ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowError("龙王智能控制台地址无效。");
            return;
        }

        var launched = await Launcher.LaunchUriAsync(uri);
        if (!launched)
            ShowError("无法打开龙王智能控制台。");
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        NavigateAdvancedOrNext();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        NavigateNext();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AsyncEventHandlerGuard.Run(
            SaveAsync,
            NullLogger.Instance,
            nameof(Save_Click),
            ex => ShowError($"模型设置失败：{ex.Message}"));
    }

    private async Task SaveAsync()
    {
        if (_config == null || _isBusy)
            return;

        var model = _config.ModelSetup;
        model.UseLongwang = UseLongwangCheck.IsChecked == true;
        if (!model.UseLongwang)
        {
            NavigateAdvancedOrNext();
            return;
        }

        ApplySelectedModel();

        var apiKey = ApiKeyBox.Password.Trim();
        if (apiKey.Length == 0)
        {
            ShowError("请输入龙王智能 API Key。");
            return;
        }

        if (apiKey.Contains('\r') || apiKey.Contains('\n'))
        {
            ShowError("API Key 不能包含换行。");
            return;
        }

        model.ApiKey = apiKey;
        SetBusy(true);

        var sw = Stopwatch.StartNew();
        try
        {
            EnsureLogPath(_config);
            using var logger = new SetupLogger(
                _config.LogPath,
                Enum.TryParse<LogLevel>(_config.LogLevel, true, out var level) ? level : LogLevel.Trace,
                append: true);
            var journalPath = Path.ChangeExtension(_config.LogPath, ".journal.jsonl");
            using var journal = new TransactionJournal(journalPath);
            var commands = new CommandRunner(logger);
            var ctx = new SetupContext(_config, logger, journal, commands, CancellationToken.None);
            var step = new ConfigureLongwangModelStep();

            logger.StepStarted(step.Id, step.DisplayName);
            journal.RecordStepStarted(step.Id);
            var result = await step.ExecuteAsync(ctx, CancellationToken.None);
            sw.Stop();
            logger.StepCompleted(step.Id, result, sw.Elapsed);
            journal.RecordStepCompleted(step.Id, result.Outcome, sw.Elapsed, result.Message);

            if (!result.IsSuccess)
            {
                ShowError($"模型设置失败：{result.Message}");
                return;
            }

            NavigateNext();
        }
        finally
        {
            model.ApiKey = null;
            SetBusy(false);
        }
    }

    private void NavigateAdvancedOrNext()
    {
        if (_config == null)
            return;

        if (_config.SkipWizard)
            NavigateNext();
        else
            SetupWindow.Active?.NavigateToWizard();
    }

    private void NavigateNext()
    {
        if (_config == null)
            return;

        if (_config.SkipPermissions)
            SetupWindow.Active?.NavigateToComplete(true, TimeSpan.Zero, _config.LogPath);
        else
            SetupWindow.Active?.NavigateToPermissions();
    }

    private void PopulateModelCombo(ModelSetupConfig model)
    {
        ModelCombo.Items.Clear();
        var models = model.EffectiveModels;
        var selectedIndex = 0;

        for (var i = 0; i < models.Count; i++)
        {
            var availableModel = models[i];
            ModelCombo.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(availableModel.Name) ? availableModel.Id : availableModel.Name,
                Tag = availableModel
            });

            if (string.Equals(availableModel.Id, model.ModelId, StringComparison.OrdinalIgnoreCase))
                selectedIndex = i;
        }

        ModelCombo.SelectedIndex = selectedIndex;
        ApplySelectedModel();
    }

    private void ApplySelectedModel()
    {
        if (_config?.ModelSetup is not { } model)
            return;
        if (ModelCombo.SelectedItem is not ComboBoxItem { Tag: LongwangModelConfig selected })
            return;

        model.SelectModel(selected);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void UpdateFormState()
    {
        var useLongwang = UseLongwangCheck.IsChecked == true;
        ApiKeyBox.IsEnabled = useLongwang && !_isBusy;
        OpenConsoleButton.IsEnabled = useLongwang && !_isBusy;
        ModelCombo.IsEnabled = useLongwang && !_isBusy && ModelCombo.Items.Count > 1;
        SaveButton.Content = useLongwang ? "保存并继续" : "继续";
        SaveButton.IsEnabled = !_isBusy && (!useLongwang || ApiKeyBox.Password.Trim().Length > 0);
        SkipButton.IsEnabled = !_isBusy;
        AdvancedButton.IsEnabled = !_isBusy;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        UpdateFormState();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        SetBusy(false);
    }

    private static void EnsureLogPath(SetupConfig config)
    {
        config.LogPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray", "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
    }
}

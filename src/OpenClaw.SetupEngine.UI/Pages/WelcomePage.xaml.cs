using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using System.Numerics;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WelcomePage : Page
{
    private SetupConfig? _config;

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var isDark = ActualTheme == ElementTheme.Dark;
        InfoCard.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x2C, 0x2C, 0x2C)
            : Color.FromArgb(255, 0xF0, 0xF0, 0xF0));

        InfoText.Text = "本地安装会创建一个专用于 OpenClaw 的小型 WSL Linux 实例。"
                      + "如果你想连接已有或远程网关，请选择高级设置。";

        StartLobsterBreatheAnimation();
    }

    private void StartLobsterBreatheAnimation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(LobsterHero);
        var compositor = visual.Compositor;
        var centerX = LobsterHero.ActualWidth > 0 ? LobsterHero.ActualWidth / 2 : LobsterHero.Width / 2;
        var centerY = LobsterHero.ActualHeight > 0 ? LobsterHero.ActualHeight / 2 : LobsterHero.Height / 2;
        visual.CenterPoint = new Vector3((float)centerX, (float)centerY, 0f);

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
        pulse.InsertKeyFrame(0.5f, new Vector3(1.025f, 1.025f, 1f));
        pulse.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        pulse.Duration = TimeSpan.FromMilliseconds(4200);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Scale", pulse);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            StartButtonClickAsync,
            NullLogger.Instance,
            nameof(StartButton_Click));

    private async Task StartButtonClickAsync()
    {
        var dataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");

        var existing = ExistingConfigDetector.Detect(dataDir, _config!.DistroName);
        var summary = ExistingConfigDetector.BuildReplacementSummary(existing);

        var dialog = new ContentDialog
        {
            Title = existing.HasLocalGateway || existing.HasDistro
                ? "替换现有 WSL 网关？"
                : "安装新的 WSL 网关？",
            Content = summary,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            SetupWindow.Active?.NavigateToCapabilities();
    }

    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.RequestAdvancedSetup();
    }
}

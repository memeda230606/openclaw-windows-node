using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private SetupConfig? _config;
    private readonly Dictionary<string, ToggleSwitch> _toggles = new();

    // (config property, display name, description, fluent icon glyph)
    private static readonly (string Key, string Name, string Desc, string Glyph)[] Capabilities =
    [
        ("System", "系统", "Shell 命令、文件、剪贴板", "\uE756"),
        ("Canvas", "画布", "白板与标注", "\uE790"),
        ("Screen", "屏幕捕获", "截图与录屏", "\uE7F4"),
        ("Camera", "摄像头", "拍照与视频", "\uE722"),
        ("Location", "位置", "共享设备位置", "\uE81D"),
        ("Browser", "浏览器", "网页导航与自动化", "\uE774"),
        ("Device", "设备", "音量、亮度、系统信息", "\uE772"),
        ("Tts", "文本转语音", "朗读文本", "\uE767"),
        ("Stt", "语音转文字", "转写语音", "\uE720"),
    ];

    public CapabilitiesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        BuildToggles();
    }

    private void BuildToggles()
    {
        var caps = _config!.Capabilities;
        var totalRows = (Capabilities.Length + 1) / 2; // ceiling division for 2 columns

        // Add row definitions
        for (int i = 0; i < totalRows; i++)
            CapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < Capabilities.Length; i++)
        {
            var (key, name, desc, glyph) = Capabilities[i];
            var prop = typeof(CapabilitiesConfig).GetProperty(key);
            var isEnabled = (bool)(prop?.GetValue(caps) ?? true);

            var toggle = new ToggleSwitch
            {
                IsOn = isEnabled,
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
            };
            _toggles[key] = toggle;

            // Card-like item: icon + text + toggle
            var item = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Padding = new Thickness(10, 12, 6, 12),
            };

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Opacity = 0.85,
            };

            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            textStack.Children.Add(new TextBlock { Text = desc, FontSize = 11, Opacity = 0.55 });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(toggle, 2);
            item.Children.Add(icon);
            item.Children.Add(textStack);
            item.Children.Add(toggle);

            int row = i / 2;
            int col = i % 2;
            Grid.SetRow(item, row);
            Grid.SetColumn(item, col);
            CapGrid.Children.Add(item);
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var caps = _config!.Capabilities;
        foreach (var (key, _, _, _) in Capabilities)
        {
            if (_toggles.TryGetValue(key, out var toggle))
            {
                var prop = typeof(CapabilitiesConfig).GetProperty(key);
                prop?.SetValue(caps, toggle.IsOn);
            }
        }

        SetupWindow.Active?.NavigateToProgress();
    }
}

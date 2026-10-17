using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinAirPlay.App
{
    public partial class MainWindow : Window
    {
        private sealed record AudioOutputChoice(string Name, string? DeviceId);

        private AirPlayEngineService _airPlayService;

        public MainWindow()
        {
            InitializeComponent();
            _airPlayService = new AirPlayEngineService();

            _airPlayService.OnLogReceived += HandleEngineLog;
            _airPlayService.OnConnectionStateChanged += HandleEngineStateChanged;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.ComboBox outputSelector =
                FindTaggedElement<System.Windows.Controls.ComboBox>("audio-output-selector");
            try
            {
                IReadOnlyList<AudioOutputDevice> devices = await _airPlayService.GetAudioOutputDevicesAsync();
                outputSelector.Items.Clear();

                int defaultDeviceIndex = -1;
                foreach (AudioOutputDevice device in devices)
                {
                    string displayName = device.IsDefault ? $"Default — {device.Name}" : device.Name;
                    outputSelector.Items.Add(new System.Windows.Controls.ComboBoxItem
                    {
                        Content = displayName,
                        Tag = new AudioOutputChoice(device.Name, device.Id),
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDF, 0xE2, 0xEE)),
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x1C, 0x24))
                    });
                    if (device.IsDefault && defaultDeviceIndex < 0)
                        defaultDeviceIndex = outputSelector.Items.Count - 1;
                }

                outputSelector.SelectedIndex = defaultDeviceIndex >= 0 ? defaultDeviceIndex : 0;
                ConsoleLog.AppendText($"[AUDIO] Found {devices.Count} Windows playback device(s).{Environment.NewLine}");
                UpdateSelectedAudioOutputStatus();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
                or Win32Exception)
            {
                ConsoleLog.AppendText($"[AUDIO DEVICE ERROR] {ex.Message}{Environment.NewLine}");
                ConsoleLog.ScrollToEnd();
                SetAudioStatus("Using Windows default", "#E9B44C");
            }
        }

        private void AudioOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsInitialized)
                UpdateSelectedAudioOutputStatus();
        }

        private AudioOutputChoice? GetSelectedAudioOutput()
        {
            return (FindTaggedElement<System.Windows.Controls.ComboBox>("audio-output-selector").SelectedItem
                as System.Windows.Controls.ComboBoxItem)?.Tag as AudioOutputChoice;
        }

        private void UpdateSelectedAudioOutputStatus()
        {
            AudioOutputChoice? audioOutput = GetSelectedAudioOutput();
            SetAudioStatus(audioOutput?.Name ?? "Windows default playback device", "#4CD7F6");
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            string serverName = ServerNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = "WinAirPlay";
                ServerNameBox.Text = serverName;
            }
            bool record = RecordCheckBox.IsChecked ?? false;
            System.Windows.Controls.ComboBox outputSelector =
                FindTaggedElement<System.Windows.Controls.ComboBox>("audio-output-selector");
            AudioOutputChoice? audioOutput = GetSelectedAudioOutput();
            string preset = GetSelectedPreset();
            var (width, height, fps) = preset switch
            {
                "smooth" => (1280, 720, 30),
                "high" => (3840, 2160, 60),
                _ => (1920, 1080, 60)
            };

            _airPlayService.StartEngine(serverName, record, width: width, height: height, fps: fps,
                audioOutputDeviceId: audioOutput?.DeviceId);
            if (!_airPlayService.IsRunning)
            {
                return;
            }

            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            outputSelector.IsEnabled = false;
            SetEngineBadge("●  ADVERTISING", "#4EDEA3");
            FindTaggedElement<Border>("video-placeholder").Visibility = Visibility.Visible;
            SetStatus("Waiting for iPhone", "#E9B44C");
            SetConnectionHint("Receiver is advertising on your network");
            SetAudioStatus($"Active: {audioOutput?.Name ?? "Windows default output"}", "#4EDEA3");
        }

        private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsInitialized)
                UpdateQualitySummary();
        }

        private string GetSelectedPreset()
        {
            return (FindTaggedElement<System.Windows.Controls.ComboBox>("quality-selector").SelectedItem
                as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
                ?? "balanced";
        }

        private void UpdateQualitySummary()
        {
            if (FindTaggedElement<TextBlock>("quality-summary") is not { } summary)
                return;

            summary.Text = GetSelectedPreset() switch
            {
                "smooth" => "Smooth  ·  720p / 30 fps",
                "high" => "High  ·  4K / 60 fps",
                _ => "Balanced  ·  1080p / 60 fps"
            };
        }

        private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_airPlayService != null && _airPlayService.IsRunning)
            {
                _airPlayService.ResizeVideo((int)e.NewSize.Width, (int)e.NewSize.Height);
            }
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _airPlayService.StopEngine();
            UpdateControls(false);
        }

        private void HandleEngineLog(string log)
        {
            Dispatcher.Invoke(() =>
            {
                ConsoleLog.AppendText(log + Environment.NewLine);
                ConsoleLog.ScrollToEnd();

                if (log.Contains("raop_rtp_mirror starting mirroring", StringComparison.OrdinalIgnoreCase))
                {
                    VideoHost.Visibility = Visibility.Visible;
                    FindTaggedElement<Border>("video-placeholder").Visibility = Visibility.Collapsed;
                    SetStatus("iPhone connected", "#65D6B3");
                    SetConnectionHint("iPhone connected");
                    _ = DockVideoWhenReadyAsync();
                }
                else if (log.StartsWith("[AUDIO]", StringComparison.OrdinalIgnoreCase))
                {
                    AudioOutputChoice? audioOutput = GetSelectedAudioOutput();
                    SetAudioStatus($"Active: {audioOutput?.Name ?? "Windows default output"}", "#4EDEA3");
                }
                else if (log.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("Engine error", "#F07178");
                }
                else if (log.Contains("[STOPPED]", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateControls(false);
                }
            });
        }

        private void HandleEngineStateChanged(bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                if (!isRunning)
                {
                    UpdateControls(false);
                }
            });
        }

        private void UpdateControls(bool isRunning)
        {
            StartBtn.IsEnabled = !isRunning;
            StopBtn.IsEnabled = isRunning;
            FindTaggedElement<System.Windows.Controls.ComboBox>("audio-output-selector").IsEnabled = !isRunning;
            if (!isRunning)
            {
                VideoHost.Visibility = Visibility.Collapsed;
                FindTaggedElement<Border>("video-placeholder").Visibility = Visibility.Visible;
                SetStatus("Receiver stopped", "#718096");
                SetEngineBadge("●  OFFLINE", "#869397");
                SetConnectionHint("Ready for an iPhone connection");
                UpdateSelectedAudioOutputStatus();
            }
        }

        private void SetEngineBadge(string text, string color)
        {
            TextBlock badge = FindTaggedElement<TextBlock>("engine-badge");
            badge.Text = text;
            badge.Foreground = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        private async Task DockVideoWhenReadyAsync()
        {
            IntPtr hostHandle = VideoPanel.Handle;
            await _airPlayService.DockVideoWindowAsync(hostHandle, VideoPanel.Width, VideoPanel.Height);
        }

        private void SetConnectionHint(string text)
        {
            FindTaggedElement<TextBlock>("connection-hint").Text = text;
        }

        private void SetAudioStatus(string text, string color)
        {
            TextBlock audioStatus = FindTaggedElement<TextBlock>("audio-status");
            audioStatus.Text = text;
            audioStatus.Foreground = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        private void SetStatus(string text, string color)
        {
            FindTaggedElement<TextBlock>("status-text").Text = text;
            FindTaggedElement<Ellipse>("status-indicator").Fill = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        private T FindTaggedElement<T>(string tag) where T : FrameworkElement
        {
            return FindTaggedElement<T>(this, tag)
                ?? throw new InvalidOperationException($"Could not find the UI element tagged '{tag}'.");
        }

        private static T? FindTaggedElement<T>(DependencyObject parent, string tag) where T : FrameworkElement
        {
            if (parent is T element && string.Equals(element.Tag as string, tag, StringComparison.Ordinal))
                return element;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                T? match = FindTaggedElement<T>(VisualTreeHelper.GetChild(parent, i), tag);
                if (match != null)
                    return match;
            }

            return null;
        }

        protected override void OnClosed(EventArgs e)
        {
            _airPlayService.Dispose();
            base.OnClosed(e);
        }
    }
}
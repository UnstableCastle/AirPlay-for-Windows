using System;
using System.Windows;

namespace WinAirPlay.App
{
    public partial class MainWindow : Window
    {
        private AirPlayEngineService _airPlayService;

        public MainWindow()
        {
            InitializeComponent();
            _airPlayService = new AirPlayEngineService();

            // Route engine logs to the UI text box safely across threads
            _airPlayService.OnLogReceived += (log) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ConsoleLog.AppendText(log + Environment.NewLine);
                    ConsoleLog.ScrollToEnd();
                });
            };
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            string serverName = ServerNameBox.Text;
            bool record = RecordCheckBox.IsChecked ?? false;

            _airPlayService.StartEngine(serverName, record);

            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;

            // Grab the raw HWND of the Windows Forms Panel and wait for the video to hijack it
            IntPtr hostHandle = VideoPanel.Handle;
            await _airPlayService.DockVideoWindowAsync(hostHandle, VideoPanel.Width, VideoPanel.Height);
        }

        private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Keeps the video properly sized when you resize the WPF window.
            // The Direct3D11 sink automatically adds black bars (letterboxing) to preserve the iPhone's aspect ratio.
            if (_airPlayService != null && _airPlayService.IsRunning)
            {
                _airPlayService.ResizeVideo((int)e.NewSize.Width, (int)e.NewSize.Height);
            }
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _airPlayService.StopEngine();

            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _airPlayService.Dispose();
            base.OnClosed(e);
        }
    }
}
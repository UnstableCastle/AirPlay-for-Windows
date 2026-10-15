using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WinAirPlay.App
{
    public class AirPlayEngineService : IDisposable
    {
        private Process? _engineProcess;
        public event Action<string>? OnLogReceived;
        public event Action<bool>? OnConnectionStateChanged;

        // Win32 APIs for window manipulation
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;

        private IntPtr _videoWindowHandle = IntPtr.Zero;

        public bool IsRunning => _engineProcess != null && !_engineProcess.HasExited;

        public void StartEngine(string serverName = "Windows Studio", bool recordToMp4 = false, string? outputDirectory = null, int fps = 60)
        {
            if (IsRunning) StopEngine();

            string exePath = @"C:\msys64\home\Amaan\UxPlay\build\uxplay.exe";
            if (!File.Exists(exePath))
            {
                OnLogReceived?.Invoke($"[ERROR] Engine not found at: {exePath}");
                return;
            }

            var args = new StringBuilder();
            args.Append($"-n \"{serverName}\" ");
            args.Append("-p "); // Lock UDP audio to ports 6000/6001/7011
            args.Append($"-fps {fps} ");
            args.Append("-vsync no ");
            args.Append("-vd d3d11h265dec ");
            args.Append("-vs d3d11videosink ");
            args.Append("-as wasapi2sink ");
            args.Append("-h265 ");


            if (recordToMp4)
            {
                string folder = outputDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                Directory.CreateDirectory(folder);
                string fileName = Path.Combine(folder, $"iPhone_Capture_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                args.Append($"-mp4 \"{fileName}\" ");
                OnLogReceived?.Invoke($"[REC] Lossless stream capture armed -> {fileName}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args.ToString(),
                WorkingDirectory = @"C:\msys64\home\Amaan\UxPlay\build",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // AUDIO FIX 2: Explicitly register GStreamer's plugin path so it can load audio decoders (libgstfaad / libgstaudioconvert)
            string msysBin = @"C:\msys64\ucrt64\bin";
            string gstPlugins = @"C:\msys64\ucrt64\lib\gstreamer-1.0";

            startInfo.EnvironmentVariables["PATH"] = $"{msysBin};{Environment.GetEnvironmentVariable("PATH")}";
            startInfo.EnvironmentVariables["GST_PLUGIN_PATH"] = gstPlugins;
            startInfo.EnvironmentVariables["GST_PLUGIN_SYSTEM_PATH"] = gstPlugins;

            // Surface GStreamer errors in your UI log if a plugin is missing
            startInfo.EnvironmentVariables["GST_DEBUG"] = "2";

            _engineProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            _engineProcess.OutputDataReceived += (s, e) => ParseEngineOutput(e.Data);
            _engineProcess.ErrorDataReceived += (s, e) => ParseEngineOutput(e.Data);
            _engineProcess.Exited += (s, e) => OnConnectionStateChanged?.Invoke(false);

            _engineProcess.Start();
            _engineProcess.BeginOutputReadLine();
            _engineProcess.BeginErrorReadLine();

            OnConnectionStateChanged?.Invoke(true);
            OnLogReceived?.Invoke($"[STARTED] Broadcasting '{serverName}' via mDNS...");
        }

        private void ParseEngineOutput(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            OnLogReceived?.Invoke(line);

            if (line.Contains("raop_rtp_mirror starting mirroring", StringComparison.OrdinalIgnoreCase))
            {
                OnLogReceived?.Invoke(">>> iPhone Screen Mirroring Connected! <<<");
            }
        }

        public async Task DockVideoWindowAsync(IntPtr parentHandle, int width, int height)
        {
            _videoWindowHandle = IntPtr.Zero;

            // Poll for the GStreamer window creation
            for (int i = 0; i < 20; i++)
            {
                _videoWindowHandle = FindWindow(null, "Direct3D11 renderer");
                if (_videoWindowHandle == IntPtr.Zero) _videoWindowHandle = FindWindow(null, "UxPlay");

                if (_videoWindowHandle != IntPtr.Zero) break;
                await Task.Delay(500);
            }

            if (_videoWindowHandle != IntPtr.Zero)
            {
                int style = GetWindowLong(_videoWindowHandle, GWL_STYLE);
                SetWindowLong(_videoWindowHandle, GWL_STYLE, style & ~WS_CAPTION & ~WS_THICKFRAME);

                SetParent(_videoWindowHandle, parentHandle);
                MoveWindow(_videoWindowHandle, 0, 0, width, height, true);
            }
        }

        public void ResizeVideo(int width, int height)
        {
            if (_videoWindowHandle != IntPtr.Zero)
            {
                MoveWindow(_videoWindowHandle, 0, 0, width, height, true);
            }
        }

        public void StopEngine()
        {
            if (_engineProcess != null && !_engineProcess.HasExited)
            {
                _engineProcess.Kill(entireProcessTree: true);
                _engineProcess.Dispose();
                _engineProcess = null;
                OnLogReceived?.Invoke("[STOPPED] AirPlay receiver stopped.");
            }
        }

        public void Dispose() => StopEngine();
    }
}
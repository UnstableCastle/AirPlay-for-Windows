using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinAirPlay.App
{
    public sealed record AudioOutputDevice(string Name, string Id, bool IsDefault);

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

        private const string MsysBin = @"C:\msys64\ucrt64\bin";
        private const string GstPlugins = @"C:\msys64\ucrt64\lib\gstreamer-1.0";
        private const string GstPluginScanner = @"C:\msys64\ucrt64\libexec\gstreamer-1.0\gst-plugin-scanner.exe";

        public bool IsRunning => _engineProcess != null && !_engineProcess.HasExited;

        public async Task<IReadOnlyList<AudioOutputDevice>> GetAudioOutputDevicesAsync()
        {
            string monitorPath = Path.Combine(MsysBin, "gst-device-monitor-1.0.exe");
            if (!File.Exists(monitorPath))
                throw new FileNotFoundException("GStreamer audio device monitor was not found.", monitorPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = monitorPath,
                WorkingDirectory = Path.GetDirectoryName(monitorPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("Audio");
            ConfigureGStreamerEnvironment(startInfo);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("GStreamer audio device monitor did not start.");

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw new TimeoutException("Timed out while enumerating Windows audio output devices.");
            }

            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"GStreamer audio device enumeration failed with exit code {process.ExitCode}: {error.Trim()}");

            var devices = new List<AudioOutputDevice>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string block in Regex.Split(output, @"(?m)(?=^[ \t]*Device found:)"))
            {
                if (!Regex.IsMatch(block, @"^\s*Device found:", RegexOptions.Multiline)
                    || !Regex.IsMatch(block, @"^\s*class\s*:\s*Audio/Sink\s*$", RegexOptions.Multiline))
                    continue;

                string? name = ReadMonitorProperty(block, "wasapi2.device.description")
                    ?? ReadMonitorField(block, "name");
                string? id = ReadMonitorProperty(block, "device.actual-id")
                    ?? ReadMonitorProperty(block, "device.id");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)
                    || !seenIds.Add(id))
                    continue;

                string? actualName = ReadMonitorProperty(block, "device.actual-name");
                if (!string.IsNullOrWhiteSpace(actualName))
                    name = actualName;

                bool isDefault = ReadMonitorProperty(block, "device.default")
                    ?.StartsWith("true", StringComparison.OrdinalIgnoreCase) == true;
                devices.Add(new AudioOutputDevice(name, id, isDefault));
            }

            if (devices.Count == 0)
                throw new InvalidOperationException(
                    "GStreamer did not find any Windows audio playback devices (Audio/Sink).");

            return devices;
        }

        private static string? ReadMonitorField(string block, string field)
        {
            Match match = Regex.Match(block, $@"^\s*{Regex.Escape(field)}\s*:\s*(.+?)\s*$",
                RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        private static string? ReadMonitorProperty(string block, string property)
        {
            Match match = Regex.Match(block, $@"^\s*{Regex.Escape(property)}\s*=\s*(.+?)\s*$",
                RegexOptions.Multiline);
            if (!match.Success)
                return null;

            string value = match.Groups[1].Value.Trim();
            int annotationStart = value.IndexOf(" (", StringComparison.Ordinal);
            return (annotationStart >= 0 ? value[..annotationStart] : value).Trim('"');
        }

        public void StartEngine(string serverName = "Windows Studio", bool recordToMp4 = false, string? outputDirectory = null,
            int width = 1920, int height = 1080, int fps = 60, string? audioOutputDeviceId = null)
        {
            if (IsRunning) StopEngine();

            string exePath = Path.Combine(AppContext.BaseDirectory, "Engine", "uxplay.exe");
            if (!File.Exists(exePath))
            {
                OnLogReceived?.Invoke($"[ERROR] Engine not found at: {exePath}");
                return;
            }

            var args = new StringBuilder();
            args.Append($"-n \"{serverName}\" ");
            args.Append("-p "); // Lock UDP audio to ports 6000/6001/7011
            args.Append($"-s {width}x{height}@{fps} ");
            args.Append($"-fps {fps} ");
            args.Append("-vsync no ");
            args.Append("-vc d3d12convert ");
            args.Append("-vs d3d12videosink ");
            string audioSink = "wasapisink role=multimedia low-latency=true";
            if (!string.IsNullOrWhiteSpace(audioOutputDeviceId))
                audioSink = $"wasapisink device=\\\"{audioOutputDeviceId}\\\" role=multimedia low-latency=true";
            args.Append($"-as \"{audioSink}\" ");
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
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            ConfigureGStreamerEnvironment(startInfo);

            _engineProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            _engineProcess.OutputDataReceived += (s, e) => ParseEngineOutput(e.Data);
            _engineProcess.ErrorDataReceived += (s, e) => ParseEngineOutput(e.Data);
            _engineProcess.Exited += (s, e) =>
            {
                int exitCode = s is Process exitedProcess ? exitedProcess.ExitCode : -1;
                OnLogReceived?.Invoke($"[ENGINE EXIT] UxPlay stopped with exit code {exitCode}.");
                OnConnectionStateChanged?.Invoke(false);
            };

            if (!_engineProcess.Start())
            {
                OnLogReceived?.Invoke("[ERROR] UxPlay process did not start.");
                return;
            }
            _engineProcess.BeginOutputReadLine();
            _engineProcess.BeginErrorReadLine();

            OnConnectionStateChanged?.Invoke(true);
            OnLogReceived?.Invoke($"[ENGINE] {args}");
            OnLogReceived?.Invoke($"[STARTED] Broadcasting '{serverName}' via mDNS...");
            string outputDescription = string.IsNullOrWhiteSpace(audioOutputDeviceId)
                ? "Windows default playback device"
                : $"selected WASAPI endpoint {audioOutputDeviceId}";
            OnLogReceived?.Invoke($"[AUDIO] UxPlay audio is routed to {outputDescription}.");
        }

        private void ConfigureGStreamerEnvironment(ProcessStartInfo startInfo)
        {
            startInfo.EnvironmentVariables["PATH"] = $"{MsysBin};{Environment.GetEnvironmentVariable("PATH")}";
            startInfo.EnvironmentVariables["GST_PLUGIN_PATH"] = GstPlugins;
            startInfo.EnvironmentVariables["GST_PLUGIN_PATH_1_0"] = GstPlugins;
            startInfo.EnvironmentVariables["GST_PLUGIN_SYSTEM_PATH"] = GstPlugins;
            startInfo.EnvironmentVariables["GST_PLUGIN_SYSTEM_PATH_1_0"] = GstPlugins;
            startInfo.EnvironmentVariables["GST_PLUGIN_SCANNER"] = GstPluginScanner;
            startInfo.EnvironmentVariables["GST_PLUGIN_SCANNER_1_0"] = GstPluginScanner;
            string? homeDirectory = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(homeDirectory))
                homeDirectory = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(homeDirectory))
                startInfo.EnvironmentVariables["HOME"] = homeDirectory;
            startInfo.EnvironmentVariables["GST_DEBUG"] = "2";
        }

        private void ParseEngineOutput(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            OnLogReceived?.Invoke(line);

            if (line.Contains("raop_rtp_mirror starting mirroring", StringComparison.OrdinalIgnoreCase))
            {
                OnLogReceived?.Invoke(">>> iPhone Screen Mirroring Connected! <<<");
                OnConnectionStateChanged?.Invoke(true);
            }
        }

        public async Task DockVideoWindowAsync(IntPtr parentHandle, int width, int height)
        {
            _videoWindowHandle = IntPtr.Zero;

            // Poll for the GStreamer window creation
            for (int i = 0; i < 20; i++)
            {
                _videoWindowHandle = FindWindow(null, "Direct3D12 Renderer");
                if (_videoWindowHandle == IntPtr.Zero)
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
                OnLogReceived?.Invoke("[VIDEO] Direct3D12 renderer attached to the mirroring stage.");
            }
            else
            {
                OnLogReceived?.Invoke("[VIDEO] Renderer window was not found after the iPhone connected.");
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
            Process? engineProcess = _engineProcess;
            _engineProcess = null;
            if (engineProcess != null)
            {
                if (!engineProcess.HasExited)
                {
                    engineProcess.Kill(entireProcessTree: true);
                    OnLogReceived?.Invoke("[STOPPED] AirPlay receiver stopped.");
                }
                engineProcess.Dispose();
            }
        }

        public void Dispose() => StopEngine();
    }
}
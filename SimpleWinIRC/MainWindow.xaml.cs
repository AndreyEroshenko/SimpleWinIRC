using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SimpleWinIRC
{
    public sealed partial class MainWindow : Window
    {
        private readonly DispatcherQueue _dispatcher;
        private TcpClient? _tcpClient;
        private Stream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _cts;
        private bool _isConnected;
        private string? _currentChannel;
        private string? _currentNickname;

        public MainWindow()
        {
            InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            ServerComboBox.SelectedIndex = 0;

            var settings = LoadSettings();
            if (!string.IsNullOrEmpty(settings.Nickname))
                NicknameTextBox.Text = settings.Nickname;

            Closed += (_, _) =>
            {
                SaveSettings();
                _ = DisconnectAsync();
            };
        }

        private sealed class AppSettings
        {
            public string? Nickname { get; set; }
        }

        private static string GetSettingsPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleWinIRC");
            return Path.Combine(dir, "settings.json");
        }

        private static AppSettings LoadSettings()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path)) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                var path = GetSettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var settings = new AppSettings { Nickname = NicknameTextBox.Text?.Trim() };
                File.WriteAllText(path, JsonSerializer.Serialize(settings));
            }
            catch { }
        }

        private void AdvancedOptionsCheckBox_Toggle(object sender, RoutedEventArgs e)
        {
            var visible = AdvancedOptionsCheckBox.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
            PortNumberBox.Visibility = visible;
            UseSslCheckBox.Visibility = visible;
            IgnoreCertCheckBox.Visibility = visible;
        }

        private void ServerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = ServerComboBox.SelectedItem as string;
            if (selected == "<add new>")
            {
                ServerComboBox.Text = string.Empty;
                return;
            }
            switch (selected)
            {
                case "irc.irchighway.net":
                    PortNumberBox.Value = 9999;
                    UseSslCheckBox.IsChecked = true;
                    IgnoreCertCheckBox.IsChecked = true;
                    ChannelTextBox.Text = "#ebooks";
                    break;
                case "irc.undernet.org":
                    PortNumberBox.Value = 6667;
                    UseSslCheckBox.IsChecked = false;
                    IgnoreCertCheckBox.IsChecked = false;
                    ChannelTextBox.Text = "#Bookz";
                    break;
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                await DisconnectAsync();
                return;
            }

            var server = ServerComboBox.Text?.Trim() ?? string.Empty;
            var port = (int)PortNumberBox.Value;
            var nickname = NicknameTextBox.Text?.Trim() ?? string.Empty;
            var useSsl = UseSslCheckBox.IsChecked == true;
            var ignoreCert = IgnoreCertCheckBox.IsChecked == true;

            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(nickname))
            {
                SetStatus("Please enter a server and a nickname.");
                return;
            }

            if (server != "<add new>" && !ServerComboBox.Items.Contains(server))
            {
                var addNewIndex = ServerComboBox.Items.IndexOf("<add new>");
                if (addNewIndex >= 0)
                    ServerComboBox.Items.Insert(addNewIndex, server);
                else
                    ServerComboBox.Items.Add(server);
            }

            ConnectButton.IsEnabled = false;
            SetStatus($"Connecting to {server}:{port}...");

            try
            {
                _cts = new CancellationTokenSource();
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(server, port, _cts.Token);

                Stream networkStream = _tcpClient.GetStream();
                if (useSsl)
                {
                    RemoteCertificateValidationCallback? validate = ignoreCert
                        ? (_, _, _, _) => true
                        : null;
                    var ssl = new SslStream(networkStream, leaveInnerStreamOpen: false, validate);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = server,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, _cts.Token);
                    networkStream = ssl;
                }
                _stream = networkStream;
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                _reader = new StreamReader(_stream, encoding);
                _writer = new StreamWriter(_stream, encoding) { NewLine = "\r\n", AutoFlush = true };

                _isConnected = true;
                _currentNickname = nickname;
                ConnectButton.Content = "Disconnect";
                ConnectButton.IsEnabled = true;
                InputTextBox.IsEnabled = true;
                SendButton.IsEnabled = true;
                ChannelTextBox.IsEnabled = true;
                JoinButton.IsEnabled = true;
                ServerComboBox.IsEnabled = false;
                NicknameTextBox.IsEnabled = false;
                AdvancedOptionsCheckBox.IsEnabled = false;
                SetStatus($"Connected to {server}:{port}.");

                await SendAsync($"NICK {nickname}");
                await SendAsync($"USER {nickname} 0 * :{nickname}");

                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                SetStatus($"Connection failed: {ex.Message}");
                await DisconnectAsync();
                ConnectButton.IsEnabled = true;
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _reader != null)
                {
                    var line = await _reader.ReadLineAsync(token);
                    if (line == null) break;
                    AppendLine(line);

                    if (line.StartsWith("PING ", StringComparison.Ordinal))
                    {
                        var payload = line.Substring(5);
                        await SendAsync($"PONG {payload}");
                        continue;
                    }

                    TryHandleDccSend(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppendLine($"[error] {ex.Message}");
            }
            finally
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_isConnected)
                    {
                        _isConnected = false;
                        _currentChannel = null;
                        _currentNickname = null;
                        ConnectButton.Content = "Connect";
                        InputTextBox.IsEnabled = false;
                        InputTextBox.PlaceholderText = string.Empty;
                        SendButton.IsEnabled = false;
                        ChannelTextBox.IsEnabled = false;
                        JoinButton.IsEnabled = false;
                        ServerComboBox.IsEnabled = true;
                        NicknameTextBox.IsEnabled = true;
                        AdvancedOptionsCheckBox.IsEnabled = true;
                        SetStatus("Disconnected.");
                    }
                });
            }
        }

        private async Task SendAsync(string line)
        {
            if (_writer == null) return;
            try
            {
                await _writer.WriteLineAsync(line);
                AppendLine($">> {line}");
            }
            catch (Exception ex)
            {
                AppendLine($"[send error] {ex.Message}");
            }
        }

        private async Task DisconnectAsync()
        {
            try
            {
                if (_writer != null && _isConnected)
                {
                    try { await _writer.WriteLineAsync("QUIT :bye"); } catch { }
                }
            }
            finally
            {
                _cts?.Cancel();
                _writer?.Dispose();
                _reader?.Dispose();
                _stream?.Dispose();
                _tcpClient?.Dispose();
                _cts?.Dispose();
                _writer = null;
                _reader = null;
                _stream = null;
                _tcpClient = null;
                _cts = null;
                _isConnected = false;
                _currentChannel = null;
                _currentNickname = null;
                _dispatcher.TryEnqueue(() =>
                {
                    ConnectButton.Content = "Connect";
                    ConnectButton.IsEnabled = true;
                    InputTextBox.IsEnabled = false;
                    InputTextBox.PlaceholderText = string.Empty;
                    SendButton.IsEnabled = false;
                    ChannelTextBox.IsEnabled = false;
                    JoinButton.IsEnabled = false;
                    ServerComboBox.IsEnabled = true;
                    NicknameTextBox.IsEnabled = true;
                    AdvancedOptionsCheckBox.IsEnabled = true;
                });
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendInputAsync();
        }

        private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await SendInputAsync();
            }
        }

        private async Task SendInputAsync()
        {
            if (!_isConnected) return;
            var line = InputTextBox.Text?.Trim() ?? string.Empty;
            if (line.Length == 0) return;
            InputTextBox.Text = string.Empty;
            if (!string.IsNullOrEmpty(_currentChannel))
                await SendAsync($"PRIVMSG {_currentChannel} :{line}");
            else
                await SendAsync(line);
        }

        private async void JoinButton_Click(object sender, RoutedEventArgs e)
        {
            await JoinChannelAsync();
        }

        private async void ChannelTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await JoinChannelAsync();
            }
        }

        private async Task JoinChannelAsync()
        {
            if (!_isConnected) return;
            var channel = ChannelTextBox.Text?.Trim() ?? string.Empty;
            if (channel.Length == 0) return;
            if (channel[0] != '#' && channel[0] != '&' && channel[0] != '+' && channel[0] != '!')
                channel = "#" + channel;
            ChannelTextBox.Text = string.Empty;
            await SendAsync($"JOIN {channel}");
            _currentChannel = channel;
            InputTextBox.PlaceholderText = $"Message {channel}";
        }

        private void TryHandleDccSend(string line)
        {
            if (line.Length < 2 || line[0] != ':') return;
            var sp1 = line.IndexOf(' ');
            if (sp1 < 1) return;
            var prefix = line.Substring(1, sp1 - 1);
            var rest1 = line.Substring(sp1 + 1);
            const string privmsg = "PRIVMSG ";
            if (!rest1.StartsWith(privmsg, StringComparison.Ordinal)) return;
            var rest2 = rest1.Substring(privmsg.Length);
            var sp2 = rest2.IndexOf(' ');
            if (sp2 < 1) return;
            var target = rest2.Substring(0, sp2);
            var rest3 = rest2.Substring(sp2 + 1);
            if (rest3.Length < 2 || rest3[0] != ':') return;
            var body = rest3.Substring(1);

            if (string.IsNullOrEmpty(_currentNickname) ||
                !string.Equals(target, _currentNickname, StringComparison.OrdinalIgnoreCase))
                return;

            if (body.Length < 2 || body[0] != '\x01' || body[^1] != '\x01') return;
            var inner = body.Substring(1, body.Length - 2);

            var parsed = TryParseDccSend(inner);
            if (parsed == null) return;

            var sender = prefix.Split('!', 2)[0];
            var (filename, ip, port, size) = parsed.Value;
            var ipStr = IpFromUint(ip);

            _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await HandleIncomingDccSendAsync(sender, filename, ipStr, port, size);
                }
                catch (Exception ex)
                {
                    AppendLine($"[dcc error] {ex.Message}");
                }
            });
        }

        private static (string filename, uint ip, int port, long size)? TryParseDccSend(string inner)
        {
            const string prefix = "DCC SEND ";
            if (!inner.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var rest = inner.Substring(prefix.Length);
            string filename;
            if (rest.Length > 0 && rest[0] == '"')
            {
                var end = rest.IndexOf('"', 1);
                if (end < 0) return null;
                filename = rest.Substring(1, end - 1);
                rest = rest.Substring(end + 1).TrimStart();
            }
            else
            {
                var sp = rest.IndexOf(' ');
                if (sp < 0) return null;
                filename = rest.Substring(0, sp);
                rest = rest.Substring(sp + 1);
            }
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            if (!uint.TryParse(parts[0], out var ip)) return null;
            if (!int.TryParse(parts[1], out var port) || port <= 0 || port > 65535) return null;
            if (!long.TryParse(parts[2], out var size) || size < 0) return null;
            return (filename, ip, port, size);
        }

        private static string IpFromUint(uint ip) =>
            $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

        private async Task HandleIncomingDccSendAsync(string sender, string filename, string ip, int port, long size)
        {
            var dialog = new ContentDialog
            {
                Title = "Incoming file (DCC)",
                Content = $"{sender} is offering:\n\n{filename}\n{size:N0} bytes\nfrom {ip}:{port}",
                PrimaryButtonText = "Accept",
                CloseButtonText = "Decline",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.SuggestedFileName = filename;
            var ext = Path.GetExtension(filename);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            picker.FileTypeChoices.Add("File", new List<string> { ext });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            _ = Task.Run(() => DownloadDccFileAsync(file.Path, ip, port, size, filename));
        }

        private async Task DownloadDccFileAsync(string savePath, string ip, int port, long expectedSize, string displayName)
        {
            AppendLine($"[dcc] starting {displayName} ({expectedSize:N0} bytes) from {ip}:{port}");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(ip, port);
                using var net = client.GetStream();
                using var file = File.Create(savePath);
                var buf = new byte[8192];
                var ack = new byte[4];
                long total = 0;
                long lastReportedKb = 0;
                while (total < expectedSize)
                {
                    var read = await net.ReadAsync(buf, 0, buf.Length);
                    if (read == 0) break;
                    await file.WriteAsync(buf, 0, read);
                    total += read;
                    ack[0] = (byte)((total >> 24) & 0xFF);
                    ack[1] = (byte)((total >> 16) & 0xFF);
                    ack[2] = (byte)((total >> 8) & 0xFF);
                    ack[3] = (byte)(total & 0xFF);
                    try { await net.WriteAsync(ack, 0, 4); } catch { }

                    var kb = total / 1024;
                    if (kb - lastReportedKb >= 64)
                    {
                        lastReportedKb = kb;
                        AppendLine($"[dcc] {displayName}: {total:N0} / {expectedSize:N0} bytes");
                    }
                }
                AppendLine($"[dcc] complete: {displayName} -> {savePath} ({total:N0} bytes)");
            }
            catch (Exception ex)
            {
                AppendLine($"[dcc error] {displayName}: {ex.Message}");
            }
        }

        private void AppendLine(string line)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (OutputTextBlock.Text.Length > 0)
                    OutputTextBlock.Text += Environment.NewLine;
                OutputTextBlock.Text += line;
                OutputScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
            });
        }

        private void SetStatus(string text)
        {
            _dispatcher.TryEnqueue(() => StatusTextBlock.Text = text);
        }
    }
}

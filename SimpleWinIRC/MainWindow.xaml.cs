using System;
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
                    break;
                case "irc.undernet.org":
                    PortNumberBox.Value = 6667;
                    UseSslCheckBox.IsChecked = false;
                    IgnoreCertCheckBox.IsChecked = false;
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
                    }
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
                        ConnectButton.Content = "Connect";
                        InputTextBox.IsEnabled = false;
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
                _dispatcher.TryEnqueue(() =>
                {
                    ConnectButton.Content = "Connect";
                    ConnectButton.IsEnabled = true;
                    InputTextBox.IsEnabled = false;
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

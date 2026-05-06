using Microsoft.UI.Xaml;

namespace SimpleWinIRC
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var server = ServerTextBox.Text?.Trim() ?? string.Empty;
            var port = (int)PortNumberBox.Value;
            var nickname = NicknameTextBox.Text?.Trim() ?? string.Empty;
            var useSsl = UseSslCheckBox.IsChecked == true;

            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(nickname))
            {
                StatusTextBlock.Text = "Please enter a server and a nickname.";
                return;
            }

            var scheme = useSsl ? "ircs" : "irc";
            StatusTextBlock.Text = $"Would connect to {scheme}://{server}:{port} as {nickname}.";
        }
    }
}

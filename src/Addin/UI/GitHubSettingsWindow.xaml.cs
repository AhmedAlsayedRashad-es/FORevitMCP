using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FirstOption.RevitMcp.Shared;

namespace FirstOption.RevitMcp.Addin.UI
{
    public partial class GitHubSettingsWindow : Window
    {
        private readonly McpSettings _settings;
        private bool _removeToken;

        public static void ShowFor(IntPtr owner)
        {
            var window = new GitHubSettingsWindow();
            if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
            window.ShowDialog();
        }

        public GitHubSettingsWindow()
        {
            InitializeComponent();
            _settings = McpSettings.Load();
            OwnerBox.Text = _settings.GitHubOwner;
            RepoBox.Text = _settings.GitHubRepo;
            BranchBox.Text = _settings.EffectiveBranch;
            LibraryBox.Text = _settings.EffectiveLibraryPath;
            AuthorNameBox.Text = _settings.AuthorName;
            AuthorEmailBox.Text = _settings.AuthorEmail;
            AutoPushBox.IsChecked = _settings.AutoPush;
            NotifyBox.IsChecked = _settings.NotifyOnPush;
            UpdateTokenState();
        }

        private void UpdateTokenState()
        {
            var saved = _settings.HasToken && !_removeToken;
            TokenStateText.Text = saved
                ? "A token is saved. Leave the box empty to keep it."
                : "No token saved.";
            RemoveTokenLink.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RemoveToken_Click(object sender, RoutedEventArgs e)
        {
            _removeToken = true;
            TokenBox.Password = "";
            UpdateTokenState();
        }

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            var owner = OwnerBox.Text.Trim();
            var repo = RepoBox.Text.Trim();
            if (owner.Length == 0 || repo.Length == 0)
            {
                ShowResult(false, "Fill in the repository owner and name first.");
                return;
            }
            var token = TokenBox.Password.Length > 0 ? TokenBox.Password : _removeToken ? "" : _settings.GetToken();

            TestButton.IsEnabled = false;
            TestResultText.Foreground = (Brush)FindResource("MutedBrush");
            TestResultText.Text = "Testing...";
            try
            {
                var result = await GitHubCheck.TestAsync(owner, repo, token);
                ShowResult(result.Item1, result.Item2);
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        private void ShowResult(bool ok, string text)
        {
            TestResultText.Foreground = (Brush)FindResource(ok ? "OkBrush" : "ErrorBrush");
            TestResultText.Text = text;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Command library folder (a pyRevit extension and a git repository)",
                SelectedPath = LibraryBox.Text,
                ShowNewFolderButton = true,
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) LibraryBox.Text = dialog.SelectedPath;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.GitHubOwner = OwnerBox.Text.Trim();
            _settings.GitHubRepo = RepoBox.Text.Trim();
            _settings.Branch = string.IsNullOrWhiteSpace(BranchBox.Text) ? "main" : BranchBox.Text.Trim();
            _settings.LibraryPath = string.Equals(LibraryBox.Text.Trim(), AppPaths.DefaultLibraryPath, StringComparison.OrdinalIgnoreCase) ? "" : LibraryBox.Text.Trim();
            _settings.AuthorName = AuthorNameBox.Text.Trim();
            _settings.AuthorEmail = AuthorEmailBox.Text.Trim();
            _settings.AutoPush = AutoPushBox.IsChecked == true;
            _settings.NotifyOnPush = NotifyBox.IsChecked == true;
            if (TokenBox.Password.Length > 0) _settings.SetToken(TokenBox.Password.Trim());
            else if (_removeToken) _settings.TokenProtected = "";

            try
            {
                _settings.Save();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the settings:\n" + ex.Message, "GitHub Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal static class GitHubCheck
    {
        public static async Task<Tuple<bool, string>> TestAsync(string owner, string repo, string token)
        {
#if NETFRAMEWORK
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Uri.EscapeDataString(owner) + "/" + Uri.EscapeDataString(repo)))
                {
                    request.Headers.UserAgent.ParseAdd("FirstOption-RevitMCP/0.1");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using (var response = await http.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        switch ((int)response.StatusCode)
                        {
                            case 200:
                                var d = MiniJson.Parse(body) as Dictionary<string, object> ?? new Dictionary<string, object>();
                                var name = d.TryGetValue("full_name", out var fn) ? fn as string : owner + "/" + repo;
                                var isPrivate = d.TryGetValue("private", out var pv) && pv is bool pb && pb;
                                if (!(d.TryGetValue("permissions", out var perms) && perms is Dictionary<string, object> pd))
                                    return Tuple.Create(true, "Found " + name + " (" + (isPrivate ? "private" : "public") + "). Add a token to check push access.");
                                var canPush = pd.TryGetValue("push", out var push) && push is bool b && b;
                                return Tuple.Create(canPush, "Connected to " + name + " (" + (isPrivate ? "private" : "public") + "). Push access: " + (canPush ? "yes." : "NO. Give the token Contents: Read and write."));
                            case 401:
                                return Tuple.Create(false, "The token is not valid, or it expired.");
                            case 403:
                                return Tuple.Create(false, "GitHub refused the request (rate limit or token permissions).");
                            case 404:
                                return Tuple.Create(false, "Repository not found, or the token has no access to it.");
                            default:
                                return Tuple.Create(false, "GitHub answered HTTP " + (int)response.StatusCode + ".");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, "Connection failed: " + ex.Message);
            }
        }
    }
}

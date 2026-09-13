using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using FirstOption.RevitMcp.Shared;

namespace FirstOption.RevitMcp.Addin.UI
{
    public sealed class ActivityRow
    {
        private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x15, 0x80, 0x3D));
        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xB9, 0x1C, 0x1C));

        public ActivityRow(ActivityEntry entry) { Entry = entry; }

        public ActivityEntry Entry { get; }
        public string Id => Entry.Id;
        public string Name => string.IsNullOrEmpty(Entry.CommandName) ? "ad-hoc script" : Entry.CommandName;
        public string StatusGlyph => Entry.Ok ? "✓" : "✗";
        public Brush StatusBrush => Entry.Ok ? OkBrush : ErrorBrush;
        public string TimeText => Entry.LocalTime.ToString("HH:mm:ss");

        public string Meta => string.Join(" · ", new[]
        {
            Entry.Client,
            Entry.Language == "csharp" ? "C#" : Entry.Language == "ironpython" ? "IronPython" : Entry.Language,
            Entry.DurationMs >= 1000 ? (Entry.DurationMs / 1000.0).ToString("0.0") + " s" : Entry.DurationMs + " ms",
        }.Where(s => !string.IsNullOrEmpty(s)));

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    public partial class McpPanel : Page, IDockablePaneProvider
    {
        private static readonly Brush Online = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
        private static readonly Brush Offline = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

        private readonly ObservableCollection<ActivityRow> _rows = new ObservableCollection<ActivityRow>();
        private readonly HashSet<string> _seenPushes = new HashSet<string>();
        private readonly DateTime _sessionStart = DateTime.Now;
        private readonly int _pid = Process.GetCurrentProcess().Id;
        private readonly DispatcherTimer _timer;

        private DateTime _clearedAt = DateTime.MinValue;
        private long _logLength = -1;
        private DateTime _logStamp = DateTime.MinValue;
        private string _newestId;
        private int _myPort;
        private int _tick;
        private bool _busy;
        private string _bannerUrl;

        public McpPanel()
        {
            InitializeComponent();
            CommandsList.ItemsSource = _rows;
            VersionText.Text = "v" + typeof(McpPanel).Assembly.GetName().Version.ToString(3);

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (s, e) => await RefreshAsync(++_tick % 3 == 0, false);
            _timer.Start();
            Loaded += async (s, e) => await RefreshAsync(true, true);
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }

        public void Stop() => _timer.Stop();

        private async Task RefreshAsync(bool probe, bool forceLog)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var settings = McpSettings.Load();
                if (probe)
                {
                    var result = await Task.Run(() => StatusProbe.Find(settings, _pid));
                    ApplyStatus(result, settings);
                    LibraryText.Text = await Task.Run(() => LibrarySummary(settings));
                }

                var info = new FileInfo(AppPaths.ActivityFile);
                var length = info.Exists ? info.Length : 0;
                var stamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                if (forceLog || length != _logLength || stamp != _logStamp)
                {
                    _logLength = length;
                    _logStamp = stamp;
                    var entries = await Task.Run(() => ActivityLog.ReadTail(400));
                    ApplyEntries(entries, settings);
                }
            }
            catch (Exception ex)
            {
                CheckedText.Text = "Panel error: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private void ApplyStatus(ProbeResult r, McpSettings settings)
        {
            CheckedText.Text = "Last check: " + DateTime.Now.ToString("HH:mm:ss") + " · " + settings.EffectiveHost + ":" + settings.PortStart + "-" + (settings.PortStart + settings.PortCount - 1);
            var me = r.Mine;
            if (me != null)
            {
                _myPort = me.Port;
                StatusDot.Fill = Online;
                StatusText.Text = "pyRevit Routes online";
                PortText.Text = me.Port.ToString();
                RevitText.Text = me.RevitVersion ?? "-";
                DocumentText.Text = string.IsNullOrEmpty(me.Document) ? "(no document open)" : me.Document;
                PyRevitText.Text = me.PyRevit ?? "-";
                PythonText.Text = me.Python ?? "-";
                RunnerText.Text = me.CSharpRunner ? "ready (Roslyn)" : "not found by the bridge";
                OfflineHint.Visibility = Visibility.Collapsed;
                return;
            }

            var previousPort = _myPort;
            _myPort = 0;
            StatusDot.Fill = Offline;
            StatusText.Text = "pyRevit Routes offline";
            PortText.Text = "-";
            RevitText.Text = "-";
            DocumentText.Text = "-";
            PyRevitText.Text = "-";
            PythonText.Text = "-";
            RunnerText.Text = "loaded, waiting for the bridge";
            OfflineHint.Text = r.Others.Count > 0
                ? "The bridge answers on port " + string.Join(", ", r.Others.Select(o => o.Port)) + " for another Revit, not this one. Reload pyRevit in this Revit."
                : "No answer. In pyRevit Settings turn on the Routes server (or run: pyrevit configs routes enable), make sure FirstOptionMCP.extension is installed, then reload pyRevit.";
            OfflineHint.Visibility = Visibility.Visible;
            if (previousPort != 0) _newestId = null;
        }

        private void ApplyEntries(List<ActivityEntry> entries, McpSettings settings)
        {
            var executes = entries.Where(e => e.Kind == ActivityKinds.Execute && (_myPort == 0 || e.Port == _myPort)).ToList();
            var session = executes.Where(e => e.LocalTime >= _sessionStart).ToList();
            var failed = session.Count(e => !e.Ok);
            SessionCountText.Text = session.Count + (session.Count == 1 ? " command" : " commands") + (failed > 0 ? " (" + failed + " failed)" : "");
            var last = executes.LastOrDefault();
            LastCallText.Text = last == null ? "-" : last.LocalTime.ToString("HH:mm:ss") + (string.IsNullOrEmpty(last.Client) ? "" : " by " + last.Client);

            var visible = executes.Where(e => e.LocalTime > _clearedAt).Reverse().Take(200).ToList();
            var newest = visible.FirstOrDefault()?.Id;
            if (newest != _newestId || visible.Count != _rows.Count)
            {
                var selectedId = (CommandsList.SelectedItem as ActivityRow)?.Id;
                _rows.Clear();
                foreach (var e in visible) _rows.Add(new ActivityRow(e));
                _newestId = newest;
                if (selectedId != null) CommandsList.SelectedItem = _rows.FirstOrDefault(x => x.Id == selectedId);
            }
            ListCountText.Text = _rows.Count.ToString();
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var push in entries.Where(e => e.Kind == ActivityKinds.GitHubPush))
            {
                if (!_seenPushes.Add(push.Id)) continue;
                if (push.LocalTime < _sessionStart || !settings.NotifyOnPush) continue;
                ShowBanner(push);
            }
        }

        private void ShowBanner(ActivityEntry push)
        {
            BannerText.Text =
                push.Files + (push.Files == 1 ? " file" : " files") + " to " + push.Repo + " @ " + push.Commit +
                (string.IsNullOrEmpty(push.Message) ? "" : "\n" + push.Message) +
                "\n" + push.LocalTime.ToString("HH:mm:ss") + (string.IsNullOrEmpty(push.Client) ? "" : " by " + push.Client);
            _bannerUrl = push.Url;
            PushBanner.Visibility = Visibility.Visible;
        }

        private static string LibrarySummary(McpSettings settings)
        {
            var panel = AppPaths.LibraryPanelDir(settings.EffectiveLibraryPath);
            var count = Directory.Exists(panel) ? Directory.GetDirectories(panel, "*.pushbutton").Length : 0;
            var github = settings.GitHubConfigured ? " · " + settings.GitHubOwner + "/" + settings.GitHubRepo : " · GitHub not set";
            return count + (count == 1 ? " command" : " commands") + github;
        }

        private void CommandsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(CommandsList.SelectedItem is ActivityRow row))
            {
                DetailsPanel.Visibility = Visibility.Collapsed;
                return;
            }
            var entry = row.Entry;
            CodeLabel.Text = "Code (" + (entry.Language == "csharp" ? "C#" : entry.Language) + ")";
            CodeBox.Text = entry.Code ?? "";
            OutputLabel.Text = entry.Ok ? "Output" : "Error";
            OutputLabel.Foreground = entry.Ok ? Brushes.Black : (Brush)FindResource("ErrorBrush");
            OutputBox.Text = string.Join("\n", new[] { entry.Output, entry.Error }.Where(s => !string.IsNullOrWhiteSpace(s)));
            DetailsPanel.Visibility = Visibility.Visible;
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(true, true);

        private void CloseBanner_Click(object sender, RoutedEventArgs e) => PushBanner.Visibility = Visibility.Collapsed;

        private void BannerView_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_bannerUrl)) Process.Start(new ProcessStartInfo(_bannerUrl) { UseShellExecute = true });
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(CodeBox.Text ?? ""); } catch { /* clipboard busy */ }
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.DataDir + "\"") { UseShellExecute = true });

        private async void Clear_Click(object sender, RoutedEventArgs e)
        {
            _clearedAt = DateTime.Now;
            await RefreshAsync(false, true);
        }

        private async void GitHub_Click(object sender, RoutedEventArgs e)
        {
            GitHubSettingsWindow.ShowFor(Process.GetCurrentProcess().MainWindowHandle);
            await RefreshAsync(true, true);
        }
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Audio.Vst3;
using WaveLab.Util;

namespace WaveLab.Views;

/// <summary>
/// What is installed, what survived a scan, and what will be offered in the rack.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rejections are listed, not hidden.</b> A plugin that failed is more interesting than one that
/// worked: if something the user paid for is missing from the Add Effect menu, the question is why,
/// and a list of only the successes leaves them wondering whether the scan even reached it. Each
/// rejection carries the scanner's own reason.
/// </para>
/// <para>
/// The scan itself runs one child process per plugin — see <see cref="Vst3Catalogue"/> — so a plugin
/// that faults on load is recorded rather than fatal. That is why this dialog can be honest about
/// crashes at all: the app is still running to report them.
/// </para>
/// </remarks>
public partial class Vst3ManagerDialog : Window
{
    private static readonly Brush UsableName = new SolidColorBrush(Color.FromRgb(0xE7, 0xEA, 0xEE));
    private static readonly Brush RejectedName = new SolidColorBrush(Color.FromRgb(0x9C, 0x84, 0x80));
    private static readonly Brush OkPill = new SolidColorBrush(Color.FromRgb(0x1B, 0x4A, 0x44));
    private static readonly Brush OkText = new SolidColorBrush(Color.FromRgb(0x9B, 0xE9, 0xDC));
    private static readonly Brush WarnPill = new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x1B));
    private static readonly Brush WarnText = new SolidColorBrush(Color.FromRgb(0xF0, 0xC4, 0x86));
    private static readonly Brush ErrorPill = new SolidColorBrush(Color.FromRgb(0x4A, 0x23, 0x20));
    private static readonly Brush ErrorText = new SolidColorBrush(Color.FromRgb(0xE8, 0xA2, 0x9C));
    private static readonly Brush FolderPresent = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xA9));
    private static readonly Brush FolderMissing = new SolidColorBrush(Color.FromRgb(0x6A, 0x5A, 0x58));
    private static readonly Brush MutedNote = new SolidColorBrush(Color.FromRgb(0x6A, 0x71, 0x7A));
    private static readonly Brush WarningNote = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54));

    private readonly ObservableCollection<FolderRow> _folders = [];
    private readonly List<PluginRow> _all = [];
    private CancellationTokenSource? _scan;
    private bool _rowsChanged;

    static Vst3ManagerDialog()
    {
        UsableName.Freeze();
        RejectedName.Freeze();
        OkPill.Freeze();
        OkText.Freeze();
        WarnPill.Freeze();
        WarnText.Freeze();
        ErrorPill.Freeze();
        ErrorText.Freeze();
        FolderPresent.Freeze();
        FolderMissing.Freeze();
        MutedNote.Freeze();
        WarningNote.Freeze();
    }

    public Vst3ManagerDialog()
    {
        InitializeComponent();
        folderList.ItemsSource = _folders;
        Loaded += (_, _) =>
        {
            Vst3PluginHost.Instance.EnsureCatalogueLoaded();
            RefreshFolders();
            RefreshRows();
        };
        Closed += (_, _) =>
        {
            _scan?.Cancel();
            if (_rowsChanged) AppSettings.Instance.Save();
        };
    }

    /// <summary>True when the catalogue changed, so the caller knows to rebuild any plugin menus.</summary>
    public bool CatalogueChanged { get; private set; }

    // ── folders ──────────────────────────────────────────────────

    private sealed record FolderRow(string Path, string CountText, bool CanRemove, Brush PathBrush);

    private void RefreshFolders()
    {
        _folders.Clear();
        var results = Vst3PluginHost.Instance.Catalogue.Results;

        foreach (string folder in Vst3PluginHost.ScanFolders)
        {
            bool exists = Directory.Exists(folder);
            int found = results.Count(r =>
                r.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
            bool isDefault = Vst3Catalogue.DefaultFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);

            _folders.Add(new FolderRow(
                folder,
                exists ? $"{found} found" : "folder not present",
                !isDefault,
                exists ? FolderPresent : FolderMissing));
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder to scan for VST3 plugins",
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true) return;

        string chosen = picker.FolderName;
        if (string.IsNullOrWhiteSpace(chosen)) return;
        if (Vst3PluginHost.ScanFolders.Contains(chosen, StringComparer.OrdinalIgnoreCase)) return;

        (AppSettings.Instance.Vst3ExtraFolders ??= []).Add(chosen);
        AppSettings.Instance.Save();
        RefreshFolders();
        statusText.Text = $"Added {chosen} · rescan to pick up what is in it.";
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string folder }) return;

        (AppSettings.Instance.Vst3ExtraFolders ??= []).RemoveAll(
            p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        AppSettings.Instance.Save();

        // The plugins found there are forgotten with it, otherwise they would go on being offered
        // from a folder that is no longer being looked at.
        foreach (Vst3ScanResult result in Vst3PluginHost.Instance.Catalogue.Results.ToArray())
            if (result.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                Vst3PluginHost.Instance.Catalogue.Forget(result.Path);

        Vst3PluginHost.Instance.SaveCatalogue();
        CatalogueChanged = true;
        RefreshFolders();
        RefreshRows();
    }

    // ── the table ────────────────────────────────────────────────

    private sealed class PluginRow(Vst3ScanResult scan)
    {
        public Vst3ScanResult Scan { get; } = scan;

        public string Path => Scan.Path;
        public string Name => string.IsNullOrWhiteSpace(Scan.Name)
            ? System.IO.Path.GetFileNameWithoutExtension(Scan.Path)
            : Scan.Name;
        public string Vendor => string.IsNullOrWhiteSpace(Scan.Vendor) ? "—" : Scan.Vendor;
        public string Category => string.IsNullOrWhiteSpace(Scan.Category) ? "—" : Scan.Category;
        public bool IsUsable => Scan.IsUsable;

        public string ChannelsText => IsUsable
            ? $"{Scan.InputChannels} / {Scan.OutputChannels}"
            : "—";

        public string ParametersText => IsUsable ? Scan.Parameters.ToString() : "—";

        public bool Allowed { get; set; } = Vst3PluginHost.IsAllowed(scan.Path);

        public Brush NameBrush => IsUsable ? UsableName : RejectedName;

        public string StatusText => Scan.Outcome switch
        {
            Vst3ScanOutcome.Usable => Scan.Parameters > 0 ? "USABLE" : "NO PARAMS",
            Vst3ScanOutcome.Rejected => "REJECTED",
            Vst3ScanOutcome.Crashed => "CRASHED",
            _ => "FAILED",
        };

        public Brush StatusBackground => Scan.Outcome switch
        {
            Vst3ScanOutcome.Usable => OkPill,
            Vst3ScanOutcome.Crashed => ErrorPill,
            _ => WarnPill,
        };

        public Brush StatusForeground => Scan.Outcome switch
        {
            Vst3ScanOutcome.Usable => OkText,
            Vst3ScanOutcome.Crashed => ErrorText,
            _ => WarnText,
        };
    }

    private void RefreshRows()
    {
        _all.Clear();
        foreach (Vst3ScanResult scan in Vst3PluginHost.Instance.Catalogue.Results
                     .OrderByDescending(r => r.IsUsable)
                     .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            _all.Add(new PluginRow(scan));

        ApplyFilter();
        UpdateCounts();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (list == null) return;

        string needle = (filterBox?.Text ?? "").Trim();
        if (filterHint != null)
            filterHint.Visibility = needle.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool includeFailed = showFailed?.IsChecked == true;

        list.ItemsSource = _all.Where(row =>
            (includeFailed || row.IsUsable)
            && (needle.Length == 0
                || row.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || row.Vendor.Contains(needle, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    private void UpdateCounts()
    {
        int total = _all.Count;
        int usable = _all.Count(r => r.IsUsable);
        int blocked = _all.Count(r => r.IsUsable && !r.Allowed);

        statusText.Text = total == 0
            ? "Nothing scanned yet — press Rescan everything."
            : $"{total} scanned · {usable} usable · {total - usable} rejected"
              + (blocked > 0 ? $" · {blocked} not offered" : "");
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (list.SelectedItem is not PluginRow row)
        {
            detailPath.Text = detailFacts.Text = detailMessage.Text = "";
            return;
        }

        Vst3ScanResult scan = row.Scan;
        detailPath.Text = scan.Path;

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(scan.Version)) facts.Add($"version {scan.Version}");
        if (!string.IsNullOrWhiteSpace(scan.SdkVersion)) facts.Add(scan.SdkVersion);
        if (row.IsUsable) facts.Add($"latency {scan.LatencySamples} smp");
        detailFacts.Text = facts.Count > 0 ? string.Join("  ·  ", facts) : "";

        // The scanner's note is a warning only when the plugin is not going to be usable. On a
        // plugin that works, "no host-visible parameters" is a fact about it, and colouring a fact
        // like a fault teaches the user to distrust the colour.
        detailMessage.Text = scan.Message;
        detailMessage.Foreground = row.IsUsable ? MutedNote : WarningNote;
    }

    private void OnAllowedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PluginRow row }) return;

        Vst3PluginHost.SetAllowed(row.Path, row.Allowed);
        _rowsChanged = true;
        CatalogueChanged = true;
        UpdateCounts();
    }

    // ── scanning ─────────────────────────────────────────────────

    private void OnRescanAll(object sender, RoutedEventArgs e) => _ = RescanAsync(full: true);

    private void OnRescanChanged(object sender, RoutedEventArgs e) => _ = RescanAsync(full: false);

    private async Task RescanAsync(bool full)
    {
        if (_scan != null) return;

        _scan = new CancellationTokenSource();
        rescanButton.IsEnabled = rescanChangedButton.IsEnabled = false;
        try
        {
            var progress = new Progress<(int Done, int Total, string Name)>(p =>
                statusText.Text = p.Total == 0
                    ? "No plugins found in the scan folders."
                    : $"Scanning {p.Done + 1} of {p.Total} — {p.Name}");

            int scanned = await Vst3PluginHost.Instance.RefreshAsync(full, progress, _scan.Token);
            CatalogueChanged = true;
            RefreshFolders();
            RefreshRows();

            if (scanned == 0 && _all.Count > 0)
                statusText.Text += " · nothing had changed since the last scan";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            statusText.Text = $"The scan stopped: {ex.Message}";
        }
        finally
        {
            _scan?.Dispose();
            _scan = null;
            rescanButton.IsEnabled = rescanChangedButton.IsEnabled = true;
        }
    }

    // ── footer ───────────────────────────────────────────────────

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if (list.SelectedItem is not PluginRow row) return;

        Vst3PluginHost.Instance.Catalogue.Forget(row.Path);
        Vst3PluginHost.Instance.SaveCatalogue();
        CatalogueChanged = true;
        RefreshRows();
        statusText.Text = $"{row.Name} forgotten · it will be scanned again on the next rescan.";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        string? folder = list.SelectedItem is PluginRow row
            ? Path.GetDirectoryName(row.Path)
            : Vst3PluginHost.ScanFolders.FirstOrDefault(Directory.Exists);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            statusText.Text = $"Could not open the folder: {ex.Message}";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ReolinkDBWin.Core.Discovery;

namespace ReolinkDBWin.Views;

public partial class DiscoveryWindow : Window
{
    private readonly string? _username;
    private readonly string? _password;
    private readonly ObservableCollection<Row> _rows = [];
    private CancellationTokenSource? _cts;

    public DiscoveryWindow(string? username, string? password)
    {
        _username = username;
        _password = password;
        InitializeComponent();
        Results.ItemsSource = _rows;
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    public DiscoveredDevice? SelectedDevice { get; private set; }

    private async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _rows.Clear();
        RescanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        var discovery = new ReolinkDiscovery();
        discovery.Progress += (_, msg) => Dispatcher.BeginInvoke(() => StatusText.Text = msg);
        discovery.DeviceFound += (_, d) => Dispatcher.BeginInvoke(() => Upsert(d));
        try
        {
            var results = await Task.Run(() => discovery.DiscoverAsync(_username, _password, scanSubnet: true, ct), ct);
            foreach (var d in results) Upsert(d);
            StatusText.Text = results.Count == 0
                ? "Aucun appareil trouvé. Vérifiez que ce PC est sur le même réseau que la sonnette."
                : $"{results.Count} appareil(s) trouvé(s).";
            var best = _rows.FirstOrDefault(r => r.Device.IsReolink && (r.Device.Model?.Contains("doorbell", StringComparison.OrdinalIgnoreCase) ?? false))
                       ?? _rows.FirstOrDefault(r => r.Device.IsReolink);
            if (best is not null) Results.SelectedItem = best;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Erreur : " + ex.Message;
        }
        finally
        {
            RescanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void Upsert(DiscoveredDevice d)
    {
        var existing = _rows.FirstOrDefault(r => r.Device.Host == d.Host);
        var row = new Row(d);
        if (existing is null)
        {
            // Les appareils Reolink en premier.
            var index = d.IsReolink ? _rows.TakeWhile(r => r.Device.IsReolink).Count() : _rows.Count;
            _rows.Insert(index, row);
        }
        else
        {
            var selected = ReferenceEquals(Results.SelectedItem, existing);
            _rows[_rows.IndexOf(existing)] = row;
            if (selected) Results.SelectedItem = row;
        }
    }

    private void Results_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UseButton.IsEnabled = Results.SelectedItem is Row;

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is Row) UseButton_Click(sender, e);
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not Row row) return;
        SelectedDevice = row.Device;
        DialogResult = true;
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    public sealed record Row(DiscoveredDevice Device)
    {
        public string Host => Device.Host;
        public string Name => Device.Name ?? "";
        public string Model => Device.Model ?? "";
        public string ReolinkText => Device.IsReolink ? "Oui" : "?";
        public string Info => Device.Details ?? $"{Device.Method}{(Device.Port > 0 ? $" :{Device.Port}" : "")}";
    }
}

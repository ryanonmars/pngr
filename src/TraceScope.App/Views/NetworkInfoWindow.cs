using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using TraceScope.Core;

namespace TraceScope.App.Views;

public sealed class NetworkInfoWindow : Window
{
    private readonly Func<string, Task> _traceTarget;
    private readonly DataGrid _interfaces;
    private readonly DataGrid _neighbors;
    private readonly TextBlock _status;
    private readonly TextBlock _gatewayText;
    private readonly Button _refresh;
    private readonly Button _traceGateway;
    private readonly CancellationTokenSource _closed = new();
    private string? _gateway;
    private bool _refreshing;

    public NetworkInfoWindow(Func<string, Task> traceTarget)
    {
        _traceTarget = traceTarget;
        Title = "pngr · Local Network";
        Width = 1000; Height = 590; MinWidth = 650; MinHeight = 400;
        Background = Brush.Parse("#191A1D"); Foreground = Brush.Parse("#D5D8DE");
        _refresh = new Button { Name = "NetworkRefresh", Content = "Refresh" };
        _traceGateway = new Button { Name = "TraceGateway", Content = "Trace gateway", IsEnabled = false };
        _gatewayText = new TextBlock { Text = "Gateway: reading…", VerticalAlignment = VerticalAlignment.Center };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(10, 8) };
        toolbar.Children.Add(_refresh); toolbar.Children.Add(_traceGateway); toolbar.Children.Add(_gatewayText);
        _interfaces = CreateTable("NetworkInterfaces");
        AddColumn(_interfaces, "Interface", nameof(InterfaceRow.Name), 110);
        AddColumn(_interfaces, "State", nameof(InterfaceRow.Status), 65);
        AddColumn(_interfaces, "Type", nameof(InterfaceRow.Type), 105);
        AddColumn(_interfaces, "Addresses", nameof(InterfaceRow.Addresses), 260);
        AddColumn(_interfaces, "Gateway", nameof(InterfaceRow.Gateways), 130);
        AddColumn(_interfaces, "DNS", nameof(InterfaceRow.Dns), 180);
        AddColumn(_interfaces, "MTU", nameof(InterfaceRow.Mtu), 55);
        AddColumn(_interfaces, "MAC", nameof(InterfaceRow.Mac), 130);
        _neighbors = CreateTable("NetworkNeighbors");
        AddColumn(_neighbors, "Cached address", nameof(LocalNeighbor.Address), 180);
        AddColumn(_neighbors, "MAC", nameof(LocalNeighbor.MacAddress), 180);
        AddColumn(_neighbors, "Interface", nameof(LocalNeighbor.Interface), 160);
        AddColumn(_neighbors, "State", nameof(LocalNeighbor.State), 120);
        var heading = new TextBlock { Text = "Existing local neighbor cache · no subnet scanning", Margin = new Thickness(10, 8), Foreground = Brush.Parse("#A8ADB7") };
        _status = new TextBlock { Text = "Reading network interfaces…", Margin = new Thickness(10, 8), TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A8ADB7") };
        var traceNeighbor = new Button { Content = "Trace selected cached address", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 4) };
        traceNeighbor.Click += async (_, _) =>
        { if (_neighbors.SelectedItem is LocalNeighbor neighbor) await TraceAsync(neighbor.Address); };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*,Auto,Auto") };
        layout.Children.Add(toolbar);
        Grid.SetRow(_interfaces, 1); layout.Children.Add(_interfaces);
        Grid.SetRow(heading, 2); layout.Children.Add(heading);
        Grid.SetRow(_neighbors, 3); layout.Children.Add(_neighbors);
        Grid.SetRow(traceNeighbor, 4); layout.Children.Add(traceNeighbor);
        Grid.SetRow(_status, 5); layout.Children.Add(_status);
        Content = layout;
        _refresh.Click += async (_, _) => await RefreshAsync();
        _traceGateway.Click += async (_, _) => { if (_gateway is not null) await TraceAsync(_gateway); };
        Opened += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _closed.Cancel();
    }

    private static DataGrid CreateTable(string name) => new() { Name = name, AutoGenerateColumns = false,
        IsReadOnly = true, RowHeight = 23, ColumnHeaderHeight = 25,
        SelectionMode = DataGridSelectionMode.Single, Background = Brush.Parse("#1E2024") };
    private static void AddColumn(DataGrid table, string title, string property, int width)
        => table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = new DataGridLength(width) });

    public async Task RefreshAsync()
    {
        if (_refreshing || _closed.IsCancellationRequested) return;
        _refreshing = true; _refresh.IsEnabled = false;
        try
        {
            var token = _closed.Token;
            var inventory = await Task.Run(NetworkDiscovery.GetInterfaces, token);
            var gatewayTask = NetworkDiscovery.GetLocalGatewayAsync(token);
            var neighborTask = NetworkDiscovery.LocalNeighborsAsync(token);
            _interfaces.ItemsSource = inventory.Select(i => new InterfaceRow(i)).ToArray();
            _gateway = await gatewayTask;
            var neighbors = await neighborTask;
            if (token.IsCancellationRequested) return;
            _neighbors.ItemsSource = neighbors;
            _gatewayText.Text = "Gateway: " + (_gateway ?? "not reported by the operating system");
            _traceGateway.IsEnabled = _gateway is not null;
            _status.Text = $"{inventory.Count} interfaces · {neighbors.Count} cached neighbors · Updated {DateTime.Now:HH:mm:ss}. Only the selected address is monitored when you press Trace. Empty caches are normal.";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = "Could not read local network information: " + ex.Message; }
        finally { _refreshing = false; _refresh.IsEnabled = true; }
    }

    private async Task TraceAsync(string address)
    {
        try { await _traceTarget(address); _status.Text = "Started monitoring " + address; }
        catch (Exception ex) { _status.Text = "Could not start trace: " + ex.Message; }
    }

    public sealed record InterfaceRow(NetworkInterfaceInfo Info)
    {
        public string Name => Info.Name;
        public string Status => Info.Status;
        public string Type => Info.Type;
        public string Addresses => string.Join(", ", Info.Addresses);
        public string Gateways => string.Join(", ", Info.Gateways);
        public string Dns => string.Join(", ", Info.DnsServers);
        public string Mtu => Info.Mtu?.ToString() ?? "—";
        public string Mac => Info.MacAddress;
    }
}

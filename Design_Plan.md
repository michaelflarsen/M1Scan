# M1Scan — MaterialDesign UI Redesign Plan

## Context
Replace the plain dark-themed TabControl layout with a polished MaterialDesign sidebar-based UI. The scan/ViewModel logic is working and must not change — only the presentation layer (Views, HostInfo model properties, App.xaml resources) is in scope.

---

## Files Changed

| File | Change |
|------|--------|
| `M1Scan.csproj` | Add `MaterialDesignThemes` NuGet |
| `Models/HostInfo.cs` | Add `OtherPorts` computed property |
| `App.xaml` | Merge MD resources before DarkTheme.xaml |
| `Views/MainWindow.xaml` | Full redesign — sidebar + DataGrid |
| `Views/MainWindow.xaml.cs` | Page navigation, stats counts, search filter |

**No changes to**: ViewModels, Services, Utils/, RelayCommand.cs, scan logic, existing bindings.

---

## Step 1 — NuGet Package

```
dotnet add package MaterialDesignThemes
```

Adds `MaterialDesignThemes.Wpf` assembly which provides `PackIcon`, `Card`, `BundledTheme`, and MD style resources.

---

## Step 2 — HostInfo.cs: Add `OtherPorts`

`IsPort80Open/443/8080` booleans already exist — no change needed there.

Add to `Models/HostInfo.cs`:
```csharp
public string OtherPorts => _isPort502Open ? "502" : string.Empty;
```

Update the `IsPort502Open` setter's existing `OnPropertyChanged` call to also fire:
```csharp
OnPropertyChanged(nameof(OtherPorts));
```

This is purely additive — existing `Port502Text` remains untouched.

---

## Step 3 — App.xaml: Add MD Resources

Add these three entries to `ResourceDictionary.MergedDictionaries` **in this order** (MD first, DarkTheme last so our implicit global styles override MD defaults):

```xml
<materialDesign:BundledTheme
    BaseTheme="Dark"
    PrimaryColor="Blue"
    SecondaryColor="Cyan" />
<ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Defaults.xaml"/>
<ResourceDictionary Source="Resources/Themes/DarkTheme.xaml"/>
```

Add namespace to `<Application>`:
```
xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
```

---

## Step 4 — MainWindow.xaml.cs: Page Navigation + Stats + Search

Replace the current minimal code-behind with:

```csharp
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private MainViewModel _vm;
    private string _selectedPage = "Devices";
    private int _onlineCount;
    private int _offlineCount;
    private string _searchText = string.Empty;
    private ICollectionView? _filteredHosts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SelectedPage    { get => _selectedPage;  set { _selectedPage  = value; Notify(); UpdatePageVisibility(); } }
    public int    OnlineCount     { get => _onlineCount;   set { _onlineCount   = value; Notify(); } }
    public int    OfflineCount    { get => _offlineCount;  set { _offlineCount  = value; Notify(); } }
    public ICollectionView? FilteredHosts { get => _filteredHosts; set { _filteredHosts = value; Notify(); } }
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; Notify(); _filteredHosts?.Refresh(); }
    }

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Wire CollectionChanged to refresh stats and filter view
        _vm.NetworkScanVm.DiscoveredHosts.CollectionChanged += (_, _) => RefreshStats();

        var view = CollectionViewSource.GetDefaultView(_vm.NetworkScanVm.DiscoveredHosts);
        view.Filter = obj => obj is HostInfo h && MatchesSearch(h);
        FilteredHosts = view;

        UpdatePageVisibility();
    }

    private bool MatchesSearch(HostInfo h)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var q = _searchText.ToLowerInvariant();
        return h.IpAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || h.HostName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || h.Vendor.Contains(q, StringComparison.OrdinalIgnoreCase)
            || h.MacAddress.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshStats()
    {
        OnlineCount  = _vm.NetworkScanVm.DiscoveredHosts.Count(h => h.IsReachable);
        OfflineCount = _vm.NetworkScanVm.DiscoveredHosts.Count(h => !h.IsReachable);
    }

    private void UpdatePageVisibility()
    {
        // Named panels in XAML: DevicesPanel, AdaptersPanel, IpConfigPanel
        // + placeholder panels: PortsPanel, PingPanel, etc.
        if (DevicesPanel   != null) DevicesPanel.Visibility   = _selectedPage == "Devices"  ? Visibility.Visible : Visibility.Collapsed;
        if (AdaptersPanel  != null) AdaptersPanel.Visibility  = _selectedPage == "Adapters" ? Visibility.Visible : Visibility.Collapsed;
        if (IpConfigPanel  != null) IpConfigPanel.Visibility  = _selectedPage == "IpConfig" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SideNav_Click(object sender, RoutedEventArgs e)
        => SelectedPage = ((FrameworkElement)sender).Tag?.ToString() ?? "Devices";

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

---

## Step 5 — MainWindow.xaml: Full Redesign

### Window root
```xml
<Window ... Width="1200" Height="760"
        xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
        Style="{StaticResource MaterialDesignWindow}">
```

### Root layout (2 columns)
```
Grid
├── Column 0 (200px) — Sidebar
└── Column 1 (*)     — Content area
```

### Sidebar (column 0)
Background `#0F0F0F` (deeper than main bg). Sections:

```
Border (full height, bg #0F0F0F)
└── DockPanel
    ├── Logo area (top, 56px): PackIcon kind=Radar (Foreground="{StaticResource PrimaryHueMidBrush}") + "M1Scan" text
    ├── ScrollViewer (*)
    │   ├── Section header "SCAN" (label style, muted text)
    │   ├── NavButton Tag="Devices"  icon=Devices       "Devices"
    │   ├── NavButton Tag="Ports"    icon=Ethernet       "Ports"  (placeholder)
    │   ├── NavButton Tag="Ping"     icon=Radar          "Ping monitor" (placeholder)
    │   ├── Section header "ANALYSE"
    │   ├── NavButton Tag="Finger"   icon=Fingerprint    "Fingerprints" (placeholder)
    │   ├── NavButton Tag="OUI"      icon=DatabaseSearch "OUI Lookup"   (placeholder)
    │   └── NavButton Tag="History"  icon=History        "Historik"     (placeholder)
    └── NavButton (bottom-docked) Tag="IpConfig" icon=Cog "Settings"
```

Each NavButton is a `RadioButton` (GroupName="SideNav") styled as a flat button with `Click="SideNav_Click"`. Active state: left blue border + accent bg. Use `Style="{StaticResource MaterialDesignFlatButton}"` as a base with local overrides.

### Content area (column 1)
```
Grid (3 rows: Auto, Auto, *)
├── Row 0: TopBar (48px)
├── Row 1: StatsRow (88px) — only visible on Devices page
└── Row 2: Content panels
```

**TopBar (Row 0):**
```
DockPanel bg=#1A1A1A, Padding="16,0"
├── Right: ScanButton (MaterialDesignRaisedButton, Command={Binding NetworkScanVm.ScanNetworkCommand})
│         CancelButton (visible when IsScanning)
├── Right: SearchBox (materialDesign:HintAssist.Hint="Search...", Width=220, Text={Binding SearchText, RelativeSource=Window})
└── Left:  TextBlock "M1Scan — {Binding NetworkScanVm.SubnetInput}.x" FontSize=16
           StatusBar TextBlock (smaller, muted, binds NetworkScanVm.StatusMessage)
```

**StatsRow (Row 1):** 4 `materialDesign:Card` items in a UniformGrid:
1. "Devices found" — `{Binding NetworkScanVm.DiscoveredHosts.Count}`
2. "Online" — `{Binding OnlineCount, RelativeSource={RelativeSource AncestorType=Window}}`
3. "Offline" — `{Binding OfflineCount, RelativeSource={RelativeSource AncestorType=Window}}`
4. "Status" — `{Binding NetworkScanVm.StatusMessage}` (last scan message)

**Progress bar:** Thin (4px) `ProgressBar` between stats and content rows, Value bound to `NetworkScanVm.ScanProgress`, visible only when `IsScanning`.

**Content panels (Row 2):** Three named Grids — visibility managed by `UpdatePageVisibility()` in code-behind:

- `x:Name="DevicesPanel"` — DataContext=`{Binding NetworkScanVm}`
- `x:Name="AdaptersPanel"` — DataContext inherited (MainViewModel)
- `x:Name="IpConfigPanel"` — DataContext=`{Binding IpConfigVm}`

AdaptersPanel and IpConfigPanel reuse the existing tab content almost verbatim (styles already reference existing DarkTheme keys — no changes needed).

### DevicesPanel DataGrid

Bind to `FilteredHosts` (the ICollectionView from code-behind):
```xml
<DataGrid ItemsSource="{Binding FilteredHosts, RelativeSource={RelativeSource AncestorType=Window}}"
          Style="{StaticResource MaterialDesignDataGrid}"
          ...existing attributes (IsReadOnly, AutoGenerateColumns=False)...>
```

**Columns** (exact order from spec):

| # | Header | Width | Binding/Template |
|---|--------|-------|------------------|
| 1 | — (status dot) | 28 | `Ellipse` 10×10, DataTrigger IsReachable→green/red fill |
| 2 | IP address | 115 | Hyperlink → CopyIpCommand (same as current) |
| 3 | Hostname | * | `{Binding HostName}` |
| 4 | MAC address | 135 | `{Binding MacAddress}` |
| 5 | Vendor | 110 | `{Binding Vendor}` |
| 6 | :80 | 36 | `PackIcon`, DataTrigger IsPort80Open→Check green/Minus #555 |
| 7 | :443 | 36 | `PackIcon`, DataTrigger IsPort443Open→Check green/Minus #555 |
| 8 | :8080 | 42 | `PackIcon`, DataTrigger IsPort8080Open→Check green/Minus #555 |
| 9 | Other | 60 | `Border` badge with `TextBlock Text={Binding OtherPorts}`, Visibility=Collapsed when empty |
| 10 | ms | 48 | `{Binding ResponseTime}` |

Port icon template pattern (no converter needed — pure DataTriggers):
```xml
<DataTemplate>
    <materialDesign:PackIcon x:Name="ico" Kind="Minus" Width="16" Height="16"
                             Foreground="#555555" HorizontalAlignment="Center"/>
    <DataTemplate.Triggers>
        <DataTrigger Binding="{Binding IsPort80Open}" Value="True">
            <Setter TargetName="ico" Property="Kind"       Value="Check"/>
            <Setter TargetName="ico" Property="Foreground" Value="#4CAF50"/>
        </DataTrigger>
    </DataTemplate.Triggers>
</DataTemplate>
```

Status dot pattern:
```xml
<DataTemplate>
    <Ellipse x:Name="dot" Width="10" Height="10" Fill="#F44336" HorizontalAlignment="Center"/>
    <DataTemplate.Triggers>
        <DataTrigger Binding="{Binding IsReachable}" Value="True">
            <Setter TargetName="dot" Property="Fill" Value="#4CAF50"/>
        </DataTrigger>
    </DataTemplate.Triggers>
</DataTemplate>
```

**ContextMenu** preserved verbatim (same PingHostCommand / CopyIpCommand + BindingProxy).

**RowStyle** DataTriggers for green/red row background preserved (same `#1A3320` / `#3A1A1A`).

**Adapter row** (above DataGrid): Keep `ComboBox` + refresh button + auto-detect + auto-refresh controls. These move to a toolbar-style row inside DevicesPanel above the DataGrid.

---

## Existing Bindings Preserved

All these must keep working without any change to ViewModels:
- `ScanNetworkCommand`, `CancelScanCommand`, `ClearResultsCommand`
- `AutoDetectSubnetCommand`, `ToggleAutoRefreshCommand`
- `RefreshAdaptersCommand`, `AutoDetectSubnetCommand`
- `PingHostCommand`, `CopyIpCommand`, `OpenInBrowserCommand`
- `SubnetInput`, `StartIp`, `EndIp`, `IsScanning`, `ScanProgress`, `StatusMessage`
- `DiscoveredHosts` (DataGrid source via FilteredHosts ICollectionView)
- IpConfigVm bindings (DHCP, StaticIP, FlushDns)
- MainViewModel adapter bindings (ActiveAdapters, InactiveAdapters)

---

## Verification

1. `dotnet build` — 0 errors (MD namespace, PackIcon usage, code-behind compile)
2. `dotnet run` — window opens with sidebar visible, "Devices" page active
3. Click "Scan Network" → progress bar fills, DevicesPanel populates
4. Open/closed ports → green check / muted dash in port columns
5. Device with port 502 open → "502" badge in Other column
6. Type in search box → DataGrid filters by IP/hostname/vendor/MAC
7. Stats cards update: Devices, Online, Offline counts reflect scan results
8. Sidebar "Settings" click → IpConfigPanel visible, AdaptersPanel and DevicesPanel hidden
9. Sidebar "Network Adapters" (within Settings flow or separate nav) → AdaptersPanel visible
10. All existing right-click context menu items work on DataGrid rows

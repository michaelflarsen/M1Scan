# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
dotnet build
dotnet run
```

The app requires **Windows 10+**, **.NET 8.0 Runtime**, and **Administrator privileges** (for ARP/netsh operations). Launch from an elevated terminal or configure a manifest for auto-elevation.

No test project exists in the solution.

## Architecture

M1Scan is a WPF desktop utility (net8.0-windows) for Windows network management. It follows MVVM using `CommunityToolkit.Mvvm` v8.2.2.

### Layer overview

| Layer | Location | Responsibility |
|---|---|---|
| Views | `Views/` | XAML UI — single `MainWindow.xaml` with 3 tabs |
| ViewModels | `ViewModels/` | UI state and command logic |
| Services | `Services/` | Network and IP operations (interface + implementation) |
| Models | `Models/` | `HostInfo`, `NetworkAdapter`, `IpProfile` |
| Utils | `Utils/` | `OuiLookup` (hardcoded MAC→vendor dict), `RelayCommand`, `InverseBoolConverter` |

### Three UI tabs (each backed by its own ViewModel)

- **Network Adapters** (`MainViewModel`) — lists/refreshes system network interfaces
- **Network Scan** (`NetworkScanViewModel`) — ping single hosts or sweep subnet ranges; populates a `HostInfo` list with IP, MAC, TTL, vendor, port 80 status
- **IP Configuration** (`IpConfigViewModel`) — toggle DHCP/static IP on adapters; flush DNS cache

### Key design points

- `INetworkService` / `NetworkService` — async ping, ARP, NetBIOS, and port checks. All scan operations run on background threads; results are marshalled back via `ObservableCollection` on the UI thread.
- `IIpConfigService` / `IpConfigService` — shells out to `netsh` via `System.Diagnostics.Process` for IP changes and DNS flush.
- `HostInfo` includes TTL-based OS guessing and sorts by IP octets.
- `OuiLookup` is a static in-memory dictionary — no external lookup at runtime.
- Dark theme defined in `Resources/Themes/DarkTheme.xaml` and merged in `App.xaml`.

### Dependency injection

Services are instantiated manually in the ViewModels (no DI container). If adding new services, follow the existing constructor-injection pattern used in `NetworkScanViewModel` and `IpConfigViewModel`.


## Documentation — always use Context7
Always fetch up-to-date documentation via Context7 before writing or editing code that touches:
- CommunityToolkit.Mvvm (ObservableProperty, RelayCommand, ObservableObject)
- WPF / System.Windows (DispatcherTimer, Binding, DataTemplate)
- System.Net.NetworkInformation (Ping, NetworkInterface)
- System.Text.Json (serialization/deserialization)

## Adding new pages — required conventions
Each new UI page MUST follow this structure:
- Views/<PageName>View.xaml
- ViewModels/<PageName>ViewModel.cs
- Wired in MainWindow.xaml + MainViewModel.cs only

Never modify existing completed pages unless the task explicitly requires it:
- DevicesView.xaml / DevicesViewModel.cs  ← do not touch
- NetworkScanView.xaml / NetworkScanViewModel.cs  ← do not touch

## Persistent storage
User data (watchlists, profiles etc.) is saved as JSON to:
  %APPDATA%\M1Scan\<filename>.json
Use System.Text.Json. Create the directory if it does not exist.

## Elevation
The app requires Administrator privileges.
netsh calls use System.Diagnostics.Process with UseShellExecute = false.
If the manifest is missing, add app.manifest with requestedExecutionLevel level="requireAdministrator".
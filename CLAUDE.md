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

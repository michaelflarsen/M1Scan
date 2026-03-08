<!-- Use this file to provide workspace-specific custom instructions to Copilot. -->

# MMPing - Network Utility Application

**Project Type:** C# WPF Desktop Application  
**Framework:** .NET 8.0 Windows Desktop  
**Architecture:** MVVM (Model-View-ViewModel)

## Project Overview

MMPing is an advanced Windows network utility featuring:
- Network adapter management and monitoring
- Network scanning (Ping/ARP discoveries)
- IP address configuration (Static/DHCP)
- DNS flushing and adapter reset
- Modern dark theme UI

## Technology Stack

- **Language:** C# (Latest)
- **UI Framework:** WPF
- **Architecture:** MVVM with RelayCommand
- **Dependencies:** CommunityToolkit.Mvvm
- **Target:** Windows 10/11, .NET 8.0+

## Key Components

### Models (Data Contracts)
- `NetworkAdapter` - Network interface information
- `HostInfo` - Ping/discovered host data
- `IpProfile` - Saved IP configurations

### Services (Business Logic)
- `INetworkService` / `NetworkService` - Network operations (Ping, ARP, scanning)
- `IIpConfigService` / `IpConfigService` - IP configuration (Static/DHCP, DNS)

### ViewModels (MVVM)
- `MainViewModel` - Network adapter management
- `NetworkScanViewModel` - Network scanning logic
- `IpConfigViewModel` - IP configuration management

### Views (UI)
- `MainWindow.xaml` - Tabbed interface with three main sections

### Utils
- `RelayCommand` - MVVM command implementation
- `ObservableObject` - Base class for INotifyPropertyChanged

## Development Guidelines

1. **MVVM Pattern:** Keep UI logic in ViewModels, not code-behind
2. **Async/Await:** Use async methods for network operations
3. **Error Handling:** Wrap network calls in try-catch, show user-friendly messages
4. **Dark Theme:** All new UI components should follow DarkTheme.xaml styles
5. **Admin Elevation:** Network configuration requires elevation via netsh/ipconfig

## Building & Running

```bash
# Restore dependencies
dotnet restore

# Build project
dotnet build

# Run application
dotnet run

# Run with elevation (if needed for IP config)
# Right-click MMPing.exe > Run as Administrator
```

## Known Limitations

1. Network configuration changes require Windows 10/11
2. Some operations need administrator privileges
3. Scanning large subnets (255+ IPs) can be slow
4. Virtual adapters may not display
5. Firewall might block certain operations

## Code Style

- Use nullable reference types (`#nullable enable`)
- Follow C# naming conventions (PascalCase for public)
- Add XML documentation for public classes/methods
- Use dependency injection where applicable

## Testing Focus Areas

1. Network adapter enumeration
2. Ping functionality (Online/Offline hosts)
3. Static IP configuration
4. DHCP toggle operations
5. DNS flush command
6. Error handling for permission denied scenarios

---

**Status:** Initial setup complete  
**Last Updated:** March 8, 2026

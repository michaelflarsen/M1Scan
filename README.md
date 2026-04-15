# MMPing - Advanced Windows Network Utility

A professional Windows desktop application built with C# WPF for network management, scanning, and IP configuration.

## 🎯 Features

### Network Management
- **Network Adapter Monitoring** - View all network adapters with detailed information
- **Advanced Network Scanning** - Real-time ping/ARP scan discovering devices instantly as found
- **Comprehensive Device Discovery** - Finds devices that don't respond to ping via ARP table
- **Device Information** - Get IP addresses, MAC addresses, DNS servers, gateway info
- **Connection Status** - Monitor adapter status and connectivity
- **Port Scanning** - Check for open HTTP, HTTPS, SSH, SMB and other services

### IP Configuration
- **Static IP Assignment** - Configure static IP addresses with custom subnet and gateway
- **DHCP Management** - Enable/disable DHCP per adapter
- **DNS Flushing** - Clear DNS cache quickly
- **Adapter Reset** - Reset network adapters with one click
- **IP Profiles** - Save and load multiple IP configurations

### User Interface
- **Dark Theme** - Modern dark interface reducing eye strain
- **Tabbed Interface** - Organized sections for different tasks
- **Real-time Updates** - Live network adapter status
- **Status Messages** - Clear feedback on operations

## 🛠️ Technology Stack

- **Framework:** .NET 8.0 Windows Desktop
- **UI:** WPF (Windows Presentation Foundation)
- **Architecture:** MVVM (Model-View-ViewModel)
- **Networking:** System.Net.NetworkInformation
- **Admin Features:** netsh, ipconfig commands via elevation

## 📋 Requirements

- Windows 10 or later
- .NET 8.0 Runtime
- Administrator privileges (for IP configuration changes)
- Visual Studio 2022 (for development)

## 🚀 Installation

### From Source
1. Clone the repository
2. Open `MMPing.csproj` in Visual Studio 2022
3. Restore NuGet packages
4. Build the project (Ctrl+Shift+B)
5. Run with F5

### From Binary
- Download the latest release from GitHub
- Extract and run `MMPing.exe`
- Right-click and select "Run as administrator" for IP configuration features

## 📖 Usage

### Viewing Network Adapters
1. Launch the application
2. Go to "Network Adapters" tab
3. Click "Refresh Adapters" to see current devices
4. Select an adapter to view details

### Scanning Network for Devices
1. Go to "Network Scan" tab
2. Enter subnet (e.g., `192.168.1`)
3. Set IP range (e.g., 1-254)
4. Click "Scan Network"
5. View discovered devices

### Configuring IP Address
1. Go to "IP Configuration" tab
2. Select network adapter from dropdown
3. Choose DHCP or Static IP
4. Enter IP details if using static
5. Click "Apply"

### Pinging Single Host
1. Go to "Network Scan" tab
2. Enter hostname or IP address
3. Click "Ping"
4. View response time and status

## 🏗️ Project Structure

```
MMPing/
├── Models/                    # Data models
│   ├── NetworkAdapter.cs      # Network adapter info
│   ├── HostInfo.cs            # Ping/discovered host info
│   └── IpProfile.cs           # Saved IP configurations
├── ViewModels/                # MVVM ViewModels
│   ├── MainViewModel.cs       # Main adapter management
│   ├── NetworkScanViewModel.cs # Network scanning logic
│   └── IpConfigViewModel.cs   # IP configuration logic
├── Views/                     # XAML UI
│   └── MainWindow.xaml        # Main application window
├── Services/                  # Business logic
│   ├── NetworkService.cs      # Network operations
│   └── IpConfigService.cs     # IP configuration
├── Utils/                     # Utilities
│   └── RelayCommand.cs        # MVVM command implementation
├── Resources/
│   └── Themes/DarkTheme.xaml  # Dark theme styles
├── App.xaml                   # Application resources
└── MMPing.csproj              # Project file
```

## 🔐 Permissions

This application requires administrator privileges for:
- Setting static IP addresses
- Enabling/disabling DHCP
- Resetting network adapters
- Flushing DNS cache

The application will prompt for elevation when needed on Windows 10/11.

## 🎨 Dark Theme

All components feature a professional dark theme with:
- Dark background (#1E1E1E)
- Blue accent color (#0D47A1)
- Clear text and icons
- Hover and active states for feedback

## 🐛 Troubleshooting

### "Access Denied" when changing IP
- Run application as Administrator
- Windows Firewall or security software might block changes

### Network scan is slow
- Reduce the IP range (e.g., 1-100 instead of 1-254)
- Check your network connectivity
- Try scanning a smaller subnet

### Adapters not showing
- Click "Refresh Adapters"
- Check if adapters are enabled in Device Manager
- Some virtual adapters might not show

## 📄 License

MIT License - see LICENSE file for details

## 👤 Author

Michael Larsen (mm@nice1.dk)

## 🤝 Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Open a Pull Request

## 🚀 Future Enhancements

- [ ] ARP cache visualization
- [ ] MAC address database lookup
- [ ] Network traffic monitoring
- [ ] Port scanning
- [ ] Remote shutdown/restart
- [ ] Configuration export/import
- [ ] Multi-language support
- [ ] Automatic adapter discovery profiles

## 📞 Support

For issues, questions, or suggestions:
- Open an issue on GitHub
- Contact: mm@nice1.dk

---

**Made with ❤️ for Windows network administrators and IT professionals**

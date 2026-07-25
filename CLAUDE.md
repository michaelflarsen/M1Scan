# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
dotnet build
dotnet test        # 45 tests i M1Scan.Tests
dotnet run
```

The app requires **Windows 10+**, **.NET 8.0 Runtime**, and **Administrator privileges** (for ARP/netsh/raw sockets). `app.manifest` sets `requestedExecutionLevel = requireAdministrator`, so Windows prompts for elevation automatically.

**Bemærk:** `dotnet build` fejler med MSB3027 (fillås) hvis M1Scan kører. Stop processen først:
```powershell
Stop-Process -Name "M1Scan" -Force -ErrorAction SilentlyContinue
```

## Architecture

M1Scan is a WPF desktop utility (net8.0-windows) following MVVM.

### Layer overview

| Layer | Location | Responsibility |
|---|---|---|
| Views | `Views/` | XAML UI — `MainWindow.xaml` (shell + 3 indbyggede sider) plus 5 udskilte `*View.xaml` |
| ViewModels | `ViewModels/` | UI state and command logic |
| Services | `Services/` | Network, IP, diagnostics, export, update (interface + implementation) |
| Models | `Models/` | `HostInfo`, `NetworkAdapter`, `IpProfile`, `PingEntry`, `SniffedDevice`, `DashboardModels` |
| Utils | `Utils/` | `OuiLookup`, `RelayCommand` + `AsyncRelayCommand` + `ObservableObject`, `CrashLog`, konvertere |
| Controls | `Controls/` | `SparklineControl` |

### MVVM-basisklasser — vigtigt

Projektet bruger **egne** basisklasser i [Utils/RelayCommand.cs](Utils/RelayCommand.cs), ikke CommunityToolkit's source-generatorer. Der er ingen brug af `[ObservableProperty]` eller `[RelayCommand]`; properties skrives manuelt med `SetProperty`.

`CommunityToolkit.Mvvm` er stadig en dependency, men bruges kun til at `Models/HostInfo.cs` nedarver fra `CommunityToolkit.Mvvm.ComponentModel.ObservableObject`. **Alt andet** bruger `M1Scan.Utils.ObservableObject`. Der findes altså to `ObservableObject`-typer i kodebasen — tjek `using`-listen før du antager hvilken en fil bruger.

### Kommandoer — brug AsyncRelayCommand til async arbejde

`new RelayCommand(async _ => ...)` bliver til `async void`: en undtagelse der undslipper lambdaen dræber processen. Brug **`AsyncRelayCommand`** til alt asynkront — den await'er internt, fanger fejl til `onError` + `CrashLog`, og blokerer re-entrancy (ingen dobbeltklik-scanninger).

```csharp
ScanNetworkCommand = new AsyncRelayCommand(_ => ScanNetworkAsync(), _ => !IsScanning, OnCommandError);
```

`DispatcherTimer.Tick` og `event`-handlere er også `async void`-flader — pak deres krop i try/catch, eller kald en `AsyncRelayCommand`.

### Navigation og sidernes livscyklus

Navigationen slår `Visibility` til/fra på navngivne paneler i `MainWindow.UpdatePageVisibility()`. **Alle side-ViewModels konstrueres i `MainViewModel`'s constructor og lever hele appens levetid.**

Derfor: en side der har timere eller løbende netværkstrafik skal implementere [`IActivatablePage`](ViewModels/IActivatablePage.cs) og starte/stoppe dem i `OnActivated`/`OnDeactivated` — **ikke** i sin constructor. Ellers kører de fra opstart og for evigt, også for sider brugeren aldrig åbner. `HomeViewModel` og `WorkspaceViewModel` gør dette; aktiveringen drives fra `UpdatePageActivation()`.

### Key design points

- `INetworkService` / `NetworkService` — ping, ARP, NetBIOS, port checks, ICMP-sweep. Alt scan-arbejde kører på baggrundstråde; resultater marshalles til UI-tråden via `Dispatcher`.
  - `PingSweepBoundAsync` returnerer `SweepResult` med et `Error`-felt. **Tjek altid `Failed`** — en tom `Hosts` uden fejl betyder "ingen svarede", med fejl betyder det "vi kunne ikke spørge". De to må ikke forveksles i UI'et.
  - Sweeps serialiseres (`_sweepGate`), fordi en rå ICMP-socket modtager alle ICMP-svar til sin adresse og ellers ville stjæle et samtidigt sweeps svar.
- `IIpConfigService` / `IpConfigService` — kalder `netsh`/`ipconfig` via `Process` med `ArgumentList` (ingen manuel escaping). Returnerer `IpConfigResult` med `Success` + `Message`; **exit-koden tjekkes**, og `netsh`'s egen fejltekst vises til brugeren.
- `HostInfo.MergeFrom(other, authoritative)` er det ENE sted hvor to observationer af samme host flettes. `authoritative: true` kun ved eksplicit gen-ping af én host (hvor "nu offline" skal slå igennem). `NetworkScanViewModel._hostIndex` giver O(1)-opslag pr. IP.
- `HostInfo.DisplayName` — DNS-navn → NetBIOS-navn → IP. Reverse-DNS svigter for de fleste LAN-enheder, så NetBIOS er en reel kilde, ikke en nødløsning.
- `OuiLookup` læser hele IEEE MA-L-registret (~39.700 præfikser) fra `Resources/Data/oui.txt.gz`, indlejret i assemblyen og lazy-udpakket ved første opslag — intet netværksopslag ved runtime. Regenerér med `scripts/update-oui.py`.
- `IGeoIpService` — **slået fra som standard.** Opslaget sender rutens offentlige IP'er til ip-api.com, og gratisniveauet kan kun nås over HTTP (HTTPS svarer 403). Brugeren aktiverer det selv på Traceroute-siden; svaret behandles som upålideligt input og saniteres.
- `IUpdateService` — henter releases fra GitHub. Kræver en `SHA256:`-linje i release-noterne og verificerer hashen både under download og igen i det elevated updater-script. Se `.claude/commands/release.md`.
- Dark theme i `Resources/Themes/DarkTheme.xaml`, merged i `App.xaml`.

### Fejlhåndtering

`App.OnStartup` wirer `DispatcherUnhandledException`, `AppDomain.UnhandledException` og `TaskScheduler.UnobservedTaskException`. Uhåndterede fejl logges til `%APPDATA%\M1Scan\crash.log` via [`CrashLog`](Utils/CrashLog.cs), og Dispatcher-fejl vises som en dialog uden at lukke appen.

Undgå tomme `catch { }` i nye stier: hvis en fejl er forventet og uinteressant, skriv hvorfor i en kommentar. Konvertér **aldrig** en fejl til en succesværdi (tom liste, `true`, "0 fundet") — det var årsagen til flere alvorlige fejl i denne kodebase.

### Dependency injection

Ingen DI-container — `MainViewModel` er composition root: den opretter appens eneste service-instanser og sender dem videre via constructor-parametre, typet mod interfaces. Tilføjer du en ViewModel der har brug for en service, så tag den som constructor-parameter og wire den i `MainViewModel` — aldrig `new` en service inde i en ViewModel.

Kendte undtagelser fra reglen (ryd op hvis du er i nærheden): `OuiLookup.SetMacAliasService` er en statisk service-locator, `HomeViewModel` laver selv `new KnownDevicesStore()`, og `MainWindow`'s constructor laver `new MainViewModel()`.

## Documentation — always use Context7
Always fetch up-to-date documentation via Context7 before writing or editing code that touches:
- WPF / System.Windows (DispatcherTimer, Binding, DataTemplate)
- System.Net.NetworkInformation (Ping, NetworkInterface)
- System.Text.Json (serialization/deserialization)

## Adding new pages — required conventions
Each new UI page MUST follow this structure:
- `Views/<PageName>View.xaml`
- `ViewModels/<PageName>ViewModel.cs`
- Wired in `MainWindow.xaml` + `MainViewModel.cs` only
- Implementér `IActivatablePage` hvis siden har timere eller løbende netværkstrafik

`MainWindow.xaml` er ~1.500 linjer, fordi siderne **Adapters**, **Scan** og **IP-skift** stadig ligger inline som paneler i stedet for at være udskilt til egne `*View.xaml`. Nye sider skal ikke følge det mønster — udskil dem. (De tre er kandidater til at blive flyttet ud.)

## Persistent storage
User data er JSON i `%APPDATA%\M1Scan\`:
`known_devices.json`, `mac-aliases.json`, `dashboard.json`, `ui_settings.json`, `workspace.json`, `traceroute_settings.json`, `crash.log`.

Brug `System.Text.Json`, opret mappen hvis den mangler, og skriv **atomisk** (temp-fil + `File.Move(..., overwrite: true)`) — en afbrudt skrivning må ikke efterlade en tom fil hvor der før var brugbare data.

## Elevation
The app requires Administrator privileges (`app.manifest`). `netsh`-kald bruger `Process` med `UseShellExecute = false` og `ArgumentList`.

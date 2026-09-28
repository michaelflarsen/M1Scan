# Roadmap

Prioriteret liste over kommende arbejde for M1Scan. Gennemgås punkt for punkt med brugeren; punkter markeres ✅ når de er lavet i stedet for at blive slettet, så listen forbliver et historisk overblik.

Denne fil holdes i sync med Claude Codes egen memory (`project_roadmap` i `%APPDATA%`-agtig `.claude`-profil) — opdateres begge steder samtidig.

*Sidst opdateret: 2026-09-28*

*Nummerering: punkt-numrene i denne fil følger den historiske rækkefølge fra roadmappens oprettelse og ændres ikke, selv når underpunkter tilføjes (fx 3a, 3b).*

## 🔴 Høj prioritet

1. ✅ **Vendor/OS-fingerprinting i UI** — Vendor vises i NetworkScanView + HomeView, OsGuess (TTL) findes, og `Utils/DeviceFingerprint.cs` giver fuld regelbaseret enhedskategorisering (Router/Server/Printer/Kamera/TV/Telefon/NAS/PLC/IoT/Computer) med ikon+label, testdækket i `DeviceFingerprintTests.cs`.

2. ✅ **Åben-port-historik** — implementeret og live-testet fuldt færdig 2026-09-28.
   - Kerne: ny `port_events`-tabel i `HistoryService`/`history.db` (nøglet på MAC), "Port-historik…" context-menu i NetworkScanView, `PortHistoryDialog.xaml` med "Ryd historik"-knap (kun for den valgte enhed, bekræftelse først).
   - "Ændring i portopsætning"-UI: gul ring om den port-chip der skiftede (:80/:443/:8080/Other) + "!"-badge-kolonne med tooltip, uanset om porten åbnede eller lukkede. Nulstilles ved næste scan uden ny ændring.
   - Krævede fem separate rettelser i `NetworkScanViewModel`/`HostInfo.MergeFrom` før ændringssporingen var pålidelig på tværs af almindelige og flettede scans — se commit-historik og `.claude`-memory `project_hostinfo_mergefrom_gotcha` for detaljer, hvis lignende diff-logik skal bygges igen.
   - 171/171 tests grønne.

3. 🟡 **UI/integrationstests** — scopet 2026-09-28 på tværs af alle 12 ViewModels. Kun 2 har tests i dag: `PingMonitorViewModel` (10 tests, 2026-09-24) og `WorkspaceViewModel` — begge testbare fordi konstruktøren ikke laver rigtigt arbejde (timere/netværk starter først i `OnActivated`, IActivatablePage-mønsteret). De resterende 10:

   | ViewModel | Sværhedsgrad | Hvorfor |
   |---|---|---|
   | HistoryViewModel | ✅ Testet 2026-09-28 | 5 tests — `HistoryViewModelTests.cs`. |
   | MacAliasViewModel | ✅ Testet 2026-09-28 | 8 tests — `MacAliasViewModelTests.cs`. |
   | IpConfigViewModel | ✅ Testet 2026-09-28 | 7 tests — `IpConfigViewModelTests.cs`. |
   | UpdateViewModel | ✅ Testet 2026-09-28 | 4 tests — `UpdateViewModelTests.cs` (kun `CheckForUpdateSilentlyAsync`; `UpdateNowAsync`'s success-sti rammer `Application.Current.Shutdown()` og hører til en anden test-form). |
   | FindIpViewModel | 🟡 Kræver refaktor | `DispatcherTimer` + ubeskyttede `Application.Current.Dispatcher`-kald i MAC-opløsning. |
   | TracerouteViewModel | 🟡 Kræver refaktor | Konstruktør er fin, men trace/probe-kommandoerne crasher uden en WPF-Dispatcher. |
   | HomeViewModel | 🟡 Kræver refaktor | Implementerer allerede `IActivatablePage`, men konstruktøren fyrer stadig `_ = LoadAsync()` (rigtig ping/HTTP/disk) af med det samme — skal flyttes til `OnActivated`. |
   | NetworkScanViewModel | 🔴 Svær | ~14 kommandoer, ubeskyttede Dispatcher-kald, ægte OS-netværksabonnement (`NetworkChange`), kæmpe scan-metoder. |
   | MainViewModel | 🔴 Svær | Composition root — `new`'er alle services direkte, ingen DI-sømme uden en ekstra constructor-overload. |

   194/194 tests grønne (var 171, +23 nye). **Tilbage:** HomeViewModel (mest værdifuld, kræver kun at `LoadAsync()` flyttes til `OnActivated`), derefter FindIpViewModel/TracerouteViewModel, og til sidst de to "svære" hvis det stadig er relevant.

4. ✅ **Ryd inline-paneler ud af MainWindow.xaml** — udskilt til `NetworkScanView`/`AdaptersView`/`IpConfigView` 2026-09-23. `MainWindow.xaml` 1557 → ~650 linjer.

## 🟡 Medium prioritet

5. **Wake-on-LAN** — naturlig udvidelse af eksisterende ARP/MAC-data.
6. **Subnet/VLAN-beregner** — passer sammen med `IIpConfigService`.
7. **DNS-opslagsværktøj** (A/PTR/CNAME) — kan genbruge reverse-DNS/mDNS-logik fra `NetworkService`.
8. **Eksportformat-dækning** — bekræft/udvid hvilke formater `IExportService` understøtter (CSV/JSON minimum).

## 🟢 Lav prioritet

9. **Installer (MSI/MSIX) + code signing** — i dag selv-kontrolleret single-file exe med SHA-256-verificeret auto-update.
10. **Lys/mørk tema-toggle** — kun `DarkTheme.xaml` findes i dag.
11. **Lokalisering** — UI og commits er dansk-only i dag; kun relevant ved bredere deling.

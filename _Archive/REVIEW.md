# M1Scan — Grundig gennemgang

> Udført 2026-07-04 af Claude Code. Fire faser: kodegennemgang/arkitektur,
> performance, UX, nye features. Prioriteret liste nederst.
>
> **Version gennemgået:** v1.3.27 (net8.0-windows, WPF, CommunityToolkit.Mvvm 8.2.2,
> MaterialDesignThemes 5.3.2). Bemærk: `CLAUDE.md` er drevet ud af sync med koden
> (beskriver "tre tabs: Network Adapters / Network Scan / IP Configuration" og
> filer som `DevicesView.xaml` der ikke findes — appen har nu et sidebar-layout
> med Dashboard/Workspace/Scan/Adapters/Settings i én `MainWindow.xaml`). Se A1.
>
> **Status 2026-07-04:** **A8** (død kode i netværkslaget), **F1** (rigtig IEEE
> OUI-database), **A7** (dependency injection) og **F2** (CSV/JSON-eksport) er
> implementeret og verificeret — se markeringer nedenfor.

---

## FASE 1 — Kodegennemgang og arkitektur

### Overordnet vurdering

Netværkslaget er faktisk gennemtænkt: native ARP-cache-læsning via `GetIpNetTable2`
(ingen `arp.exe`-subprocess), ARP-flood samtidig med ping-sweep, batch-opdatering
af UI via en `ConcurrentQueue` der flushes af en 100 ms `DispatcherTimer`, og
throttling med `SemaphoreSlim`. Overlap-beskyttelse med `Interlocked`/`SemaphoreSlim`
i dashboardets samplere er korrekt. Kommentarerne forklarer *hvorfor*.

De væsentligste problemer er (a) **manglende adskillelse i `MainWindow.xaml.cs`** —
en del ægte view-model-tilstand og navigations-/menulogik ligger i code-behind,
(b) **ingen dependency injection** trods pæne interfaces, hvilket gør de fleste
ViewModels ikke-testbare, og (c) **død kode i netværkslaget** med et latent
parallelismeproblem.

### 1.1 MVVM-adskillelse

| # | Alvor | Fund |
|---|---|---|
| A1 | **Bør** | **`MainWindow.xaml.cs` (275 linjer) bærer ægte VM-tilstand.** `SelectedPage`, `OnlineCount`, `OfflineCount`, `LastScanTime`, `FilteredHosts`, `SearchText` + `MatchesSearch()`-filteret er præsentationslogik, der implementeres direkte på `Window` med dens egen `INotifyPropertyChanged`. Det hører til i en `ShellViewModel` (eller udvidet `MainViewModel`). Søgning/filtrering af `DiscoveredHosts` bør ligge i `NetworkScanViewModel`. |
| A2 | **Bør** | **Navigation sker imperativt i code-behind.** `UpdatePageVisibility()` (`MainWindow.xaml.cs:159`) tænder/slukker `Visibility` på fem navngivne paneler via en `switch` på en streng-tag. Idiomatisk WPF: en `ContentControl` bundet til en `CurrentView`-property + `DataTemplate`-mapping, eller MaterialDesigns egne navigations-primitiver. Nuværende form gør det umuligt at teste navigation og betyder at alle fem sider altid er instansieret og lever i det visuelle træ. |
| A3 | **Bør** | **View-konstruktion i C# — duplikeret.** Adapter-dropdown-menuen bygges manuelt med `Ellipse`/`DropShadowEffect`/`TextBlock`/`MenuItem` i både `MainWindow.xaml.cs:169-242` og `WorkspaceView.xaml.cs:61-136` — næsten identisk kode, inkl. to kopier af det samme `XamlReader.Parse`-baserede `_adapterItemTemplate`. Bør være én genbrugt `DataTemplate`/`Style` i XAML + en delt adapter-liste-kontrol. ~150 linjer duplikeret view-kode. |
| A4 | **Nice-to-have** | **`WorkspaceView.xaml.cs` sætter hover-farver i code-behind** (`MyIpCard_MouseEnter/Leave`, hardcodede `Color.FromRgb`). Hører til som `Trigger`/`VisualState` i temaet. |
| A5 | **Nice-to-have** | **`HomeView.xaml.cs` manipulerer `RowDefinitions[n].Height` direkte** for at vise/skjule grafer og diagnostik (`ApplyGraphRows`/`ApplyDiagRows`). Kunne drives af converters/`VisualStateManager`. *Bemærk:* selve connector-linjetegningen (`UpdateConnector`) er legitim view-kode — den kan ikke laves rent i XAML. (Perf-siden af den: se P6.) |
| A6 | **Observation** | Positivt: `HomeView`/`WorkspaceView` er ægte `UserControl`s med `DataContext`-bundne VMs, og alle Models/VMs bruger `SetProperty`. Grundstrukturen (Views/ViewModels/Services/Models/Utils) er ren. Problemet er koncentreret i `MainWindow` og de to menu-code-behinds. |

### 1.2 Dependency injection / testbarhed

| # | Alvor | Fund |
|---|---|---|
| A7 | ~~**Bør**~~ **✅ RETTET** | **Ingen DI — services `new`'es inde i hver ViewModel.** `HomeViewModel`, `NetworkScanViewModel`, `IpConfigViewModel` og `MainViewModel` lavede alle deres egne `new NetworkService()` osv. — 4 separate instanser, ingen af de tunge VMs kunne unit-testes med en fake service. CLAUDE.md hævdede fejlagtigt at `NetworkScanViewModel`/`IpConfigViewModel` allerede brugte constructor-injection (endnu et A21-tilfælde af dokumentationsdrift) — kun `WorkspaceViewModel` gjorde reelt. *Fix (2026-07-04): alle fire ViewModels tager nu deres services som constructor-parametre (`INetworkService`, `IIpConfigService`, `IDiagnosticsService`, `IExportService` — typet mod interfaces). `MainViewModel` er nu den eneste composition root: opretter hver service ÉN gang og sender dem ned. Ingen DI-container tilføjet — bevidst, jf. eksisterende "no DI container"-politik i CLAUDE.md, som er opdateret til at beskrive det nye, faktiske mønster. Verificeret: build 0 errors/0 warnings; statisk grep bekræfter kun ét `new NetworkService()`/`new IpConfigService()`/`new DiagnosticsService()`-kald i hele kodebasen (alle i `MainViewModel`); live-testet på en garanteret frisk proces (StartTime matchet) — Dashboard, Scan (inkl. OUI-vendor-opslag) og Adapters kørte alle korrekt med de injicerede services.* |

### 1.3 Netværkslag — scanning/discovery

Flowet (i `NetworkScanViewModel.ScanNetworkAsync`, den metode UI'et faktisk bruger):
**Fase 0+1** ARP-flood (`SendARP`, sem 64) samtidig med ping-sweep (`SemaphoreSlim(150)`,
`PingHostAsync` pr. IP). **Fase 2** MAC via native ARP-tabel (instant). **Fase 3**
port-tjek (80/443/8080/502, sem 50). **Fase 4** NetBIOS-navn (uthrottlet, kun online).
Resultater batches via `_uiQueue` → flushes hver 100 ms. Dette er en god pipeline.

| # | Alvor | Fund |
|---|---|---|
| A8 | ~~**Kritisk (kode)**~~ **✅ RETTET** | **Død kode med ubegrænset parallelisme.** `NetworkService.ScanNetworkAsync` (`:289-349`) oprettede en `PingHostAsync`-task for *hver* IP **uden nogen throttle** og lavede derefter uthrottlet NetBIOS på alle. Blev **aldrig** kaldt — UI'et bruger VM'ens egen throttlede kopi. *Fix (2026-07-04): `ScanNetworkAsync` + `GetArpInfoAsync` (ubrugt `arp -a`-shell) fjernet fra både `INetworkService` og `NetworkService`, inkl. det nu-ubrugte `using System.Diagnostics`. Verificeret: build 0 errors/0 warnings, ingen resterende referencer (kun VM'ens egen private, throttlede `ScanNetworkAsync(bool merge)` findes fortsat — den er korrekt og skal blive).* |
| A9 | **Bør** | **`PingHostAsync` ignorerer `CancellationToken` under selve ping'et.** `ping.SendPingAsync(hostOrIp, 600)` (`:235`) bruger ikke .NET 8-overloaden med `CancellationToken`. `ct` tjekkes først *før* TCP-fallback (`:271`). Ved annullering af en /24-scan fortsætter alle igangværende pings i op til 600 ms (+ TCP-fallback op til 4×300 ms for hosts der allerede var forbi ping-tjekket). Reel afbrydelses-latens. Fix: brug `SendPingAsync(host, TimeSpan.FromMilliseconds(600), … , ct)`-overloaden. |
| A10 | **Bør (perf, se Fase 2)** | **TCP-fallback kører for hver ikke-svarende host.** For hver IP der ikke svarer på ICMP åbnes 4 TCP-forbindelser (80/443/22/445, 300 ms hver). På et tyndt subnet (fx 240 døde IP'er) er det ~960 connect-forsøg — den dominerende omkostning i en scan. Detaljer + fix i Fase 2 (P1). |
| A11 | **Nice-to-have** | **`GetNetBiosNameAsync` bruger blokerende `udp.Receive`** (`:372`) inde i `Task.Run`. `ct` kan ikke afbryde et blokeret receive — kun 500 ms socket-timeout gør. På online-hosts kun, men lægger beslag på en thread-pool-tråd pr. host i op til 500 ms, og fase 4 throttler dem ikke. Overvej `ReceiveAsync(ct)` + `SemaphoreSlim`. |
| A12 | **Observation** | `ReadArpCacheNative` bruger et fast `RowSize = 88` for `MIB_IPNET_ROW2`. Korrekt på x64 Windows, men fragilt ift. struct-packing — det er dog dokumenteret med offset-kommentarer, og alternativet (fuld marshalling) er tungere. OK som er, men værd at kende. |

### 1.4 Async/await, UI-tråd

| # | Alvor | Fund |
|---|---|---|
| A13 | **Observation (godt)** | Alt netværksarbejde er async og kører på baggrundstråde; UI-mutationer marshalles tilbage via `Dispatcher.InvokeAsync`. `HomeViewModel.LoadAsync` bruger `_loadLock.WaitAsync(0)` mod overlap, og samplerne bruger `Interlocked.CompareExchange` som in-flight-guard. Ingen blokerende `.Result`/`.Wait()` fundet. |
| A14 | **Bør** | **Inkonsistent null-guard på `Application.Current.Dispatcher`.** `HomeViewModel` guarder pænt (`Application.Current?.Dispatcher`), men `NetworkScanViewModel:514` og flere steder bruger `Application.Current.Dispatcher.InvokeAsync(...)` uguarderet i baggrundstasks. Under nedlukning midt i en scan kan `Application.Current` være `null` → uobserveret NRE i en `Task.Run`. Guard konsekvent. |
| A15 | **Nice-to-have** | `OnNetworkAddressChanged` er `async void` (flere VMs) og kalder `Dispatcher.InvokeAsync(RefreshAdaptersAsync)` — `InvokeAsync` på en `async` metode giver `Task<Task>`; den indre task await'es ikke. Fungerer, men undtagelser i refresh svælges. |

### 1.5 Ressourcehåndtering / IDisposable

| # | Alvor | Fund |
|---|---|---|
| A16 | **Observation (godt)** | `HttpClient` er `static` (genbrugt) i både `HomeViewModel` og `DiagnosticsService`. `Ping`/`UdpClient`/`TcpClient`/`CancellationTokenSource` disposes via `using`. `_speedCts` disposes i `finally`. Timere stoppes i `Dispose`. |
| A17 | **Bør** | **`MainViewModel.Dispose()` disposer ikke `IpConfigVm`.** Den disposer Home/NetworkScan/Workspace men springer `IpConfigVm` over (`:84-89`). `IpConfigViewModel` har i dag ingen timer/unmanaged ressource, så det lækker ikke *nu* — men det er en fælde næste gang nogen tilføjer en timer der. Gør `IpConfigViewModel : IDisposable` (evt. tom) og dispose den for konsistens. |
| A18 | **Nice-to-have** | Fire `new NetworkService()`-instanser (A7) er hver harmløse (stateless), men det er spild og signalerer den manglende DI. |

### 1.6 Memory-adfærd ved store subnets

| # | Alvor | Fund |
|---|---|---|
| A19 | **Observation** | En scan er iboende /24-begrænset: `SubnetInput` er 3 oktetter og `StartIp`/`EndIp` er clamped 0-254, så maks 254 `HostInfo` pr. scan. Ingen memory-eksplosion. **Men** `SortHostsByIp()` bruger `ObservableCollection.Move` i en løkke (`:373-382`) — op til O(n²) Move-operationer der hver rejser `CollectionChanged`. Ved 254 hosts er det håndterbart, men det er unødigt dyrt (se P4). |
| A20 | **Observation** | `Workspace`-watchlisten er ubegrænset (bulk-add), og `PingAllAsync` pinger *alle* poster parallelt hver 3. sekund uden throttle (`:335-386`). Bulk-add af fx 254 adresser → 254 samtidige pings hvert interval. Se P5. |

### 1.7 Dokumentation / oprydning

| # | Alvor | Fund |
|---|---|---|
| A21 | **Bør** | **`CLAUDE.md` matcher ikke koden** (se toppen). Den beskriver en 3-tab-app og "rør ikke `DevicesView.xaml`/`NetworkScanView.xaml`" — filer der ikke findes. Det er aktivt vildledende for fremtidige ændringer. Opdater den til det reelle sidebar-layout og de faktiske filer (`MainWindow.xaml` med indlejrede paneler + `HomeView`/`WorkspaceView`/`FollowDialog`). |
| A22 | **Nice-to-have** | Død kode at fjerne: `NetworkService.ScanNetworkAsync`, `GetArpInfoAsync` (A8). `HostInfo.IpSortValue` genparser `IPAddress.TryParse` ved hvert opslag (brug den cachede `IpToUint`-tankegang fra VM'en). Blandet dansk/engelsk i status-strenge (fx "Ready to scan", "Loaded N adapters" vs. danske beskeder). |

---

## FASE 2 — Performance og optimering

### Hvad der allerede er gjort rigtigt

- **Native ARP-læsning** (`GetIpNetTable2`) i stedet for at parse `arp -a` — sparer en subprocess pr. scan.
- **ARP-flood samtidig med ping-sweep** (`floodTask` startes før sweepet, await'es efter).
- **Batch-UI**: online-hosts lægges i en `ConcurrentQueue` og flushes samlet hver 100 ms af én `DispatcherTimer` i stedet for et dispatcher-kald pr. host. God beslutning.
- **Throttling** med `SemaphoreSlim`: 150 pings, 50 port-tjek, 64 ARP-flood.

### Flaskehalse (rangeret efter effekt på scan-tid)

| # | Alvor | Fund + konkret optimering |
|---|---|---|
| P1 | **Bør (størst effekt)** | **TCP-fallback dominerer scan-tiden på tynde subnets.** `PingHostAsync` gør for *hver* ICMP-tavs host 4 TCP-connects (80/443/22/445 @ 300 ms). Et typisk /24 har 5-20 levende hosts og 230+ døde → ~920+ connect-forsøg. Selv med sem(150) på ping-tasken er det den reelle bundlinje. **Optimering:** (a) Kør de 4 fallback-porte som `Task.WhenAny` med early-exit så snart én svarer (i dag `Task.WhenAll` — venter altid på alle 4). (b) Overvej at springe TCP-fallback over for IP'er der hverken er i ARP-tabellen efter flood'et eller svarede på ICMP — en host der ikke engang ARP-svarer er næsten altid død, og ARP-resultatet er der allerede. Det alene kan halvere scan-tiden på et tyndt net. |
| P2 | **Bør** | **Reverse-DNS ligger inline i den varme sti.** `GetHostNameWithTimeoutAsync` (2000 ms timeout) kaldes *inde i* `PingHostAsync` for hver reachable host, før tasken frigiver sin semaphore-plads. En enkelt langsom PTR-opslag holder på en af de 150 pladser i op til 2 s. **Optimering:** flyt reverse-DNS ud til en egen efter-fase (som NetBIOS allerede er), throttlet, så ping-sweepet ikke bremses af navneopslag. |
| P3 | **Nice-to-have** | **Progress-dispatch pr. host.** I ping-fasen fyres `Dispatcher.InvokeAsync` for `ScanProgress`/`StatusMessage` ved *hver* af de 254 completions (`:514`). Det er 254 dispatcher-kald til en tekstopdatering. **Optimering:** opdatér en `Interlocked`-tæller og lad den eksisterende 100 ms flush-timer også skrive progress — ét sted, én kadence. |
| P4 | **Nice-to-have** | **`SortHostsByIp` er O(n²) i `ObservableCollection.Move`.** (`:373`) Hver `Move` rejser `CollectionChanged` → UI-relayout. **Optimering:** hosts opdages næsten i IP-rækkefølge i forvejen; sortér via en `ICollectionView` med `SortDescription` på `IpSortValue` (findes allerede på `HostInfo`) i stedet for manuel Move — så sorterer WPF ved visning uden at flytte i den underliggende samling. Bonus: `MainWindow` bruger allerede en `CollectionViewSource` til filtrering, så en `SortDescription` kan sættes samme sted. |
| P5 | **Nice-to-have** | **Workspace-watchlist pinger uthrottlet.** `PingAllAsync` (`:335`) pinger alle poster parallelt hvert interval. Ved store lister → periodiske bursts. **Optimering:** `SemaphoreSlim` (fx 64) om ping-løkken, som scan-siden allerede gør. |
| P6 | **Nice-to-have** | **`HomeView` gentegner connector-linjen på hver `LayoutUpdated`.** (`:105`) `LayoutUpdated` fyrer meget hyppigt; selv med `_connectorUpdateQueued`-guard + `DispatcherPriority.Background` kører `SyncGraphWidth`+`UpdateConnector` ofte. **Optimering:** abonnér i stedet kun på `SizeChanged`/`ActualWidth`-ændringer på de relevante elementer, eller invalidér kun når adapter-listen eller graf-synligheden ændrer sig. |
| P7 | **Observation** | `UpdateHostInUI` (`:611`) laver `DiscoveredHosts.FirstOrDefault(linear)` pr. host i port- og NetBIOS-faserne → O(n²) samlet. Ved 254 hosts trivielt, men en `Dictionary<string,HostInfo>`-indeks ved siden af samlingen ville gøre både dette og `FlushUiQueue`'s opslag O(1). |

### Multi-subnet (Workspace / Dashboard)

Opgaven nævner "multi-subnet scans (Workspace-siden)". Som koden er nu:

- **Der findes ingen aktiv multi-subnet-*scanning*.** `NetworkScanViewModel` scanner ét /24 ad gangen. `Workspace` er en flad watchlist (ping-monitor) der *kan* indeholde IP'er fra flere subnets, men den scanner ikke ranges. Dashboardets "Nærved"-grupper (`NearbyGroups`) grupperer blot den *passive* ARP-cache pr. /24 — ingen aktiv probing.
- **Konsekvens:** den reelle optimering her er ikke at gøre eksisterende multi-subnet-scan hurtigere (den findes ikke), men at *tilføje* den som feature — se **Fase 4 (F3: gemte scan-profiler / multi-range)**. Hvis/når den bygges: kør subnets sekventielt men hosts-inden-for-subnet parallelt med den eksisterende throttlede pipeline, og genbrug ARP-tabellen på tværs (den er global, så ét `GetArpTableNative()`-kald dækker alle subnets).

### Netto-anbefaling (Fase 2)

De to ændringer med størst effekt-per-indsats: **P1 (early-exit + drop fallback på ARP-tavse hosts)** og **P2 (flyt reverse-DNS ud af den varme sti)**. Sammen vil de mærkbart forkorte en /24-scan på et typisk (tyndt) net uden at ændre arkitekturen.

---

## FASE 3 — UX og brugervenlighed

> Vurderet på den kørende app (v1.3.27, elevated) med screenshots af Dashboard,
> Device Follow og Scan. Adapters/Settings-panelerne blev læst i XAML (kunne ikke
> aktiveres live pga. vindue på sekundær skærm). Screenshots i `ScreenShot/`.

### Overordnet indtryk

Appen ser **markant mere gennemført ud end en typisk hobby-netværksscanner**.
Dark theme + magenta/cyan-branding er konsistent og flot, Dashboardet er tæt men
velorganiseret, og der er gennemtænkte detaljer: menneskelige verdicts på
sundhedsscoren ("Fremragende — klar til gaming og videomøder"), en topologi-kæde
(Ethernet → Router → WAN med geo/ISP), sammenklappelige sektioner, og en
zoom-kontrol (50-200 %) i sidebaren. Det er stærkt.

De vigtigste UX-problemer er (1) **5 af 10 sidebar-punkter er deaktiverede
placeholders uden forklaring**, (2) **tom Vendor-kolonne** i scanneren trods gyldige
MAC-adresser, og (3) **læsbarhed**: lav kontrast på sekundærtekst og tvetydig
farvekodning på port-badges.

### 3.1 Navigation

| # | Alvor | Fund |
|---|---|---|
| U1 | **Bør** | **Halvdelen af sidebaren er døde placeholders.** Ports, Ping monitor, Fingerprints, OUI Lookup og Historik er alle `IsEnabled="False"` (`MainWindow.xaml:363-416`) — de fremstår som fuldgyldige menupunkter men reagerer ikke på klik, har ingen tooltip og intet "kommer snart"-mærke. En ny bruger klikker og tror appen hænger. **Fix:** tilføj et "SNART"-badge eller tooltip, eller skjul dem til de er klar. (Sjovt nok er tre af dem — OUI Lookup, Fingerprints, Historik — præcis features jeg foreslår i Fase 4; UI'et signalerer allerede den ønskede retning.) |
| U2 | **Nice-to-have** | Navigationsvalg er ikke persisteret — appen åbner altid på Dashboard. For en bruger der mest bruger Scan/Device Follow ville "husk sidste side" spare et klik hver gang. |

### 3.2 Scan-siden

| # | Alvor | Fund |
|---|---|---|
| U3 | **Bør** | **Vendor-kolonnen er tom (—) for ALLE enheder**, selv med gyldige MAC-adresser og velkendte OUI'er (Google `44:07:0B`, Chromecast `80:D2:1D`, Ubiquiti `BC:24:11`). Årsag: `OuiLookup` er en hardkodet ~300-linjers dict (`Utils/OuiLookup.cs`) der kun dækker en håndfuld OUI'er ud af 30.000+ registrerede — så næsten alt viser ingen vendor. Det er en synlig data-huls-oplevelse i en kolonne der fylder i gridet. Kobler til **Fase 4 / F1** (rigtig OUI-database). |
| U4 | **Bør** | **Farvekodning på port-badges er semantisk forvirrende.** Åben `:8080` vises i rød/pink, åben `:80` i cyan — men rød læses universelt som "fejl/advarsel", ikke "port åben". Og lukkede porte vises stadig som dæmpede labels, hvilket støjer. **Fix:** ét konsistent "åben"-farvesprog (fx grøn/cyan for alle åbne), og skjul helt lukkede porte i stedet for at vise dem dæmpet. |
| U5 | **Observation (godt)** | Stat-kortene (Fundet/Online/Offline/Sidst scannet) opdateres korrekt, kolonneoverskrifter er tydelige, og hostname-opløsning virker fint (seer.dk, homeassistant…, unifi, Chromecast-Audio osv.). En scan af /24 kørte hurtigt til 31 enheder. |

### 3.3 Device Follow (Workspace)

| # | Alvor | Fund |
|---|---|---|
| U6 | **Bør** | **Watchlisten mangler kolonneoverskrifter.** Modsat Scan-gridet har rækkerne ingen headers, så `:80/:443/:8080`, statustallet (roundtrip ms) og tidsstemplet er uforklaret for en ny bruger. Tilføj en header-række (eller genbrug Scan-gridets). |
| U7 | **Bør** | **Samme port-badge-læsbarhed som U4** — på den tætte watchlist er det endnu sværere at se åben vs. lukket. |
| U8 | **Observation (godt)** | Den store "MY IP"-header med adapter-skift + DHCP-knap er en god ankerplacering. Bulk-add (base + range) er praktisk. "Ryd alle" har to-trins-bekræftelse ("Bekræft?") — pæn detalje mod utilsigtet sletning. |

### 3.4 Dashboard

| # | Alvor | Fund |
|---|---|---|
| U9 | **Nice-to-have** | **APIPA-støj i "Kendte enheder".** `169.254.83.0/24` (link-local/APIPA) vises som en egen subnet-gruppe med 1 enhed — det er teknisk støj der aldrig er en rigtig enhed. Filtrér `169.254.*` fra eller dæmp den. |
| U10 | **Nice-to-have** | **Lav kontrast på sekundærtekst.** Subnetmaske, adapterbeskrivelse, "Gw —" osv. er mørkegrå på mørk baggrund (under WCAG AA). Et trin lysere grå ville løfte læsbarheden uden at bryde æstetikken. |
| U11 | **Observation (godt)** | Dashboardet er ærligt om sine data ("ARP cache (ikke live scan)"), sundhedsscoren har en forståelig verdict, og topologi-kæden med den stiplede connector-linje er et flot touch. Sammenklappelige `// Diagnostik ▼`/`// Graph ▼`-sektioner holder densiteten nede. |

### 3.5 Konsistens / tema / tilgængelighed

| # | Alvor | Fund |
|---|---|---|
| U12 | **Bør** | **Blandet dansk/engelsk gennem hele UI'et.** "Watch List", "Follow", "Devices found", "Online", "Last scanned", "Ready to scan" står side om side med "Netværksoverblik", "Kendte enheder", "Mine adaptere", "Annuller", "Historik". Vælg ét sprog (dansk, givet resten) og oversæt konsekvent — også status-strengene i ViewModel-koden (`A22`). |
| U13 | **Nice-to-have** | Ingen tastatur-/screenreader-affordances observeret (fokus-ringe, `AutomationProperties`). Til et internt værktøj er det lav prioritet, men zoom-kontrollen viser at der allerede tænkes på tilgængelighed — `AutomationProperties.Name` på ikon-knapper ville være en billig forbedring. |
| U14 | **Observation (godt)** | Branding/tema er konsistent og professionelt: magenta-logo, cyan-accenter, ensartede kort-radii og spacing. Zoom-kontrollen (50-200 %) er en rigtig god inklusions-detalje der er sjælden i hobbyværktøjer. |

---

## FASE 4 — Nye features

Otte forslag, skræddersyet til værktøjets formål (netværks-discovery/-diagnostik)
og din arbejdsgang som elektriker/netværksmenneske i felten. Kompleksitet: **S**
(timer), **M** (en dag-to), **L** (flere dage). De **★-mærkede** er dem jeg selv
ville bygge først — begrundelse til sidst.

### ★ F1 — Rigtig OUI-vendor-database (S–M) — ✅ IMPLEMENTERET

**Hvad:** Erstat den hardkodede ~300-linjers `OuiLookup`-dict med IEEE's fulde OUI-liste
(~35.000 præfikser). Enten bundtet som en komprimeret ressource (`oui.csv.gz`,
~1-2 MB) indlæst i en `Dictionary` ved opstart, eller en engangs-download til
`%APPDATA%\M1Scan\`.
**Hvorfor:** Løser U3 direkte — Vendor-kolonnen er tom i dag for stort set alt.
Vendor er den *mest* nyttige enkeltoplysning når man skal identificere en ukendt
enhed på et anlæg ("hvem er 192.168.5.126? → Hikvision → det er kameraet").
**Kompleksitet:** S hvis bundtet statisk, M med download+cache.
**Arkitektur:** Kun `Utils/OuiLookup.cs` — samme `Lookup(mac)`-signatur, så intet
kalder-site ændres. Nul risiko for resten af appen.

**Implementeret 2026-07-04:** IEEE's officielle MA-L-register (39.688 unikke
OUI'er, hentet fra `standards-oui.ieee.org/oui/oui.csv`) parset til et kompakt
`OUI|Vendor`-tekstformat, gzip-komprimeret (387 KB) og indlejret i assemblyen som
`EmbeddedResource` (`Resources/Data/oui.txt.gz`, `LogicalName=M1Scan.Resources.Data.oui.txt.gz`).
`OuiLookup.cs` er omskrevet til lazy at indlæse + udpakke ressourcen ved første
opslag (samme `Lookup(mac)`-signatur — ingen kalder-sites ændret). Regenereres med
det nye `scripts/update-oui.py` når IEEE-registret opdateres.
**Verificeret live:** Scan-siden viste før udelukkende "—" i Vendor-kolonnen; efter
fixet viser den korrekte producentnavne som "Ubiquiti Inc", "Google, Inc.",
"Hangzhou Hikvision Digital Technology Co.,L…", "Zhejiang Dahua Technology Co., Ltd.",
"Espressif Inc.", "Proxmox Server Solutions GmbH", "ASRock Incorporation" — se
`ScreenShot/scan_after_oui_fix.png`.

### ★ F2 — Eksport af scan-resultater (CSV/JSON) (S) — ✅ IMPLEMENTERET

**Hvad:** "Eksportér"-knap på Scan- og Device-Follow-siderne → CSV og JSON af de
viste hosts (IP, hostname, MAC, vendor, åbne porte, OS-gæt, sidst set).
**Hvorfor:** Som elektriker skal du kunne aflevere en enhedsliste til
dokumentation/kunde, eller åbne den i Excel. Det er den klassiske "get data out"-mangel.
**Kompleksitet:** S — dataene ligger allerede i `DiscoveredHosts`/`WatchList`.
**Arkitektur:** Ny `Services/ExportService.cs` (`System.Text.Json` findes allerede;
CSV er triviel string-building) + en `RelayCommand` i `NetworkScanViewModel`/`WorkspaceViewModel`
+ `Microsoft.Win32.SaveFileDialog`. Ingen ændring i netværkslaget.

**Implementeret 2026-07-04:** `IExportService`/`ExportService` med to metoder
(`ExportHostsAsync`, `ExportWatchListAsync`) — formatet (CSV vs. JSON) afgøres af
filendelsen fra `SaveFileDialog`, så ViewModel'en blot videregiver stien. CSV bruger
korrekt RFC4180-escaping (citationstegn/komma/linjeskift); JSON bruger en udvidet
Unicode-encoder så æ/ø/å forbliver læsbare i rå tekst i stedet for at blive
`\uXXXX`-escaped. `ExportCommand` tilføjet til både `NetworkScanViewModel` (Scan-siden)
og `WorkspaceViewModel` (Device Follow), med en "Eksportér"-knap i hver views XAML,
wired via den nye `IExportService` constructor-injiceret fra `MainViewModel` (samme
A7-mønster).
**Verificeret:** (1) Et separat standalone testprogram kaldte `ExportService` direkte
med data indeholdende citationstegn, kommaer og danske tegn — CSV-escaping, JSON-
struktur og null-håndtering for nullable bools (ubesvarede port-tjek) er alle korrekte.
(2) Build er ren (0 fejl/advarsler). (3) Live i appen: "Eksportér"-knappen ses korrekt
placeret og stylet på både Scan-siden (`ScreenShot/f2_scan_click.png`) og Device
Follow-siden (`ScreenShot/final_devicefollow_export_button.png`); et klik åbner en
ægte Windows SaveFileDialog (bekræftet via UI Automation — dialogens kontroller
"Ny mappe", "Organiser", "Filterrulleliste" m.fl. blev fundet i skærmtræet efter klik).
(4) **Brugertestet end-to-end** (`m1scan-scan-2026-07-04_1231.csv`, delt af bruger):
47 rigtige enheder fra det faktiske netværk, med korrekt CSV-quoting af
producentnavne der indeholder kommaer (`"Google, Inc."`, `"Zhejiang Dahua Technology
Co., Ltd."`, `"Beijing Roborock Technology Co., Ltd."` m.fl.) — det endegyldige bevis
på at både F1 (vendor-opslag) og F2 (eksport) virker korrekt sammen i produktion.

### ★ F3 — Gemte scan-profiler + multi-range (M)

**Hvad:** Navngivne profiler (fx "Kundeanlæg A", "Hjemmenet") der gemmer subnet(s),
range, valgt adapter og port-sæt. Tillad *flere* ranges i én profil så en scan kan
dække fx `10.0.1.0/24` + `10.0.2.0/24`.
**Hvorfor:** Du scanner de samme net igen og igen; i dag skal subnet/range tastes
hver gang. Multi-range er den reelle "multi-subnet"-feature som Fase 2 konstaterede
mangler.
**Kompleksitet:** M.
**Arkitektur:** Ny `Models/ScanProfile.cs` + persistens (`%APPDATA%\M1Scan\profiles.json`,
samme mønster som `workspace.json`). Kræver at scan-orkestreringen kan tage en liste
af ranges — hænger sammen med **A8** (flyt orkestrering til service): kør subnets
sekventielt, hosts parallelt, og genbrug ét `GetArpTableNative()`-kald på tværs.

### ★ F4 — Historik + diff mellem scans (M–L)

**Hvad:** Gem hver scan som et snapshot; vis en tidslinje og en diff mod forrige:
**nye**, **forsvundne** og **ændrede** enheder (ny IP, nye åbne porte).
**Hvorfor:** "Hvad har ændret sig på anlægget siden sidst?" er kernespørgsmålet ved
service/fejlfinding. Sidebaren har allerede et deaktiveret **Historik**-punkt (U1) —
featuren er tydeligt planlagt.
**Kompleksitet:** M–L.
**Arkitektur:** Bygger oven på F2's snapshot-serialisering + `KnownDevicesStore`s
`firstSeen`-mønster (som allerede laver ny-enheds-detektion på Dashboardet — meget af
logikken findes). Ny `Services/ScanHistoryStore.cs` + en `HistoryViewModel`/-view der
aktiverer det eksisterende sidebar-punkt.

### F5 — Notifikationer ved nye/forsvundne enheder (M)

**Hvad:** Baggrunds-rescan på interval; Windows toast-notifikation når en ukendt
enhed dukker op (eller en vigtig forsvinder). Byg på det eksisterende
"NY enhed"-badge-system i `KnownDevicesStore`.
**Hvorfor:** Uvedkommende-enhed-detektion (sikkerhed) + "gik serveren offline?"-overvågning.
**Kompleksitet:** M.
**Arkitektur:** `KnownDevicesStore` leverer allerede ny-enheds-signalet; tilføj en
baggrunds-scheduler + `Microsoft.Toolkit.Uwp.Notifications` (eller
`System.Windows.Forms.NotifyIcon` for tray+balloon). Berører ikke scan-pipelinen.

### F6 — Udvidet service-/OS-detektion på åbne porte (M–L)

**Hvad:** Udover åben/lukket: grib service-banner (HTTP `Server:`-header, SSH-banner,
TLS-cert-CN) og vis "nginx", "OpenSSH 8.9", "Modbus" osv. Skærp OS-gættet ved at
kombinere TTL med åbne porte (445→Windows, 22+ingen 445→Linux).
**Hvorfor:** Gør enhedslisten langt mere sigende — særligt Modbus (502) er allerede
med i scanneren, hvilket peger på industrielt udstyr (relevant for dit elektrikerdomæne).
**Kompleksitet:** M–L.
**Arkitektur:** Udvid `INetworkService` med `GrabBannerAsync(ip, port, ct)`; kør som
en ekstra fase efter port-tjekket (samme throttlede mønster). Nye felter på `HostInfo`.

### F7 — Wake-on-LAN + hurtig-handlinger pr. enhed (S–M)

**Hvad:** Højreklik-menu på en host: Wake-on-LAN (magic packet til MAC), kopiér
IP/MAC, åbn i browser (findes delvist), `ping -t` i nyt vindue, tilføj til Device Follow.
**Hvorfor:** Praktiske felt-handlinger. WoL er guld når du skal vække en server/PC du
lige har fundet.
**Kompleksitet:** S (WoL er en 6-byte + 16×MAC UDP-broadcast) – M (hele menuen).
**Arkitektur:** `Services/WakeOnLanService.cs` + en context-menu i host-gridet. De fleste
handlinger findes allerede som kommandoer (`OpenInBrowserCommand`, `CopyIpCommand`) —
det er mest at samle dem i en menu.

### F8 — PDF/Markdown-rapport for et anlæg (M)

**Hvad:** "Generér rapport" → et pænt dokument med dato, adapter/gateway, sundhedsscore,
speedtest, fuld enhedsliste og diff siden sidst. Markdown (trivielt) eller PDF.
**Hvorfor:** Aflevér professionel dokumentation til kunden efter et servicebesøg — et
reelt differentiator for en elektriker/netværkskonsulent.
**Kompleksitet:** M (Markdown: S; PDF kræver et bibliotek, fx QuestPDF).
**Arkitektur:** Bygger på F2 (eksport) + F4 (historik/diff) + Dashboardets eksisterende
health/speed-data. Ny `Services/ReportService.cs`.

### Prioritering af features

**Byg først: F1, F2, F3 (+ F4 tæt efter).**

- **F1 (OUI-database)** — størst værdi-per-indsats. Retter en synlig mangel (tom
  Vendor-kolonne), er isoleret til én fil med uændret signatur, og gør *hele* appen
  mere brugbar med det samme. Ren S–M-gevinst uden risiko.
- **F2 (eksport)** — lille indsats, høj praktisk værdi for din arbejdsgang, og
  fundament for både F4 (historik) og F8 (rapport). Naturligt næste skridt.
- **F3 (profiler + multi-range)** — rammer den daglige friktion (taste subnet hver
  gang) og leverer den multi-subnet-kapacitet der reelt mangler. Motiverer samtidig
  den sunde refaktorering i A8 (orkestrering ned i servicen).
- **F4 (historik/diff)** ligger lige efter, fordi den bygger direkte på F2 + det
  eksisterende `firstSeen`-mønster og aktiverer et sidebar-punkt der allerede er tegnet.

De fire hænger sammen som en kæde (F2 → F4 → F8; F1 og F3 forstærker alt) og løfter
M1Scan fra "scanner" til "anlægs-dokumentationsværktøj" — som er der din elektriker-
arbejdsgang får mest ud af det.

---

## PRIORITERET LISTE (på tværs af alle fire faser)

### 🔴 Kritisk

Ingen decideret produktionskritiske/data-tab-fejl fundet. Den vigtigste at rydde
først (kode-hygiejne med reel faldgrube):

1. ~~**[A8]** Fjern eller throttle `NetworkService.ScanNetworkAsync`~~ **✅ RETTET 2026-07-04** — død kode (ScanNetworkAsync + GetArpInfoAsync) fjernet fra service og interface, build verificeret ren.
2. ~~**[A7]** Indfør dependency injection~~ **✅ RETTET 2026-07-04** — alle fire ViewModels tager nu services via constructor; `MainViewModel` er eneste composition root. CLAUDE.md opdateret til at beskrive det faktiske mønster.

### 🟡 Bør

3. **[A1/A2]** Flyt view-model-tilstand (`SelectedPage`, tællere, `FilteredHosts`, søgning) ud af `MainWindow.xaml.cs` og erstat den imperative panel-visibility-navigation med `ContentControl` + `DataTemplate`.
4. **[A3]** Afdupliker den håndbyggede adapter-dropdown (identisk i `MainWindow.xaml.cs` + `WorkspaceView.xaml.cs`) → én XAML-`DataTemplate`.
5. **[P1]** Optimér TCP-fallback: `Task.WhenAny` med early-exit, og drop fallback for ARP-tavse hosts — den største enkeltgevinst på scan-tid.
6. **[P2]** Flyt reverse-DNS ud af `PingHostAsync`'s varme sti til en egen throttlet efter-fase.
7. **[A9]** Send `CancellationToken` til `Ping.SendPingAsync` (`.NET 8`-overload) så en scan reelt kan afbrydes med det samme.
8. ~~**[U3 + F1]** Rigtig OUI-database~~ **✅ RETTET 2026-07-04** — retter den tomme Vendor-kolonne (mest synlige data-hul).
9. **[U1]** Marker de fem deaktiverede sidebar-punkter som "kommer snart" (badge/tooltip) eller skjul dem.
10. **[U4/U6/U7]** Ensret port-badge-farver (åben = ét konsistent farvesprog, skjul lukkede) og tilføj kolonneoverskrifter i Device Follow.
11. **[U12/A22]** Vælg ét sprog (dansk) og oversæt UI + status-strenge konsekvent.
12. **[A14]** Konsistent `Application.Current?.Dispatcher`-null-guard i baggrundstasks.
13. **[A21]** Opdatér `CLAUDE.md` — den beskriver en app-struktur der ikke længere findes.

### 🟢 Nice-to-have

14. **[A17]** Gør `IpConfigViewModel : IDisposable` og dispose den fra `MainViewModel`.
15. **[P4]** Erstat O(n²) `ObservableCollection.Move`-sortering med `ICollectionView`+`SortDescription`.
16. **[P3]** Saml progress-opdateringer i den eksisterende 100 ms flush-timer i stedet for ét dispatcher-kald pr. host.
17. **[P5]** Throttle Workspace-watchlistens periodiske ping-burst.
18. **[P6/A5]** Gentegn Dashboard-connectoren kun ved reelle størrelses-/synlighedsændringer i stedet for hver `LayoutUpdated`.
19. **[U9]** Filtrér APIPA (`169.254.*`) fra "Kendte enheder".
20. **[U10]** Løft kontrasten på sekundærtekst (mørkegrå på mørk).
21. **[A11/P7]** Async NetBIOS-receive + `Dictionary`-indeks på `DiscoveredHosts` for O(1)-opslag.
22. **[A4]** Flyt hover-farver i `WorkspaceView.xaml.cs` til XAML-triggers.

### Nye features (anbefalet rækkefølge)

**F1** (OUI-database) → **F2** (CSV/JSON-eksport) → **F3** (scan-profiler + multi-range)
→ **F4** (historik/diff), derefter F5–F8. De fire første hænger sammen og løfter M1Scan
fra scanner til anlægs-dokumentationsværktøj.

---

## Samlet vurdering

M1Scan er **teknisk stærkere end koden umiddelbart antyder** — netværkslaget (native
ARP, throttlet pipeline, batch-UI, samtidig flood/sweep) og Dashboardet (health-score,
diagnostik, topologi) er gennemtænkte og pænt kommenterede. Der er ingen kritiske
runtime-fejl.

De reelle svagheder er koncentrerede og velafgrænsede: **manglende DI** (gør det
utestbart), **kode-behind-tyngde i `MainWindow`** (bryder MVVM-konventionen appen ellers
følger), **død dobbelt-implementering i netværkslaget**, og på UX-siden **den tomme
Vendor-kolonne** + **de døde placeholder-nav-punkter**. Alt sammen er punktvise
rettelser, ikke omskrivninger.

Byg DI + OUI-database + eksport først; så står fundamentet til at aktivere de
allerede-tegnede sidebar-features (Historik, Fingerprints, OUI Lookup).

*Gennemgang afsluttet 2026-07-04.*

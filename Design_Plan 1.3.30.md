# M1Scan Roadmap — Vejen til det bedste netværksfejlfindingsværktøj

*Design-plan udarbejdet ved v1.3.30 — 5. juli 2026*

## Kontekst

M1Scan v1.3.30 er i dag en solid WPF/.NET 8-app med et flot dashboard (Netværksscore, latency-grafer, diagnostik), subnet-scanning (ARP + ping sweep), Device Follow, adapterstyring og auto-update via GitHub Releases. Men:

- **5 af 10 sidebar-punkter er tomme pladsholdere**: Ports, Ping monitor, Fingerprints, OUI Lookup, Historik (`MainWindow.xaml` linje 389–442).
- **Ingen historik**: intet gemmes over tid — ingen trends, ingen "hvornår gik det galt?".
- **Manglende kerneværktøjer**: traceroute, mDNS/SSDP-discovery, Wake-on-LAN, SNMP, UDP-scanning, Wi-Fi-analyse.
- Fundamentet er stærkt: MVVM, interface-baserede services, native ARP via P/Invoke, embedded OUI-database — alt kan genbruges.

## Det store forkromede mål (North Star)

> **M1Scan skal kunne besvare "Hvorfor er mit netværk langsomt eller nede?" på under 60 sekunder — automatisk, på dansk, med en konkret anbefaling.**

Konkurrenterne løser hver sit hjørne: Advanced IP Scanner (discovery), PingPlotter (path-monitoring), Fing (enhedsgenkendelse), Wireshark (dyb analyse). Ingen samler **fejlfinding + status + historik** i ét poleret værktøj der *konkluderer* i stedet for bare at vise rå tal. Det er M1Scans niche: **værktøjet der diagnosticerer, ikke bare måler.**

Tre søjler:
1. **Se alt** — komplet discovery og identifikation af alle enheder og forbindelser.
2. **Forstå alt** — automatisk diagnose der oversætter målinger til konklusioner på dansk.
3. **Husk alt** — historik så du kan svare på "hvornår startede problemet, og hvad ændrede sig?".

---

## Fase 1 — Fundamentet: Gør alle 10 sidebar-punkter levende

Aktivér de 5 døde menupunkter. Meget af logikken findes allerede spredt i koden og skal blot generaliseres.

### 1.1 Historik (vigtigst — låser alt andet op)
- **SQLite-database** (`Microsoft.Data.Sqlite`) i `%APPDATA%\M1Scan\history.db` som erstatter/supplerer JSON-filerne.
- Gem: scan-resultater (enheder set pr. scan), latency/jitter/loss-samples, Netværksscore over tid, speedtest-resultater, enheds-events (ny enhed, enhed forsvundet).
- Historik-siden viser: tidslinje med score-graf (dag/uge/måned), udfalds-log ("Internet nede 14:32–14:41"), enheds-tidslinje ("Ny enhed: Sonos, første gang set 3. juli").
- Genbrug: `KnownDevicesStore` (`Services/KnownDevicesStore.cs`) udvides til at skrive events til databasen; `HomeViewModel.SampleOnceAsync` persisterer samples i batches.

### 1.2 Ping monitor
- Dedikeret side: flere mål samtidig (IP/hostname), hver med sparkline (genbrug `Controls/SparklineControl.cs`), uptime %, min/avg/max/jitter/loss.
- Konfigurerbart interval, tærskler og alarm ved down/up (Windows toast-notifikation).
- Genbrug ping-motoren fra `WorkspaceViewModel` — generalisér til en `PingMonitorService`.

### 1.3 Ports
- Fuld portscanner: brugerdefinerede porte/ranges, presets (Web, Fjernadgang, IoT/Modbus, Top-100), TCP connect-scan med bounded concurrency (mønstret fra `NetworkScanViewModel` med `SemaphoreSlim`).
- Servicenavne pr. port (embedded IANA-tabel, samme mønster som `oui.txt.gz`) + simpel banner-grabbing (læs første linje fra åbne porte → "OpenSSH 9.6", "nginx").
- UDP-scanning af de vigtigste porte (53, 123, 161, 1900, 5353).

### 1.4 OUI Lookup
- Lille side oven på eksisterende `Utils/OuiLookup.cs`: indtast MAC → leverandør, søg leverandør → prefixes. Hurtig gevinst.

### 1.5 Fingerprints
- Enhedsklassificering: kombinér TTL-baseret OS-gæt (findes i `NetworkService`), OUI-leverandør, åbne porte, hostname-mønstre og mDNS/SSDP-data (fase 2) → enhedstype med ikon (router, printer, kamera, TV, telefon, server, PLC).
- Regelbaseret scoring-motor med redigérbare regler; resultat vises i Scan-listen og gemmes i historikken.

---

## Fase 2 — Fejlfindingskernen (differentiatoren)

### 2.1 Visuel traceroute / path-monitor ("PingPlotter-killeren")
- ICMP traceroute med per-hop kontinuerlig måling: latency, jitter, loss **pr. hop over tid** — så man kan se *hvor* på ruten problemet ligger (eget net vs. ISP vs. destination).
- Visuel hop-graf i samme stil som topologi-kortet på dashboardet. Reverse-DNS + geo/ASN pr. hop (genbrug ip-api-integrationen fra `DiagnosticsService`).

### 2.2 "Diagnosticér nu" — den automatiske fejlfindings-wizard
Kronjuvelen. Én knap der kører et beslutningstræ og konkluderer på dansk:
1. Adapter/link OK? → 2. Gateway svarer? → 3. DNS virker (flere servere)? → 4. WAN svarer? → 5. Traceroute: hvor stiger latency/loss? → 6. MTU-test (DF-bit sweep) → 7. Double-NAT-detektion (privat WAN-IP?) → 8. Captive portal? → 9. Pakketab lokalt vs. eksternt.
- Output: **"Problemet er hos din ISP — hop 3 (TDC) taber 8% pakker. Dit eget netværk er OK."** + anbefalet handling og kopiérbar rapport til ISP-support.
- Genbrug: alle byggeklodser findes allerede i `DiagnosticsService` og `NetworkService` — wizarden er orkestrering + konklusionslogik.

### 2.3 Udvidet discovery
- **mDNS/Bonjour** (UDP 5353) og **SSDP/UPnP** (UDP 1900) lytning + aktiv forespørgsel → rigtige enhedsnavne ("Sonos Stue", "HP LaserJet") i stedet for bare IP/MAC.
- Beriger både Scan-siden og Fingerprints (1.5).

### 2.4 Wake-on-LAN
- Magic packet pr. enhed (MAC kendes allerede fra ARP-scan). Højreklik i Scan-listen → "Væk enhed". Lille indsats, stor "wow".

### 2.5 Alarmer og notifikationer
- Windows toast + alarm-log i Historik: enhed down/up, ny ukendt enhed på nettet, Netværksscore under tærskel, internet-udfald.

---

## Fase 3 — Status og overvågning (fra værktøj til vagthund)

### 3.1 Baggrundsovervågning
- Minimér til system tray; letvægts-sampler kører videre (genbrug den eksisterende 2s-sampler). Tray-ikon farves efter Netværksscore.
- Resultat: M1Scan har svaret klar *før* du spørger — fuld historik over udfaldet i går aftes.

### 3.2 Internet-SLA / ISP-rapport
- Uptime %, antal udfald, gennemsnitshastighed over tid (planlagte speedtests, f.eks. dagligt), alt fra historik-databasen.
- Eksportér **"ISP-rapport"** (PDF/HTML via `ExportService`): dokumentation til klage/support med grafer og udfalds-log. Intet konkurrerende gratisværktøj gør dette godt.

### 3.3 Wi-Fi-analyse
- Via native WLAN API (`wlanapi.dll`, samme P/Invoke-mønster som ARP): signalstyrke (RSSI) over tid, kanal + kanaloverlap fra nabo-net, link-hastighed, roaming-events, båndinfo (2,4/5/6 GHz).
- Wi-Fi-faktor ind i Netværksscore (`HealthScore.Compute` i `Models/DashboardModels.cs`) når adapteren er trådløs.

---

## Fase 4 — Pro-niveau (helt i toppen)

- **SNMP v2c/v3**: switch/router-info, interface-tællere (fejl, throughput pr. port), "hvilken switchport sidder enheden i?".
- **Pakkeanalyse light**: ETW-baseret (`Microsoft.Diagnostics.Tracing`) uden Npcap-krav — top-talkers, protokolfordeling, "hvem æder båndbredden?". Fuld capture som valgfrit Npcap-plugin.
- **IPv6-scanning**: udvid fra ren detektion til discovery (multicast ping ff02::1, NDP-tabel via `GetIpNetTable2` som allerede bruges).
- **Engelsk lokalisering** (.resx-refaktor) → åbner for internationalt publikum på GitHub.
- **CLI-mode** (`m1scan.exe --scan --json`) til scripting/scheduled tasks.

---

## Prioriteret rækkefølge (anbefaling)

| # | Punkt | Hvorfor først |
|---|-------|---------------|
| 1 | Historik + SQLite (1.1) | Fundament for alarmer, SLA, trends — jo før den samler data, jo mere værd |
| 2 | Ping monitor (1.2) | Mest efterspurgte fejlfindingsfunktion; motoren findes næsten |
| 3 | Traceroute/path-monitor (2.1) | Størst differentiator; intet dansk værktøj har det |
| 4 | "Diagnosticér nu"-wizard (2.2) | North Star-featuren — bygger på 1–3 |
| 5 | Ports + OUI Lookup (1.3, 1.4) | Fjerner de sidste døde menupunkter; lav indsats |
| 6 | mDNS/SSDP + Fingerprints + WoL (2.3, 1.5, 2.4) | Gør Scan-siden markant bedre |
| 7 | Tray + alarmer + SLA-rapport (3.1, 2.5, 3.2) | Fra værktøj til vagthund |
| 8 | Wi-Fi-analyse (3.3) | Stor værdi for hjemmebrugere |
| 9 | Fase 4-punkter | Pro-segmentet |

**Tekniske forudsætninger undervejs:** tilføj `Microsoft.Data.Sqlite` (eneste nye afhængighed i fase 1–2), behold manuel DI (bevidst valg i `MainViewModel.cs`), fasthold mønstrene: interface-services, `SemaphoreSlim`-concurrency, `ArgumentList` mod injection. Overvej simpel fil-logging tidligt — fejlfinding af fejlfindingsværktøjet.

## Verifikation

- Hver feature verificeres med `/run`-skillen (start app, screenshot, visuel kontrol af den nye side).
- Historik: kør app'en i baggrunden i nogle timer, træk netværkskablet/sluk Wi-Fi kortvarigt, og bekræft at udfaldet fremgår af historik-siden med korrekt tidsstempel.
- Wizard: test mod kendte fejlscenarier (forkert DNS sat manuelt, gateway ned, captive portal på mobilt hotspot) og bekræft korrekt konklusion.
- `code-reviewer`- og `security-reviewer`-agenterne køres pr. fase; `agent-updater` efter hver større feature; release via `/release`-skillen.

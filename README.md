![M1Scan Logo](Resources/m1scan-logo.jpg)

![Version](https://img.shields.io/badge/version-1.3.19-blue?style=for-the-badge)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey?style=for-the-badge)
![.NET](https://img.shields.io/badge/.NET-8.0-purple?style=for-the-badge)

# M1Scan — Netværksværktøj til Windows

Avanceret Windows-netværksværktøj med live overvågning, topologivisualisering, sundhedsscore, enhedssporing og IP-konfiguration. Bygget i WPF med dark theme og MVVM-arkitektur.

---

## Funktioner

### Dashboard
Det centrale overblik over dit netværk i realtid.

- **// TOPOLOGI** — Visuel kæde der viser din aktive adapter → router → WAN med IP-adresser, latenstid og ISP-info. Forbindelseslinje fra aktiv adapter til topologikortet.
- **// GRAPH** — Live latency-sparklines for gateway og internet (1.1.1.1) med rullebuffer (~5 min historik). Viser aktuel ping, gennemsnit, maks, jitter og pakketab. Kan kollapses og husker tilstand.
- **Netværksscore** — Score 0–100 med karakter A–F baseret på latenstid, jitter, pakketab, DNS-svartid og gateway-stabilitet. Reset-knap og detaljeret forklaring i tooltip.
- **// DIAGNOSTIK** — Fire kompakte kort der kører automatisk ved opstart:
  - **DNS-svartider** — Måler alle dine DNS-servere + 8.8.8.8 + 1.1.1.1 parallelt
  - **DHCP-lease** — Server, tildelt tidspunkt og udløbstidspunkt (eller "Statisk IP")
  - **IPv6 + Captive portal** — Global IPv6-adresse og om der er en fangeportal i vejen
  - **Hastighedstest** — Download + upload via Cloudflare, gemmer seneste resultat
- **// MINE ADAPTERE** — Alle aktive netværksadaptere sorteret med internet-adapteren øverst
- **Ping-bar** — Realtidsstatus for 8.8.8.8, 1.1.1.1 og 8.8.4.4
- **Kendte enheder** — ARP-baseret oversigt over enheder på netværket med "NY"-badges for nye enheder siden sidst

### Netværksscanning
- Ping-sweep af hele subnettet eller enkelt host
- ARP-opslag: MAC-adresse, vendoroplysninger, NetBIOS-navn
- TTL-baseret OS-gæt (Windows / Linux / Cisco)
- Port 80-tjek

### Enhedssporing (Follow)
- Overvåg en enkelt enhed og hold øje med om den forsvinder fra netværket

### Netværksadaptere
- Vis alle systemets netværksgrænseflader med detaljer
- Nulstil adapter

### IP-konfiguration
- Skift mellem DHCP og statisk IP via netsh
- Angiv IP-adresse, subnetmaske og gateway
- Flush DNS-cache med ét klik

---

## Persistens

Brugerpræferencer og data gemmes i `%APPDATA%\M1Scan\`:

| Fil | Indhold |
|---|---|
| `ui_settings.json` | Graph og Diagnostik kollapset/udfoldet tilstand |
| `dashboard.json` | Seneste hastighedstestresultat |
| `known_devices.json` | Kendte enheder og ny-enheds-status |

---

## Krav

- Windows 10 eller nyere
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Administratorrettigheder** (til ARP, netsh og netværksændringer)

---

## Byg og kør

```powershell
dotnet build
dotnet run
```

Fra en forhøjet terminal, eller via `run.ps1` som anmoder om elevation automatisk.

---

## Forfatter

Michael Larsen — mm@nice1.dk

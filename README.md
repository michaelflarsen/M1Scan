![M1Scan Logo](Resources/m1scan-logo.jpg)

![Version](https://img.shields.io/badge/version-1.3.19-blue?style=for-the-badge)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey?style=for-the-badge)
![.NET](https://img.shields.io/badge/.NET-8.0-purple?style=for-the-badge)

# M1Scan — Netværksværktøj til Windows

Avanceret Windows-netværksværktøj med live overvågning, topologivisualisering, sundhedsscore, enhedssporing og IP-konfiguration. Bygget i WPF med dark theme og MVVM-arkitektur.

---

## Funktioner

- **Live netværksovervågning** — Se latenstid, jitter og pakketab i realtid med historiske grafer for både gateway og internet. En samlet netværksscore (0–100, karakter A–F) giver et øjebliksbillede af forbindelseskvaliteten.
- **Netværkstopologi** — Visuel oversigt over stien fra din aktive adapter til routeren og videre ud på internettet, med IP-adresser og statusindikatorer.
- **Diagnostik** — Automatisk måling af DNS-svartider, DHCP-leaseinfo, IPv6-tilgængelighed, captive portal-detektion og on-demand hastighedstest.
- **Netværksscanning** — Ping/ARP-sweep af subnet eller enkelt host med MAC-adresse, vendoroplysninger, NetBIOS-navn og TTL-baseret OS-gæt.
- **Enhedssporing** — Hold øje med kendte enheder på netværket og få besked om nye enheder siden sidst.
- **IP-konfiguration** — Skift mellem DHCP og statisk IP, sæt gateway og subnet, flush DNS-cache.

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

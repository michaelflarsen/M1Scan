# M1Scan Dashboard — Prioriteret Layout

## Struktur: 3 Niveauer

### Niveau 1: Status + Topologi (Hero)
- **Netværksscore** (100) som standalone kort foran kæden
- **Topologi-kæde** (Adapter → Router → WAN) med live data
  - Adapter-kortet absorberer "Mine adaptere"-sektion
  - Tailscale bliver en badge på adapteren (ikke separat kort)
- **Icon + kort tekst** (Ethernet 2, 192.168.5.32, +Tailscale badge)
- **Ét blik svar**: er jeg online, og hvor godt?

**Layout:** Horisontalt — Score (fast bredde) + Kæde (*-flexed)

---

### Niveau 2: Bevis (Ping + Hastighed)
- **3 grafikort i grid**: Gateway, Internet, Hastighed
- Hver viser:
  - Label + værdi (0 ms, 3 ms, 806/105 Mbit/s)
  - Mini-sparkline (ping tidsserie)
  - Metadata (jitter, tab%, alder)
- **Hastigheds-kort** viser alderen i orange ("18 t gammel")
  - "Test igen"-knap for refresh uden at køre fuld scan
- **Svar**: hvad understøtter scoren?

**Layout:** 3-kolonne grid (auto-fit 160px min)

---

### Niveau 3: Reference (Sammenklappet)
- **Detaljer**-expander:
  - DHCP-lease udløber i…
  - IPv6-status
  - Captive portal
  - Preview i header ("DHCP udløber 23 t · IPv6 ikke tilgængelig · Ingen portal")
  
- **Kendte enheder**-expander:
  - Enhedsantal, subnets, ARP-cache-info i header
  - Detaljer under fold

- **Svar**: hvor er fejlene, hvis der er nogen?

**Layout:** Stacked expandere med preview-tekst (chevron + label + grå metadata)

---

## Fjernede/Sammenlagte Elementer
- ❌ "Mine adaptere" kort → absorberet i topologi-kæde
- ❌ Diagnostik-sektion øverst → flyttet til "Detaljer"-expander
- ❌ Duplikeret adapter-info → én kilde i kæde
- ✅ Hastigheds-alder → synlig som orange warning

---

## Visuelt Princip
**Kognitivt hierarki over visuelt**
- Topologi-kæden får samme visuelle vægt som før, men adaptere-detaljerne er nu *inden* kæden
- Score skalerer højere (større font/mere kontrastfarve)
- Grafer er mindre end før (understøttende, ikke hero)
- Expandere på grå baggrund (lav kontrastfarve = "detalje-niveau")

---

## Implementering (WPF)
```
Grid (RowDefinitions: Auto, Auto, Auto)
├── Row 0: HorizontalStackPanel
│   ├── ScoreCard (120px wide, centered)
│   ├── TopologyChain (*, flex)
├── Row 1: UniformGrid (3 columns)
│   ├── PingCard (Gateway)
│   ├── PingCard (Internet)
│   ├── SpeedCard (+ "Test igen")
├── Row 2: StackPanel
│   ├── DetailsExpander (with preview)
│   ├── DevicesExpander (with preview)
```

---

## Farver (behold eksisterende)
- Magenta `#e0337a` (Score, topologi-accents)
- Cyan `#4fc3f7` (Kæde-chevrons, aktive badges)
- Mørk `#07030d` (baggrund)
- Orange (alders-warning på hastigheds-kort)

# M1Scan Performance Plan — Slår Angry IP Scanner

## Mål
M1Scan scanner i dag et /24 på ~15-30s. Angry IP Scanner (default) tager ~10s.
Efter disse ændringer: **under 3s** for et fuldt /24.

## 8 Forbedringer

1. **Pre-ARP Flood + GetIpNetTable2** — sender ARP til alle IPs parallelt, læser native ARP-tabel (ingen `arp -a` subprocess)
2. **SendARP timeout** — 1500ms timeout, forhindrer at programmet hænger på døde hosts
3. **Ping-timeout** — reduceret fra 800ms×2 til 600ms×1 (LAN-optimeret)
4. **Fix O(n²) WhenAny loop** — Task.WhenAll + ConcurrentBag, ingen list-removal
5. **TcpClient memory leak** — CancellationToken til ConnectAsync (korrekt annullering)
6. **CancellationToken** — gennemgående support + Cancel-knap i UI
7. **Batch UI-opdateringer** — DispatcherTimer 100ms batching under ping-fase
8. **DNS timeout** — 2000ms cap via CancellationToken

## Ydelsessammenligning

| Fase | Før | Efter |
|---|---|---|
| Ping /24 (sem=100, 800ms×2) | ~5-8s | ~1s (sem=150, 600ms×1) |
| MAC-opløsning (SendARP pr host, ingen timeout) | 0s–∞ | ~1ms (native ARP-tabel) |
| ARP subprocess | 50-100ms | 0ms (elimineret) |
| Portcheck (10 hosts × 4 porte) | 1s + socket-leak | 1s (leak fixet) |
| **Total /24** | **15-30s** | **< 3s** |

# Handlingsplan — Bedre udnyttelse af VS Code + Claude Code

*Vurdering baseret på 6 ugers git-historik (nice1dk, M1Scan, Relay, testhp), Claude Code-opsætning og sessioner — 6. juli 2026*

## TL;DR

Opsætningen er mere avanceret end de fleste — egne agenter, projekt-skills, CLAUDE.md-filer og code-review før commits. Det store tidsspilde ligger tre steder: **alt køres fra ét workspace (nice1dk), alt arbejde ligger direkte på main, og opsætningen har samlet støj og døde stier**. Fikses de tre ting, giver hver session markant mere.

## Hvad der allerede gøres rigtigt

- **CLAUDE.md i 3 af 4 projekter** — M1Scans er reel arkitektur-dokumentation, ikke bare fyld. Den enkeltfaktor der løfter Claudes output mest.
- **Egne agenter og skills**: `code-reviewer`, `web-builder`, `windows-app-builder` globalt, `agent-updater`/`security-reviewer` + `/release` og `/run` i M1Scan. Commits som *"v1.3.21: bugfixes fra code-review"* viser at review-loopet faktisk bruges — præcis sådan fanges fejl billigt.
- **Stabil kadence og gode commit-beskeder**: 49 commits på M1Scan og 51 på Relay på 6 uger, langt de fleste med beskrivende danske beskeder og versionsnumre. Relay-historikken (mailpoller-forløbet 19.–20. juni) læses som en logbog.

## De 5 vigtigste forbedringer

### 1. Ét VS Code-vindue pr. projekt (største gevinst)
Alle 9 Claude-sessioner ligger under nice1dk-workspacet — også ved arbejde på M1Scan. Konsekvens: Claude indlæser **nice1dk's frontend-regler** (Tailwind, screenshots, "single index.html") mens den skriver WPF/C#-kode, M1Scans egen CLAUDE.md læses ikke automatisk, og hukommelsen på tværs af sessioner er nice1dk-scoped. Der betales også kontekst-skat på irrelevante regler i hver prompt.

**Gør:** Åbn `c:\VS Code projekt\M1Scan` som sit eget vindue og start Claude Code dér. Drop `additionalDirectories`-krykken i settings til dagligt arbejde.

### 2. Feature-branches i stedet for alt på main
Alle tre repos committer direkte til main/master. Det virker for én-mands-projekter, men man mister: nem fortrydelse af en halvfærdig feature, mulighed for `/code-review ultra` på en branch, og render som *"colors"*, *"bits and bobs"* og de 7 header-pillerier på Relay (20-06, 13:28–13:57) kunne være squashet til én pæn commit.

**Gør:** Ved features fra roadmappen (f.eks. OUI Lookup): `git checkout -b feature/oui-lookup`, arbejd, kør code-review, squash-merge. Små hotfixes må gerne blive på main.

### 3. Ryd op i global settings.json
Den globale allowlist indeholder engangs-ting der aldrig skulle have været globale: `Stop-Process -Id 33080` (et konkret PID der er dødt for længst), `gh release create v1.3.10 ...` (én bestemt version), og stier til `c:\VS Code projekt\mmping` — M1Scans gamle mappenavn, som ikke findes mere.

**Gør:** Slet de døde entries, flyt M1Scan-specifikke tilladelser til `M1Scan\.claude\settings.json`, og kør `/fewer-permission-prompts` i hvert projekt — den analyserer transcripts og foreslår en fornuftig allowlist, så der kommer færre afbrydende prompts.

### 4. Ret den døde sti i nice1dk's CLAUDE.md
Den peger på Puppeteer under `C:/Users/gimig/...` — en anden maskine/bruger. Hele screenshot-workflowet (som CLAUDE.md kræver ved alt frontend-arbejde) fejler ved første forsøg, og Claude brænder tid på at fejlsøge det i hver session.

### 5. Giv Claude en måde at verificere M1Scan selv
M1Scan har ingen tests — så alle fejl fanges enten af code-revieweren (læsning) eller manuelt i UI'et. Ren logik som `HealthScore.Compute`, `OuiLookup` og subnet-beregninger er oplagt testbar.

**Gør:** Tilføj ét lille xUnit-testprojekt for Models/Utils (ikke UI). Så kan Claude selv køre `dotnet test` efter hver ændring i stedet for at regressioner opdages i v1.3.x+1. Kombinér med `/verify`-skillen før commits.

## Handlingsplan — Status

**✅ Denne uge (én times oprydning) — ALLE DONE:**
1. **DONE**: Ryd global `settings.json` (punkt 3) — fjernet døde entries og M1Scan-specifikke regler.
2. **DONE**: Slet `testhp` — mappen er slettet ✓
3. **DONE**: Åbn M1Scan i eget VS Code-vindue ✓

**✅ Ny vane fra næste feature — GENNEMFØRT 6. juli 2026:**
4. **DONE**: Feature-branch + `/code-review` (evt. `ultra`) før merge (punkt 2).
   - Visuel Traceroute (v1.3.31) blev bygget på `feature/test-new-features`: 7 arbejds-commits på branchen, code-review kørt **før** merge, squash-merged til main som én ren commit.
   - Reviewet fandt **5 kritiske fejl før release** (2 WPF-threading-crashes, memory leak i event handlers, CancellationTokenSource-leak, slugt OperationCanceledException) — fixet, re-reviewet (PASS) og først derefter releaset. Beviset på at workflowet virker.
   - Læring til næste gang: brug beskrivende branch-navne (`feature/traceroute`, ikke `feature/test-new-features`), og slet branchen efter merge.
5. OUI Lookup fra `Design_Plan 1.3.30.md` blev sprunget over til fordel for Traceroute (større scope, men gik godt). OUI Lookup er stadig åben som fremtidig feature.

**✅ Inden for en måned:**
6. **DONE**: xUnit-testprojekt i M1Scan (punkt 5), start med `HealthScore.Compute`.
   - Oprettet `M1Scan.Tests/M1Scan.Tests.csproj` med xUnit 2.7.0
   - Skrevet 15 tests for `HealthScore.Compute` der dækker:
     - Insufficient samples (< 5) → "Måler..."
     - 100% packet loss → "Offline"
     - Lav latency → Grade A
     - Høj latency → lavere score
     - Packet loss effekt
     - Gateway bonus/malus
     - DNS bonus effekt
     - Jitter impact
     - Score clamping (0-100)
     - Grade matching
     - Color + verdict sempre present
   - Alle 15 tests passerer ✓

7. **DONE**: `PostToolUse`-hook der kører `dotnet test` efter .cs-redigeringer i M1Scan.
   - Hook tilføjet til `.claude/settings.json`
   - Trigger: `Edit` tool på `.cs`-filer
   - Kører: `dotnet test --logger=console -v minimal`
   - Status message: "Running tests..."
   - Hook kører never-block (fejl undertrykkes)

## Udestående review-fund fra v1.3.31 (Traceroute)

Code-reviewet 6. juli 2026 fandt 15 fejl; de 5 kritiske blev fixet før release. Disse 10 er ikke release-blokkere, men bør tages ved næste lejlighed (evt. som `fix/traceroute-review-fund`-branch):

**Trådsikkerhed / robusthed:**
1. `TracerouteService.ContinuousProbeAsync` — `hop.LatencySeries.Add()` kaldes fra baggrundstråd mens UI-tråden læser `Avg`/`LossPercent` i `RedrawGraph`. Virker i praksis (ingen bindings direkte på collection), men er teoretisk race. Fix: marshal Add til Dispatcher eller gør LatencySeries trådsikker.
2. `TracerouteViewModel` — `Hops.Clear()` ved ny trace uden guard mod at en kørende probe stadig bruger listen.
3. `ToggleProbingAsync` — kopierer `Hops` til en `List<>`; hvis brugeren rydder/erstatter `Hops` under probing, opdaterer proben en forældet liste som UI'et ikke viser.

**API-kontrakt / arkitektur:**
4. `ContinuousProbeAsync` muterer de indsendte `TraceHopInfo`-objekter in-place uden at dokumentere det — utydelig kontrakt, inviterer til misbrug.
5. DNS reverse lookup bruger bare `Dns.GetHostEntryAsync` uden timeout — kan hænge længe ved træg DNS. Genbrug timeout-wrapper-mønstret fra `NetworkService`.
6. `TracerouteService` får `IDiagnosticsService` injiceret men bruger den aldrig — fjern parameteren eller tag den i brug.

**Ydelse:**
7. `LatencySeries.Add()` (DashboardModels.cs) enumererer samples-køen 5+ gange pr. kald (Where+ToList, Average, Max, Count) — mærkbart ved kontinuerlig probing hvert 2. sekund. Fix: én gennemløbning eller inkrementelle aggregater.
8. `UpdateMaxLatency` gennemløber alle hops ved hver enkelt hop-opdatering (O(n²) over en trace) — kunne blot sammenligne det nye hops Avg mod nuværende max.

**UI-finish:**
9. `TracerouteView.xaml.cs` — labelplacering på canvas bruger hardcodede pixel-offsets (12, 16, 8, 5) i stedet for faktiske tekstmål; skæv ved lange labels/andre DPI.
10. Graf-tegnekoden genskaber alle Rectangle/TextBlock-elementer ved hver redraw i stedet for at genbruge — OK nu, men spild ved live-probing hvor der redrawes hvert 2. sekund.

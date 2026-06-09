Du er ved at lave en ny release af M1Scan. Følg disse trin i rækkefølge:

## Trin 1 – Code review (valgfrit)
Spørg brugeren: "Vil du køre code-reviewer før release? (ja/nej)"
- Hvis ja: kør `code-reviewer` agenten på seneste ændringer, vis resultatet, og spørg om vi skal fortsætte.
- Hvis nej: fortsæt direkte til trin 2.

## Trin 2 – Bump version
Læs `<Version>` fra `M1Scan.csproj`.
Bump patch-nummeret med 1 (fx 1.3.3 → 1.3.4).
Opdater filen. Gem den nye version i variablen `{NY_VERSION}`.

## Trin 3 – Stop kørende instans
Kør via PowerShell:
```
Stop-Process -Name "M1Scan" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
```
Dette frigør `.exe`-filen så buildet kan overskrive den.

## Trin 4 – Build Release
Kør:
```
dotnet build --configuration Release
```
Hvis build fejler: stop og fortæl brugeren hvad der gik galt.

Den kompilerede `.exe` ligger i:
`bin\Release\net8.0-windows\M1Scan.exe`

## Trin 5 – Commit og push
Stage alle ændrede trackede filer + evt. nye projektfiler:
```
git add -u
git status --short
```
Vis hvilke filer der er staged. Hvis der er nye untracked filer der hører til releasen, tilføj dem eksplicit.

Commit og push:
```
git commit -m "v{NY_VERSION}: <kort beskrivelse af hvad der er nyt>"
git push origin main
```

Commit-beskrivelsen skal opsummere de vigtigste ændringer i denne version, ikke bare "bump version".

## Trin 6 – Opret GitHub Release med binær
Brug `gh` CLI til at oprette et release-tag og uploade `.exe`:
```
gh release create v{NY_VERSION} \
  "bin\Release\net8.0-windows\M1Scan.exe#M1Scan.exe" \
  --title "M1Scan v{NY_VERSION}" \
  --notes "<release notes: hvad er nyt, hvad er fixet>"
```

Release notes skal indeholde:
- Nye features
- Bugfixes
- Eventuelle breaking changes

## Trin 7 – Bekræft
Fortæl brugeren:
- `v{NY_VERSION}` er committet og pushet til GitHub
- GitHub Release er oprettet med `M1Scan.exe` som artifact
- Link til release: `https://github.com/michaelflarsen/M1Scan/releases/tag/v{NY_VERSION}`

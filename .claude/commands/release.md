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

## Trin 4 – Publish self-contained single-file exe
Kør:
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
Hvis build fejler: stop og fortæl brugeren hvad der gik galt.

VIGTIGT: Brug `dotnet publish` (ikke `dotnet build`). En almindelig build laver kun en lille launcher-`.exe` (~165 KB) der kræver løse DLL'er ved siden af + installeret .NET 8 Runtime. Hvis man kun uploader den ene exe, kan den ikke starte hos brugeren. `publish` med `--self-contained` + `PublishSingleFile` pakker kode, alle DLL'er og hele runtimen ind i én exe (~165 MB) der kører på enhver Windows-maskine uden installation.

Den selvstændige `.exe` ligger i:
`bin\Release\net8.0-windows\win-x64\publish\M1Scan.exe`

Bemærk: `.pdb`-filen ved siden af er kun debug-symboler og skal IKKE med i releasen.

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

## Trin 6 – Beregn SHA-256 af exe'en (OBLIGATORISK)
```
(Get-FileHash "bin\Release\net8.0-windows\win-x64\publish\M1Scan.exe" -Algorithm SHA256).Hash.ToLower()
```
Gem resultatet i `{SHA256}`.

**Dette trin må ikke springes over.** Den indbyggede opdatering nægter at installere
en release hvis release-noterne ikke indeholder en `SHA256:`-linje, og hvis den hentede
fil ikke matcher hashen (se `Services/UpdateService.cs`). Uden hashen kan brugerne ikke
opdatere fra appen — de vil blot aldrig få vist "Update now".

## Trin 7 – Opret GitHub Release med binær
Brug `gh` CLI til at oprette et release-tag og uploade den self-contained `.exe`:
```
gh release create v{NY_VERSION} \
  "bin\Release\net8.0-windows\win-x64\publish\M1Scan.exe#M1Scan.exe" \
  --title "M1Scan v{NY_VERSION}" \
  --notes "<release notes>

SHA256: {SHA256}"
```

Release notes skal indeholde:
- Nye features
- Bugfixes
- Eventuelle breaking changes
- En linje `SHA256: {SHA256}` (kræves af auto-opdateringen)

## Trin 8 – Bekræft
Fortæl brugeren:
- `v{NY_VERSION}` er committet og pushet til GitHub
- GitHub Release er oprettet med den self-contained `M1Scan.exe` som artifact (kører uden .NET-installation)
- SHA-256 er offentliggjort i release-noterne, så auto-opdateringen kan verificere downloadet
- Link til release: `https://github.com/michaelflarsen/M1Scan/releases/tag/v{NY_VERSION}`

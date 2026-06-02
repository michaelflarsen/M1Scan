Du er ved at lave en ny release af M1Scan. Følg disse trin i rækkefølge:

## Trin 1 – Code review (valgfrit)
Spørg brugeren: "Vil du køre code-reviewer før release? (ja/nej)"
- Hvis ja: kør `code-reviewer` agenten på seneste ændringer, vis resultatet, og spørg om vi skal fortsætte.
- Hvis nej: fortsæt direkte til trin 2.

## Trin 2 – Bump version
Læs `<Version>` fra `M1Scan.csproj`.
Bump patch-nummeret med 1 (fx 1.3.3 → 1.3.4).
Opdater filen.

## Trin 3 – Build
Kør `dotnet build --configuration Release`.
Hvis build fejler: stop og fortæl brugeren hvad der gik galt.

## Trin 4 – Commit og push
```
git add M1Scan.csproj
git commit -m "v{NY_VERSION}: bump version"
git push origin main
```

## Trin 5 – Bekræft
Fortæl brugeren at v{NY_VERSION} er pushet til GitHub.

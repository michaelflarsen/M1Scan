## Udvikler-opsætning (Windows)

Følgende hjælper dig i gang med udvikling på `M1Scan` på Windows.

Krav:
- Windows 10 eller nyere
- .NET 8 SDK (ikke kun runtime)

1) Åbn PowerShell i projektmappen og tjek .NET SDK:

```powershell
dotnet --version
```

2) Gør klar og byg:

```powershell
.\dev\build.ps1
```

3) Kør applikationen i udvikling:

```powershell
.\dev\run.ps1
```

Hvis du mangler .NET 8 SDK, hent det fra https://dotnet.microsoft.com/

# Bidrag til M1Scan

Tak fordi du overvejer at bidrage til M1Scan! Alle bidrag er velkomne.

## Kom i gang

### Krav til udviklingsmiljø

- [Visual Studio 2022](https://visualstudio.microsoft.com/) eller nyere (Community er gratis)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10 eller Windows 11

### Opsætning

1. Fork projektet på GitHub
2. Klon dit fork: `git clone https://github.com/DIT-BRUGERNAVN/mmping.git`
3. Åbn `M1Scan.csproj` i Visual Studio
4. Byg projektet: `dotnet build`

## Rapportér en fejl

Brug GitHub Issues og vælg skabelonen **Bug report**. Inkluder:

- En klar beskrivelse af fejlen
- Trin til at genskabe problemet
- Hvad du forventede skulle ske
- Din Windows-version og .NET-version

Rapportér **aldrig** sikkerhedsproblemer som et offentligt issue — se [SECURITY.md](SECURITY.md).

## Foreslå en feature

Brug GitHub Issues og vælg skabelonen **Feature request**. Beskriv problemet du vil løse og din foreslåede løsning.

## Pull Requests

### Branch-navngivning

| Type | Eksempel |
|------|---------|
| Ny feature | `feature/adapter-grupering` |
| Fejlrettelse | `fix/ping-timeout-crash` |
| Refaktorering | `refactor/scan-service` |

### Krav

1. Opret en branch fra `main`
2. Følg MVVM-arkitekturen — ViewModels må ikke have direkte UI-afhængigheder
3. Byg projektet uden fejl: `dotnet build`
4. Beskriv ændringen tydeligt i PR-beskrivelsen

### Kodekonventioner

- Sprog: C# med .NET 8-syntaks
- Navngivning: PascalCase for klasser og properties, camelCase for lokale variabler
- Asynkron kode: brug `async`/`await` — undgå `.Result` og `.Wait()`
- MVVM: brug `CommunityToolkit.Mvvm` til commands og observable properties
- Kommentarer kun når *hvorfor* ikke er indlysende — ikke hvad koden gør

## Spørgsmål?

Skriv til **mm@nice1.dk** eller åbn et GitHub Discussion.

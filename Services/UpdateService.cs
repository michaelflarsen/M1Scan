using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace M1Scan.Services
{
    public interface IUpdateService
    {
        Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default);
        Task DownloadUpdateAsync(string downloadUrl, string destinationPath, string expectedSha256,
            IProgress<double> progress, CancellationToken ct = default);
        void LaunchUpdaterAndRestart(string newExePath, string expectedSha256);
    }

    public sealed record UpdateCheckResult(
        Version LatestVersion, string TagName, string DownloadUrl, string ReleaseUrl, string Sha256);

    // Tjekker GitHub Releases for en nyere version af M1Scan og kan hente +
    // installere den. Appen kan ikke overskrive sin egen kørende .exe (fillås),
    // så selve installationen sker via et lille PowerShell-script der venter på
    // at processen lukker, verificerer den hentede fil, kopierer den ind og genstarter.
    //
    // SIKKERHED — appen kører elevated (requireAdministrator), så et opdaterings-
    // flow uden verifikation er en direkte vej til kodeudførsel som administrator.
    // Derfor gælder tre regler her:
    //   1. Download-URL'en skal ligge på en GitHub-vært (allowlist nedenfor).
    //   2. Den hentede exe skal matche en SHA-256 offentliggjort i release-noterne,
    //      og hashen verificeres IGEN inde i det elevated script lige før kopiering
    //      (filen ligger i en bruger-skrivbar mappe mellem de to trin — uden den
    //      anden kontrol kunne den byttes i tidsvinduet).
    //   3. Updater-scriptet sendes som -EncodedCommand, ikke som en .ps1 på disken,
    //      så der ikke findes en scriptfil en angriber kan overskrive før den kører
    //      med administrator-rettigheder.
    public class UpdateService : IUpdateService
    {
        private const string RepoOwner = "michaelflarsen";
        private const string RepoName = "M1Scan";
        private const string AssetName = "M1Scan.exe";

        // Kun disse værter må levere en opdatering. browser_download_url kommer fra
        // JSON og må ikke pege hvor som helst.
        private static readonly string[] AllowedDownloadHosts =
        {
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com"
        };

        // Release-noterne skal indeholde en linje med "SHA256: <64 hex>".
        // Negativ lookahead (?![0-9a-fA-F]) forhindrer at et 65.+ tegn langt
        // hex-stykke matches som en (trunkeret, forkert) 64-tegns hash — uden
        // grænsen ville "SHA256: <65 hex-tegn>" matche de første 64 og give en
        // hash der IKKE er den der reelt blev skrevet.
        private static readonly Regex Sha256Pattern =
            new(@"SHA-?256\s*[:=]\s*([0-9a-fA-F]{64})(?![0-9a-fA-F])", RegexOptions.Compiled);

        // API-kaldet er en lille JSON-respons — 8s timeout er rigelig.
        private static readonly HttpClient _apiHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

        // HttpClient.Timeout gælder hele request-levetiden, også response-body-læsning,
        // selv med ResponseHeadersRead. Den self-contained exe er ~166 MB, så et
        // klient-timeout her ville afbryde downloadet på enhver almindelig forbindelse.
        // Kald-stedet styrer i stedet den reelle deadline via CancellationToken.
        private static readonly HttpClient _downloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

        static UpdateService()
        {
            foreach (var client in new[] { _apiHttp, _downloadHttp })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("M1Scan-UpdateChecker");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            }
        }

        private sealed record GitHubRelease(
            [property: JsonPropertyName("tag_name")] string TagName,
            [property: JsonPropertyName("body")] string? Body,
            [property: JsonPropertyName("assets")] System.Collections.Generic.List<GitHubAsset>? Assets);

        private sealed record GitHubAsset(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

        public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
        {
            try
            {
                string url = ResolveCheckUrl();

                var json = await _apiHttp.GetStringAsync(url, ct);
                var release = System.Text.Json.JsonSerializer.Deserialize<GitHubRelease>(json);
                if (release?.TagName is null) return null;

                var tagVersionText = release.TagName.TrimStart('v', 'V');
                if (!Version.TryParse(tagVersionText, out var latest)) return null;

                var asset = release.Assets?.FirstOrDefault(a =>
                    string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
                if (asset is null) return null;

                if (!IsAllowedDownloadUrl(asset.BrowserDownloadUrl))
                {
                    Debug.WriteLine($"UpdateService: afvist download-URL '{asset.BrowserDownloadUrl}'.");
                    return null;
                }

                // Fail closed: uden en offentliggjort hash kan vi ikke afgøre om den
                // hentede exe er den rigtige, og vi nægter at tilbyde opdateringen.
                var sha = Sha256Pattern.Match(release.Body ?? string.Empty);
                if (!sha.Success)
                {
                    Debug.WriteLine("UpdateService: release-noterne mangler en SHA256-linje — opdatering afvist.");
                    return null;
                }

                // "1.3.29" parses med Build/Revision = -1 hvis tagget har færre end 4
                // led, mens assembly-versionen fra csproj ("1.3.28.0") altid har alle
                // felter udfyldt af MSBuild — uden normalisering ville -1 < 0 altid
                // gøre den parsede tag-version "mindre" end den faktisk er, selv ved
                // ens versioner. Normaliser manuelt i stedet for .ToString(3), som
                // kaster hvis Build/Revision ikke er sat (fx et fremtidigt "v2.0"-tag).
                var current = Normalize(Assembly.GetExecutingAssembly().GetName().Version!);
                var latestNormalized = Normalize(latest);

                if (latestNormalized.CompareTo(current) <= 0) return null;

                return new UpdateCheckResult(latestNormalized, release.TagName, asset.BrowserDownloadUrl,
                    $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/{release.TagName}",
                    sha.Groups[1].Value.ToLowerInvariant());
            }
            catch (Exception ex)
            {
                // Stille fejl ved opstartstjek: offline / GitHub nede / rate-limited /
                // uventet JSON — må aldrig forsinke eller crashe appen. Debug.WriteLine
                // er no-op i release-builds uden debugger, så det er gratis diagnostik.
                Debug.WriteLine($"UpdateService.CheckForUpdateAsync fejlede: {ex}");
                return null;
            }
        }

        private static string ResolveCheckUrl()
        {
            const string defaultUrl =
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
#if DEBUG
            // Test-override, se release.md-testplanen — peger evt. på
            // /releases/tags/{tag} i stedet for /releases/latest.
            // KUN i debug-builds: i en release-build ville en env-variabel kunne
            // omdirigere opdateringstjekket til en vilkårlig server, og den hentede
            // exe erstatter en proces der kører som administrator.
            return Environment.GetEnvironmentVariable("M1SCAN_UPDATE_CHECK_URL") ?? defaultUrl;
#else
            return defaultUrl;
#endif
        }

        private static bool IsAllowedDownloadUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

        private static Version Normalize(Version v) =>
            new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        public async Task DownloadUpdateAsync(string downloadUrl, string destinationPath,
            string expectedSha256, IProgress<double> progress, CancellationToken ct = default)
        {
            // Kontrollér igen her: metoden er public på interfacet, så den må ikke
            // stole på at kalderen allerede har valideret URL'en.
            if (!IsAllowedDownloadUrl(downloadUrl))
                throw new SecurityException($"Download-URL er ikke tilladt: {downloadUrl}");
            if (!IsValidSha256(expectedSha256))
                throw new SecurityException("Ugyldig eller manglende SHA-256 for opdateringen.");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var resp = await _downloadHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;

            // Hash beregnes mens vi streamer, så filen ikke skal læses igen bagefter
            // (den er ~166 MB).
            using var sha = SHA256.Create();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total is > 0) progress.Report(done * 100.0 / total.Value);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }

            var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destinationPath);
                throw new SecurityException(
                    $"Opdateringen blev afvist: SHA-256 stemmer ikke.\n" +
                    $"Forventet: {expectedSha256}\nFaktisk:   {actual}");
            }

            progress.Report(100);
        }

        private static bool IsValidSha256(string? hex) =>
            hex is { Length: 64 } && hex.All(Uri.IsHexDigit);

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* bedste forsøg */ }
        }

        public void LaunchUpdaterAndRestart(string newExePath, string expectedSha256)
        {
            if (!IsValidSha256(expectedSha256))
                throw new SecurityException("Ugyldig SHA-256 — opdatering afbrudt.");

            string oldExePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Kunne ikke finde kørende exe-sti.");

            // Scriptet sendes som -EncodedCommand i stedet for at skrives som en .ps1.
            // En scriptfil ville ligge i en bruger-skrivbar mappe i tidsrummet mellem
            // at vi skriver den og PowerShell (elevated) læser den — altså en TOCTOU
            // hvor enhver proces som brugeren kunne udskifte indholdet og få det kørt
            // som administrator. -EncodedCommand efterlader ingen fil at bytte.
            var script = BuildUpdaterScript(Environment.ProcessId, oldExePath, newExePath, expectedSha256);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);

            using var process = Process.Start(psi);
        }

        // Stierne indsættes som single-quoted PowerShell-literaler; ' fordobles, så en
        // sti med apostrof ikke kan bryde ud af strengen. Hashen er valideret til 64
        // hex-tegn ovenfor og kan derfor ikke indeholde noget aktivt.
        private static string BuildUpdaterScript(int procId, string oldExe, string newExe, string sha256)
        {
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";

            // $$""" — to dollartegn betyder at en interpolation kræver {{...}}, så
            // PowerShells egne { } forbliver almindelige tegn og ikke skal dobles.
            return $$"""
                $ErrorActionPreference = 'Stop'
                $procId = {{procId}}
                $oldExe = {{Q(oldExe)}}
                $newExe = {{Q(newExe)}}
                $expected = {{Q(sha256)}}

                try {
                    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
                    if ($p) { $p.WaitForExit(30000) | Out-Null }
                } catch {}

                Start-Sleep -Milliseconds 500

                # Anden hash-kontrol, nu i den elevated kontekst og umiddelbart før
                # kopieringen. $newExe ligger i en bruger-skrivbar mappe, så uden denne
                # kunne filen være byttet efter at appen verificerede den.
                try {
                    $actual = (Get-FileHash -LiteralPath $newExe -Algorithm SHA256).Hash
                } catch {
                    exit 1
                }
                if ($actual -ne $expected) {
                    Remove-Item -LiteralPath $newExe -Force -ErrorAction SilentlyContinue
                    exit 1
                }

                $copied = $false
                for ($i = 0; $i -lt 20; $i++) {
                    try { Copy-Item -LiteralPath $newExe -Destination $oldExe -Force; $copied = $true; break }
                    catch { Start-Sleep -Milliseconds 500 }
                }

                if ($copied) { Start-Process -FilePath $oldExe }

                Remove-Item -LiteralPath $newExe -Force -ErrorAction SilentlyContinue
                """;
        }
    }
}

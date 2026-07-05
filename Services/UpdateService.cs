using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace M1Scan.Services
{
    public interface IUpdateService
    {
        Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default);
        Task DownloadUpdateAsync(string downloadUrl, string destinationPath, IProgress<double> progress, CancellationToken ct = default);
        void LaunchUpdaterAndRestart(string newExePath);
    }

    public sealed record UpdateCheckResult(Version LatestVersion, string TagName, string DownloadUrl, string ReleaseUrl);

    // Tjekker GitHub Releases for en nyere version af M1Scan og kan hente +
    // installere den. Appen kan ikke overskrive sin egen kørende .exe (fillås),
    // så selve installationen sker via et lille PowerShell-script der venter på
    // at processen lukker, kopierer den nye exe ind, og genstarter.
    public class UpdateService : IUpdateService
    {
        private const string RepoOwner = "michaelflarsen";
        private const string RepoName = "M1Scan";
        private const string AssetName = "M1Scan.exe";

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
            [property: JsonPropertyName("assets")] System.Collections.Generic.List<GitHubAsset>? Assets);

        private sealed record GitHubAsset(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

        public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
        {
            try
            {
                // M1SCAN_UPDATE_CHECK_URL: manuel test-override, se release.md-testplan —
                // peger evt. på /releases/tags/{tag} i stedet for /releases/latest.
                string url = Environment.GetEnvironmentVariable("M1SCAN_UPDATE_CHECK_URL")
                             ?? $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

                var json = await _apiHttp.GetStringAsync(url, ct);
                var release = System.Text.Json.JsonSerializer.Deserialize<GitHubRelease>(json);
                if (release?.TagName is null) return null;

                var tagVersionText = release.TagName.TrimStart('v', 'V');
                if (!Version.TryParse(tagVersionText, out var latest)) return null;

                var asset = release.Assets?.FirstOrDefault(a =>
                    string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
                if (asset is null) return null;

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
                    $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/{release.TagName}");
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

        private static Version Normalize(Version v) =>
            new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        public async Task DownloadUpdateAsync(string downloadUrl, string destinationPath,
            IProgress<double> progress, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var resp = await _downloadHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0) progress.Report(done * 100.0 / total.Value);
            }
            progress.Report(100);
        }

        public void LaunchUpdaterAndRestart(string newExePath)
        {
            string oldExePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Kunne ikke finde kørende exe-sti.");
            string updateDir = Path.GetDirectoryName(newExePath)!;
            string scriptPath = Path.Combine(updateDir, "apply_update.ps1");

            File.WriteAllText(scriptPath, UpdaterScript, Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // ArgumentList undgår manuel citat-escaping af stier (fremfor en
            // interpoleret Arguments-streng) — konsistent med projektets
            // konvention for at undgå shell-injection i Process.Start-kald.
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-ProcId");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("-OldExe");
            psi.ArgumentList.Add(oldExePath);
            psi.ArgumentList.Add("-NewExe");
            psi.ArgumentList.Add(newExePath);

            using var process = Process.Start(psi);
        }

        private const string UpdaterScript = """
            param([int]$ProcId, [string]$OldExe, [string]$NewExe)

            try {
                $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
                if ($p) { $p.WaitForExit(30000) | Out-Null }
            } catch {}

            Start-Sleep -Milliseconds 500

            $copied = $false
            for ($i = 0; $i -lt 20; $i++) {
                try { Copy-Item -LiteralPath $NewExe -Destination $OldExe -Force; $copied = $true; break }
                catch { Start-Sleep -Milliseconds 500 }
            }

            if ($copied) { Start-Process -FilePath $OldExe }

            Remove-Item -LiteralPath $NewExe -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;
    }
}

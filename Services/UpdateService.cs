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

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

        static UpdateService()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("M1Scan-UpdateChecker");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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

                var json = await _http.GetStringAsync(url, ct);
                var release = System.Text.Json.JsonSerializer.Deserialize<GitHubRelease>(json);
                if (release?.TagName is null) return null;

                var tagVersionText = release.TagName.TrimStart('v', 'V');
                if (!Version.TryParse(tagVersionText, out var latest)) return null;

                var asset = release.Assets?.FirstOrDefault(a =>
                    string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
                if (asset is null) return null;

                // "1.3.29" parses with Revision = -1, mens assembly-versionen fra csproj
                // ("1.3.28.0") har Revision = 0 — uden normalisering til 3 felter ville
                // -1 < 0 altid gøre den parsede tag-version "mindre" end den faktisk er.
                var current = Version.Parse(
                    Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
                var latestNormalized = Version.Parse(latest.ToString(3));

                if (latestNormalized.CompareTo(current) <= 0) return null;

                return new UpdateCheckResult(latestNormalized, release.TagName, asset.BrowserDownloadUrl,
                    $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/{release.TagName}");
            }
            catch
            {
                // Stille fejl ved opstartstjek: offline / GitHub nede / rate-limited /
                // uventet JSON — må aldrig forsinke eller crashe appen.
                return null;
            }
        }

        public async Task DownloadUpdateAsync(string downloadUrl, string destinationPath,
            IProgress<double> progress, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                            $"-File \"{scriptPath}\" -ProcId {Environment.ProcessId} " +
                            $"-OldExe \"{oldExePath}\" -NewExe \"{newExePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
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

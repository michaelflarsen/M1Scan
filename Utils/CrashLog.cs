using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace M1Scan.Utils
{
    /// <summary>
    /// Skriver uhåndterede undtagelser til %APPDATA%\M1Scan\crash.log.
    /// Debug.WriteLine er en no-op i release-builds uden debugger, så uden denne
    /// fil findes der ingen spor efter et crash hos en bruger.
    /// Loggen roteres ved 1 MB, så den ikke vokser i det uendelige.
    /// </summary>
    public static class CrashLog
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object _gate = new();

        public static string LogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "M1Scan", "crash.log");

        /// <summary>Logger en undtagelse. Kaster aldrig — en fejlende logger må ikke
        /// erstatte den oprindelige fejl.</summary>
        public static void Write(string source, Exception? ex)
        {
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                    // Rotér i stedet for at slette, så det seneste crash aldrig
                    // forsvinder sammen med historikken.
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(LogPath, LogPath + ".1", overwrite: true);

                    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
                    var sb = new StringBuilder()
                        .AppendLine("──────────────────────────────────────────────")
                        .AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  v{version}  [{source}]")
                        .AppendLine(ex?.ToString() ?? "(ingen exception-detaljer)")
                        .AppendLine();

                    File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Disk fuld, ACL-problem, roaming-profil offline — intet vi kan gøre.
            }
        }
    }
}

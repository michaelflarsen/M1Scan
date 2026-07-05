using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class UpdateViewModel : ObservableObject
    {
        private readonly IUpdateService _updateService;
        private string? _pendingDownloadUrl;

        private bool _isUpdateAvailable;
        private string _latestVersionLabel = string.Empty;
        private bool _isDownloading;
        private double _downloadProgressPercent;
        private string _statusText = string.Empty;
        private bool _hasError;

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set => SetProperty(ref _isUpdateAvailable, value);
        }

        public string LatestVersionLabel
        {
            get => _latestVersionLabel;
            private set => SetProperty(ref _latestVersionLabel, value);
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            private set => SetProperty(ref _isDownloading, value);
        }

        public double DownloadProgressPercent
        {
            get => _downloadProgressPercent;
            private set => SetProperty(ref _downloadProgressPercent, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public bool HasError
        {
            get => _hasError;
            private set => SetProperty(ref _hasError, value);
        }

        public RelayCommand UpdateNowCommand { get; }

        public UpdateViewModel(IUpdateService updateService)
        {
            _updateService = updateService;
            UpdateNowCommand = new RelayCommand(async _ => await UpdateNowAsync(),
                                                 _ => IsUpdateAvailable && !IsDownloading);
        }

        // Kaldes fire-and-forget fra MainViewModel-constructoren. Kaster aldrig —
        // UpdateService svelger alle fejl internt (offline/GitHub-nede skal ikke
        // forsinke eller afbryde app-opstart).
        public async Task CheckForUpdateSilentlyAsync()
        {
            var result = await _updateService.CheckForUpdateAsync();
            if (result is null) return;

            _pendingDownloadUrl = result.DownloadUrl;
            LatestVersionLabel = "v" + result.LatestVersion.ToString(3);
            StatusText = $"{LatestVersionLabel} tilgængelig — Update now!";
            IsUpdateAvailable = true;
        }

        private async Task UpdateNowAsync()
        {
            if (_pendingDownloadUrl is null) return;

            IsDownloading = true;
            HasError = false;
            StatusText = "Downloader 0%";

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "M1Scan", "Update");
                string destPath = Path.Combine(dir, "M1Scan_new.exe");

                var progress = new Progress<double>(p =>
                {
                    DownloadProgressPercent = p;
                    StatusText = $"Downloader {p:0}%";
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await _updateService.DownloadUpdateAsync(_pendingDownloadUrl, destPath, progress, cts.Token);

                StatusText = "Genstarter...";
                _updateService.LaunchUpdaterAndRestart(destPath);

                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                // Download-fejl under aktivt klik: brugeren bad selv om det, så en
                // synlig fejlbesked er OK her (modsat det stille opstartstjek).
                IsDownloading = false;
                HasError = true;
                StatusText = $"Update fejlede: {ex.Message}";
            }
        }
    }
}

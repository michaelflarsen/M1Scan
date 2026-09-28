using System;
using System.Threading;
using System.Threading.Tasks;
using M1Scan.Services;
using M1Scan.ViewModels;
using Xunit;

namespace M1Scan.Tests.ViewModels
{
    /// <summary>
    /// UpdateViewModel's konstruktør er triviel (kun kommando-wiring). Kun
    /// CheckForUpdateSilentlyAsync testes her — success-stien i UpdateNowAsync
    /// rammer System.Windows.Application.Current.Shutdown() og downloader/verificerer
    /// en fil på disk, hvilket kræver en WPF-kontekst og hører til en anden test-form
    /// (fx en manuel/UI-test), ikke en ren ViewModel-unittest.
    /// </summary>
    public class UpdateViewModelTests
    {
        private sealed class FakeUpdateService : IUpdateService
        {
            public UpdateCheckResult? ResultToReturn;
            public Exception? ThrowOnCheck;

            public Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
            {
                if (ThrowOnCheck != null) throw ThrowOnCheck;
                return Task.FromResult(ResultToReturn);
            }

            public Task DownloadUpdateAsync(string downloadUrl, string destinationPath, string expectedSha256,
                IProgress<double> progress, CancellationToken ct = default) =>
                throw new NotSupportedException("Ikke dækket af disse tests — kræver WPF Application.Current.");

            public void LaunchUpdaterAndRestart(string newExePath, string expectedSha256) =>
                throw new NotSupportedException();
        }

        [Fact]
        public void Constructor_StartsWithNoUpdateAvailable()
        {
            var vm = new UpdateViewModel(new FakeUpdateService());

            Assert.False(vm.IsUpdateAvailable);
            Assert.False(vm.UpdateNowCommand.CanExecute(null));
        }

        [Fact]
        public async Task CheckForUpdateSilentlyAsync_NoUpdate_LeavesStateUnchanged()
        {
            var fake = new FakeUpdateService { ResultToReturn = null };
            var vm = new UpdateViewModel(fake);

            await vm.CheckForUpdateSilentlyAsync();

            Assert.False(vm.IsUpdateAvailable);
            Assert.Equal(string.Empty, vm.StatusText);
        }

        [Fact]
        public async Task CheckForUpdateSilentlyAsync_UpdateFound_SetsAvailableStateAndEnablesCommand()
        {
            var fake = new FakeUpdateService
            {
                ResultToReturn = new UpdateCheckResult(
                    new Version(1, 4, 0), "v1.4.0", "https://github.com/x/y/releases/download/v1.4.0/M1Scan.exe",
                    "https://github.com/x/y/releases/tag/v1.4.0", "abc123"),
            };
            var vm = new UpdateViewModel(fake);

            await vm.CheckForUpdateSilentlyAsync();

            Assert.True(vm.IsUpdateAvailable);
            Assert.Equal("v1.4.0", vm.LatestVersionLabel);
            Assert.Contains("v1.4.0", vm.StatusText);
            Assert.True(vm.UpdateNowCommand.CanExecute(null));
        }

        [Fact]
        public async Task CheckForUpdateSilentlyAsync_ServiceThrows_PropagatesToCaller()
        {
            // CheckForUpdateSilentlyAsync svelger IKKE selv fejl (kommentaren i
            // UpdateViewModel siger UpdateService gør det internt) — bekræfter at
            // ViewModel-laget ikke tavst spiser en uventet undtagelse fra servicen.
            var fake = new FakeUpdateService { ThrowOnCheck = new InvalidOperationException("GitHub nede") };
            var vm = new UpdateViewModel(fake);

            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.CheckForUpdateSilentlyAsync());
            Assert.False(vm.IsUpdateAvailable);
        }
    }
}

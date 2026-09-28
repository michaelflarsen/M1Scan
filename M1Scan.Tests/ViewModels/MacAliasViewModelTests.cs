using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using M1Scan.Services;
using M1Scan.ViewModels;
using Xunit;

namespace M1Scan.Tests.ViewModels
{
    /// <summary>
    /// MacAliasViewModel kalder RefreshCommand synkront i konstruktøren, men rammer
    /// kun IMacAliasService — ingen WPF/Dispatcher — så en fake service er nok til
    /// at gøre konstruktionen deterministisk og testbar.
    /// </summary>
    public class MacAliasViewModelTests
    {
        private sealed class FakeMacAliasService : IMacAliasService
        {
            public readonly Dictionary<string, string> Store = new(StringComparer.OrdinalIgnoreCase);
            public int LoadCallCount;

            public Task LoadAsync() { LoadCallCount++; return Task.CompletedTask; }
            public Task SaveAsync() => Task.CompletedTask;
            public Task AddOrUpdateAsync(string macPrefix, string description)
            {
                Store[macPrefix] = description;
                return Task.CompletedTask;
            }
            public Task RemoveAsync(string macPrefix)
            {
                Store.Remove(macPrefix);
                return Task.CompletedTask;
            }
            public string? Lookup(string mac) => Store.TryGetValue(mac, out var v) ? v : null;
            public Dictionary<string, string> GetAll() => new(Store, StringComparer.OrdinalIgnoreCase);
        }

        private static (MacAliasViewModel vm, FakeMacAliasService fake) CreateSut()
        {
            var fake = new FakeMacAliasService();
            var vm = new MacAliasViewModel(fake);
            return (vm, fake);
        }

        [Fact]
        public void Constructor_LoadsExistingAliases()
        {
            var fake = new FakeMacAliasService();
            fake.Store["AABBCC"] = "Min router";

            var vm = new MacAliasViewModel(fake);

            var entry = Assert.Single(vm.Aliases);
            Assert.Equal("AABBCC", entry.MacPrefix);
            Assert.Equal("Min router", entry.Description);
            Assert.Equal(1, fake.LoadCallCount);
        }

        [Fact]
        public async Task AddCommand_NormalizesMacAndPersists()
        {
            var (vm, fake) = CreateSut();
            vm.MacInput = "aa:bb:cc";
            vm.DescriptionInput = "Test-enhed";

            await vm.AddCommand.ExecuteAsync(null);

            Assert.True(fake.Store.ContainsKey("AABBCC"));
            Assert.Equal("Test-enhed", fake.Store["AABBCC"]);
            var entry = Assert.Single(vm.Aliases);
            Assert.Equal("AABBCC", entry.MacPrefix);
            Assert.Equal(string.Empty, vm.MacInput); // ryddet efter succes
            Assert.Contains("added", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("AABB")]      // for kort
        [InlineData("AABBCCDDEE")] // hverken 6 eller 12 hex-tegn
        public async Task AddCommand_RejectsInvalidLength(string invalidMac)
        {
            var (vm, fake) = CreateSut();
            vm.MacInput = invalidMac;

            await vm.AddCommand.ExecuteAsync(null);

            Assert.Empty(fake.Store);
            Assert.Empty(vm.Aliases);
            Assert.Contains("6 or 12", vm.StatusMessage);
        }

        [Fact]
        public void AddCommand_CanExecute_FalseWhenMacInputEmpty()
        {
            var (vm, _) = CreateSut();
            vm.MacInput = "";

            Assert.False(vm.AddCommand.CanExecute(null));

            vm.MacInput = "AABBCC";
            Assert.True(vm.AddCommand.CanExecute(null));
        }

        [Fact]
        public async Task RemoveCommand_RemovesFromServiceAndCollection()
        {
            var (vm, fake) = CreateSut();
            vm.MacInput = "AABBCC";
            await vm.AddCommand.ExecuteAsync(null);
            var entry = Assert.Single(vm.Aliases);

            await vm.RemoveCommand.ExecuteAsync(entry);

            Assert.Empty(vm.Aliases);
            Assert.False(fake.Store.ContainsKey("AABBCC"));
            Assert.Equal("Alias removed", vm.StatusMessage);
        }

        [Fact]
        public async Task RemoveCommand_NullEntry_IsNoOp()
        {
            var (vm, fake) = CreateSut();
            fake.Store["AABBCC"] = "x";

            await vm.RemoveCommand.ExecuteAsync(null);

            // Skal ikke kaste eller røre status — ingenting at fjerne.
            Assert.True(fake.Store.ContainsKey("AABBCC"));
        }

        [Fact]
        public async Task RefreshCommand_ReloadsFromService()
        {
            var (vm, fake) = CreateSut();
            fake.Store["112233"] = "Ekstern ændring";

            await vm.RefreshCommand.ExecuteAsync(null);

            var entry = Assert.Single(vm.Aliases);
            Assert.Equal("112233", entry.MacPrefix);
            Assert.Equal(2, fake.LoadCallCount); // ctor + dette kald
        }
    }
}

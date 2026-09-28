using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.ViewModels;
using Xunit;

namespace M1Scan.Tests.ViewModels
{
    /// <summary>
    /// IpConfigViewModel kalder RefreshAdaptersCommand synkront i konstruktøren,
    /// men rammer kun INetworkService.GetNetworkAdaptersAsync — ingen WPF/Dispatcher
    /// — så fakes af INetworkService/IIpConfigService er nok.
    /// </summary>
    public class IpConfigViewModelTests
    {
        private sealed class FakeNetworkService : INetworkService
        {
            public List<NetworkAdapter> AdaptersToReturn = new();

            public Task<List<NetworkAdapter>> GetNetworkAdaptersAsync() => Task.FromResult(AdaptersToReturn);

            public Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName = "", CancellationToken ct = default) =>
                throw new NotSupportedException("Ikke brugt af IpConfigViewModel");
            public Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName, string srcIp, CancellationToken ct = default) =>
                throw new NotSupportedException();
            public Task<SweepResult> PingSweepBoundAsync(IEnumerable<string> ips, string srcIp, int timeoutMs, CancellationToken ct = default) =>
                throw new NotSupportedException();
            public Task<Dictionary<string, string>> GetArpTableAsync() => throw new NotSupportedException();
            public Dictionary<string, string> GetArpTableNative() => throw new NotSupportedException();
            public Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000, CancellationToken ct = default) =>
                throw new NotSupportedException();
            public Task<bool> CheckPortAsync(string ip, int port, string srcIp, int timeoutMs = 1000, CancellationToken ct = default) =>
                throw new NotSupportedException();
            public Task<string> GetNetBiosNameAsync(string ipAddress, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<string> GetNetBiosNameAsync(string ipAddress, string srcIp, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<string> GetMacAddressAsync(string ipAddress, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<string> ResolveHostNameAsync(string ip, int timeoutMs = 2000, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<string> ResolveMdnsNameAsync(string ip, string srcIp, int timeoutMs = 1000, CancellationToken ct = default) => throw new NotSupportedException();
            public Task FloodArpAsync(string subnet, int startIp, int endIp, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<string> SendArpRequestAsync(string ip, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<string> SendArpRequestAsync(string ip, string srcIp, CancellationToken ct = default) => throw new NotSupportedException();
        }

        private sealed class FakeIpConfigService : IIpConfigService
        {
            public IpConfigResult StaticResult = IpConfigResult.Ok("Static IP sat");
            public IpConfigResult DhcpResult = IpConfigResult.Ok("DHCP aktiveret");
            public IpConfigResult FlushResult = IpConfigResult.Ok("DNS-cache ryddet");
            public (string adapter, string ip, string mask, string gw)? LastStaticCall;

            public Task<IpConfigResult> SetStaticIpAsync(string adapterName, string ipAddress, string subnetMask, string gateway)
            {
                LastStaticCall = (adapterName, ipAddress, subnetMask, gateway);
                return Task.FromResult(StaticResult);
            }
            public Task<IpConfigResult> SetDhcpAsync(string adapterName) => Task.FromResult(DhcpResult);
            public Task<IpConfigResult> ResetNetworkAdapterAsync(string adapterName) => Task.FromResult(IpConfigResult.Ok());
            public Task<IpConfigResult> FlushDnsAsync() => Task.FromResult(FlushResult);
            public Task<IpConfigResult> RenewDhcpAsync(string adapterName) => Task.FromResult(IpConfigResult.Ok());
        }

        private static NetworkAdapter MakeAdapter(string name = "Ethernet") => new()
        {
            Name = name,
            Description = "Realtek Test NIC",
            IpAddresses = new[] { "192.168.1.50" },
            SubnetMask = "255.255.255.0",
            Gateway = "192.168.1.1",
            IsDhcpEnabled = true,
            IsConnected = true,
        };

        private static (IpConfigViewModel vm, FakeIpConfigService ipConfig, FakeNetworkService network) CreateSut(
            List<NetworkAdapter>? adapters = null)
        {
            var network = new FakeNetworkService { AdaptersToReturn = adapters ?? new List<NetworkAdapter> { MakeAdapter() } };
            var ipConfig = new FakeIpConfigService();
            var vm = new IpConfigViewModel(ipConfig, network);
            return (vm, ipConfig, network);
        }

        [Fact]
        public void Constructor_LoadsAdaptersFromNetworkService()
        {
            var (vm, _, _) = CreateSut();

            var adapter = Assert.Single(vm.NetworkAdapters);
            Assert.Equal("Ethernet", adapter.Name);
            Assert.Equal("Adapters refreshed", vm.StatusMessage);
        }

        [Fact]
        public void SelectingAdapter_LoadsItsCurrentConfig()
        {
            var (vm, _, _) = CreateSut();

            vm.SelectedAdapter = vm.NetworkAdapters[0];

            Assert.True(vm.IsDhcp);
            Assert.Equal("192.168.1.50", vm.IpAddress);
            Assert.Equal("255.255.255.0", vm.SubnetMask);
            Assert.Equal("192.168.1.1", vm.Gateway);
            Assert.Contains("Loaded config", vm.StatusMessage);
        }

        [Fact]
        public async Task ApplyStaticIpCommand_CallsServiceWithFormValues()
        {
            var (vm, ipConfig, _) = CreateSut();
            vm.SelectedAdapter = vm.NetworkAdapters[0];
            vm.IsDhcp = false;
            vm.IpAddress = "10.0.0.5";
            vm.SubnetMask = "255.255.255.0";
            vm.Gateway = "10.0.0.1";

            await vm.ApplyStaticIpCommand.ExecuteAsync(null);

            Assert.Equal(("Ethernet", "10.0.0.5", "255.255.255.0", "10.0.0.1"), ipConfig.LastStaticCall);
            Assert.Equal("Static IP sat", vm.StatusMessage);
            Assert.False(vm.IsConfiguring);
        }

        [Fact]
        public void ApplyStaticIpCommand_CanExecute_FalseWhenDhcpOrNoAdapterSelected()
        {
            var (vm, _, _) = CreateSut();

            // Ingen adapter valgt endnu.
            Assert.False(vm.ApplyStaticIpCommand.CanExecute(null));

            vm.SelectedAdapter = vm.NetworkAdapters[0]; // IsDhcp er true fra adapteren
            Assert.False(vm.ApplyStaticIpCommand.CanExecute(null));

            vm.IsDhcp = false;
            Assert.True(vm.ApplyStaticIpCommand.CanExecute(null));
        }

        [Fact]
        public async Task ApplyDhcpCommand_ReportsResultMessage()
        {
            var (vm, ipConfig, _) = CreateSut();
            vm.SelectedAdapter = vm.NetworkAdapters[0];
            ipConfig.DhcpResult = IpConfigResult.Fail("Adapter optaget");

            await vm.ApplyDhcpCommand.ExecuteAsync(null);

            Assert.Equal("Adapter optaget", vm.StatusMessage);
            Assert.False(vm.IsConfiguring);
        }

        [Fact]
        public async Task FlushDnsCommand_ReportsResultMessage()
        {
            var (vm, _, _) = CreateSut();

            await vm.FlushDnsCommand.ExecuteAsync(null);

            Assert.Equal("DNS-cache ryddet", vm.StatusMessage);
        }
    }
}

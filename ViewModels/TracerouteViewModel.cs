using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class TracerouteViewModel : ObservableObject
    {
        private readonly ITracerouteService _tracerouteService;
        private CancellationTokenSource? _traceCts;

        private ObservableCollection<TraceHopInfo> _hops = new();
        private string _targetInput = "8.8.8.8";
        private bool _isTracing;
        private bool _isProbing;
        private bool _isTraceComplete;
        private string _statusMessage = "Ready to trace";
        private int _traceProgress;
        private double _maxLatency = 100;

        public ObservableCollection<TraceHopInfo> Hops
        {
            get => _hops;
            set => SetProperty(ref _hops, value);
        }

        public string TargetInput
        {
            get => _targetInput;
            set => SetProperty(ref _targetInput, value);
        }

        public bool IsTracing
        {
            get => _isTracing;
            set => SetProperty(ref _isTracing, value);
        }

        public bool IsTraceComplete
        {
            get => _isTraceComplete;
            set => SetProperty(ref _isTraceComplete, value);
        }

        public bool IsProbing
        {
            get => _isProbing;
            set => SetProperty(ref _isProbing, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public int TraceProgress
        {
            get => _traceProgress;
            set => SetProperty(ref _traceProgress, value);
        }

        public double MaxLatency
        {
            get => _maxLatency;
            set => SetProperty(ref _maxLatency, value);
        }

        public ICommand TraceCommand { get; }
        public ICommand ProbeLoopCommand { get; }
        public ICommand CancelTraceCommand { get; }

        public TracerouteViewModel(ITracerouteService tracerouteService)
        {
            _tracerouteService = tracerouteService;

            TraceCommand = new RelayCommand(_ => _ = ExecuteTraceAsync(), _ => !IsTracing && !IsProbing);
            ProbeLoopCommand = new RelayCommand(_ => _ = ToggleProbingAsync(), _ => IsTraceComplete || IsProbing);
            CancelTraceCommand = new RelayCommand(_ => CancelTrace(), _ => IsTracing || IsProbing);
        }

        private async Task ExecuteTraceAsync()
        {
            if (string.IsNullOrWhiteSpace(TargetInput))
            {
                StatusMessage = "Angiv værtsnavn eller IP-adresse";
                return;
            }

            IsTracing = true;
            IsTraceComplete = false;
            TraceProgress = 0;
            Hops.Clear();
            StatusMessage = "Sporer rute...";

            _traceCts?.Dispose();
            _traceCts = new CancellationTokenSource();

            try
            {
                await foreach (var hop in _tracerouteService.TraceRouteAsync(
                    TargetInput,
                    maxHops: 32,
                    timeoutMs: 600,
                    ct: _traceCts.Token))
                {
                    if (_traceCts.Token.IsCancellationRequested)
                        break;

                    // Marshal UI updates to dispatcher thread — Hops.Add must run on UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Hops.Add(hop);
                        TraceProgress = (int)(100.0 * Hops.Count / 32);
                        UpdateMaxLatency();
                    });
                }

                StatusMessage = Hops.Count > 0
                    ? $"Sporing fuldført — {Hops.Count} hop'er"
                    : "Sporing fuldført — ingen svar";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Sporing annulleret";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fejl: {ex.Message}";
            }
            finally
            {
                IsTracing = false;
                IsTraceComplete = Hops.Count > 0;
                _traceCts?.Dispose();
                _traceCts = null;
            }
        }

        private void UpdateMaxLatency()
        {
            if (Hops.Count == 0)
            {
                MaxLatency = 100;
                return;
            }

            // Only recalc if the last hop might exceed current max
            double currentMax = MaxLatency / 1.2; // Back out the 20% padding
            double lastHopAvg = Hops[Hops.Count - 1].LatencySeries.Avg;

            // If last hop is lower, max hasn't changed
            if (lastHopAvg <= currentMax && MaxLatency >= 100)
                return;

            // Recalc max across all hops
            double max = 0;
            foreach (var hop in Hops)
            {
                if (hop.LatencySeries.Avg > max)
                    max = hop.LatencySeries.Avg;
            }

            // Add 20% padding
            MaxLatency = Math.Max(100, max * 1.2);
        }

        private async Task ToggleProbingAsync()
        {
            if (IsProbing)
            {
                CancelTrace();
                return;
            }

            if (Hops.Count == 0)
                return;

            IsProbing = true;
            StatusMessage = "🔄 Løbende probe kører...";
            _traceCts?.Dispose();
            _traceCts = new CancellationTokenSource();

            try
            {
                var hopsList = new System.Collections.Generic.List<TraceHopInfo>(Hops);

                await foreach (var hop in _tracerouteService.ContinuousProbeAsync(
                    hopsList,
                    timeoutMs: 600,
                    delayBetweenHopsMs: 2000,
                    ct: _traceCts.Token))
                {
                    if (_traceCts.Token.IsCancellationRequested)
                        break;

                    // Marshal MaxLatency update to UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() => UpdateMaxLatency());
                }

                StatusMessage = $"Probe stoppet";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Probe stoppet";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fejl ved probe: {ex.Message}";
            }
            finally
            {
                IsProbing = false;
                _traceCts?.Dispose();
                _traceCts = null;
            }
        }

        private void CancelTrace()
        {
            _traceCts?.Cancel();
        }
    }
}

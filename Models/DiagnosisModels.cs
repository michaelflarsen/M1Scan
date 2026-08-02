using System;
using System.Collections.Generic;
using M1Scan.Utils;

namespace M1Scan.Models
{
    public enum DiagnosisStepStatus { Pending, Running, Ok, Warning, Failed, Skipped }

    /// <summary>Ét trin i "Diagnosticér nu"-beslutningstræet (fx "Gateway svarer?").
    /// Samme instans muteres flere gange (Running → Ok/Failed) mens wizarden kører,
    /// så UI'et kan vise live fremdrift — derfor observable.</summary>
    public class DiagnosisStep : ObservableObject
    {
        private DiagnosisStepStatus _status = DiagnosisStepStatus.Pending;
        private string _detail = string.Empty;

        public string Name { get; init; } = string.Empty;

        public DiagnosisStepStatus Status
        {
            get => _status;
            set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusIcon)); }
        }

        public string Detail
        {
            get => _detail;
            set => SetProperty(ref _detail, value);
        }

        public string StatusIcon => Status switch
        {
            DiagnosisStepStatus.Running => "⏳",
            DiagnosisStepStatus.Ok      => "✓",
            DiagnosisStepStatus.Warning => "⚠",
            DiagnosisStepStatus.Failed  => "✗",
            DiagnosisStepStatus.Skipped => "—",
            _                           => "·"
        };
    }

    /// <summary>Slutresultat af en diagnosekørsel: alle trin plus den udledte konklusion.</summary>
    public class DiagnosisResult
    {
        public IReadOnlyList<DiagnosisStep> Steps { get; init; } = Array.Empty<DiagnosisStep>();
        public string Conclusion { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;
        public string CopyableReport { get; init; } = string.Empty;
    }
}

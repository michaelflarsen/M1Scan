using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class MacAliasEntry : ObservableObject
    {
        private string _macPrefix = string.Empty;
        private string _description = string.Empty;
        private string _originalVendor = string.Empty;

        public string MacPrefix
        {
            get => _macPrefix;
            set => SetProperty(ref _macPrefix, value);
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        public string OriginalVendor
        {
            get => _originalVendor;
            set => SetProperty(ref _originalVendor, value);
        }
    }

    public class MacAliasViewModel : ObservableObject, IDisposable
    {
        private readonly IMacAliasService _macAliasService;
        private ObservableCollection<MacAliasEntry> _aliases = new();
        private string _macInput = string.Empty;
        private string _descriptionInput = string.Empty;
        private string _statusMessage = "Ready";
        private bool _isLoading;

        public ObservableCollection<MacAliasEntry> Aliases
        {
            get => _aliases;
            set => SetProperty(ref _aliases, value);
        }

        public string MacInput
        {
            get => _macInput;
            set => SetProperty(ref _macInput, value);
        }

        public string DescriptionInput
        {
            get => _descriptionInput;
            set => SetProperty(ref _descriptionInput, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public AsyncRelayCommand AddCommand { get; }
        public AsyncRelayCommand RemoveCommand { get; }
        public AsyncRelayCommand LookupCommand { get; }
        public AsyncRelayCommand RefreshCommand { get; }

        public MacAliasViewModel(IMacAliasService macAliasService)
        {
            _macAliasService = macAliasService;

            AddCommand = new AsyncRelayCommand(_ => AddAliasAsync(), _ => !string.IsNullOrWhiteSpace(MacInput), OnCommandError);
            RemoveCommand = new AsyncRelayCommand(param => RemoveAliasAsync(param as MacAliasEntry), onError: OnCommandError);
            LookupCommand = new AsyncRelayCommand(param => LookupAliasAsync(param as MacAliasEntry), onError: OnCommandError);
            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), onError: OnCommandError);

            RefreshCommand.Execute(null);
        }

        private void OnCommandError(Exception ex) => StatusMessage = $"Fejl: {ex.Message}";

        private async Task RefreshAsync()
        {
            IsLoading = true;
            try
            {
                await _macAliasService.LoadAsync();
                Aliases.Clear();
                var all = _macAliasService.GetAll();
                foreach (var kvp in all)
                {
                    var entry = new MacAliasEntry
                    {
                        MacPrefix = kvp.Key,
                        Description = kvp.Value,
                        OriginalVendor = OuiLookup.LookupOuiOnly(kvp.Key)
                    };
                    Aliases.Add(entry);
                }
                StatusMessage = $"Loaded {Aliases.Count} MAC aliases";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task AddAliasAsync()
        {
            try
            {
                var normalized = MacInput.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
                if (normalized.Length != 6 && normalized.Length != 12)
                {
                    StatusMessage = "MAC must be 6 or 12 hex characters";
                    return;
                }

                // Opslag før tilføjelse (kun original OUI, ikke aliaser)
                var originalVendor = OuiLookup.LookupOuiOnly(normalized);

                await _macAliasService.AddOrUpdateAsync(normalized, DescriptionInput);

                var entry = new MacAliasEntry
                {
                    MacPrefix = normalized,
                    Description = DescriptionInput,
                    OriginalVendor = originalVendor ?? string.Empty
                };
                Aliases.Add(entry);

                MacInput = string.Empty;
                DescriptionInput = string.Empty;
                StatusMessage = "Alias added successfully";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private async Task RemoveAliasAsync(MacAliasEntry? entry)
        {
            if (entry == null) return;
            try
            {
                await _macAliasService.RemoveAsync(entry.MacPrefix);
                Aliases.Remove(entry);
                StatusMessage = "Alias removed";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private async Task LookupAliasAsync(MacAliasEntry? entry)
        {
            if (entry == null) return;
            try
            {
                var originalVendor = OuiLookup.LookupOuiOnly(entry.MacPrefix);
                entry.OriginalVendor = originalVendor ?? string.Empty;
                StatusMessage = $"Lookup: OUI={entry.OriginalVendor}, Custom={entry.Description}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        public void Dispose() { }
    }
}

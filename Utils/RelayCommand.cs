using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace M1Scan.Utils
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter)
        {
            try
            {
                _execute(parameter);
            }
            catch (Exception ex)
            {
                // ICommand.Execute kaldes af WPF's command-plumbing; en undtagelse her
                // bobler op som en uhåndteret Dispatcher-fejl. Log den og lad
                // App's DispatcherUnhandledException vise beskeden.
                CrashLog.Write("RelayCommand", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Async-kommando der IKKE er async void. En <c>new RelayCommand(async _ =&gt; ...)</c>
    /// bliver til async void: enhver undtagelse der undslipper lambdaen postes til
    /// SynchronizationContext og dræber processen. Her await'es opgaven internt,
    /// fejl fanges og eksponeres via <see cref="Error"/> + crash-loggen, og
    /// kommandoen er samtidig re-entrancy-sikret (ingen dobbeltklik-scanninger).
    /// </summary>
    public class AsyncRelayCommand : ICommand, INotifyPropertyChanged
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Func<object?, bool>? _canExecute;
        private readonly Action<Exception>? _onError;
        private bool _isRunning;

        public AsyncRelayCommand(
            Func<object?, Task> executeAsync,
            Func<object?, bool>? canExecute = null,
            Action<Exception>? onError = null)
        {
            _executeAsync = executeAsync;
            _canExecute = canExecute;
            _onError = onError;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        /// <summary>True mens kommandoen kører. CanExecute er false i mellemtiden.</summary>
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value) return;
                _isRunning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
                // Tving WPF til at re-evaluere CanExecute nu, ikke ved næste input-event.
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Seneste fejl, eller null hvis sidste kørsel lykkedes.</summary>
        public Exception? Error { get; private set; }

        public bool CanExecute(object? parameter)
            => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

        public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

        /// <summary>Kør kommandoen og await resultatet — brugbar fra tests og fra kode
        /// der skal vide hvornår handlingen er færdig.</summary>
        public async Task ExecuteAsync(object? parameter)
        {
            if (_isRunning) return;

            IsRunning = true;
            Error = null;
            try
            {
                await _executeAsync(parameter);
            }
            catch (OperationCanceledException)
            {
                // Annullering er en normal afslutning, ikke en fejl.
            }
            catch (Exception ex)
            {
                Error = ex;
                CrashLog.Write($"AsyncRelayCommand({_executeAsync.Method.Name})", ex);
                _onError?.Invoke(ex);
            }
            finally
            {
                IsRunning = false;
            }
        }
    }

    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}

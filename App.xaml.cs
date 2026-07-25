using System;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using M1Scan.Utils;

namespace M1Scan
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Registreres FØR alt andet: en fejl i opstarten (fx MacAliasService der
            // ikke kan oprette %APPDATA%\M1Scan) skal give en besked og en log — ikke
            // en proces der bare forsvinder.
            HookGlobalExceptionHandlers();

            if (!IsRunningAsAdmin())
            {
                MessageBox.Show(
                    "M1Scan requires Administrator privileges to manage network adapters and IP configuration.\n\n" +
                    "Please restart the application with administrator rights.",
                    "Administrator Privileges Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            base.OnStartup(e);
        }

        private void HookGlobalExceptionHandlers()
        {
            // UI-tråden: alt der bobler op gennem Dispatcher'en, inkl. de async void-
            // lambdaer i DispatcherTimer.Tick og i event-handlere.
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // Baggrundstråde: kan ikke redde processen, men skal efterlade et spor.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                CrashLog.Write("AppDomain", args.ExceptionObject as Exception);

            // Task-fejl ingen await'ede. Uden SetObserved() river disse processen
            // ned ved næste GC på ældre runtime-konfigurationer.
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                CrashLog.Write("UnobservedTask", args.Exception);
                args.SetObserved();
            };
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            CrashLog.Write("Dispatcher", e.Exception);

            // Håndtér fejlen så appen bliver i live: et mislykket scan eller
            // IP-skift må ikke koste brugeren hele sessionen. Vises der ikke noget,
            // ser en tabt handling ud som om intet skete.
            e.Handled = true;

            MessageBox.Show(
                $"Der opstod en uventet fejl:\n\n{e.Exception.Message}\n\n" +
                $"Handlingen blev afbrudt, men M1Scan kører videre.\n" +
                $"Detaljer er gemt i:\n{CrashLog.LogPath}",
                "Uventet fejl",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                if (identity == null)
                    return false;

                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}

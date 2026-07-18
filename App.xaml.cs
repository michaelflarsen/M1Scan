using System.Security.Principal;
using System.Windows;

namespace M1Scan
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
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

        private static bool IsRunningAsAdmin()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

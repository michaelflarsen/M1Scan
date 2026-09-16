using System;
using System.Media;
using M1Scan.Utils;

namespace M1Scan.Services
{
    public class NotificationSoundService : INotificationSoundService
    {
        public bool Enabled { get; set; } = true;

        public void PlayOnlineRestored()
        {
            if (!Enabled) return;

            try
            {
                // SystemSounds.Play() afspiller asynkront på en separat tråd og
                // blokerer ikke UI-tråden. Bruger Windows' egen "Asterisk"-lyd i
                // stedet for en indlejret lydfil, så den altid matcher brugerens
                // eget lydtema og kræver ingen ekstra ressourcer i appen.
                SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                // Lyd er deaktiveret i OS'et, ingen lydenhed, e.l. — skal aldrig
                // vælte appen for en rent kosmetisk notifikation.
                CrashLog.Write("NotificationSoundService", ex);
            }
        }
    }
}

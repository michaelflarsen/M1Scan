namespace M1Scan.Services
{
    /// <summary>
    /// Afspiller korte Windows-systemlyde til at gøre brugeren opmærksom på
    /// netværkshændelser uden at kræve at dashboardet er synligt.
    /// </summary>
    public interface INotificationSoundService
    {
        /// <summary>Om lyd overhovedet skal afspilles — styret af brugerens indstilling.</summary>
        bool Enabled { get; set; }

        /// <summary>Afspilles når forbindelsen går fra offline til online igen.</summary>
        void PlayOnlineRestored();
    }
}

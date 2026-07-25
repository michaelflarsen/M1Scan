namespace M1Scan.ViewModels
{
    /// <summary>
    /// En side-ViewModel der har baggrundsarbejde (timere, løbende ping) som kun bør
    /// køre mens siden faktisk er synlig.
    ///
    /// Alle sider konstrueres i MainViewModel's constructor og lever hele appens
    /// levetid, fordi navigationen blot slår Visibility til/fra på panelerne i
    /// MainWindow. Startede en side sine timere i sin constructor, kørte de derfor fra
    /// opstart og for evigt — WorkspaceViewModel pingede fx hele sin watchlist hvert
    /// 3. sekund, selvom brugeren måske aldrig åbnede siden. Det er usynlig
    /// netværkstrafik brugeren ikke har bedt om, i et værktøj hvis brugere netop går op
    /// i netværksstøj.
    ///
    /// MainWindow.UpdatePageVisibility er det ene sted der ved hvilken side der vises,
    /// så aktiveringen styres derfra.
    /// </summary>
    public interface IActivatablePage
    {
        /// <summary>Siden er blevet synlig — start timere og løbende målinger.</summary>
        void OnActivated();

        /// <summary>Siden er skjult — stop alt baggrundsarbejde.</summary>
        void OnDeactivated();
    }
}

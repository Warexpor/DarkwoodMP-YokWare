namespace DWMPHorde
{
    /// <summary>
    /// Title screen or pause menu. Vanilla's in-game pause menu (Esc) is the title <c>MainMenu</c>
    /// opened over the running chapter: it sets <c>Core.mainMenu</c> while the world stays loaded
    /// (<c>Core.coreStarted</c> stays true; quitting to the title clears it). A check that means
    /// "not in a chapter" must use <see cref="AtTitle"/>; <c>Core.mainMenu</c> alone also matches a
    /// player who only paused, and in co-op the world keeps running behind that menu.
    /// </summary>
    internal static class GameScreen
    {
        /// <summary>The in-game pause menu is open over a loaded chapter.</summary>
        internal static bool InPauseMenu =>
            Core.mainMenu && Core.coreStarted && !Core.loadingGame && Player.Instance != null;

        /// <summary>On the title screen (no chapter loaded), as opposed to the pause menu.</summary>
        internal static bool AtTitle => Core.mainMenu && !InPauseMenu;
    }
}

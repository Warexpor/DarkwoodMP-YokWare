using DWMPHorde.Networking;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Vanilla pauses the game while the player levels up at the oven or talks (trading
    /// included): nothing can reach the player in a menu. Co-op keeps the world running for
    /// the others (NoWorldPausePatch), so a player in one of those menus was chased and hit
    /// while unable to act. While this player is in one, creatures ignore it (vanilla
    /// <c>ignoreMe</c>, which reaches the host through PlayerEffectSync) and nothing hurts it
    /// (<c>invulnerable</c>, which every player hit and damage-over-time step checks).
    /// </summary>
    internal static class MenuShield
    {
        private static bool _on;               // reset-in: Reset
        private static bool _savedIgnoreMe;    // reset-in: Reset
        private static bool _savedInvulnerable; // reset-in: Reset

        internal static bool Active => _on;

        internal static void Tick(LanNetworkManager net)
        {
            Player p = Player.Instance;
            bool want = net != null && net.IsConnected && p != null && p.alive && !p.dying && InMenu(p);
            if (want == _on)
                return;
            if (want)
            {
                _savedIgnoreMe = p.ignoreMe;
                _savedInvulnerable = p.invulnerable;
                p.ignoreMe = true;
                p.invulnerable = true;
                _on = true;
                ModRuntime.LegacyInfo("[MenuShield] on (level-up / dialogue)");
                return;
            }
            Release(p);
        }

        private static bool InMenu(Player p)
        {
            if (p.inLevelingMenu)
                return true;
            // The host applying a peer's dialogue opens the window for a moment without
            // the host talking.
            if (DialogHostApplyGuard.DialogueApplyActive)
                return false;
            var ui = Singleton<UI>.Instance;
            bool windowOpen = ui != null && ui.dialogueWindow != null && ui.dialogueWindow.opened;
            return (p.inDialogue || p.inShop) && windowOpen;
        }

        private static void Release(Player p)
        {
            if (!_on)
                return;
            _on = false;
            if (p != null)
            {
                p.ignoreMe = _savedIgnoreMe;
                p.invulnerable = _savedInvulnerable;
            }
            ModRuntime.LegacyInfo("[MenuShield] off");
        }

        /// <summary>Session end: hand the player its own flags back.</summary>
        internal static void Reset()
        {
            Release(Player.Instance);
            _savedIgnoreMe = false;
            _savedInvulnerable = false;
        }
    }
}

using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Player-visible wrong-save / campaign-mismatch warnings.
    /// String formatting lives on <see cref="WorldSharePolicy"/> (unit-tested);
    /// this wrapper surfaces HUD / join ProgressText.
    /// </summary>
    internal static class WrongSaveWarning
    {
        internal static string Format(string reason)
            => WorldSharePolicy.FormatWrongSave(reason);

        internal static bool IsWrongSaveMessage(string text)
            => WorldSharePolicy.IsWrongSaveMessage(text);

        /// <summary>
        /// In-world HUD tip when a backup restore is refused, or menu ProgressText
        /// when a join/share path can surface it.
        /// </summary>
        internal static void Notify(string reason, bool alsoSetShareProgress = false)
        {
            string msg = Format(reason);
            ModLog.Warn(LogCat.Save, msg);

            try
            {
                if (alsoSetShareProgress)
                {
                    var net = ModRuntime.Network;
                    if (net?.WorldSaveShare != null)
                        net.WorldSaveShare.SetWrongSaveProgress(msg);
                }
            }
            catch { /* non-fatal */ }

            try
            {
                if (Player.Instance != null && !GameScreen.AtTitle && !Core.loadingGame)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage(Loc.T(msg)); }
                    finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
                }
            }
            catch { /* non-fatal */ }
        }
    }
}

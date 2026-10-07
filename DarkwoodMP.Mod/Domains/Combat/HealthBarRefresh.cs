using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// The enemy health bar (vanilla <c>UI.enemyHealthBar</c>) reads the object's health once, when
    /// shown or refreshed. Vanilla refreshes it inside <c>getHit</c>, after the damage. A client's
    /// hit lands on the host instead, so the client shows the bar from its own swing with the
    /// health before the hit; the host's numbers arrive afterwards and are written straight into
    /// the fields. Without a refresh there the bar kept the pre-hit value: a dog the client just
    /// killed showed a full bar, a door the client just broke showed it nearly broken.
    /// </summary>
    internal static class HealthBarRefresh
    {
        /// <summary>Refresh the bar if it is showing this object (any health a peer just applied).</summary>
        internal static void IfShowing(GameObject go)
        {
            if (go == null)
                return;
            var ui = Singleton<UI>.Instance;
            EnemyHealthBar bar = ui != null ? ui.enemyHealthBar : null;
            if (bar == null || bar.currentObj != go)
                return;
            try
            {
                bar.show(go, onlyRefresh: true);
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[HealthBar] refresh: " + ex.Message);
            }
        }
    }
}

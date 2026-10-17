using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Hovering another player shows their health in the vanilla enemy health bar
    /// (<c>UI.enemyHealthBar</c>), the way hovering a creature, door or barricade does. The
    /// stand-in is not a vanilla <c>Character</c> or <c>Selectable</c>, so vanilla's
    /// <c>selectObject</c> never shows a bar for it; this runs after it, only when vanilla
    /// selected nothing, under the same gates (<c>canSeeAtCursor</c> and <c>withinReach</c>).
    /// The fill is the peer's own health (<c>RemoteHealthPct</c>, PlayerEffectSync), not the
    /// stand-in's CharBase pool. A downed or dead player shows no bar, like a dead creature.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.selectObject))]
    public static class PeerHealthBarHoverPatch
    {
        private static readonly AccessTools.FieldRef<EnemyHealthBar, float> TimeShown =
            AccessTools.FieldRefAccess<EnemyHealthBar, float>("timeShown");
        private static readonly AccessTools.FieldRef<EnemyHealthBar, bool> WantToHide =
            AccessTools.FieldRefAccess<EnemyHealthBar, bool>("wantToHide");
        private static readonly AccessTools.FieldRef<EnemyHealthBar, bool> Hiding =
            AccessTools.FieldRefAccess<EnemyHealthBar, bool>("hiding");

        private static readonly RaycastHit[] Hits = new RaycastHit[32];
        /// <summary>Vanilla's selection mask (<c>Player.selectableLayerMask</c>), Characters included.</summary>
        private const int SelectableMask = 44165633;

        private static void Postfix(Player __instance)
        {
            if (__instance == null || __instance != Player.Instance || ModRuntime.Network == null)
                return;
            var ui = Singleton<UI>.Instance;
            EnemyHealthBar bar = ui != null ? ui.enemyHealthBar : null;
            if (bar == null)
                return;

            RemotePlayerProxy hovered = null;
            if (__instance.selectedObject == null && !Core.forbidInputs && !__instance.inEpilogue)
                hovered = HoveredProxy(__instance);

            if (hovered != null)
            {
                Show(bar, hovered, onlyRefresh: false);
                return;
            }
            // Vanilla's deselect starts the fade; this covers hovering off onto something
            // vanilla keeps selected, and a peer who went down under the cursor.
            GameObject shown = bar.currentObj;
            if (shown != null && shown.GetComponent<RemotePlayerProxy>() != null)
                bar.startHiding();
        }

        private static RemotePlayerProxy HoveredProxy(Player local)
        {
            CamMain cam = Singleton<CamMain>.Instance;
            Camera camera = cam != null ? cam.GetComponent<Camera>() : null;
            if (camera == null)
                return null;
            int n = Physics.RaycastNonAlloc(camera.ScreenPointToRay(Core.cursorPos()), Hits, 600f, SelectableMask);
            for (int i = 0; i < n; i++)
            {
                Collider col = Hits[i].collider;
                RemotePlayerProxy proxy = col != null ? col.GetComponentInParent<RemotePlayerProxy>() : null;
                if (proxy == null || !Alive(proxy))
                    continue;
                if (local.canSeeAtCursor(Vector3.zero, proxy.transform) && local.withinReach())
                    return proxy;
            }
            return null;
        }

        private static bool Alive(RemotePlayerProxy proxy)
        {
            if (!proxy.isActiveAndEnabled || proxy.RemoteHealthPct == 0)
                return false;
            CharBase cb = proxy.CachedCharBase;
            return cb == null || cb.alive;
        }

        /// <summary>The peer's health just changed: refresh the bar if it is showing them.</summary>
        internal static void RefreshIfShowing(RemotePlayerProxy proxy)
        {
            var ui = Singleton<UI>.Instance;
            EnemyHealthBar bar = ui != null ? ui.enemyHealthBar : null;
            if (bar == null || proxy == null || bar.currentObj != proxy.gameObject)
                return;
            if (Alive(proxy))
                Show(bar, proxy, onlyRefresh: true);
            else
                bar.hide();
        }

        /// <summary>Vanilla <c>EnemyHealthBar.show(Character)</c> with the peer's health percent.</summary>
        private static void Show(EnemyHealthBar bar, RemotePlayerProxy proxy, bool onlyRefresh)
        {
            if (Player.Instance != null && Player.Instance.inEpilogue)
                return;
            bar.currentObj = proxy.gameObject;
            if (!onlyRefresh)
            {
                Hiding(bar) = false;
                WantToHide(bar) = false;
                TimeShown(bar) = Time.time;
            }
            bar.full.SetSprite("healthBar_enemy_full");
            bar.empty.SetSprite("healthBar_enemy_empty");
            bar.full.color = new Color(1f, 0f, 0f, bar.full.color.a);
            bar.full.clipTopRight = new Vector2(Mathf.Clamp01(proxy.RemoteHealthPct / 100f), 1f);
            bar.full.Build();
            if (!onlyRefresh)
                Core.tweenAlphaInChildren(bar.gameObject, 1f, 1f);
        }
    }
}

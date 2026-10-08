using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Keeps vanilla gameplay input quiet while one of our IMGUI overlays has the keyboard
    /// (chat, F2 settings, F3 saves, join slot picker). Vanilla gates movement, hotbar keys,
    /// inventory, attacks and the controller on <c>Core.forbidInputs</c> (Player.ProcessMovement,
    /// InputScript.handleHotbar, InventoryController), so the lock drives that flag rather than
    /// fighting Rewired. The world is never paused: only local input is held.
    ///
    /// Owners call <see cref="Set"/> every frame from their Update with their own visibility.
    /// While held the flag is re-asserted before and after the vanilla Update pass (dialogue /
    /// controller code clears <c>Core.forbidInputs</c> on its own schedule). On release the flag
    /// is cleared only when this lock raised it and nothing in vanilla wants it held right now.
    /// Overlays do not open at all while vanilla already forbids input (dialogue, cutscene, dream
    /// entry, loading): see <see cref="CanOpenOverlay"/>.
    /// </summary>
    public static class UiInputLock
    {
        private static readonly HashSet<string> Owners = new HashSet<string>(); // process-scoped: overlay owners re-Set every frame; ModRuntime.Stop calls ReleaseAll
        private static bool _forced; // process-scoped: overlay input lock
        private static int _releasedFrame = -1; // process-scoped: overlay input lock
        private static GameObject _driverGo; // process-scoped: DontDestroyOnLoad driver

        /// <summary>True while any overlay holds the lock.</summary>
        public static bool IsHeld => Owners.Count > 0;

        /// <summary>
        /// True while held, and on the frame it was released. Esc that closes an overlay would
        /// otherwise also reach <c>InputScript.onPressEsc</c> and open the pause menu / close the
        /// inventory underneath it.
        /// </summary>
        internal static bool SwallowEsc => Owners.Count > 0 || _releasedFrame == Time.frameCount;

        /// <summary>
        /// An overlay may open now: either one is already open, or vanilla is not holding input
        /// itself. Opening chat over a cutscene or a dream fade would otherwise release vanilla's
        /// own lock when the overlay closed.
        /// </summary>
        public static bool CanOpenOverlay
        {
            get
            {
                if (IsHeld)
                    return true;
                try { return !Core.forbidInputs && !VanillaWantsInputsForbidden(); }
                catch { return true; }
            }
        }

        public static void Set(string owner, bool locked)
        {
            if (locked)
                Acquire(owner);
            else
                Release(owner);
        }

        private static void Acquire(string owner)
        {
            if (Owners.Add(owner) && Owners.Count == 1)
            {
                EnsureDriver();
                try { _forced = !Core.forbidInputs; }
                catch { _forced = false; }
            }
            Assert();
        }

        private static void Release(string owner)
        {
            if (!Owners.Remove(owner) || Owners.Count > 0)
                return;

            _releasedFrame = Time.frameCount;
            // Vanilla may have raised the flag for its own reasons while we held it (a cutscene or
            // dream fade started under the overlay); then it is vanilla's to clear, not ours.
            if (_forced && !VanillaWantsInputsForbidden())
            {
                try { Core.forbidInputs = false; }
                catch { /* Core not ready (scene teardown) */ }
            }
            _forced = false;
        }

        /// <summary>Drop every holder (session teardown / scene reload safety).</summary>
        public static void ReleaseAll()
        {
            if (Owners.Count == 0)
                return;
            var copy = new List<string>(Owners);
            for (int i = 0; i < copy.Count; i++)
                Release(copy[i]);
        }

        /// <summary>States in which vanilla itself holds <c>Core.forbidInputs</c> (and expects to keep it).</summary>
        private static bool VanillaWantsInputsForbidden()
        {
            try
            {
                if (Core.loadingGame)
                    return true;
                Player p = Player.Instance;
                if (p != null && p.inDialogue)
                    return true;
                Controller ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && ctrl.playingCutscene)
                    return true;
                Dreams dreams = Singleton<Dreams>.Instance;
                if (dreams != null && (dreams.switchingDream || dreams.wantToDream))
                    return true;
            }
            catch { /* Core not ready */ }
            return false;
        }

        private static void Assert()
        {
            if (Owners.Count == 0)
                return;
            try
            {
                if (!Core.forbidInputs)
                {
                    Core.forbidInputs = true;
                    // Someone cleared it under us: we own the raise again.
                    if (Core.forbidInputs)
                        _forced = true;
                }
            }
            catch { /* Core not ready */ }
        }

        private static void EnsureDriver()
        {
            if (_driverGo != null)
                return;
            _driverGo = new GameObject("YokWare_UiInputLock");
            Object.DontDestroyOnLoad(_driverGo);
            _driverGo.AddComponent<EarlyLockDriver>();
            _driverGo.AddComponent<LateLockDriver>();
        }

        /// <summary>Re-asserts before every vanilla Update, and in LateUpdate.</summary>
        [DefaultExecutionOrder(-10000)]
        private sealed class EarlyLockDriver : MonoBehaviour
        {
            private void Update() => Assert();
            private void LateUpdate() => Assert();
        }

        /// <summary>
        /// Re-asserts after the vanilla Update pass too: <c>Controller.Update</c> clears the flag,
        /// and scripts ordered after it would otherwise see input allowed for the rest of the frame.
        /// </summary>
        [DefaultExecutionOrder(10000)]
        private sealed class LateLockDriver : MonoBehaviour
        {
            private void Update() => Assert();
        }
    }

    /// <summary>Esc that dismissed one of our overlays must not also open the vanilla pause menu.</summary>
    [HarmonyPatch(typeof(InputScript), "onPressEsc")]
    internal static class UiInputLockEscPatch
    {
        private static bool Prefix() => !UiInputLock.SwallowEsc;
    }

    /// <summary>
    /// The Map and Journal keys are read by <c>InputScript</c> whatever <c>Core.forbidInputs</c> says,
    /// so a letter typed into chat or a map pin label opened or closed the map or the journal.
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.tryOpenClose))]
    internal static class UiInputLockMapKeyPatch
    {
        private static bool Prefix() => !UiInputLock.IsHeld;
    }

    [HarmonyPatch(typeof(Journal), nameof(Journal.tryOpenClose))]
    internal static class UiInputLockJournalKeyPatch
    {
        private static bool Prefix() => !UiInputLock.IsHeld;
    }
}

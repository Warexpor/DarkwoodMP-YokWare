using System.Collections;
using DWMPHorde.Spectator;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// One death model for host and clients. A death that vanilla would turn into
    /// permadeath (nightmare, or hard on the last life) runs the normal night/day death
    /// instead, so one peer's death never freezes the shared world behind a game-over
    /// screen the others never see. A night party wipe made only of such deaths ends
    /// the run for everyone together (<see cref="PartyWipeOutcome"/>).
    /// </summary>
    internal static class SharedPermadeathDeath
    {
        /// <summary>True while the rewrite re-enters <c>Player.onDeath</c> by reflection.</summary>
        internal static bool Bypass { get; private set; }

        private static int _bodiesRunning;

        /// <summary>
        /// Bumped by <see cref="Reset"/>. A body only decrements the counter of the world it
        /// started in, so a body that outlives a reset cannot eat a later body's count.
        /// </summary>
        private static int _generation;

        private static bool _sceneHooked; // process-scoped: one-time sceneLoaded hook

        /// <summary>A rewritten death body is still respawning this peer.</summary>
        internal static bool BodyRunning => _bodiesRunning > 0;

        /// <summary>
        /// Unity never runs a coroutine's <c>finally</c> when the scene unloads or the game quits
        /// to the menu, so the counter cannot rely on it: drop every in-flight body when a new
        /// world is loaded or the network session stops.
        /// </summary>
        internal static void Install()
        {
            if (_sceneHooked) return;
            _sceneHooked = true;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                // Additive loads (dream pads) do not kill the player's coroutines.
                if (mode == LoadSceneMode.Single)
                    Reset();
            };
        }

        /// <summary>Forget every in-flight rewritten death body and any pending party-wipe outcome.</summary>
        internal static void Reset()
        {
            ResetBodies();
            PartyWipeOutcome.Reset();
        }

        /// <summary>Forget every in-flight rewritten death body (and put the real difficulty back).</summary>
        internal static void ResetBodies()
        {
            if (_bodiesRunning > 0)
                ModRuntime.LegacyInfo($"[Death] Dropped {_bodiesRunning} in-flight shared death body(ies)");
            _generation++;
            _bodiesRunning = 0;
            Bypass = false;
            ClientPermadeathSaveGuard.Reset();
        }

        /// <summary>Vanilla would end the run for this death (lives before the decrement).</summary>
        internal static bool IsPermadeathEligible(Player player)
        {
            var profile = Core.currentProfile;
            return profile != null && player != null
                && PermadeathPolicy.IsPermadeathDeath((int)profile.difficulty, player.lifes);
        }

        /// <summary>
        /// Returns the death coroutine to use instead of vanilla when this connected
        /// peer's death is permadeath-eligible; null leaves vanilla in place.
        /// </summary>
        internal static IEnumerator TryRewrite(Player player, bool connected)
        {
            var profile = Core.currentProfile;
            if (profile == null || player == null)
                return null;
            if (!PermadeathPolicy.UsesSharedDeath(connected, (int)profile.difficulty, player.lifes))
                return null;

            ModRuntime.LegacyInfo(
                "[Death] Permadeath — shared respawn instead of local game-over");
            _bodiesRunning++;
            return Run(player, _generation);
        }

        /// <summary>
        /// Run vanilla onDeath with difficulty normal so the nightmare / last-life
        /// branch is skipped, then put the profile difficulty back. Save sees the
        /// real difficulty.
        /// </summary>
        private static IEnumerator Run(Player player, int generation)
        {
            var profile = Core.currentProfile;
            var saved = profile.difficulty;
            ClientPermadeathSaveGuard.Arm(saved);
            profile.difficulty = GameProfile.Difficulty.normal;
            Bypass = true;
            IEnumerator inner = null;
            try
            {
                var method = AccessTools.Method(typeof(Player), "onDeath");
                inner = method != null ? method.Invoke(player, null) as IEnumerator : null;
            }
            finally
            {
                Bypass = false;
            }

            try
            {
                if (inner != null)
                {
                    while (inner.MoveNext())
                        yield return inner.Current;
                }
            }
            finally
            {
                // Save Prefix also restores; this covers suppress-Save / throw paths so
                // nightmare cannot stay forced to normal across a later Save.
                ClientPermadeathSaveGuard.RestoreIfArmed();
                if (generation == _generation && _bodiesRunning > 0)
                    _bodiesRunning--;
            }
        }
    }

    /// <summary>
    /// Every peer was night-dead on a permadeath difficulty: run the real vanilla
    /// permadeath screen here once the peer's own shared death has finished. The host
    /// declares it (<see cref="NightDeathStateMessage.PartyWipe"/>) and runs it too;
    /// "start over" is the existing host-coordinated chapter reload.
    /// </summary>
    internal static class PartyWipeOutcome
    {
        /// <summary>Longest wait for this peer's own shared death body before the outcome runs anyway.</summary>
        private const float BodyWaitTimeoutSec = 30f;

        private static bool _running;

        /// <summary>Its coroutine died with the world (scene unload / quit to menu); allow a new one.</summary>
        internal static void Reset()
        {
            _running = false;
            DeathStateTracker.ClearPartyWipeDeclared();
        }

        /// <returns>True when the outcome coroutine was started; false when one is already
        /// running or there is no local player yet. Callers that raised a declaration flag
        /// (<see cref="DeathStateTracker.PartyWipeDeclared"/>) must withdraw it on false.</returns>
        internal static bool Begin(string reason)
        {
            if (_running)
                return false;
            Player player = Player.Instance;
            if (player == null)
                return false;
            _running = true;
            ModRuntime.LegacyInfo($"[Death] Party wipe outcome ({reason})");
            player.StartCoroutine(Run());
            return true;
        }

        private static IEnumerator Run()
        {
            try
            {
                // Let this peer's own shared death (respawn, save) finish first.
                // Bounded: a body whose coroutine was killed without its finally must not
                // hold the party-wipe outcome (and PartyWipeDeclared) forever.
                float waitStart = Time.unscaledTime;
                while (SharedPermadeathDeath.BodyRunning)
                {
                    if (Time.unscaledTime - waitStart > BodyWaitTimeoutSec)
                    {
                        ModRuntime.LegacyInfo(
                            $"[Death] Party wipe outcome: shared death body still flagged running after {BodyWaitTimeoutSec}s — continuing");
                        SharedPermadeathDeath.ResetBodies();
                        break;
                    }
                    yield return null;
                }

                var spec = SpectatorModeController.Instance;
                if (spec != null && spec.IsSpectating)
                    spec.ExitAndRespawn();
                DeathStateTracker.Reset();

                UI ui = Singleton<UI>.Instance;
                if (ui == null)
                    yield break;
                yield return ui.StartCoroutine(ui.PreparePermadeathVideo());
            }
            finally
            {
                _running = false;
                // DeathStateTracker.Reset() above normally clears it; an early exit must too.
                DeathStateTracker.ClearPartyWipeDeclared();
            }
        }
    }
}

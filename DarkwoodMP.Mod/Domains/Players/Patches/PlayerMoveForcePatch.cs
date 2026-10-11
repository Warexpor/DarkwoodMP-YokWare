using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The local player's walk force (vanilla <c>Player.ProcessMovement</c>: input direction times
    /// move speed times 33000, pushed into the player's rigidbody each frame, behind every vanilla
    /// gate). Its one <c>Rigidbody.AddForce</c> call goes through <see cref="Apply"/>, which keeps
    /// the force as this frame's walk intent (what a shared drag and a two-player push send) and
    /// applies it scaled for a shared drag (<see cref="SharedDrag.LocalForceScale"/>): the
    /// dragger's force times the pair's scale, a co-holder's not at all (the dragger's machine
    /// applies it to the co-holder's body there).
    /// </summary>
    [HarmonyPatch(typeof(Player), "ProcessMovement")]
    internal static class PlayerMoveForcePatch
    {
        /// <summary>Vanilla's force factor: force = direction × move speed × this × frame time.</summary>
        internal const float VanillaForceFactor = 33000f;

        private static Vector3 _frameForce; // process-scoped: per-frame scratch, set each ProcessMovement
        private static bool _frameHadForce; // process-scoped: per-frame scratch, set each ProcessMovement
        /// <summary>The player whose movement runs now: only the main local player's counts (a couch co-op second player runs it too).</summary>
        private static bool _mainPlayer; // process-scoped: per-frame scratch, set each ProcessMovement

        /// <summary>
        /// The local player's walk intent this frame: input direction times move speed (zero when
        /// not walking, or when vanilla's gates allow no move). Vanilla's force is this times
        /// <see cref="VanillaForceFactor"/>. Zero once the player's movement stops running
        /// (paused, dead, in a cutscene): the last walking frame does not linger.
        /// </summary>
        internal static Vector3 Intent => Time.frameCount - _intentFrame <= 1 ? _intent : Vector3.zero;

        private static Vector3 _intent; // process-scoped: this frame's walk intent, expires by frame count
        private static int _intentFrame = -10; // process-scoped: frame stamp of _intent

        private static void Prefix(Player __instance)
        {
            _mainPlayer = __instance != null && __instance == Player.Instance;
            _frameForce = Vector3.zero;
            _frameHadForce = false;
        }

        private static void Postfix(Player __instance)
        {
            bool main = _mainPlayer;
            _mainPlayer = false;
            if (!main)
                return;
            float dt = Time.deltaTime;
            _intentFrame = Time.frameCount;
            _intent = _frameHadForce && dt > 0f
                ? _frameForce / (VanillaForceFactor * dt)
                : Vector3.zero;
        }

        /// <summary>Stands in for the vanilla <c>Rigidbody.AddForce(force)</c> call.</summary>
        public static void Apply(Rigidbody rb, Vector3 force)
        {
            if (!_mainPlayer)
            {
                rb.AddForce(force);
                return;
            }
            _frameForce += force;
            _frameHadForce = true;
            float scale = SharedDrag.LocalForceScale();
            if (scale == 1f)
                rb.AddForce(force);
            else if (scale > 0f)
                rb.AddForce(force * scale);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo addForce = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForce),
                new[] { typeof(Vector3) });
            MethodInfo ours = AccessTools.Method(typeof(PlayerMoveForcePatch), nameof(Apply));
            var list = new List<CodeInstruction>(instructions);
            int patched = 0;
            if (addForce != null && ours != null)
            {
                foreach (var ins in list)
                {
                    if (ins.Calls(addForce))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = ours;
                        patched++;
                    }
                }
            }
            if (patched == 0)
                ModLog.Error(LogCat.Physics, "[SharedDrag] Player.ProcessMovement walk force hook not applied");
            return list;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Every explosion sound, on every machine, the same way: panned fully 3D and muffled behind
    /// a wall. Vanilla <c>Explodes.explode</c> plays it at a point with no parent object, and
    /// <c>AudioController</c> only runs its wall check (0.75 volume, 1500 Hz low pass) for a
    /// sound with a parent; the explosion audio object (AO_loud_explosions_3d) is also centred
    /// (2D) out to about 690 units. A barrel going off behind a wall sounded raw and dead centre.
    /// The game's own rolloff (full out to about 780 units, silent at 3000) is kept.
    /// </summary>
    internal static class ExplosionSound
    {
        private const int WallMask = 32769; // AudioController's own wall layers (0 and 15)
        private const float WallVolume = 0.75f;
        private const float WallCutoff = 1500f;

        internal static AudioObject Play(string id, Vector3 pos, Transform parentObj)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            bool walled = Walled(pos, LocalAudioService.GetListenPosition());
            return PeerSpatialPlay.Play(
                () => AudioController.Play(id, pos, parentObj, walled ? WallVolume : 1f),
                src =>
                {
                    src.spatialBlend = 1f;
                    PeerSpatialPlay.SetMuffle(src, walled, WallCutoff);
                });
        }

        internal static AudioObject Play(string id, Vector3 pos) => Play(id, pos, null);

        private static bool Walled(Vector3 from, Vector3 to)
        {
            try
            {
                Vector3 delta = to - from;
                float mag = delta.magnitude;
                if (mag < 1f)
                    return false;
                return Physics.Raycast(from, delta / mag, mag, WallMask, QueryTriggerInteraction.Ignore);
            }
            catch (Exception ex)
            {
                ModLog.Swallowed(ex);
                return false;
            }
        }
    }

    /// <summary>Vanilla's explosion sound goes through <see cref="ExplosionSound"/>.</summary>
    [HarmonyPatch(typeof(Explodes), "explode")]
    public static class ExplosionSoundPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo play = AccessTools.Method(typeof(AudioController), nameof(AudioController.Play),
                new[] { typeof(string), typeof(Vector3), typeof(Transform) });
            MethodInfo ours = AccessTools.Method(typeof(ExplosionSound), nameof(ExplosionSound.Play),
                new[] { typeof(string), typeof(Vector3), typeof(Transform) });
            var list = new List<CodeInstruction>(instructions);
            int patched = 0;
            if (play != null && ours != null)
            {
                foreach (var ins in list)
                {
                    if (ins.Calls(play))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = ours;
                        patched++;
                    }
                }
            }
            if (patched == 0)
                ModLog.Error(LogCat.Audio, "[ExplosionSound] Explodes.explode sound hook not applied");
            return list;
        }
    }
}

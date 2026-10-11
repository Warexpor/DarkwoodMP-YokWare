using System;
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
    /// Vanilla <c>Door.open</c>'s push on the door body goes through <see cref="DoorSwing.OpenForce"/>:
    /// an open echoed from another machine opens without it (the opener's machine runs the swing
    /// and the door here follows), a local open pushes and this machine runs the swing.
    /// </summary>
    [HarmonyPatch(typeof(Door), "open", new[] { typeof(Vector3), typeof(Transform), typeof(float) })]
    internal static class DoorOpenForcePatch
    {
        private static void Prefix(Door __instance, out Door __state)
        {
            __state = DoorSwing.BeginOpen(__instance);
        }

        private static void Finalizer(Door __state)
        {
            DoorSwing.EndOpen(__state);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => DoorForceHook.Replace(instructions, nameof(DoorSwing.OpenForce), "Door.open");
    }

    /// <summary>
    /// Vanilla <c>Door.getHit</c>'s knock-back on an open door (a delegate it runs two frames
    /// later) goes through <see cref="DoorSwing.HitForce"/>: on the host a client's blow leaves it
    /// to the striker's machine, and a blow on a door another machine runs is sent there.
    /// </summary>
    [HarmonyPatch]
    internal static class DoorHitForcePatch
    {
        private static MethodBase _target; // process-scoped: reflection cache

        private static bool Prepare()
        {
            _target = FindKnockBack();
            if (_target == null)
                ModLog.Error(LogCat.Physics, "[DoorSwing] Door.getHit knock-back not found; a blow's swing is not shared");
            return _target != null;
        }

        private static MethodBase TargetMethod() => _target ?? FindKnockBack();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => DoorForceHook.Replace(instructions, nameof(DoorSwing.HitForce), "Door.getHit knock-back");

        /// <summary>The compiler-made method of getHit's delegate that calls <c>Rigidbody.AddForce</c>.</summary>
        private static MethodBase FindKnockBack()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;
            MethodInfo addForce = DoorForceHook.AddForce;
            var candidates = new List<MethodInfo>();
            candidates.AddRange(typeof(Door).GetMethods(all));
            foreach (Type nested in typeof(Door).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                candidates.AddRange(nested.GetMethods(all));
            foreach (MethodInfo m in candidates)
            {
                if (m.Name.IndexOf("<getHit>", StringComparison.Ordinal) < 0)
                    continue;
                try
                {
                    foreach (var ins in PatchProcessor.GetOriginalInstructions(m))
                    {
                        if (ins.Calls(addForce))
                            return m;
                    }
                }
                catch (Exception)
                {
                    // Not readable as IL: not the knock-back.
                }
            }
            return null;
        }
    }

    internal static class DoorForceHook
    {
        internal static readonly MethodInfo AddForce =
            AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForce), new[] { typeof(Vector3) });

        /// <summary>Every <c>Rigidbody.AddForce(Vector3)</c> call becomes a call to <c>DoorSwing.&lt;hook&gt;(rb, force)</c>.</summary>
        internal static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string hook, string where)
        {
            MethodInfo ours = AccessTools.Method(typeof(DoorSwing), hook);
            var list = new List<CodeInstruction>(instructions);
            int patched = 0;
            if (AddForce != null && ours != null)
            {
                foreach (var ins in list)
                {
                    if (ins.Calls(AddForce))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = ours;
                        patched++;
                    }
                }
            }
            if (patched == 0)
                ModLog.Error(LogCat.Physics, "[DoorSwing] " + where + " push hook not applied");
            return list;
        }
    }

    /// <summary>
    /// Vanilla <c>Door.Update</c> plays the hinge creak while the body turns faster than 0.2 rad/s
    /// and fades it out below that; the next turn starts a new one. A door following another
    /// player's swing waits at the last sample when the next is late, so the creak stopped and
    /// restarted at every late sample (15 creaks over one swing on a client in the acid dream
    /// while the swinger heard one). While the followed swing is under way the creak keeps going;
    /// it stops when the swing comes to rest or is given up, as the swinger's does.
    /// </summary>
    [HarmonyPatch(typeof(Door), "Update")]
    internal static class DoorFollowCreakPatch
    {
        private static readonly AccessTools.FieldRef<Door, AudioObject> RotateSound =
            AccessTools.FieldRefAccess<Door, AudioObject>("rotateSound");
        private static readonly AccessTools.FieldRef<Door, Rigidbody> BodyRb =
            AccessTools.FieldRefAccess<Door, Rigidbody>("bodyRB");

        private static bool Prefix(Door __instance)
        {
            Rigidbody rb;
            if (!DoorSwing.IsFollowingSwing(__instance) || (rb = BodyRb(__instance)) == null)
                return true;
            AudioObject creak = RotateSound(__instance);
            // Turning: vanilla sets the creak's volume from the spin. Held for a late sample: keep it.
            return creak == null || Mathf.Abs(rb.angularVelocity.y) > 0.2f;
        }
    }
}

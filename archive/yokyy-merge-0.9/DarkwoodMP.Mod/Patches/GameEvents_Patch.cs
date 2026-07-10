using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Scripted event system sync (v0.4). Design: events FIRE only on the machine
/// that owns them; their observable effects replicate through the existing
/// per-system channels (enemy spawns via espawn, flags, doors, items,
/// barricades, ...). What THIS patch adds:
///
/// - GameEvents.fire postfix: one-shot event batches (fired && !multipleFire
///   never fire again - verified IL) broadcast "gevent:&lt;id&gt;" so the other
///   machine marks its matching GameEvents as fired. Without this the second
///   player would re-trigger the same story beat when walking into the same
///   trigger later. multipleFire events are repeatable by design - not synced.
///
/// - RandomEvent.fire prefix: the scheduled daytime rolls
///   (onUpdateTime -&gt; fire(checkIfRequirementsMet:true, force:false),
///   verified IL) run on the HOST only - clients never roll their own
///   chance-based world events, so "the wolfman visited me but not you"
///   cannot happen. Scripted/night fires (checkIfRequirementsMet:false, e.g.
///   CustomEvent.fire -&gt; theEvent.fire(false, force)) still run everywhere -
///   the client's own night scenario must keep working when players are apart
///   (the actual char spawns are deduped/mirrored by EnemySpawn_Patch).
///
/// EventTriggers itself needs no patch: OnTriggerEnter compares the collider
/// against Player.Instance.gameObject (verified IL) - remote-player clones
/// can never fire area triggers.
/// </summary>
public sealed class GameEvents_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        switch (target.DeclaringType!.Name)
        {
            case "GameEvents":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(GameEvents_Patch).GetMethod(nameof(FirePrefix), statics)!),
                    postfix: new HarmonyMethod(typeof(GameEvents_Patch).GetMethod(nameof(FirePostfix), statics)!));
                break;
            case "CustomEvent":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(GameEvents_Patch).GetMethod(nameof(CustomEventFirePrefix), statics)!));
                break;
            default:
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(GameEvents_Patch).GetMethod(nameof(RandomEventFirePrefix), statics)!));
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("GameEvents", "fire");
        yield return ("RandomEvent", "fire");
        yield return ("CustomEvent", "fire");
    }

    /// <summary>
    /// Night scenario events (v0.5): while a senior player is right here, the
    /// junior machine runs NO night events at all - previously only the char
    /// SPAWNS were deduped and the junior's non-spawn effects (sounds, lights,
    /// modifiers) from a possibly DIFFERENT scenario leaked in cosmetically.
    /// The senior's effects replicate through the synced channels.
    /// </summary>
    public static bool CustomEventFirePrefix()
    {
        return !EnemySpawn_Patch.SeniorPlayerNearby();
    }

    /// <summary>Record whether this call will actually fire (fire() no-ops when already fired).</summary>
    public static void FirePrefix(object __instance, ref bool __state)
    {
        __state = __instance is GameEvents ge && (!ge.fired || ge.multipleFire);
    }

    public static void FirePostfix(object __instance, bool __state)
    {
        try
        {
            if (!__state || RemoteApply.Active) return;
            if (__instance is not GameEvents ge || ge.multipleFire) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var id = GameIds.ForComponent(ge);
            DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<EventStateSync>()
                ?.RecordLocalFired(id);

            network.SendReliable(new GameEventFirePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                EventId = id
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[GameEvents_Patch] {ex.Message}");
        }
    }

    // Parameter name matches RandomEvent.fire(bool checkIfRequirementsMet, bool force)
    public static bool RandomEventFirePrefix(bool checkIfRequirementsMet)
    {
        // Scheduled daytime roll? Authority only (in-game host, or the elected
        // client on a dedicated server). Everything else runs locally.
        if (!checkIfRequirementsMet) return true;
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsConnected || manager.IsTimeAuthority) return true;
        return false;
    }
}

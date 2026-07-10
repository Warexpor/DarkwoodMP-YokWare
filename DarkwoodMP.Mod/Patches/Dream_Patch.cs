using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Dream sync (YokWare Branch Phase 5):
/// - prepareDream: authority broadcasts preset; peers override next dream
/// - startDreaming: session begin, freeze remotes
/// - endDreaming: session teardown
/// playerDeath stays personal (never shared).
/// </summary>
public sealed class Dream_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var name = target.Name;
        if (name == "prepareDream")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Dream_Patch).GetMethod(nameof(PreparePrefix), statics)!));
        }
        else if (name == "startDreaming")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Dream_Patch).GetMethod(nameof(StartDreamingPostfix), statics)!));
        }
        else if (name == "endDreaming")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Dream_Patch).GetMethod(nameof(EndDreamingPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Dreams", "prepareDream");
        yield return ("Dreams", "startDreaming");
        yield return ("Dreams", "endDreaming");
    }

    public static void PreparePrefix(ref string presetName, ref int dreamId)
    {
        try
        {
            if (presetName == "playerDeath") return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;

            if (manager.IsTimeAuthority)
            {
                network.SendReliable(new DreamPreparePacket
                {
                    PlayerId = manager.LocalPlayerId,
                    DreamId = dreamId,
                    PresetName = presetName ?? ""
                });
                ModLogger.Msg($"[Dream_Patch] Ironbark DreamPrepare '{presetName}' (id {dreamId})");
            }
            else
            {
                var story = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<StorySync>();
                if (story != null && story.TryConsumePendingDream(out var hostPreset, out var hostId))
                {
                    ModLogger.Msg($"[Dream_Patch] Following host dream '{hostPreset}' (id {hostId}) instead of '{presetName}'");
                    presetName = hostPreset;
                    dreamId = hostId;
                }
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Dream_Patch] Prepare: {ex.Message}");
        }
    }

    public static void StartDreamingPostfix()
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;

            // Infer preset from pending session or last broadcast path
            string preset = DreamSession.PresetName;
            if (string.IsNullOrEmpty(preset))
            {
                // Local-only start (authority initiated without prepare path)
                try
                {
                    var dreams = UnityEngine.Object.FindObjectOfType(GameTypes.GetType("Dreams"));
                    // leave preset empty — session still tracks Active
                    preset = "dream";
                }
                catch { preset = "dream"; }
            }

            if (preset == "playerDeath") return;

            if (manager.IsTimeAuthority)
            {
                DreamSession.TryBegin(preset);
                DreamSession.MarkActive();
                DreamSession.FreezeAllRemotes();
                FreezeTracker.AddFreeze();

                var pos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
                network.SendReliable(new DreamStartPacket
                {
                    PlayerId = manager.LocalPlayerId,
                    PresetName = preset ?? "",
                    X = pos.x, Y = pos.y, Z = pos.z
                });
            }
            else if (DreamSession.IsActive || !string.IsNullOrEmpty(DreamSession.PresetName))
            {
                DreamSession.MarkActive();
                network.SendReliable(new DreamEnteredPacket { PlayerId = manager.LocalPlayerId });
            }

            ModLogger.Msg($"[Dream_Patch] startDreaming session={DreamSession.Current}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Dream_Patch] StartDreaming: {ex.Message}");
        }
    }

    public static void EndDreamingPostfix()
    {
        try
        {
            // Death_Patch also hooks endDreaming — it runs its own death logic.
            // Only tear down shared dream session here if we were in one.
            if (!DreamSession.IsActive) return;
            if (RemoteApply.Active) return;

            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected)
            {
                DreamSession.End();
                return;
            }

            if (manager.IsTimeAuthority)
            {
                var preset = DreamSession.PresetName ?? "";
                DreamSession.End();
                network.SendReliable(new DreamEndPacket
                {
                    PlayerId = manager.LocalPlayerId,
                    PresetName = preset
                });
            }
            // Non-authority waits for dreamend from authority unless solo leave
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Dream_Patch] EndDreaming: {ex.Message}");
        }
    }
}

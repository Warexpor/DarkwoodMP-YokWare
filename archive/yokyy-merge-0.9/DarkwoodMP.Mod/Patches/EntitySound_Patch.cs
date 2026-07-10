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
/// Authority AI CharacterSounds → peers (Horde EntitySoundSync slim).
/// Vanilla CharacterSounds exposes playGrowl / playEscapingLoop / playIdleLoop.
/// Wire: esound:&lt;stableId&gt;:&lt;type&gt;[:loopName]
/// </summary>
public sealed class EntitySound_Patch : IPatch
{
    public enum SoundType : byte
    {
        Growl = 0,
        EscapingLoop = 7,
        IdleLoop = 8,
        IdleStop = 9
    }

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name == "playGrowl")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(EntitySound_Patch).GetMethod(nameof(GrowlPostfix), statics)!));
        }
        else if (target.Name == "playEscapingLoop")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(EntitySound_Patch).GetMethod(nameof(EscapingPostfix), statics)!));
        }
        else if (target.Name == "playIdleLoop")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(EntitySound_Patch).GetMethod(nameof(IdleLoopPostfix), statics)!));
        }
        else if (target.Name == "destroySounds")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(EntitySound_Patch).GetMethod(nameof(DestroyPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("CharacterSounds", "playGrowl");
        yield return ("CharacterSounds", "playEscapingLoop");
        yield return ("CharacterSounds", "playIdleLoop");
        yield return ("CharacterSounds", "destroySounds");
    }

    public static void GrowlPostfix(object __instance) => Broadcast(__instance, SoundType.Growl, null);
    public static void EscapingPostfix(object __instance) => Broadcast(__instance, SoundType.EscapingLoop, null);

    public static void IdleLoopPostfix(object __instance, object[] __args)
    {
        string loop = "";
        if (__args != null && __args.Length > 0 && __args[0] is string s)
            loop = s;
        Broadcast(__instance, SoundType.IdleLoop, loop);
    }

    public static void DestroyPostfix(object __instance) => Broadcast(__instance, SoundType.IdleStop, "");

    private static void Broadcast(object instance, SoundType type, string loopName)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (!manager.IsTimeAuthority) return;
            if (instance is not CharacterSounds sounds) return;
            var c = sounds.character as Character;
            if (c == null) return;
            if (!CharacterTracker.TryGetStableId(c, out var id) || id == 0)
                id = CharacterTracker.GetStableId(c);
            if (id == 0) return;

            network.Send(new EntitySoundPacket
            {
                PlayerId = manager.LocalPlayerId,
                EntityId = (short)id,
                SoundType = (byte)type,
                LoopName = (type == SoundType.IdleLoop || type == SoundType.IdleStop) ? (loopName ?? "") : ""
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EntitySound] {ex.Message}");
        }
    }

    public static void ApplyRemote(short entityId, byte typeByte, string loopName = "")
    {
        try
        {
            var c = CharacterTracker.FindByStableId(entityId);
            if (c == null) return;
            var sounds = c.GetComponent<CharacterSounds>();
            if (sounds == null) return;

            RemoteApply.Active = true;
            try
            {
                switch ((SoundType)typeByte)
                {
                    case SoundType.EscapingLoop:
                        sounds.playEscapingLoop();
                        break;
                    case SoundType.IdleLoop:
                        if (!string.IsNullOrEmpty(loopName))
                            sounds.playIdleLoop(loopName, true);
                        break;
                    case SoundType.IdleStop:
                        sounds.destroySounds();
                        break;
                    default:
                        sounds.playGrowl();
                        break;
                }
            }
            catch
            {
                try { sounds.playGrowl(); } catch { }
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EntitySound] ApplyRemote: {ex.Message}");
        }
    }
}

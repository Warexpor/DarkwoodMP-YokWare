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
/// Co-op player death. Day: shared morning via pdeath (v0.5).
/// Night (YokWare Branch Phase 4): hold shared morning until all participants
/// are dead — Horde DeathStateTracker slim (spectator UI later).
/// </summary>
public sealed class Death_Patch : IPatch
{
    private static bool _awaitingDeathRespawn;
    private static bool _awaitingWasNight;
    private static float _deathTime;
    private const float DeathDreamTimeout = 120f;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _awaitingDeathRespawn = false;
        _awaitingWasNight = false;
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var name = target.DeclaringType!.Name == "Player" ? nameof(DiePostfix) : nameof(EndDreamingPostfix);
        baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Death_Patch).GetMethod(name, statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "die");
        yield return ("Dreams", "endDreaming");
    }

    public static void DiePostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player
                || player.transform.root.name.StartsWith("RemotePlayer_")) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            _awaitingDeathRespawn = true;
            _deathTime = Time.realtimeSinceStartup;

            bool isNight = false;
            try { isNight = !Core.isDay(); } catch { isNight = false; }
            _awaitingWasNight = isNight;

            var pos = player.transform.position;

            // Shared dream death — track set + lightweight spectate (not night/day morning)
            if (DreamSession.IsActive || FinalDreamsceneManager.IsActive)
            {
                FinalDreamsceneManager.OnLocalDeathInDream();
                // Still notify peers for corpse visuals
                network.SendReliable(new PlayerDiedPacket
                {
                    PlayerId = Math.Max(network.LocalClientId, 0),
                    IsNight = false,
                    X = pos.x, Y = pos.y, Z = pos.z
                });
                ModLogger.Msg("[Death_Patch] Local player died in shared dream");
                return; // do not start night/day death clock flow
            }

            if (isNight)
            {
                DeathStateTracker.OnLocalNightDeath(pos);
                NightSpectator.TryEnterIfNightDead();
            }
            else
                DeathStateTracker.OnLocalDayDeath();

            // pdied:<0|1>:<x>,<y>,<z>  — night flag + death pos for corpse
            network.SendReliable(new PlayerDiedPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                IsNight = isNight,
                X = pos.x, Y = pos.y, Z = pos.z
            });
            ModLogger.Msg($"[Death_Patch] Local player died (night={isNight}) - partner notified");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Death_Patch] {ex.Message}");
        }
    }

    public static void EndDreamingPostfix()
    {
        try
        {
            if (!_awaitingDeathRespawn) return;
            _awaitingDeathRespawn = false;
            if (Time.realtimeSinceStartup - _deathTime > DeathDreamTimeout) return;
            if (RemoteApply.Active) return;

            // Night death: hold shared morning until everyone is dead
            if (_awaitingWasNight && !DeathStateTracker.ShouldBroadcastDeathClock())
            {
                ModLogger.Msg("[Death_Patch] Night death — holding shared morning until all dead");
                return;
            }

            BroadcastDeathClock();
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Death_Patch] {ex.Message}");
        }
    }

    /// <summary>Called when the last night-dead peer arrives (or we are that peer).</summary>
    public static void TryBroadcastMorningIfAllDead()
    {
        if (!DeathStateTracker.AllDeadAtNight) return;
        BroadcastDeathClock();
    }

    private static void BroadcastDeathClock()
    {
        var controller = UnityEngine.Object.FindObjectOfType<Controller>();
        if (controller == null) return;
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;

        network.SendReliable(new PlayerDeathClockPacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            Day = controller.day,
            GameTime = controller.gameTime,
            CurrentTime = controller.CurrentTime,
            LocalNightDeath = DeathStateTracker.LocalNightDeath
        });
        ModLogger.Msg($"[Death_Patch] Death clock broadcast (day {controller.day}, time {controller.CurrentTime})");
        NightSpectator.Exit();
        DeathStateTracker.Reset();
    }
}

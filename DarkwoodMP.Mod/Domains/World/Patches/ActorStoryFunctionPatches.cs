using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla story functions on <c>Player</c> that place or hide things around "the player" (a
    /// scripted step's SendMessage, or a death). Every peer replays the step, each around its own
    /// body, so each placed or hid something different. They now work around the body of the
    /// player the scene belongs to (<see cref="GeFireActorContext.ActorBody"/>), the same on every peer.
    /// </summary>
    internal static class ActorStoryFunctions
    {
        internal static bool Connected()
            => ModRuntime.Network != null && ModRuntime.Network.IsConnected;

        private const string AuraShakePrefix = "AuraShake:";

        /// <summary>Host: a creature's damaging aura shakes and darkens this player's screen (vanilla damagesAroundMe).</summary>
        internal static void SendAuraShake(LanNetworkManager net, int playerId, float magnitude, float noise)
        {
            var msg = new PlayerSpecialMessage
            {
                Method = AuraShakePrefix
                    + magnitude.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ":"
                    + noise.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            };
            net.SendToPlayer(playerId, NetMessageType.PlayerSpecial, w => msg.Serialize(w), DeliveryMethod.Unreliable);
        }

        /// <summary>Client: the host saw a story function happen to this player.</summary>
        internal static void ApplyPlayerSpecial(LanNetworkManager net, PlayerSpecialMessage msg)
        {
            if (net.Role == NetworkRole.Host || Player.Instance == null)
                return;
            if (msg.Method != null && msg.Method.StartsWith(AuraShakePrefix, System.StringComparison.Ordinal))
            {
                string[] parts = msg.Method.Substring(AuraShakePrefix.Length).Split(':');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mag)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float noise))
                {
                    if (Singleton<CamMain>.Instance != null)
                        Singleton<CamMain>.Instance.shake(0.3f, mag);
                    if (Singleton<UI>.Instance != null)
                        Singleton<UI>.Instance.tweenNoise(noise);
                }
                return;
            }
            switch (msg.Method)
            {
                case CombatMusicSync.On:
                case CombatMusicSync.Off:
                    // Vanilla's combat music follows the creatures chasing this player; those run
                    // on the host, so the host says when that starts and stops.
                    bool fight = msg.Method == CombatMusicSync.On;
                    if (Player.Instance.InCombat != fight)
                        Player.Instance.InCombat = fight;
                    break;
                case "special_onKillNightTrader":
                    ModRuntime.LegacyInfo("[Story] host: this player killed the night trader");
                    NightTraderKillPatch.RunLocal = true;
                    try
                    {
                        Player.Instance.special_onKillNightTrader();
                    }
                    finally
                    {
                        NightTraderKillPatch.RunLocal = false;
                    }
                    break;
            }
        }
    }

    /// <summary>Act 2: hide the doctor copies far from where the scene plays (vanilla: &gt; 1500 from the player).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.special_hideDoctorsAct2))]
    public static class HideDoctorsAct2Patch
    {
        private static bool Prefix()
        {
            if (!ActorStoryFunctions.Connected())
                return true;
            Transform body = GeFireActorContext.ActorBody();
            if (body == null || Singleton<UniqueObjects>.Instance == null)
                return true;
            List<GameObject> objects = Singleton<UniqueObjects>.Instance.getObjects("doctor_act2");
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] != null && Core.trueDistance(objects[i].transform.position, body.position) > 1500f)
                    objects[i].setMeActive(activeModifier: false);
            }
            return false;
        }
    }

    /// <summary>The flamethrower scene: Maciek stands beside the player who takes it.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.special_teleportMaciek))]
    public static class TeleportMaciekPatch
    {
        private static readonly AccessTools.FieldRef<Player, Vector3> TelDist =
            AccessTools.FieldRefAccess<Player, Vector3>("maciekTelDist");
        private static readonly AccessTools.FieldRef<Player, float> TelRot =
            AccessTools.FieldRefAccess<Player, float>("maciekTelRot");

        private static bool Prefix(Player __instance)
        {
            if (!ActorStoryFunctions.Connected())
                return true;
            Transform body = GeFireActorContext.ActorBody();
            if (body == null || body == __instance._transform)
                return true;
            GameObject maciek = Singleton<UniqueObjects>.Instance.getObject("maciek_noFlamethrower");
            if (maciek == null)
                return true;
            float yaw = body.rotation.eulerAngles.y;
            maciek.transform.position = body.position + Quaternion.Euler(0f, yaw, 0f) * TelDist(__instance);
            maciek.transform.rotation = Quaternion.Euler(90f, yaw + TelRot(__instance), 0f);
            maciek.GetComponent<tk2dSprite>().SetSprite("macius_end_frame");
            Singleton<Controller>.Instance.waitFramesAndRun(delegate
            {
                GameObject m = Singleton<UniqueObjects>.Instance.getObject("maciek_noFlamethrower");
                if (m != null)
                    m.SetActive(value: true);
            }, 3);
            return false;
        }
    }

    /// <summary>The Wolfman takes his sister: the NPC in the location where the scene plays goes away.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.wolf_stealSister))]
    public static class WolfStealSisterPatch
    {
        private static bool Prefix()
        {
            if (!ActorStoryFunctions.Connected())
                return true;
            Location loc = GeFireActorContext.ActorBigLocation();
            if (loc == null)
                return true;
            NPC[] npcs = loc.gameObject.GetComponentsInChildren<NPC>();
            foreach (NPC npc in npcs)
            {
                if (npc.name == "wolfman")
                    npc.gameObject.SetActive(value: false);
            }
            return false;
        }
    }

    /// <summary>
    /// Killing the night trader (vanilla Character death → Player.special_onKillNightTrader: a
    /// blackout transition, then lying down). The trader dies on the host, so the host's player
    /// blacked out whoever swung, and the player who killed it saw nothing. The killer gets it.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.special_onKillNightTrader))]
    public static class NightTraderKillPatch
    {
        internal static bool RunLocal; // process-scoped: call-scoped, set and cleared around the client apply

        private static bool Prefix()
        {
            if (RunLocal || !NetGuard.Host(out LanNetworkManager net))
                return true;
            int actor = GeFireActorContext.PeekOr(0);
            if (actor <= 0 || actor == net.LocalPlayerId)
                return true;
            var msg = new PlayerSpecialMessage { Method = "special_onKillNightTrader" };
            net.SendToPlayer(actor, NetMessageType.PlayerSpecial, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[Story] night trader killed by p{actor} — their blackout, not the host's");
            return false;
        }
    }

    /// <summary>
    /// Host: whether creatures are chasing each client (vanilla adds a chaser to the player's
    /// attackers only when it chases <c>Player.Instance</c>, so a client never got combat music).
    /// </summary>
    internal static class CombatMusicSync
    {
        internal const string On = "InCombat=1";
        internal const string Off = "InCombat=0";

        private static readonly Dictionary<int, bool> _sent = new Dictionary<int, bool>(); // reset-in: Reset
        private static readonly Dictionary<Transform, int> _chasers = new Dictionary<Transform, int>(); // process-scoped: scratch, cleared before each use
        private static float _next; // reset-in: Reset

        internal static void Reset()
        {
            _sent.Clear();
            _next = 0f;
        }

        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host || Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 1f;
            _chasers.Clear();
            int n = CharacterTracker.CopyAll(out Character[] all);
            for (int i = 0; i < n; i++)
            {
                Character c = all[i];
                if (c == null || !c.alive || c.target == null || c.behaviour != Character.Behaviour.chasingTarget)
                    continue;
                _chasers.TryGetValue(c.target, out int k);
                _chasers[c.target] = k + 1;
            }
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null || proxy.PlayerId <= 0)
                    continue;
                bool fight = _chasers.ContainsKey(proxy.transform);
                if (_sent.TryGetValue(proxy.PlayerId, out bool was) && was == fight)
                    continue;
                if (!_sent.ContainsKey(proxy.PlayerId) && !fight)
                {
                    _sent[proxy.PlayerId] = false;
                    continue;
                }
                _sent[proxy.PlayerId] = fight;
                var msg = new PlayerSpecialMessage { Method = fight ? On : Off };
                net.SendToPlayer(proxy.PlayerId, NetMessageType.PlayerSpecial, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            _chasers.Clear();
        }
    }
}

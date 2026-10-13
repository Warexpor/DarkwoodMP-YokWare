using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using HarmonyLib;
using LiteNetLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The chapter 2 oxygen tanks are real items, one per player. Vanilla hands out one empty tank
    /// per source: the Elephants' talk, the Elephants' body (two), the mask family's shed body. A
    /// full tank in the player's own bag lets them dive (the mi17 hole, the burned cottage pond, the
    /// village cellar passages); diving does not use it up, and the hideout 5 compressor fills every
    /// empty tank in its user's bag, as often as wanted. So one player may carry the whole party's
    /// tanks, fill them in one go and hand them out at the water.
    ///
    /// The host keeps the count. The first take from a source gives the taker a set: as many tanks
    /// as there are players right then (the source's own vanilla tanks count toward it). Every
    /// player present then is covered by that source. A player who joins later and is not covered
    /// gets a spare tank dropped where that source handed out its set, unless they already carry a
    /// tank. Saves from before this rule (every player was handed a tank) get one "carried" record
    /// when the host first sees a tank in the party; it covers the players present and drops spares
    /// by the host's spot at that moment. Records are written with the host's world save.
    /// </summary>
    internal static class OxygenTankParty
    {
        internal const string Empty = "oxygenTank_empty";
        internal const string Full = "oxygenTank_full";

        internal const byte SourceTalk = 1;
        internal const byte SourceElephantsBody = 2;
        internal const byte SourceShedBody = 3;
        internal const byte SourceCarried = 4;

        /// <summary>Seconds a joiner is in the game before its bag counts (its presence sweep runs once a second).</summary>
        private const float SettleSec = 10f;
        private const float DropSpread = 10f;

        internal sealed class Record
        {
            public byte Source;
            public float X, Y, Z;
            public List<string> Covered = new List<string>();
        }

        private sealed class FileData
        {
            public List<Record> Records = new List<Record>();
        }

        private static readonly List<Record> _records = new List<Record>(); // process-scoped: the loaded campaign's records, reloaded when _campaign changes or a save loads
        private static string _campaign; // process-scoped: campaign _records belong to (null = not loaded)
        private static readonly Dictionary<int, float> _inGameSince = new Dictionary<int, float>(); // reset-in: Reset
        private static float _nextTick; // reset-in: Reset
        /// <summary>This class is adding tanks itself: not a source take.</summary>
        private static bool _granting; // process-scoped: call-scoped, set and cleared around each grant

        public static void Reset()
        {
            _inGameSince.Clear();
            _nextTick = 0f;
        }

        /// <summary>A save was loaded: the records go back to what that save was written with.</summary>
        internal static void OnWorldLoaded() => _campaign = null;

        private static int VanillaCount(byte source)
        {
            switch (source)
            {
                case SourceTalk: return 1;
                case SourceElephantsBody: return 2;
                case SourceShedBody: return 1;
                default: return 0;
            }
        }

        /// <summary>The vanilla tank source a container is, or 0.</summary>
        internal static byte ContainerSource(Inventory inv)
        {
            if (inv == null)
                return 0;
            string name = inv.gameObject.name ?? "";
            if (inv.invType == Inventory.InvType.deathDrop && name.StartsWith("Elephants", StringComparison.Ordinal))
                return SourceElephantsBody;
            if (name.StartsWith("sub_maskFamily_shed_01_body", StringComparison.Ordinal))
                return SourceShedBody;
            return 0;
        }

        // ---------------------------------------------------------------- takes

        /// <summary>Local dialogue gave an empty tank (the Elephants' talk).</summary>
        internal static void OnLocalTalkGave()
        {
            if (_granting || DialogHostApplyGuard.SuppressPersonalRewards || Player.Instance == null)
                return;
            var net = ModRuntime.Network;
            if (net == null)
                return;
            Vector3 at = Player.Instance.transform.position;
            if (net.Role == NetworkRole.Host)
            {
                HostSourceTaken(net, net.LocalPlayerId, SourceTalk, at);
                return;
            }
            if (net.Role != NetworkRole.Client || !net.IsConnected)
                return;
            var msg = new OxygenTankSetMessage { Source = SourceTalk, X = at.x, Y = at.y, Z = at.z };
            net.Send(NetMessageType.OxygenTankSet, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo("[OxygenTank] talk gave a tank; asking the host for the party's set");
        }

        /// <summary>Host: a container take of an empty tank (its own, or a client's it just applied).</summary>
        internal static void HostContainerTake(LanNetworkManager net, Inventory inv, string type, int playerId)
        {
            if (type != Empty || net == null || net.Role != NetworkRole.Host)
                return;
            byte source = ContainerSource(inv);
            if (source != 0)
                HostSourceTaken(net, playerId, source, inv.transform.position);
        }

        /// <summary>Host: the first take from a source hands the taker the rest of the party's set.</summary>
        private static void HostSourceTaken(LanNetworkManager net, int takerId, byte source, Vector3 at)
        {
            EnsureLoaded();
            if (Find(source) != null)
                return;
            var rec = new Record { Source = source, X = at.x, Y = at.y, Z = at.z };
            // The host plus every player in the world now; one still loading gets a spare once in.
            int party = 1;
            foreach (int id in PlayersInGame(net))
            {
                party++;
                string key = PlayerKey(net, id);
                if (key != null && !rec.Covered.Contains(key))
                    rec.Covered.Add(key);
            }
            _records.Add(rec);
            int extra = Math.Max(0, party - VanillaCount(source));
            ModRuntime.LegacyInfo($"[OxygenTank] source {source} first taken by player {takerId}: party of {party}, {extra} more tank(s) for the taker");
            if (extra <= 0)
                return;
            if (takerId == net.LocalPlayerId)
            {
                Grant(extra);
                return;
            }
            var msg = new OxygenTankSetMessage { Source = source, Count = (byte)Math.Min(extra, 255) };
            net.SendToPlayer(takerId, NetMessageType.OxygenTankSet, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal static void Handle(LanNetworkManager net, OxygenTankSetMessage msg)
        {
            if (net.Role == NetworkRole.Host)
            {
                // A client's own talk; container takes the host sees itself.
                int from = net.CurrentReceivePlayerId;
                if (from > 0 && msg.Source == SourceTalk)
                    HostSourceTaken(net, from, SourceTalk, new Vector3(msg.X, msg.Y, msg.Z));
                return;
            }
            if (net.Role == NetworkRole.Client && msg.Count > 0)
                Grant(msg.Count);
        }

        private static void Grant(int count)
        {
            Player p = Player.Instance;
            if (p == null || p.Inventory == null)
                return;
            _granting = true;
            try
            {
                for (int i = 0; i < count; i++)
                    p.Inventory.addItemTypeToPlayer(Empty, 1, dropIfNoRoom: true);
            }
            finally
            {
                _granting = false;
            }
            ChatHud.AddSystemLine(Loc.Russian
                ? $"Ещё баллонов с кислородом: {count}, по одному на каждого игрока."
                : $"You got {count} more oxygen tank(s): one for each player.");
            ModRuntime.LegacyInfo($"[OxygenTank] got {count} more tank(s) for the party");
        }

        // ---------------------------------------------------------------- late joiners

        /// <summary>Host, every frame (also with no peers: a carried record is made before anyone joins).</summary>
        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host)
                return;
            float now = Time.unscaledTime;
            if (now < _nextTick)
                return;
            _nextTick = now + 1f;
            if (!HostInWorld())
                return;
            EnsureLoaded();

            if (_records.Count == 0)
            {
                TryRecordCarried(net);
                return;
            }

            var seen = new HashSet<int>();
            foreach (int id in PlayersInGame(net))
            {
                seen.Add(id);
                if (!_inGameSince.ContainsKey(id))
                    _inGameSince[id] = now;
                if (now - _inGameSince[id] < SettleSec)
                    continue;
                string key = PlayerKey(net, id);
                if (key == null)
                    continue;
                bool holds = HoldsTank(id);
                foreach (Record rec in _records)
                {
                    if (rec.Covered.Contains(key))
                        continue;
                    rec.Covered.Add(key);
                    if (holds)
                    {
                        ModRuntime.LegacyInfo($"[OxygenTank] {key} joined with a tank; source {rec.Source} covers them");
                        continue;
                    }
                    DropSpare(net, rec);
                    ModRuntime.LegacyInfo($"[OxygenTank] {key} joined without a tank; spare dropped at source {rec.Source}");
                }
            }
            _gone.Clear();
            foreach (int id in _inGameSince.Keys)
            {
                if (!seen.Contains(id))
                    _gone.Add(id);
            }
            foreach (int id in _gone)
                _inGameSince.Remove(id);
        }

        private static readonly List<int> _gone = new List<int>(); // process-scoped: scratch, cleared before each use

        /// <summary>Host: a save from before this rule, where every player was handed a tank.</summary>
        private static void TryRecordCarried(LanNetworkManager net)
        {
            bool any = LocalHoldsTank();
            foreach (int id in PlayersInGame(net))
            {
                if (HoldsTank(id))
                    any = true;
            }
            if (!any)
                return;
            Vector3 at = Player.Instance.transform.position;
            var rec = new Record { Source = SourceCarried, X = at.x, Y = at.y, Z = at.z };
            foreach (int id in PlayersInGame(net))
            {
                string key = PlayerKey(net, id);
                if (key != null)
                    rec.Covered.Add(key);
            }
            _records.Add(rec);
            ModRuntime.LegacyInfo($"[OxygenTank] the party already carries tanks; spares for new players drop at {at}");
        }

        private static void DropSpare(LanNetworkManager net, Record rec)
        {
            Vector3 at = new Vector3(rec.X, rec.Y, rec.Z);
            float a = UnityEngine.Random.value * Mathf.PI * 2f;
            Vector3 p = at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * DropSpread;
            p.y = Core.getYPos(p, PosType.items1, randomize: false).y;
            QuestItemHandoff.SpawnDrop(net, Empty, p);
        }

        private static bool HostInWorld()
        {
            Player p = Player.Instance;
            if (p == null || p.Inventory == null || p.Hotbar == null || GameScreen.AtTitle || Core.loadingGame || !Core.coreStarted)
                return false;
            if (PersonalPrologue.LocalInPrologue || DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && (Dreams.Instance.dreaming || Dreams.Instance.dreamPrepared)))
                return false;
            return !ClientStateBackup.IsDreamPadCoordinate(p.transform.position);
        }

        private static bool LocalHoldsTank()
        {
            Player p = Player.Instance;
            return p.Inventory.getItemAmount(Full) + p.Hotbar.getItemAmount(Full)
                + p.Inventory.getItemAmount(Empty) + p.Hotbar.getItemAmount(Empty) > 0;
        }

        private static bool HoldsTank(int playerId)
        {
            Dictionary<string, int> bag = PeerItemPresence.CopyOf(playerId);
            if (bag == null)
                return false;
            return (bag.TryGetValue(Full, out int f) && f > 0) || (bag.TryGetValue(Empty, out int e) && e > 0);
        }

        private static IEnumerable<int> PlayersInGame(LanNetworkManager net)
        {
            foreach (int id in net.ConnectedPlayerIds)
            {
                if (PeerNotices.IsInGame(id))
                    yield return id;
            }
        }

        private static Record Find(byte source)
        {
            foreach (Record r in _records)
            {
                if (r.Source == source)
                    return r;
            }
            return null;
        }

        /// <summary>Host: steam id, else the install key a LAN client announced. Null when neither is known.</summary>
        private static string PlayerKey(LanNetworkManager net, int playerId)
        {
            if (net == null || playerId <= 0 || playerId == net.LocalPlayerId)
                return null;
            if (net.TryGetSteamIdForPlayer(playerId, out ulong steamId))
                return "s" + steamId;
            if (net.TryGetStableClientKeyForPlayer(playerId, out string key))
                return "k" + key;
            return null;
        }

        // ---------------------------------------------------------------- records on disk

        /// <summary>Host's own world save written: the records now match it.</summary>
        internal static void OnHostSaved()
        {
            if (_campaign == null)
                return;
            string path = FilePath(_campaign);
            try
            {
                if (_records.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                File.WriteAllText(path, JsonConvert.SerializeObject(new FileData { Records = _records }, Formatting.None));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[OxygenTank] could not write records: " + ex.Message);
            }
        }

        private static void EnsureLoaded()
        {
            string campaign = CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile() ?? "";
            if (_campaign == campaign)
                return;
            _campaign = campaign;
            _records.Clear();
            string path = FilePath(campaign);
            if (!File.Exists(path))
                return;
            try
            {
                FileData data = JsonConvert.DeserializeObject<FileData>(File.ReadAllText(path));
                if (data?.Records != null)
                    _records.AddRange(data.Records);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[OxygenTank] could not read records: " + ex.Message);
            }
        }

        private static string FilePath(string campaign)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in campaign ?? "")
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            return ClientStateBackup.GetProfileBackupDirectory() + "/oxygen_tanks_" + (sb.Length > 0 ? sb.ToString() : "nocampaign") + ".json";
        }
    }

    /// <summary>The Elephants' talk hands over an empty tank (a dialogue giveItem runs inside displayNextBoard).</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.addItemTypeToPlayer))]
    public static class OxygenTankTalkGivePatch
    {
        private static void Postfix(string type)
        {
            if (type != OxygenTankParty.Empty || DialogMirror.BoardDepth <= 0)
                return;
            OxygenTankParty.OnLocalTalkGave();
        }
    }
}

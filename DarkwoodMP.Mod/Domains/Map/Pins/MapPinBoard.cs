using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>The stamp a party map pin is drawn with (vanilla map ink).</summary>
    internal enum MapPinKind : byte
    {
        Mark = 0,
        Danger = 1,
        Loot = 2,
        Shelter = 3,
        Camp = 4,
        Grave = 5,
    }

    internal sealed class MapPin
    {
        public int Id;
        public MapPinKind Kind;
        public byte Color;
        public int Chapter;
        public float X, Z;
        public int Day;
        public int OwnerId;
        public string OwnerTag;
        public string OwnerName;
        public string Label;

        public MapPinWire ToWire() => new MapPinWire
        {
            Id = Id, Kind = (byte)Kind, Color = Color, Chapter = Chapter, X = X, Z = Z, Day = Day,
            OwnerId = OwnerId, OwnerTag = OwnerTag, OwnerName = OwnerName, Label = Label
        };

        public static MapPin FromWire(MapPinWire w) => new MapPin
        {
            Id = w.Id, Kind = MapPinBoard.ClampKind(w.Kind), Color = w.Color, Chapter = w.Chapter,
            X = w.X, Z = w.Z, Day = w.Day, OwnerId = w.OwnerId, OwnerTag = w.OwnerTag ?? "",
            OwnerName = w.OwnerName ?? "", Label = w.Label ?? ""
        };
    }

    /// <summary>A short-lived ping on the map.</summary>
    internal struct MapPing
    {
        public MapPin Pin;
        public float Until;
    }

    /// <summary>
    /// The party map board: every pin any player placed, one shared list the host owns. Clients ask
    /// (<see cref="NetMessageType.MapPinRequest"/>); the host numbers, checks and applies the change
    /// and sends the result to everyone (<see cref="NetMessageType.MapPinEvent"/>); a joiner gets the
    /// whole board in <c>MapStateSync</c>. Pins are addressed by the host's id, never by position, so
    /// two pins close together cannot be mixed up. An owner is a hash of the install key, so the same
    /// player keeps the same pins and colour across sessions and new player ids.
    ///
    /// Every machine keeps its copy of the board next to its world (<see cref="MapPinStore"/>), so the
    /// host's pins survive a restart and a player promoted by host migration still has them.
    /// </summary>
    internal static class MapPinBoard
    {
        internal const int KindCount = 6;
        internal const int MaxPins = 300;
        internal const int MaxLabelLength = 40;
        internal const float PingSeconds = 25f;
        private const float HostPingInterval = 1f;
        /// <summary>A pin placed by the same owner this close to one of theirs is the same pin (re-sent legacy pins).</summary>
        private const float SameSpotSqr = 1f;

        internal static readonly List<MapPin> Pins = new List<MapPin>(64);
        internal static readonly List<MapPing> Pings = new List<MapPing>(4);
        private static readonly Dictionary<int, float> _lastPingBySender = new Dictionary<int, float>();
        private static int _nextId = 1;
        /// <summary>Bumped on every board change; the open map redraws when it moves.</summary>
        internal static int Version;
        private static string _localTag; // process-scoped: hash of this install's constant key

        internal static MapPinKind ClampKind(int k) => k >= 0 && k < KindCount ? (MapPinKind)k : MapPinKind.Mark;

        internal static string KindName(MapPinKind k)
        {
            switch (k)
            {
                case MapPinKind.Danger: return "Danger";
                case MapPinKind.Loot: return "Loot";
                case MapPinKind.Shelter: return "Shelter";
                case MapPinKind.Camp: return "Camp";
                case MapPinKind.Grave: return "Grave";
                default: return "Mark";
            }
        }

        internal static MapPin Find(int id)
        {
            for (int i = 0; i < Pins.Count; i++)
                if (Pins[i].Id == id)
                    return Pins[i];
            return null;
        }

        /// <summary>The chapter the board files a pin under (each chapter has its own world map).</summary>
        internal static int CurrentChapter()
        {
            try
            {
                WorldGenerator wg = Singleton<WorldGenerator>.Instance;
                if (wg != null && wg.chapterID > 0)
                    return wg.chapterID;
                if (Core.currentProfile != null && Core.currentProfile.chapter > 0)
                    return Core.currentProfile.chapter;
            }
            catch { /* scene teardown */ }
            return 1;
        }

        private static int CurrentDay()
        {
            try { return Core.currentProfile != null ? Core.currentProfile.day : 0; }
            catch { return 0; }
        }

        internal static string LocalName()
        {
            string name = DWMPHorde.Config.ModConfig.PlayerName != null ? DWMPHorde.Config.ModConfig.PlayerName.Value : null;
            name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
            return name.Length > 24 ? name.Substring(0, 24) : name;
        }

        /// <summary>Hash of this install's LAN key: what the host files this player's pins under.</summary>
        internal static string LocalTag
        {
            get
            {
                if (_localTag == null)
                    _localTag = TagOfKey(ClientStateBackup.GetOrCreateLanClientKey()) ?? "";
                return _localTag;
            }
        }

        internal static bool IsLocalOwner(MapPin pin)
        {
            if (pin == null) return false;
            if (!string.IsNullOrEmpty(pin.OwnerTag) && pin.OwnerTag == LocalTag) return true;
            var net = ModRuntime.Network;
            return string.IsNullOrEmpty(pin.OwnerTag) && net != null && pin.OwnerId == net.LocalPlayerId;
        }

        private static string TagOfKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes("dwmp-pin:" + key));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                    sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Host: the owner tag of a player in this session.</summary>
        private static string TagOfPlayer(LanNetworkManager net, int playerId)
        {
            if (playerId == net.LocalPlayerId)
                return LocalTag;
            if (net.TryGetStableClientKeyForPlayer(playerId, out string key))
            {
                string tag = TagOfKey(key);
                if (!string.IsNullOrEmpty(tag)) return tag;
            }
            return "p" + playerId;
        }

        /// <summary>Host: an owner keeps their colour; a new owner gets the first one nobody on the board has.</summary>
        private static byte ColorFor(string tag)
        {
            int used = 0;
            for (int i = 0; i < Pins.Count; i++)
            {
                if (Pins[i].OwnerTag == tag)
                    return Pins[i].Color;
                if (Pins[i].Color < 32)
                    used |= 1 << Pins[i].Color;
            }
            for (int c = 0; c < MapPinPalette.Count; c++)
                if ((used & (1 << c)) == 0)
                    return (byte)c;
            return (byte)(((tag ?? "").GetHashCode() & 0x7fffffff) % MapPinPalette.Count);
        }

        internal static string SanitizeLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var sb = new StringBuilder(label.Length);
            foreach (char ch in label)
            {
                if (char.IsControl(ch)) continue;
                sb.Append(ch);
            }
            string s = sb.ToString().Trim();
            return s.Length > MaxLabelLength ? s.Substring(0, MaxLabelLength) : s;
        }

        /// <summary>Hosting (peers or not) or joined: the board is live.</summary>
        internal static bool SessionUp(out LanNetworkManager net)
        {
            net = ModRuntime.Network;
            return net != null && (net.Role == NetworkRole.Host || (net.Role == NetworkRole.Client && net.IsConnected));
        }

        private static bool ValidSpot(float x, float z)
            => !float.IsNaN(x) && !float.IsNaN(z) && !float.IsInfinity(x) && !float.IsInfinity(z)
               && Mathf.Abs(x) < 1e6f && Mathf.Abs(z) < 1e6f;

        // ── Local actions (host applies, client asks) ─────────────────────────────

        internal static void RequestPut(MapPinKind kind, int chapter, float x, float z, string label = "")
            => Send(new MapPinRequestMessage { Op = (byte)MapPinOp.Put, Kind = (byte)kind, Chapter = chapter, X = x, Z = z, Label = label });

        internal static void RequestRemove(int pinId)
            => Send(new MapPinRequestMessage { Op = (byte)MapPinOp.Remove, PinId = pinId });

        internal static void RequestSetKind(int pinId, MapPinKind kind)
            => Send(new MapPinRequestMessage { Op = (byte)MapPinOp.SetKind, PinId = pinId, Kind = (byte)kind });

        internal static void RequestSetLabel(int pinId, string label)
            => Send(new MapPinRequestMessage { Op = (byte)MapPinOp.SetLabel, PinId = pinId, Label = SanitizeLabel(label) });

        internal static void RequestPing(int chapter, float x, float z)
            => Send(new MapPinRequestMessage { Op = (byte)MapPinOp.Ping, Chapter = chapter, X = x, Z = z });

        private static void Send(MapPinRequestMessage req)
        {
            if (!SessionUp(out var net))
                return;
            req.OwnerName = LocalName();
            if (net.Role == NetworkRole.Host)
                HostApply(net, net.LocalPlayerId, req);
            else if (net.Role == NetworkRole.Client)
                net.Broadcast(NetMessageType.MapPinRequest, w => req.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        // ── Host authority ────────────────────────────────────────────────────────

        /// <summary>Host: apply one player's request to the board and tell everyone the result.</summary>
        internal static void HostApply(LanNetworkManager net, int senderId, MapPinRequestMessage req)
        {
            if (net == null || net.Role != NetworkRole.Host || senderId <= 0)
                return;
            EnsureSeeded(force: true);
            string name = string.IsNullOrWhiteSpace(req.OwnerName) ? ("P" + senderId) : req.OwnerName.Trim();
            if (name.Length > 24) name = name.Substring(0, 24);
            MapPin pin;
            switch ((MapPinOp)req.Op)
            {
                case MapPinOp.Put:
                {
                    if (!ValidSpot(req.X, req.Z) || req.Chapter <= 0)
                        return;
                    string tag = TagOfPlayer(net, senderId);
                    for (int i = 0; i < Pins.Count; i++)
                    {
                        MapPin p = Pins[i];
                        float dx = p.X - req.X, dz = p.Z - req.Z;
                        if (p.OwnerTag == tag && p.Chapter == req.Chapter && dx * dx + dz * dz < SameSpotSqr)
                            return;
                    }
                    if (Pins.Count >= MaxPins)
                    {
                        ModLog.Event(LogCat.UI, "[MapPin] board full (" + MaxPins + "), pin from player " + senderId + " refused");
                        return;
                    }
                    pin = new MapPin
                    {
                        Id = _nextId++,
                        Kind = ClampKind(req.Kind),
                        Color = ColorFor(tag),
                        Chapter = req.Chapter,
                        X = req.X,
                        Z = req.Z,
                        Day = CurrentDay(),
                        OwnerId = senderId,
                        OwnerTag = tag,
                        OwnerName = name,
                        Label = SanitizeLabel(req.Label)
                    };
                    ApplyAndSend(net, MapPinOp.Put, pin);
                    return;
                }
                case MapPinOp.Remove:
                    pin = Find(req.PinId);
                    if (pin != null)
                        ApplyAndSend(net, MapPinOp.Remove, pin);
                    return;
                case MapPinOp.SetKind:
                    pin = Find(req.PinId);
                    if (pin == null) return;
                    pin = Copy(pin);
                    pin.Kind = ClampKind(req.Kind);
                    ApplyAndSend(net, MapPinOp.Put, pin);
                    return;
                case MapPinOp.SetLabel:
                    pin = Find(req.PinId);
                    if (pin == null) return;
                    pin = Copy(pin);
                    pin.Label = SanitizeLabel(req.Label);
                    ApplyAndSend(net, MapPinOp.Put, pin);
                    return;
                case MapPinOp.Ping:
                {
                    if (!ValidSpot(req.X, req.Z) || req.Chapter <= 0)
                        return;
                    float now = Time.unscaledTime;
                    if (_lastPingBySender.TryGetValue(senderId, out float last) && now - last < HostPingInterval)
                        return;
                    _lastPingBySender[senderId] = now;
                    string tag = TagOfPlayer(net, senderId);
                    pin = new MapPin
                    {
                        Id = 0, Kind = MapPinKind.Mark, Color = ColorFor(tag), Chapter = req.Chapter,
                        X = req.X, Z = req.Z, Day = CurrentDay(), OwnerId = senderId, OwnerTag = tag,
                        OwnerName = name, Label = ""
                    };
                    ApplyAndSend(net, MapPinOp.Ping, pin);
                    return;
                }
            }
        }

        private static MapPin Copy(MapPin p) => MapPin.FromWire(p.ToWire());

        private static void ApplyAndSend(LanNetworkManager net, MapPinOp op, MapPin pin)
        {
            var ev = new MapPinEventMessage { Op = (byte)op, Pin = pin.ToWire() };
            Apply(ev);
            if (net.IsConnected)
                net.Broadcast(NetMessageType.MapPinEvent, w => ev.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        // ── Apply (every machine) ─────────────────────────────────────────────────

        /// <summary>One board change from the host (or the host's own, already decided).</summary>
        internal static void Apply(MapPinEventMessage ev)
        {
            MapPin incoming = MapPin.FromWire(ev.Pin);
            switch ((MapPinOp)ev.Op)
            {
                case MapPinOp.Put:
                {
                    if (incoming.Id <= 0) return;
                    MapPin existing = Find(incoming.Id);
                    if (existing == null)
                    {
                        Pins.Add(incoming);
                        if (incoming.Id >= _nextId) _nextId = incoming.Id + 1;
                        if (!IsLocalOwner(incoming))
                            Notify(incoming.OwnerName + " marked " + KindName(incoming.Kind)
                                + (string.IsNullOrEmpty(incoming.Label) ? "" : " \"" + incoming.Label + "\"") + " on the map.", false);
                    }
                    else
                    {
                        existing.Kind = incoming.Kind;
                        existing.Label = incoming.Label;
                        existing.Color = incoming.Color;
                        existing.OwnerName = incoming.OwnerName;
                    }
                    Changed();
                    ModLog.Event(LogCat.UI, "[MapPin] #" + incoming.Id + " " + KindName(incoming.Kind) + " by "
                        + incoming.OwnerName + " (p" + incoming.OwnerId + ") at " + incoming.X.ToString("F0") + "," + incoming.Z.ToString("F0")
                        + " ch" + incoming.Chapter + (existing == null ? "" : " (changed)"));
                    return;
                }
                case MapPinOp.Remove:
                {
                    for (int i = Pins.Count - 1; i >= 0; i--)
                    {
                        if (Pins[i].Id != incoming.Id) continue;
                        Pins.RemoveAt(i);
                        Changed();
                        ModLog.Event(LogCat.UI, "[MapPin] #" + incoming.Id + " erased");
                        return;
                    }
                    return;
                }
                case MapPinOp.Ping:
                {
                    Pings.Add(new MapPing { Pin = incoming, Until = Time.unscaledTime + PingSeconds });
                    while (Pings.Count > 8)
                        Pings.RemoveAt(0);
                    Version++;
                    if (!IsLocalOwner(incoming))
                        Notify(incoming.OwnerName + " pinged the map.", true);
                    ModLog.Event(LogCat.UI, "[MapPin] ping by " + incoming.OwnerName + " at "
                        + incoming.X.ToString("F0") + "," + incoming.Z.ToString("F0"));
                    return;
                }
            }
        }

        /// <summary>Client: the host's whole board (late join / rejoin) replaces this one.</summary>
        internal static void ApplySnapshot(MapPinWire[] pins, int count)
        {
            Pins.Clear();
            for (int i = 0; i < count && pins != null && i < pins.Length; i++)
            {
                if (pins[i].Id <= 0) continue;
                MapPin p = MapPin.FromWire(pins[i]);
                Pins.Add(p);
                if (p.Id >= _nextId) _nextId = p.Id + 1;
            }
            Changed();
            ModLog.Event(LogCat.UI, "[MapPin] board from host: " + Pins.Count + " pin(s)");
        }

        /// <summary>Host: the board for a joiner.</summary>
        internal static MapPinWire[] Snapshot()
        {
            EnsureSeeded(force: true);
            var arr = new MapPinWire[Pins.Count];
            for (int i = 0; i < Pins.Count; i++)
                arr[i] = Pins[i].ToWire();
            return arr;
        }

        internal static void PrunePings()
        {
            float now = Time.unscaledTime;
            for (int i = Pings.Count - 1; i >= 0; i--)
            {
                if (Pings[i].Until <= now)
                {
                    Pings.RemoveAt(i);
                    Version++;
                }
            }
        }

        private static void Changed()
        {
            Version++;
            MapPinStore.MarkDirty();
        }

        private static void Notify(string text, bool overHead)
        {
            ChatHud.AddSystemLine("[Map] " + text);
            if (!overHead) return;
            try
            {
                Map map = Map.Instance;
                if (Player.Instance == null || (map != null && map.opened)) return;
                DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                try { Player.Instance.displayMessage(text); }
                finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
            }
            catch { /* a message is never worth breaking the apply */ }
        }

        // ── Host seeding from the world ───────────────────────────────────────────

        private static int _seededProfile = -1;
        private static string _seededCampaign;
        private static float _nextSeedCheck;

        /// <summary>
        /// Host: load the world's board once per world. A board already held (a promoted host keeps
        /// the one it was sent) is kept. A different world on the same slot (new campaign) drops it.
        /// </summary>
        internal static void EnsureSeeded(bool force)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host)
                return;
            float now = Time.unscaledTime;
            if (!force && now < _nextSeedCheck)
                return;
            _nextSeedCheck = now + 10f;
            if (Core.currentProfile == null || DWMPHorde.GameScreen.AtTitle || Core.loadingGame)
                return;
            int pid = Core.currentProfile.id;
            string campaign = CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile();
            // A brand-new world gets its id when it is first shared: until then the board is held
            // (pins placed meanwhile stay) and seeded once the id is known.
            if (string.IsNullOrEmpty(campaign))
                return;
            if (pid == _seededProfile && campaign == _seededCampaign)
                return;
            bool otherWorld = _seededProfile != -1;
            _seededProfile = pid;
            _seededCampaign = campaign;
            if (otherWorld)
            {
                // Another world on this machine (start over, another slot): the old board is not
                // written into the new world's file, and not shown on its map.
                MapPinStore.ResetPending();
                Pins.Clear();
                Pings.Clear();
                Version++;
            }
            if (Pins.Count > 0)
            {
                for (int i = 0; i < Pins.Count; i++)
                    if (Pins[i].Id >= _nextId) _nextId = Pins[i].Id + 1;
                MapPinStore.MarkDirty();
                return;
            }
            List<MapPin> saved = MapPinStore.Load(pid, campaign, out int nextId);
            if (saved == null)
                return;
            Pins.AddRange(saved);
            _nextId = Math.Max(_nextId, nextId);
            for (int i = 0; i < Pins.Count; i++)
                if (Pins[i].Id >= _nextId) _nextId = Pins[i].Id + 1;
            Version++;
            ModLog.Event(LogCat.UI, "[MapPin] loaded " + Pins.Count + " pin(s) from the world (prof" + pid + ")");
        }

        internal static int NextId => _nextId;

        /// <summary>Host: the world the board was loaded for (null until known). The store writes only to it.</summary>
        internal static string SeededCampaign => _seededCampaign;

        internal static void Tick()
        {
            EnsureSeeded(force: false);
            if (Pings.Count > 0)
                PrunePings();
            MapPinStore.Tick();
        }

        /// <summary>Session end: save what changed, then forget the board (the next host sends it again).</summary>
        internal static void Reset()
        {
            MapPinStore.Flush();
            MapPinStore.ResetPending();
            Pins.Clear();
            Pings.Clear();
            _lastPingBySender.Clear();
            _nextId = 1;
            _seededProfile = -1;
            _seededCampaign = null;
            _nextSeedCheck = 0f;
            Version++;
        }
    }
}

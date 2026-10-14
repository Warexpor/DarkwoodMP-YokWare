using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The party's home moves (old oven to new oven), kept with the world. A player who lived in
    /// the hideout the party left follows the move when it happens (<see cref="OvenHomes"/>), but
    /// only a player connected at that moment heard it: one who joined or came back later, or a
    /// fresh character (its home is the world's first hideout), kept the old home and woke there
    /// after a death, alone, while the party lived in the next hideout. Every peer records each
    /// move it sees; the host writes them with its world save and hands them to a joiner. A
    /// player whose home is an oven the party moved out of follows on its next effect keepalive.
    /// Moving back to an old hideout is a move too: that oven is a home again. A player standing
    /// in its home hideout when the party leaves it stays there for good (see ChooseHome).
    /// </summary>
    internal static class PartyHomeMoves
    {
        internal const int MaxMoves = 16;
        private const float SameOvenSq = 1f;
        /// <summary>A chain longer than this is a loop of moves; the last hop is taken.</summary>
        private const int MaxHops = 8;

        internal sealed class Move
        {
            public int Chapter;
            public float FX, FY, FZ;
            public float TX, TY, TZ;
        }

        private sealed class FileData
        {
            public List<Move> Moves = new List<Move>();
        }

        private static readonly List<Move> _moves = new List<Move>(); // process-scoped: the loaded campaign's moves, reloaded when _campaign changes or a save loads
        private static string _campaign; // process-scoped: campaign _moves belong to (null = not loaded)
        /// <summary>A follow found no oven at its target: not tried again for this home.</summary>
        private static Vector3? _failedFrom; // reset-in: Reset

        public static void Reset() => _failedFrom = null;

        private static int Chapter()
        {
            var wg = Singleton<WorldGenerator>.Instance;
            return wg != null ? wg.chapterID : 0;
        }

        private static bool Same(float ax, float ay, float az, Vector3 b)
            => (new Vector3(ax, ay, az) - b).sqrMagnitude <= SameOvenSq;

        /// <summary>A move seen live: the mover's own, or a peer's MovedHome.</summary>
        internal static void Record(Vector3 from, Vector3 to)
        {
            if ((from - to).sqrMagnitude <= SameOvenSq)
                return;
            EnsureLoaded();
            int chapter = Chapter();
            for (int i = _moves.Count - 1; i >= 0; i--)
            {
                Move m = _moves[i];
                if (m.Chapter != chapter)
                    continue;
                // The new home is a home again; an old move out of where the party leaves now is replaced.
                if (Same(m.FX, m.FY, m.FZ, to) || Same(m.FX, m.FY, m.FZ, from))
                {
                    _moves.RemoveAt(i);
                    continue;
                }
                // Those who left an older hideout for the one left now go on to the new one.
                if (Same(m.TX, m.TY, m.TZ, from))
                {
                    m.TX = to.x; m.TY = to.y; m.TZ = to.z;
                }
            }
            _moves.Add(new Move { Chapter = chapter, FX = from.x, FY = from.y, FZ = from.z, TX = to.x, TY = to.y, TZ = to.z });
            // Other chapters' maps are gone; the oldest of this one go first.
            _moves.RemoveAll(m => m.Chapter != chapter);
            while (_moves.Count > MaxMoves)
                _moves.RemoveAt(0);
            _failedFrom = null;
            ModLog.Event(LogCat.World, "[HideoutUpgrade] party home move recorded " + from + " → " + to + " (" + _moves.Count + " kept)");
        }

        /// <summary>Where the party went from <paramref name="home"/>, when it moved out of it.</summary>
        internal static bool TryResolve(Vector3 home, out Vector3 to)
        {
            to = home;
            EnsureLoaded();
            int chapter = Chapter();
            bool moved = false;
            for (int hop = 0; hop < MaxHops; hop++)
            {
                Move next = null;
                for (int i = 0; i < _moves.Count; i++)
                {
                    Move m = _moves[i];
                    if (m.Chapter == chapter && Same(m.FX, m.FY, m.FZ, to))
                    {
                        next = m;
                        break;
                    }
                }
                if (next == null)
                    break;
                to = new Vector3(next.TX, next.TY, next.TZ);
                moved = true;
            }
            return moved && (to - home).sqrMagnitude > SameOvenSq;
        }

        /// <summary>
        /// Every effect keepalive: the local player still lives where the party moved out of
        /// (it was not here for the move). Not while a load, a backup restore, a prologue, an
        /// epilogue or a dream owns the player.
        /// </summary>
        internal static void CheckLocalHome(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected || Core.loadingGame || GameScreen.AtTitle)
                return;
            Player p = Player.Instance;
            ExperienceMachine home = p != null ? p.experienceMachine : null;
            if (home == null)
                return;
            if (net.Role == NetworkRole.Client && net.SaveHandlers != null && net.SaveHandlers.ClientPlacementPending)
                return;
            if (Sync.PersonalPrologue.LocalInPrologue || EpilogueNetHandlers.IsLocalInEpilogue())
                return;
            Dreams dreams = Singleton<Dreams>.Instance;
            if (Sync.DreamSyncManager.IsLocalDreamActive || (dreams != null && (dreams.dreaming || dreams.switchingDream)))
                return;
            if (OvenHomes.TryAttachOnEntry(p))
                return;
            Vector3 from = home.transform.position;
            if (_failedFrom.HasValue && (_failedFrom.Value - from).sqrMagnitude <= SameOvenSq)
                return;
            if (PartyHomeMoves.IsChosenHome(from) || !TryResolve(from, out Vector3 to))
                return;
            if (!OvenHomes.FollowNow(p, from, to, "party moved home while this player was away", mayStay: false))
                _failedFrom = from;
        }

        // ---------------------------------------------------------------- own choice

        /// <summary>
        /// The home this install's player kept when the party moved out of it (it was standing in
        /// that hideout then), by campaign. While the player lives there, party moves and recorded
        /// moves leave it. Any later move of its own, or a follow, ends it. Kept in a small file of
        /// this install (each player's own choice), written when it changes.
        /// </summary>
        private sealed class ChoiceData
        {
            public bool Has;
            public float X, Y, Z;
            public int Chapter;
        }

        private static ChoiceData _choice; // process-scoped: this install's choice for _choiceCampaign
        private static string _choiceCampaign; // process-scoped: campaign _choice belongs to (null = not loaded)

        internal static void ChooseHome(Vector3? home)
        {
            EnsureChoiceLoaded();
            _choice = home.HasValue
                ? new ChoiceData { Has = true, X = home.Value.x, Y = home.Value.y, Z = home.Value.z, Chapter = Chapter() }
                : new ChoiceData();
            try
            {
                string path = ChoicePath(_choiceCampaign);
                if (!_choice.Has)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else
                    File.WriteAllText(path, JsonConvert.SerializeObject(_choice, Formatting.None));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[HideoutUpgrade] could not write the home choice: " + ex.Message);
            }
            if (home.HasValue)
                ModLog.Event(LogCat.World, "[HideoutUpgrade] home at " + home.Value + " is this player's own choice");
        }

        /// <summary>The local player chose this home itself (it stays when the party moves on).</summary>
        internal static bool IsChosenHome(Vector3 home)
        {
            EnsureChoiceLoaded();
            return _choice != null && _choice.Has && _choice.Chapter == Chapter()
                && Same(_choice.X, _choice.Y, _choice.Z, home);
        }

        private static void EnsureChoiceLoaded()
        {
            string campaign = CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile() ?? "";
            if (_choiceCampaign == campaign)
                return;
            _choiceCampaign = campaign;
            _choice = null;
            string path = ChoicePath(campaign);
            if (!File.Exists(path))
                return;
            try { _choice = JsonConvert.DeserializeObject<ChoiceData>(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[HideoutUpgrade] could not read the home choice: " + ex.Message);
            }
        }

        private static string ChoicePath(string campaign)
            => ClientStateBackup.GetProfileBackupDirectory() + "/own_home_choice_" + Safe(campaign) + ".json";

        // ---------------------------------------------------------------- sharing

        /// <summary>Host: a joiner gets the moves (its own follow, and a copy for a promotion).</summary>
        internal static void ShareTo(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            EnsureLoaded();
            int n = Math.Min(_moves.Count, MaxMoves);
            var msg = new PartyHomeMovesMessage
            {
                Chapter = new int[n],
                FX = new float[n], FY = new float[n], FZ = new float[n],
                TX = new float[n], TY = new float[n], TZ = new float[n]
            };
            for (int i = 0; i < n; i++)
            {
                Move m = _moves[i];
                msg.Chapter[i] = m.Chapter;
                msg.FX[i] = m.FX; msg.FY[i] = m.FY; msg.FZ[i] = m.FZ;
                msg.TX[i] = m.TX; msg.TY[i] = m.TY; msg.TZ[i] = m.TZ;
            }
            net.SendToPlayer(playerId, NetMessageType.PartyHomeMoves, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Client: the host's moves replace this copy's.</summary>
        internal static void Apply(LanNetworkManager net, PartyHomeMovesMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client || msg.Chapter == null)
                return;
            _campaign = CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile() ?? "";
            _moves.Clear();
            for (int i = 0; i < msg.Chapter.Length; i++)
            {
                _moves.Add(new Move
                {
                    Chapter = msg.Chapter[i],
                    FX = msg.FX[i], FY = msg.FY[i], FZ = msg.FZ[i],
                    TX = msg.TX[i], TY = msg.TY[i], TZ = msg.TZ[i]
                });
            }
            _failedFrom = null;
            ModLog.Event(LogCat.World, "[HideoutUpgrade] party home moves from the host: " + _moves.Count);
        }

        // ---------------------------------------------------------------- on disk

        /// <summary>A save was loaded: the moves go back to what that save was written with.</summary>
        internal static void OnWorldLoaded() => _campaign = null;

        /// <summary>Host's own world save written: the moves now match it.</summary>
        internal static void OnHostSaved()
        {
            if (_campaign == null)
                return;
            string path = FilePath(_campaign);
            try
            {
                if (_moves.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                File.WriteAllText(path, JsonConvert.SerializeObject(new FileData { Moves = _moves }, Formatting.None));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[HideoutUpgrade] could not write party home moves: " + ex.Message);
            }
        }

        private static void EnsureLoaded()
        {
            string campaign = CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile() ?? "";
            if (_campaign == campaign)
                return;
            _campaign = campaign;
            _moves.Clear();
            _failedFrom = null;
            string path = FilePath(campaign);
            if (!File.Exists(path))
                return;
            try
            {
                FileData data = JsonConvert.DeserializeObject<FileData>(File.ReadAllText(path));
                if (data?.Moves != null)
                    _moves.AddRange(data.Moves);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[HideoutUpgrade] could not read party home moves: " + ex.Message);
            }
        }

        private static string FilePath(string campaign)
            => ClientStateBackup.GetProfileBackupDirectory() + "/party_home_moves_" + Safe(campaign) + ".json";

        private static string Safe(string campaign)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in campaign ?? "")
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : "nocampaign";
        }
    }
}

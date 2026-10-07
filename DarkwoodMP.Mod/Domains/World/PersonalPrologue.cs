using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Every player plays the prologue on their own, as in single player.
    ///
    /// Vanilla runs it as two dreams (<c>dream_tutorial_00</c>, then <c>dream_tutorial_01</c>) on
    /// pads of their own; the second one's end puts the player in the hideout at 05:00 with the
    /// chapter's starting pack, plus two exp_mushroom unless the chomper got them (outcomes default
    /// and hit; chomper_attack and playerDeath give nothing). Two world flags are set on the pads:
    /// dream_tutorial_01_noises (the second pad's own) and doctor_dogKilled (killing Dog_doctor on
    /// the first pad; chapter 1's outside_doctor_house_01 reads it). A host's prologue is its own
    /// world's, as in single player. A joiner's runs offline, so FlagSync sends nothing, and its
    /// flags go back to the host's world's when it wakes (<see cref="RestoreWorldFlags"/>): its
    /// own tutorial dog does not change the doctor's house for anyone, itself included. So it runs
    /// on one machine without the others:
    /// <list type="bullet">
    /// <item>A joiner new to the world (chapter 1, the host did not skip the prologue, no character
    /// of this player on this machine for the campaign) loads the world with a new-game character
    /// and plays the prologue while still offline after the world download (join phase 2), then
    /// reconnects as usual (phase 3) when it wakes in the hideout. Offline, every co-op patch
    /// stays out of its way: its creatures, items and dreams run as in single player.</item>
    /// <item>The host's own prologue (a new game) stays connected: its prologue dreams are not party
    /// dreams, and what it does on the pads is not sent (<see cref="HostBlocksSend"/>).</item>
    /// <item>Day 1 waits for everyone: while anyone is still in the prologue on a world whose first
    /// morning has not begun, the host's clock holds (<see cref="HoldDayOne"/>).</item>
    /// </list>
    /// </summary>
    internal static class PersonalPrologue
    {
        /// <summary>Game minutes past the prologue's wake-up (05) that still count as "day 1 not begun".</summary>
        private const int DayOneStartSlack = 10;
        /// <summary>Host: a joiner sent the world for its prologue that never came back stops holding day 1.</summary>
        private const float PendingTimeoutSec = 45f * 60f;

        internal static bool IsPrologueDream(string presetName)
        {
            if (string.IsNullOrEmpty(presetName))
                return false;
            return Core.getTrueLocationName(presetName).StartsWith("dream_tutorial", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>This machine's player is in its prologue (the movie, either prologue dream).</summary>
        internal static bool LocalInPrologue
        {
            get
            {
                Player p = Player.Instance;
                if (p == null)
                    return false;
                if (p.firstPlay)
                    return true;
                Dreams d = Dreams.Instance;
                return d != null && (d.dreaming || d.dreamPrepared) && d.preset != null && IsPrologueDream(d.preset.name);
            }
        }

        // ------------------------------------------------------------ host

        /// <summary>Host: stable key → when it was sent the world with the prologue offered.</summary>
        private static readonly Dictionary<string, float> _pending = new Dictionary<string, float>(); // reset-in: Reset
        private static bool _holdLogged; // reset-in: Reset

        internal static void Reset()
        {
            _pending.Clear();
            _holdLogged = false;
            HoldCount = 0;
            _clientHold = 0;
            _pads.Clear();
            _padsFrame = -1;
        }

        /// <summary>Host shares the world in chapter 1 of a game that did not skip the prologue.</summary>
        internal static bool HostOffersPrologue()
        {
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            if (wg == null || wg.chapterID != 1)
                return false;
            return Core.currentProfile == null || !Core.currentProfile.skippedPrologue;
        }

        /// <summary>Host: this peer was sent the world with the prologue offered; it may be playing it offline.</summary>
        internal static void HostNoteShared(string stableKey)
        {
            if (string.IsNullOrEmpty(stableKey))
                return;
            _pending[stableKey] = Time.realtimeSinceStartup;
        }

        /// <summary>Host: this peer is back in the session (phase 3), done with any prologue.</summary>
        internal static void HostNoteArrived(string stableKey)
        {
            if (!string.IsNullOrEmpty(stableKey) && _pending.Remove(stableKey))
                ModLog.Event(LogCat.Session, "[Prologue] a player sent the world is in the session now");
        }

        /// <summary>Host: this peer will not come back from a prologue in this world (it returned with another).</summary>
        internal static void HostNoteGone(string stableKey)
        {
            if (!string.IsNullOrEmpty(stableKey) && _pending.Remove(stableKey))
                ModLog.Event(LogCat.Session, "[Prologue] a player sent the world came back with another — day 1 no longer waits for it");
        }

        /// <summary>Host: players still in a prologue (own included; offline joiners until they return or time out).</summary>
        internal static int HostPrologueCount()
        {
            int n = LocalInPrologue ? 1 : 0;
            if (_pending.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                List<string> stale = null;
                foreach (KeyValuePair<string, float> kv in _pending)
                {
                    if (now - kv.Value > PendingTimeoutSec)
                        (stale ?? (stale = new List<string>())).Add(kv.Key);
                    else
                        n++;
                }
                if (stale != null)
                {
                    foreach (string k in stale)
                        _pending.Remove(k);
                    ModLog.Event(LogCat.Session, "[Prologue] " + stale.Count + " joiner(s) never came back from the prologue — day 1 no longer waits for them");
                }
            }
            return n;
        }

        /// <summary>The clock vanilla wakes the player to when the prologue ends (<c>Dreams.endDreaming</c>).</summary>
        private const int PrologueWakeTime = 5;

        /// <summary>The world's first morning has not begun (vanilla wakes the player at 05 on day 1).</summary>
        internal static bool DayOneNotBegun()
        {
            Controller c = Singleton<Controller>.Instance;
            // A new game's clock reads 600 until the host's own prologue ends.
            return c != null && c.day <= 1 && (LocalInPrologue || c.CurrentTime <= PrologueWakeTime + DayOneStartSlack);
        }

        /// <summary>
        /// Host: the shared world's clock. While the host is in its own prologue its clock is the new
        /// game's placeholder; the world the others are in starts at the prologue's wake-up time.
        /// </summary>
        internal static int HostWorldTime(int overworldTime)
        {
            Controller c = Singleton<Controller>.Instance;
            return c != null && c.day <= 1 && LocalInPrologue ? PrologueWakeTime : overworldTime;
        }

        /// <summary>Host: players day 1 waits for at the last clock step (sent on TimeSync).</summary>
        internal static int HoldCount { get; private set; } // reset-in: Reset

        private static int _clientHold; // reset-in: Reset

        /// <summary>Client: the host's day-1 wait, shown once when it starts and when it ends.</summary>
        internal static void ClientNoteHold(byte count)
        {
            if ((count > 0) == (_clientHold > 0))
            {
                _clientHold = count;
                return;
            }
            _clientHold = count;
            ChatHud.AddLocalSystem(count > 0
                ? "Day 1 waits: " + count + " player(s) still in the prologue."
                : "Everyone is here — day 1 begins.");
        }

        /// <summary>Host: hold the clock — day 1 starts once nobody is in the prologue.</summary>
        internal static bool HoldDayOne(out int inPrologue)
        {
            inPrologue = 0;
            HoldCount = 0;
            // Hosting, connected peers or not: a joiner plays its prologue offline, and while it does
            // the host may have nobody connected at all.
            LanNetworkManager net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host)
                return false;
            if (!DayOneNotBegun())
                return false;
            inPrologue = HostPrologueCount();
            HoldCount = inPrologue;
            bool hold = inPrologue > 0;
            // Told only when it is about someone else: a host alone in its prologue is not waiting.
            bool tell = hold ? _pending.Count > 0 || net.IsConnected : _holdLogged;
            if (tell && hold != _holdLogged)
            {
                _holdLogged = hold;
                string line = hold
                    ? "Day 1 waits: " + inPrologue + " player(s) still in the prologue."
                    : "Everyone is here — day 1 begins.";
                ModLog.Event(LogCat.Session, "[Prologue] " + line);
                ChatHud.AddLocalSystem(line);
            }
            return hold;
        }

        /// <summary>
        /// Host in its own prologue: what it does on the prologue pads is not the shared world.
        /// These are sent by the host's own actions only (relays of clients' messages go out while
        /// a message is being applied and are not stopped), and none of them is world upkeep.
        /// </summary>
        internal static bool HostBlocksSend(NetMessageType type)
        {
            // Not after the prologue's end, while its pad is still being freed: by then the host is in
            // the hideout and its sends are the world's (its hideout entry among them). Objects still on
            // the pad then are kept out by IsOnProloguePad.
            if (LanNetworkManager.IsApplyingRemoteState || !LocalInPrologue)
                return false;
            switch (type)
            {
                case NetMessageType.JournalItem:
                case NetMessageType.CutsceneSync:
                case NetMessageType.ItemSpawn:
                case NetMessageType.DroppedItemSpawn:
                case NetMessageType.DroppedItemPickup:
                case NetMessageType.ThrowableSpawn:
                case NetMessageType.ExplosionSpawnObject:
                case NetMessageType.GasTrailSpawn:
                case NetMessageType.MapElementDiscovered:
                case NetMessageType.MapMarker:
                case NetMessageType.DialogTreeState:
                case NetMessageType.DialogNpcLock:
                case NetMessageType.DialogOutcomeSync:
                case NetMessageType.DreamItemPickup:
                case NetMessageType.DreamAudio:
                case NetMessageType.DreamEntered:
                case NetMessageType.PlayerScare:
                case NetMessageType.ExamineObject:
                case NetMessageType.WorldObjectRemoved:
                case NetMessageType.ContainerItem:
                case NetMessageType.LocationEnter:
                case NetMessageType.LocationExit:
                case NetMessageType.DragSync:
                case NetMessageType.ReputationSync:
                case NetMessageType.EntitySpawn:
                case NetMessageType.EntityBurning:
                case NetMessageType.DoorOpen:
                case NetMessageType.TrapTriggered:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// This machine's prologue pads while their objects live. Vanilla <c>endDreaming</c> clears the
        /// prologue state first and frees the pad seconds later (<c>Destroy(…, 4f)</c>); what still
        /// happens on it meanwhile is not the world's either.
        /// </summary>
        private static readonly List<Transform> _pads = new List<Transform>(2); // reset-in: Reset
        private static int _padsFrame = -1; // reset-in: Reset

        private static void TrackPads()
        {
            int frame = Time.frameCount;
            if (_padsFrame == frame)
                return;
            _padsFrame = frame;
            for (int i = _pads.Count - 1; i >= 0; i--)
                if (_pads[i] == null)
                    _pads.RemoveAt(i);
            if (!LocalInPrologue)
                return;
            OutsideLocations outs = Singleton<OutsideLocations>.Instance;
            if (outs != null && outs.spawnedLocations != null)
            {
                foreach (KeyValuePair<string, Location> kv in outs.spawnedLocations)
                    if (kv.Value != null && IsPrologueDream(kv.Key) && !_pads.Contains(kv.Value.transform))
                        _pads.Add(kv.Value.transform);
            }
            Dreams d = Dreams.Instance;
            if (d != null && d.dreamLocation != null && d.preset != null && IsPrologueDream(d.preset.name)
                && !_pads.Contains(d.dreamLocation.transform))
                _pads.Add(d.dreamLocation.transform);
        }

        /// <summary>One of this machine's prologue pads still exists (the prologue, or the seconds after its end).</summary>
        internal static bool ProloguePadAlive
        {
            get
            {
                TrackPads();
                return _pads.Count > 0;
            }
        }

        /// <summary>Under one of this machine's prologue pads (entities, GameEvents there are not the world's).</summary>
        internal static bool IsOnProloguePad(Transform t)
        {
            if (t == null)
                return false;
            TrackPads();
            for (int i = 0; i < _pads.Count; i++)
                if (_pads[i] != null && t.IsChildOf(_pads[i]))
                    return true;
            if (!LocalInPrologue)
                return false;
            // While its scene loads, a pad's objects wake (Character.Start) before vanilla lists
            // the pad as spawned or as the dream's: tell it by the location around them.
            Location loc = t.GetComponentInParent<Location>(true);
            while (loc != null)
            {
                if (IsPrologueDream(loc.name))
                    return true;
                Transform up = loc.transform.parent;
                loc = up != null ? up.GetComponentInParent<Location>(true) : null;
            }
            return false;
        }

        // ------------------------------------------------------------ joiner

        private enum JoinerStage { None, Preparing, Intro, Playing, Arrived }

        /// <summary>
        /// The host's world package offered the prologue (chapter 1, not skipped). Kept across the
        /// network stop of join phase 2: the load that reads it runs offline.
        /// </summary>
        private static bool _offered;             // process-scoped: one world package, cleared in ClearJoiner
        private static string _offeredCampaign;   // process-scoped: one world package, cleared in ClearJoiner
        /// <summary>The package is a join from the title (not a chapter change of a running session).</summary>
        private static bool _joinPackage;         // process-scoped: one world package, cleared in ClearJoiner
        private static JoinerStage _joinerStage;  // process-scoped: one offline prologue per load, cleared in AbandonJoiner
        private static bool _freshCharacter;      // process-scoped: set by the join load, cleared in AbandonJoiner
        private static float _stageAt;            // process-scoped: joiner stage clock
        private static bool _introVideoSeen;      // process-scoped: joiner intro poll
        private static bool _wakeFadeDone;        // process-scoped: joiner wake fade, once per prologue
        private static float _arrivedAt = -1f;    // process-scoped: joiner home settle clock

        internal static void NoteOffered(bool offered, string campaignId, bool joinFromTitle)
        {
            _offered = offered;
            _offeredCampaign = campaignId;
            _joinPackage = joinFromTitle;
        }

        /// <summary>The join load is for a player new to this world: its character is not loaded from the save.</summary>
        internal static bool FreshCharacter => _freshCharacter;

        internal static bool JoinerActive => _joinerStage != JoinerStage.None && _joinerStage != JoinerStage.Arrived;

        /// <summary>The joiner's opening is on screen or about to be: the pad arriving, or the title and movie.</summary>
        internal static bool JoinerBeforeWake => _joinerStage == JoinerStage.Preparing || _joinerStage == JoinerStage.Intro;
        internal static bool JoinerArrived => _joinerStage == JoinerStage.Arrived;

        /// <summary>
        /// Join load (phase 2), before the save's player state is applied: a player this machine has
        /// never had a character for in this campaign (no own snapshot of it) is new. It starts with a
        /// new-game character, not the host's (the save's player block is the host's: level, skills,
        /// bag, all copied), and plays the prologue when the host's game has one.
        /// </summary>
        internal static bool DecideFreshAtLoad()
        {
            if (!_joinPackage || !ChapterSessionResume.IsPending || ChapterSessionResume.WasHost)
            {
                // Any other load (single player, hosting, after leaving a join half-way): what a
                // join left behind is over, or that load lost the save's oven and dream state.
                if (_joinerStage != JoinerStage.None || _freshCharacter)
                    ClearJoiner();
                return false;
            }
            if (_joinerStage != JoinerStage.None)
                return _freshCharacter;
            bool snapshot = true;
            _prologueDone = false;
            try
            {
                string path = ClientStateBackup.GetLocalSelfBackupPath();
                snapshot = System.IO.File.Exists(path);
                _prologueDone = System.IO.File.Exists(path + KnownSuffix);
            }
            catch (Exception ex) { ModLog.Warn(LogCat.Session, "[Prologue] backup lookup failed: " + ex.Message); }
            // No snapshot of its own: the save's player block is the host's, never this player's.
            _freshCharacter = !snapshot;
            ModLog.Event(LogCat.Session, !_freshCharacter
                ? "[Prologue] a character of this player exists for this world — loading as usual"
                : PlaysPrologue
                    ? "[Prologue] new to this world (campaign " + (_offeredCampaign ?? "?") + ") — fresh character, own prologue before joining"
                    : _prologueDone
                        ? "[Prologue] played the prologue here before, no character saved since — fresh character in the hideout"
                        : "[Prologue] new to this world — fresh character in the hideout (the game skipped the prologue)");
            return _freshCharacter;
        }

        /// <summary>This player played the prologue in this campaign already (its marker, no snapshot yet).</summary>
        private static bool _prologueDone; // process-scoped: one world package, cleared in ClearJoiner

        /// <summary>The load in progress is a join's, for a character new to the world.</summary>
        internal static bool FreshCharacterLoad
            => _freshCharacter && _joinPackage && ChapterSessionResume.IsPending && !ChapterSessionResume.WasHost;

        /// <summary>
        /// Next to the character snapshot: this player has a character in the campaign. A snapshot
        /// is only written once the character has some progress, so a player who finished the
        /// prologue and left at once would otherwise count as new again and replay it.
        /// </summary>
        private const string KnownSuffix = ".known";

        private static void MarkKnown()
        {
            try
            {
                string path = ClientStateBackup.GetLocalSelfBackupPath() + KnownSuffix;
                if (!System.IO.File.Exists(path))
                    System.IO.File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex) { ModLog.Warn(LogCat.Session, "[Prologue] marking the character failed: " + ex.Message); }
        }

        /// <summary>A fresh character plays the prologue (the host's game has one).</summary>
        internal static bool PlaysPrologue => _freshCharacter && _offered && !_prologueDone;

        /// <summary>
        /// A fresh character that does not play the prologue now: in the hideout with the chapter's
        /// starting pack, as vanilla world generation leaves a new character that skipped it (the
        /// host skipped it, or a later chapter). A player who played it here before (its marker, no
        /// character saved since) also gets the prologue's reward; which way that prologue ended was
        /// never saved, so it is the outcome vanilla falls back to (<see cref="GrantPrologueReward"/>).
        /// </summary>
        internal static void ArriveFresh()
        {
            Player p = Player.Instance;
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            bool playedBefore = _offered && _prologueDone;
            _joinerStage = JoinerStage.Arrived;
            _freshCharacter = false;
            MarkKnown();
            if (p == null)
                return;
            try
            {
                ApplyChapterStart(p, wg);
                Location home = wg != null && wg.playerBase != null ? wg.playerBase.GetComponent<Location>() : null;
                if (home == null || home.playerSpawn == null)
                {
                    ModLog.Warn(LogCat.Session, "[Prologue] no hideout spawn — fresh character stays where the save put it");
                    if (playedBefore)
                        GrantPrologueReward(p);
                    return;
                }
                p.teleportTo(home.playerSpawn.transform.position, Quaternion.Euler(90f, 0f, 0f));
                // After the move home, as vanilla endDreaming: what does not fit drops at the bed.
                if (playedBefore)
                    GrantPrologueReward(p);
                OutsideLocations outs = Singleton<OutsideLocations>.Instance;
                if (outs != null && outs.playerInOutsideLocation)
                    outs.returningOnTeleportedPlayer();
                else if (Singleton<WorldGrid>.Instance != null)
                    Singleton<WorldGrid>.Instance.refreshPosition(p.transform.position, instant: true, force: true);
                ModLog.Event(LogCat.Session, "[Prologue] fresh character placed in the hideout");
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Session, "[Prologue] placing the fresh character failed: " + ex.Message);
            }
        }

        /// <summary>
        /// A new character's start in this chapter, as vanilla world generation gives it
        /// (<c>ChapterPreset.initInventory</c> / <c>initPlayer</c>): the chapter's starting pack and
        /// level. The workbench level there is the world's, and the world has one already.
        /// </summary>
        private static void ApplyChapterStart(Player p, WorldGenerator wg)
        {
            p.Hotbar.clear();
            p.Inventory.clear();
            ChapterPreset cp = wg != null ? wg.chapterPreset : null;
            if (cp == null)
                return;
            cp.initInventory();
            if (cp.playerLevel > 0 && p.levelRequirements != null && cp.playerLevel - 1 < p.levelRequirements.Count)
                p.experience = p.levelRequirements[cp.playerLevel - 1];
        }

        /// <summary>The prologue dream whose end gives its reward.</summary>
        private const string ProloguePresetWithReward = "dream_tutorial_01";

        /// <summary>
        /// Vanilla endDreaming of dream_tutorial_01 for its "default" outcome: that outcome's bag
        /// items. "default" is what vanilla getOutcome falls back to, and it gives the same as "hit",
        /// the prologue's story ending (outcome_hit_dream_tutorial_01).
        /// </summary>
        private static void GrantPrologueReward(Player p)
        {
            Dreams d = Dreams.Instance;
            DreamPreset preset = null;
            try { preset = d != null ? d.getPreset(ProloguePresetWithReward) : null; }
            catch (KeyNotFoundException) { }
            DreamPreset.Outcome outcome = null;
            if (preset != null && preset.outcomes != null)
            {
                for (int i = 0; i < preset.outcomes.Count && outcome == null; i++)
                    if (preset.outcomes[i] != null && preset.outcomes[i].name == "default")
                        outcome = preset.outcomes[i];
            }
            if (outcome == null || outcome.effects == null)
            {
                ModLog.Warn(LogCat.Session, "[Prologue] no prologue outcome to reward the returning player from");
                return;
            }
            int given = 0;
            for (int i = 0; i < outcome.effects.Count; i++)
            {
                DreamPreset.Outcome.Effect e = outcome.effects[i];
                if (e == null || e.type != DreamPreset.Outcome.Effect.Type.createInvItem)
                    continue;
                GameObject go = e.invItem as GameObject;
                InvItem item = go != null ? go.GetComponent<InvItem>() : null;
                if (item == null)
                    continue;
                p.Inventory.addItemTypeToPlayer(item.type, e.amount, dropIfNoRoom: true);
                given += e.amount;
            }
            ModLog.Event(LogCat.Session, "[Prologue] played the prologue before — its reward given (" + given + " item(s))");
        }

        private static float _readySince = -1f; // process-scoped: joiner load settle clock, cleared in ClearJoiner

        /// <summary>
        /// The join load is completely over: vanilla's own end of load (tweenLoading, activatePlayer)
        /// has run. Load clears <c>Core.loadingGame</c> before it, and starting the prologue then
        /// sent that pending tweenLoading down the new-game branch with no prologue pad.
        /// </summary>
        internal static bool ReadyToBegin()
        {
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            bool ready = !Core.loadingGame && Core.coreStarted && !Core.forbidInputs
                && wg != null && !wg.playingIntro && Player.Instance != null;
            if (!ready)
            {
                _readySince = -1f;
                return false;
            }
            float now = Time.realtimeSinceStartup;
            if (_readySince < 0f)
                _readySince = now;
            return now - _readySince >= 1f;
        }

        /// <summary>Join phase 2 finished loading (still offline): start this player's own prologue.</summary>
        internal static void BeginJoiner()
        {
            Dreams d = Dreams.Instance;
            Player p = Player.Instance;
            UI ui = Singleton<UI>.Instance;
            if (d == null || p == null || ui == null)
                return;
            ModLog.Event(LogCat.Session, "[Prologue] starting this player's own prologue (offline)");
            _joinerStage = JoinerStage.Preparing;
            _stageAt = Time.realtimeSinceStartup;
            _introVideoSeen = false;
            Core.forbidInputs = true;
            ui.blackScreen.SetActive(true);
            ui.blackScreen.GetComponent<tk2dSprite>().color = new Color(0f, 0f, 0f, 1f);
            // The loaded save may list the host's prologue pads among its spawned locations: the
            // names come back with no object (a pad is not saved), and going there ended on a null
            // pad. Clear them so the prologue spawns its own, as in a new game.
            ForgetProloguePads();
            SnapshotJournal();
            SnapshotWorldFlags();
            // The prologue keeps the pack it began with (vanilla copies it at the dream's start and
            // gives it back at its end): a new character's, not the host's from the save.
            ApplyChapterStart(p, Singleton<WorldGenerator>.Instance);
            // As a new game: firstPlay, the first prologue dream, then the movie (WorldGenerator
            // onCreatedAllChunks → tweenLoading → activatePlayer).
            p.firstPlay = true;
            d.wantToDream = true;
            d.StartCoroutine(d.prepareDream("dream_tutorial_00"));
        }

        private static void ForgetProloguePads()
        {
            OutsideLocations outs = Singleton<OutsideLocations>.Instance;
            if (outs == null || outs.spawnedLocations == null)
                return;
            foreach (string name in new[] { "dream_tutorial_00", "dream_tutorial_01" })
            {
                if (!outs.spawnedLocations.TryGetValue(name, out Location pad))
                    continue;
                // destroyLocation's own steps, without its error lines for the parts never made.
                WorldGrid.Grid grid = Singleton<WorldGrid>.Instance != null ? Singleton<WorldGrid>.Instance.getGrid(name) : null;
                if (grid != null)
                    Singleton<WorldGrid>.Instance.grids.Remove(grid);
                Pathfinding.NavGraph graph = AstarPath.active != null ? AstarPath.active.astarData.GetGraph(name) : null;
                if (graph != null)
                    AstarPath.active.astarData.RemoveGraph(graph);
                outs.spawnedLocations.Remove(name);
                if (pad != null)
                    UnityEngine.Object.Destroy(pad.gameObject);
                ModLog.Event(LogCat.Session, "[Prologue] dropped the saved prologue pad " + name + (pad == null ? " (no object)" : ""));
            }
        }

        /// <summary>Joiner stages; true once it woke in the hideout and may reconnect.</summary>
        internal static bool TickJoiner()
        {
            if (_joinerStage == JoinerStage.None)
                return true;
            if (_joinerStage == JoinerStage.Arrived)
                return true;
            float now = Time.realtimeSinceStartup;
            Dreams d = Dreams.Instance;
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            Player p = Player.Instance;
            if (d == null || wg == null || p == null)
                return false;
            switch (_joinerStage)
            {
                case JoinerStage.Preparing:
                    // In the pad and its arrival done (OutsideLocations clears loading and unlocks
                    // input at the end; the movie starts after that, locked).
                    OutsideLocations outs = Singleton<OutsideLocations>.Instance;
                    if (!d.dreaming || (outs != null && outs.loading))
                    {
                        // Once in the dream its pad is arriving (a stall there is vanilla's own).
                        if (!d.dreaming && now - _stageAt > 60f)
                        {
                            ModLog.Warn(LogCat.Session, "[Prologue] the prologue pad never came up — joining without it");
                            AbandonJoiner(p);
                            return true;
                        }
                        return false;
                    }
                    _joinerStage = JoinerStage.Intro;
                    _stageAt = now;
                    PrologueIntro.Play(wg);
                    return false;

                case JoinerStage.Intro:
                    // The movie ends by itself or is skipped (vanilla skipCurrentMovie runs activatePlayer,
                    // and only from a locked start, as vanilla's own intro is).
                    if (wg.playingIntro && !Core.forbidInputs)
                        Core.forbidInputs = true;
                    if (wg.playingIntro && !PrologueIntro.Finished(ref _introVideoSeen))
                        return false;
                    if (wg.playingIntro)
                        PrologueIntro.Wake(wg);
                    _joinerStage = JoinerStage.Playing;
                    _stageAt = now;
                    _wakeFadeDone = false;
                    return false;

                case JoinerStage.Playing:
                    // activatePlayer's wake-up leaves the screen white; vanilla's startDreaming fades it
                    // (one frame after it ran). In a new game the pad arrives after the movie, so that
                    // fade comes after the wake. Here the pad arrived before the movie: same last step.
                    if (!_wakeFadeDone && now - _stageAt > 0.5f)
                    {
                        _wakeFadeDone = true;
                        UI ui = Singleton<UI>.Instance;
                        if (ui != null && ui.blackScreenTop != null && ui.blackScreenTop.activeInHierarchy
                            && ui.blackScreenTop.GetComponent<tk2dBaseSprite>().color.a != 0f)
                            ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f);
                    }
                    // Vanilla endDreaming of dream_tutorial_01: firstPlay off, in the hideout, awake.
                    OutsideLocations outsNow = Singleton<OutsideLocations>.Instance;
                    if (p.firstPlay || d.dreaming || d.dreamPrepared || wg.playingIntro || p.endingSleep
                        || (outsNow != null && outsNow.loading))
                    {
                        _arrivedAt = -1f;
                        return false;
                    }
                    if (_arrivedAt < 0f)
                        _arrivedAt = now;
                    if (now - _arrivedAt < 2f)
                        return false;
                    _joinerStage = JoinerStage.Arrived;
                    _freshCharacter = false;
                    MarkKnown();
                    RestoreWorldFlags();
                    _journalToShare.Clear();
                    _journalToShare.AddRange(JournalGainedSinceSnapshot());
                    ModLog.Event(LogCat.Session, "[Prologue] prologue done — joining the session"
                        + (_journalToShare.Count > 0 ? " (" + _journalToShare.Count + " journal page(s) to share)" : ""));
                    return true;
            }
            return false;
        }

        private static void AbandonJoiner(Player p)
        {
            // The pad may still arrive: vanilla onLocationSpawned starts the dream only while it is
            // prepared, and it must not start once this player is back online.
            Dreams d = Dreams.Instance;
            if (d != null)
            {
                d.wantToDream = false;
                d.dreamPrepared = false;
            }
            if (p != null)
                p.firstPlay = false;
            Core.forbidInputs = false;
            UI ui = Singleton<UI>.Instance;
            if (ui != null && ui.blackScreen != null)
                ui.blackScreen.SetActive(false);
            RestoreWorldFlags();
            // Where the prologue would have left it.
            ArriveFresh();
        }

        // ---------------------------------------------------- world flags after the prologue

        /// <summary>
        /// The joiner's world flags before its prologue: the host's world, from its save. Offline,
        /// FlagSync sends nothing the prologue sets, and the reconnect's flag bulk only overwrites
        /// flags the host's world has an entry for, so doctor_dogKilled from this player's own
        /// tutorial dog stayed true here while the host's world never had it.
        /// </summary>
        private static readonly Dictionary<string, KeyValuePair<bool, int>> _flagsBefore = new Dictionary<string, KeyValuePair<bool, int>>(); // process-scoped: one prologue, filled in BeginJoiner
        private static bool _flagsSnapshot; // process-scoped: one prologue, cleared in ClearJoiner

        private static void SnapshotWorldFlags()
        {
            _flagsBefore.Clear();
            Flags flags = Singleton<Flags>.Instance;
            _flagsSnapshot = flags != null && flags.flagsDict != null;
            if (!_flagsSnapshot)
                return;
            foreach (KeyValuePair<string, Flags.Flag> kv in flags.flagsDict)
                if (kv.Value != null)
                    _flagsBefore[kv.Key] = new KeyValuePair<bool, int>(kv.Value.isTrue, kv.Value.amount);
        }

        /// <summary>
        /// The prologue is this player's own: every world flag it changed goes back to the host's
        /// world's value (one it added, to unset). This player's own flags (where it stands, its
        /// help popups and the like: <c>PerPlayerFlagPolicy</c>, never synced) stay as it woke.
        /// </summary>
        private static void RestoreWorldFlags()
        {
            if (!_flagsSnapshot)
                return;
            _flagsSnapshot = false;
            Flags flags = Singleton<Flags>.Instance;
            int restored = 0;
            if (flags != null && flags.flagsDict != null)
            {
                foreach (KeyValuePair<string, Flags.Flag> kv in flags.flagsDict)
                {
                    Flags.Flag f = kv.Value;
                    if (f == null || PerPlayerFlagPolicy.IsPerPlayer(kv.Key))
                        continue;
                    KeyValuePair<bool, int> before;
                    if (!_flagsBefore.TryGetValue(kv.Key, out before))
                        before = new KeyValuePair<bool, int>(false, 0);
                    if (f.isTrue == before.Key && f.amount == before.Value)
                        continue;
                    f.isTrue = before.Key;
                    f.amount = before.Value;
                    restored++;
                }
            }
            _flagsBefore.Clear();
            if (restored > 0)
                ModLog.Event(LogCat.Session, "[Prologue] " + restored + " world flag(s) the prologue set put back to the host's world's");
        }

        // ---------------------------------------------------- journal after the prologue

        /// <summary>
        /// The journal is shared, and the join bulk only adds the host's pages to a joiner's. The
        /// pages a joiner's prologue wrote (offline) would stay its own: once back in the session
        /// it sends them as the live journal sync does (JournalItem), every player having the same
        /// prologue pages anyway.
        /// </summary>
        private static readonly List<KeyValuePair<JournalItemKind, string>> _journalToShare = new List<KeyValuePair<JournalItemKind, string>>(); // process-scoped: one prologue, sent once after the reconnect
        private static readonly HashSet<string> _journalBefore = new HashSet<string>(); // process-scoped: one prologue, filled in BeginJoiner

        private static IEnumerable<KeyValuePair<JournalItemKind, string>> JournalPages()
        {
            Journal j = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.journal : null;
            if (j == null)
                yield break;
            foreach (string k in j.notesDict.Keys) yield return new KeyValuePair<JournalItemKind, string>(JournalItemKind.Note, k);
            foreach (string k in j.keysDict.Keys) yield return new KeyValuePair<JournalItemKind, string>(JournalItemKind.Key, k);
            foreach (string k in j.itemsDict.Keys) yield return new KeyValuePair<JournalItemKind, string>(JournalItemKind.QuestItem, k);
            foreach (string k in j.journalEntriesDict.Keys) yield return new KeyValuePair<JournalItemKind, string>(JournalItemKind.JournalEntry, k);
            foreach (string k in j.locationsDict.Keys) yield return new KeyValuePair<JournalItemKind, string>(JournalItemKind.Location, k);
        }

        private static void SnapshotJournal()
        {
            _journalBefore.Clear();
            _journalToShare.Clear();
            foreach (KeyValuePair<JournalItemKind, string> page in JournalPages())
                _journalBefore.Add((int)page.Key + ":" + page.Value);
        }

        private static List<KeyValuePair<JournalItemKind, string>> JournalGainedSinceSnapshot()
        {
            var gained = new List<KeyValuePair<JournalItemKind, string>>();
            foreach (KeyValuePair<JournalItemKind, string> page in JournalPages())
                if (!_journalBefore.Contains((int)page.Key + ":" + page.Value))
                    gained.Add(page);
            return gained;
        }

        /// <summary>Client tick: back in the session after the prologue, share its journal pages.</summary>
        internal static void TickClient(LanNetworkManager net)
        {
            if (_journalToShare.Count == 0 || net == null || net.Role != NetworkRole.Client || !net.IsHandshakeComplete)
                return;
            foreach (KeyValuePair<JournalItemKind, string> page in _journalToShare)
                JournalSyncHelpers.SendJournalItem(page.Key, page.Value);
            ModLog.Event(LogCat.Session, "[Prologue] shared " + _journalToShare.Count + " journal page(s) from the prologue");
            _journalToShare.Clear();
        }

        /// <summary>A new join load starts over (the previous one reached the session or was abandoned).</summary>
        internal static void ClearJoiner()
        {
            _offered = false;
            _offeredCampaign = null;
            _joinPackage = false;
            _readySince = -1f;
            _joinerStage = JoinerStage.None;
            _freshCharacter = false;
            _prologueDone = false;
            _introVideoSeen = false;
            // A join that never got back to its session: its pages are not another host's.
            _journalToShare.Clear();
            _journalBefore.Clear();
            _flagsBefore.Clear();
            _flagsSnapshot = false;
        }
    }
}

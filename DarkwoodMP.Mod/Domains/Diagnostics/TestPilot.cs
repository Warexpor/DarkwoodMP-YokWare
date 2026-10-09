using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Unattended test driver, for automated dual-box runs. Off unless the game is started with
    /// the environment variable <c>DWMP_PILOT</c>: <c>host:N</c> hosts LAN and loads profile N,
    /// <c>join</c> joins 127.0.0.1 (the world copy goes to profile <c>DWMP_PILOT_SLOT</c>, default
    /// 1) and enters the world. In the world it runs commands appended to <c>pilot/cmd.txt</c>
    /// next to the game's data folder and writes what they did to <c>pilot/out.txt</c> and the log
    /// (<c>[Pilot]</c>). Commands go through the same game functions a player's action would.
    /// </summary>
    internal static class TestPilot
    {
        private static readonly string Mode = (Environment.GetEnvironmentVariable("DWMP_PILOT") ?? "").Trim(); // process-scoped: read once at start
        internal static bool Active => Mode.Length > 0;

        private enum Stage { Boot, Hosting, Loading, Joining, InWorld }

        private static Stage _stage = Stage.Boot;   // process-scoped: the pilot drives one process run
        private static float _stageAt;              // process-scoped: pilot run clock
        private static float _nextStepAt;           // process-scoped: pilot run clock
        private static int _menuStep;               // process-scoped: pilot host menu sequence
        private static long _cmdOffset;             // process-scoped: bytes of cmd.txt already run
        private static float _waitUntil;            // process-scoped: "wait" command
        private static string _dir;                 // process-scoped: pilot folder
        private static bool _hooked;                // process-scoped: log hook installed once
        private static readonly Dictionary<string, int> _errorCounts = new Dictionary<string, int>(); // process-scoped: pilot run error tally
        private static readonly List<GameEvents> _events = new List<GameEvents>(); // process-scoped: last "events" listing
        private static readonly List<NPC> _talkQueue = new List<NPC>(); // process-scoped: "talkall" queue
        private static NPC _talking;                // process-scoped: the NPC "talk" is walking through
        private static float _talkAt;               // process-scoped: when that talk began
        private static int _talkSteps;              // process-scoped: clicks in that talk
        private static int _talked;                 // process-scoped: talks run since the last "talkall"
        private static readonly HashSet<string> _visited = new HashSet<string>(); // process-scoped: dialogue options already picked
        private static readonly Dictionary<string, int> _decisionTurns = new Dictionary<string, int>(); // process-scoped: next branch per decision
        private static string[] _atCmd;             // process-scoped: "at" command waiting for its moment
        private static long _atMs;                  // process-scoped: when (Unix ms) it runs
        private static readonly List<KeyValuePair<string, Vector3>> _sounds = new List<KeyValuePair<string, Vector3>>(); // process-scoped: "audio" recording
        private static float _soundsUntil;          // process-scoped: "audio" recording end

        /// <summary>A sound started (PilotAudioTap), kept while "audio" records.</summary>
        internal static void NoteSound(string name, Vector3 at)
        {
            if (Time.realtimeSinceStartup < _soundsUntil && _sounds.Count < 20000)
                _sounds.Add(new KeyValuePair<string, Vector3>(name ?? "?", at));
        }

        /// <summary>Each distinct Unity error or exception: the first three with their stack, then a count.</summary>
        private static void OnUnityLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;
            string key = message != null && message.Length > 160 ? message.Substring(0, 160) : message ?? "";
            _errorCounts.TryGetValue(key, out int n);
            _errorCounts[key] = ++n;
            if (n <= 3)
                Out("unity " + type + " #" + n + ": " + key + "\n    " + (stack ?? "").Replace("\n", "\n    "));
            else if (n == 100 || n == 1000 || n == 10000)
                Out("unity " + type + " x" + n + ": " + key);
        }

        private static string Dir
        {
            get
            {
                if (_dir == null)
                {
                    _dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "pilot");
                    Directory.CreateDirectory(_dir);
                }
                return _dir;
            }
        }

        internal static void Tick(LanNetworkManager net)
        {
            if (!Active || net == null)
                return;
            try
            {
                Step(net);
            }
            catch (Exception ex)
            {
                Out("pilot error: " + ex.GetType().Name + ": " + ex.Message);
                _nextStepAt = Time.realtimeSinceStartup + 2f;
            }
        }

        private static void Enter(Stage s)
        {
            _stage = s;
            _stageAt = Time.realtimeSinceStartup;
            Out("stage " + s);
        }

        private static void Step(LanNetworkManager net)
        {
            float now = Time.realtimeSinceStartup;
            // "at": checked every frame, so two games told the same moment act within a frame.
            if (_atCmd != null && _stage == Stage.InWorld && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _atMs)
            {
                string[] cmd = _atCmd;
                _atCmd = null;
                Out("  at " + _atMs + ": " + string.Join(" ", cmd));
                Run(net, cmd);
            }
            if (now < _nextStepAt)
                return;
            _nextStepAt = now + 0.25f;

            switch (_stage)
            {
                case Stage.Boot:
                    if (!_hooked)
                    {
                        _hooked = true;
                        Application.runInBackground = true;
                        Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
                        Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
                        Application.logMessageReceived += OnUnityLog;
                    }
                    // The title menu is up and has settled.
                    if (!Core.mainMenu || Singleton<MainMenu>.Instance == null || now < 8f)
                        return;
                    if (Mode.StartsWith("host", StringComparison.OrdinalIgnoreCase)
                        || Mode.StartsWith("newgame", StringComparison.OrdinalIgnoreCase))
                    {
                        net.StartHost(ModConfig.GetConnectPort());
                        Out("StartHost → " + net.Role + " (" + net.StatusText + ")");
                        _menuStep = 0;
                        Enter(Stage.Hosting);
                    }
                    else if (Mode.Equals("join", StringComparison.OrdinalIgnoreCase))
                    {
                        Enter(Stage.Joining);
                    }
                    else
                    {
                        Out("unknown DWMP_PILOT '" + Mode + "'");
                        _nextStepAt = float.MaxValue;
                    }
                    return;

                case Stage.Hosting:
                    HostMenu(net, now);
                    return;

                case Stage.Joining:
                    JoinStep(net);
                    return;

                case Stage.Loading:
                    if (!Core.mainMenu && !Core.loadingGame && Core.coreStarted && Core.worldGenFinished()
                        && Player.Instance != null && now - _stageAt > 5f)
                        Enter(Stage.InWorld);
                    return;

                case Stage.InWorld:
                    _nextStepAt = now + 0.5f;
                    if (_talking != null || _talkQueue.Count > 0)
                    {
                        _nextStepAt = now + 0.35f;
                        TalkTick(now);
                        return;
                    }
                    if (now >= _waitUntil)
                        RunCommands(net);
                    return;
            }
        }

        private static void HostMenu(LanNetworkManager net, float now)
        {
            int profile = 1;
            int colon = Mode.IndexOf(':');
            if (colon > 0)
                int.TryParse(Mode.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out profile);
            MainMenu menu = Singleton<MainMenu>.Instance;
            switch (_menuStep)
            {
                case 0:
                    menu.displayProfilesMenu();
                    _menuStep = 1;
                    _nextStepAt = now + 2f;
                    return;
                case 1:
                    if (Mode.StartsWith("newgame", StringComparison.OrdinalIgnoreCase))
                    {
                        // A new game with the prologue in an EMPTY slot (displayProfile on an inactive
                        // slot clears it, as the menu does).
                        if (Core.profiles.Count < profile || Core.profiles[profile - 1].Active)
                        {
                            Out("newgame needs an empty profile slot; " + profile + " is in use");
                            _nextStepAt = float.MaxValue;
                            return;
                        }
                        menu.displayProfile(profile);
                        // "newgameskip:N" starts with the prologue skipped (the menu's toggle).
                        Singleton<Controller>.Instance.skipTutorial = Mode.StartsWith("newgameskip", StringComparison.OrdinalIgnoreCase);
                        _menuStep = 3;
                        Out("new game " + (Singleton<Controller>.Instance.skipTutorial ? "skipping" : "with") + " the prologue in profile " + profile);
                        Singleton<UI>.Instance.StartCoroutine(Singleton<UI>.Instance.initNewGame());
                        Enter(Stage.Loading);
                        return;
                    }
                    if (Core.profiles.Count < profile || !Core.profiles[profile - 1].Active)
                    {
                        Out("profile " + profile + " missing or empty (" + Core.profiles.Count + " profiles)");
                        _nextStepAt = now + 5f;
                        return;
                    }
                    menu.displayProfile(profile);
                    _menuStep = 2;
                    _nextStepAt = now + 1.5f;
                    return;
                case 2:
                    Out("loading profile " + profile + " (day " + Core.currentProfile.day + ")");
                    Singleton<UI>.Instance.StartCoroutine(Singleton<UI>.Instance.initLoadGame());
                    _menuStep = 3;
                    Enter(Stage.Loading);
                    return;
            }
        }

        private static void JoinStep(LanNetworkManager net)
        {
            WorldSaveShareService share = net.WorldSaveShare;
            if (!Core.mainMenu)
            {
                // Entering the world: the load has started.
                Enter(Stage.Loading);
                return;
            }
            _nextStepAt = Time.realtimeSinceStartup + 2f;
            if (share != null && share.IsAwaitingSlotPick)
            {
                int slot = 1;
                int.TryParse(Environment.GetEnvironmentVariable("DWMP_PILOT_SLOT") ?? "1", NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out slot);
                bool ok = share.TryCommitPermanentSlot(slot, true, out string err);
                Out("slot " + slot + " commit: " + (ok ? "ok" : err));
                return;
            }
            MainMenuMultiplayerInject.PilotJoinLan();
            Out("join step: role=" + net.Role + " " + net.StatusText);
        }

        // ------------------------------------------------------------ commands

        private static void RunCommands(LanNetworkManager net)
        {
            string path = Path.Combine(Dir, "cmd.txt");
            if (!File.Exists(path))
                return;
            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < _cmdOffset)
                    _cmdOffset = 0; // file was replaced
                if (fs.Length == _cmdOffset)
                    return;
                fs.Seek(_cmdOffset, SeekOrigin.Begin);
                buf = new byte[fs.Length - _cmdOffset];
                int read = fs.Read(buf, 0, buf.Length);
                if (read < buf.Length)
                    Array.Resize(ref buf, read);
            }
            // Whole lines only (a partial last line waits for its newline); a "wait" stops the
            // batch and the lines after it run once it is over.
            long baseOffset = _cmdOffset;
            int start = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] != (byte)'\n')
                    continue;
                string line = Encoding.UTF8.GetString(buf, start, i - start).Trim();
                start = i + 1;
                _cmdOffset = baseOffset + start;
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                Out("> " + line);
                try
                {
                    Run(net, line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception ex)
                {
                    Out("  failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                if (Time.realtimeSinceStartup < _waitUntil)
                    return;
            }
        }

        private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static void Run(LanNetworkManager net, string[] a)
        {
            Player p = Player.Instance;
            switch (a[0].ToLowerInvariant())
            {
                case "status":
                    Status(net);
                    return;
                case "wait":
                    _waitUntil = Time.realtimeSinceStartup + F(a[1]);
                    return;
                case "at":
                    // "at <unix ms> <command...>": run the command at that moment (races between games).
                    _atMs = long.Parse(a[1], CultureInfo.InvariantCulture);
                    _atCmd = new string[a.Length - 2];
                    Array.Copy(a, 2, _atCmd, 0, a.Length - 2);
                    Out("  queued for " + _atMs);
                    return;
                case "tp":
                    p.teleportTo(new Vector3(F(a[1]), p.transform.position.y, F(a[2])), p.transform.rotation);
                    Out("  at " + Pos(p.transform.position));
                    return;
                case "tpp":
                {
                    RemotePlayerProxy proxy = net.GetProxy(int.Parse(a[1], CultureInfo.InvariantCulture));
                    if (proxy == null) { Out("  no player " + a[1]); return; }
                    Vector3 to = proxy.transform.position + new Vector3(1.5f, 0f, 0f);
                    p.teleportTo(to, p.transform.rotation);
                    Out("  at " + Pos(p.transform.position));
                    return;
                }
                case "god":
                    p.invulnerable = a.Length < 2 || a[1] != "0";
                    Out("  invulnerable=" + p.invulnerable);
                    return;
                case "die":
                    // A killing hit, as a creature's would be (die() alone skips the damage path).
                    p.invulnerable = false;
                    p.getHit(p.maxHealth * 2f);
                    Out("  hp=" + Mathf.RoundToInt(p.health) + " alive=" + p.alive);
                    return;
                case "hitp":
                {
                    // "hitp <player id> <damage>": this player's melee hit on that player's stand-in
                    // (what MeleeSensor does on a swing that lands: friendly fire).
                    RemotePlayerProxy proxy = net.GetProxy(int.Parse(a[1], CultureInfo.InvariantCulture));
                    CharBase cb = proxy != null ? proxy.CachedCharBase : null;
                    if (cb == null) { Out("  no player " + a[1]); return; }
                    cb.getHit(F(a[2]), p.transform, false, true, true);
                    Out("  hit p" + a[1] + " for " + a[2] + " from " + Pos(p.transform.position));
                    return;
                }
                case "hurt":
                    p.getHit(F(a[1]));
                    Out("  hp=" + Mathf.RoundToInt(p.health));
                    return;
                case "time":
                    if (net.Role != NetworkRole.Host) { Out("  host only"); return; }
                    // A jump inside the day only: the per-minute edges in between (dawn, nightfall)
                    // are skipped, as they would be for any clock set by hand.
                    Singleton<Controller>.Instance.CurrentTime = int.Parse(a[1], CultureInfo.InvariantCulture);
                    Singleton<Controller>.Instance.refreshTimeNoLogic();
                    net.SendTimeSyncTo(-1);
                    Out("  time=" + Singleton<Controller>.Instance.CurrentTime);
                    return;
                case "prologue":
                {
                    Dreams d = Dreams.Instance;
                    WorldGenerator wg = Singleton<WorldGenerator>.Instance;
                    Out("  local=" + PersonalPrologue.LocalInPrologue + " firstPlay=" + p.firstPlay
                        + " intro=" + (wg != null && wg.playingIntro)
                        + " dreaming=" + (d != null && d.dreaming) + " preset=" + (d != null && d.preset != null ? d.preset.name : "-")
                        + " fresh=" + PersonalPrologue.FreshCharacter + " joiner=" + PersonalPrologue.JoinerActive
                        + " hold=" + PersonalPrologue.HoldCount + " forbid=" + Core.forbidInputs
                        + " cantChange=" + Core.cantChangeForbidInputs + " cutscene=" + Singleton<Controller>.Instance.playingCutscene);
                    return;
                }
                case "padcycle":
                {
                    // Debug: switch a spawned location off and on (re-runs its OnEnable / trigger enters).
                    string name = a.Length > 1 ? a[1] : "dream_tutorial_00";
                    OutsideLocations outs = Singleton<OutsideLocations>.Instance;
                    if (outs == null || !outs.spawnedLocations.TryGetValue(name, out Location loc) || loc == null)
                    { Out("  no location " + name); return; }
                    loc.gameObject.SetActive(false);
                    loc.gameObject.SetActive(true);
                    Out("  cycled " + name);
                    return;
                }
                case "ui":
                {
                    UI ui = Singleton<UI>.Instance;
                    tk2dSprite top = ui.blackScreenTop != null ? ui.blackScreenTop.GetComponent<tk2dSprite>() : null;
                    tk2dSprite bs = ui.blackScreen != null ? ui.blackScreen.GetComponent<tk2dSprite>() : null;
                    Out("  top=" + (ui.blackScreenTop != null && ui.blackScreenTop.activeInHierarchy) + " a=" + (top != null ? top.color.a : -1f)
                        + " black=" + (ui.blackScreen != null && ui.blackScreen.activeInHierarchy) + " a=" + (bs != null ? bs.color.a : -1f)
                        + " video=" + (ui.videoOverlay != null && ui.videoOverlay.activeInHierarchy)
                        + " endingSleep=" + p.endingSleep + " performing=" + p.performingAction);
                    return;
                }
                case "cam":
                {
                    CamMain cam = Singleton<CamMain>.Instance;
                    if (cam == null) { Out("  no camera"); return; }
                    Vector3 d = cam.transform.position - p.transform.position;
                    Out("  seeDistance=" + cam.seeDistance.ToString(CultureInfo.InvariantCulture)
                        + " screen=" + Screen.width + "x" + Screen.height
                        + " camOffset=" + Pos(new Vector3(d.x, 0f, d.z))
                        + " cursor=" + Pos(new Vector3(Core.cursorPos().x, 0f, Core.cursorPos().y)));
                    return;
                }
                case "skipmovie":
                    Out("  skipped=" + Singleton<Controller>.Instance.skipCurrentMovie());
                    return;
                case "dreamend":
                {
                    // End the current dream with an outcome, as its GameEvent would (prologue:
                    // dream_tutorial_00 "default" moves on to _01; _01 "chomper_attack" wakes home).
                    Dreams d = Dreams.Instance;
                    if (d == null || !d.dreaming) { Out("  not dreaming"); return; }
                    // Mid-cutscene the dream's own scripts never end it; ending it there leaves the
                    // cutscene running on a pad that is gone.
                    if (Singleton<Controller>.Instance.playingCutscene) { Out("  a cutscene is playing — try again after it"); return; }
                    d.outcome = a.Length > 1 ? a[1] : "default";
                    d.initiateEndDreaming();
                    Out("  ending " + (d.preset != null ? d.preset.name : "-") + " with " + d.outcome);
                    return;
                }
                case "epilogue":
                case "dream":
                {
                    // A story step's GameEvent startDream (no entry movie): "epilogue" is the road
                    // home's step into the ending, "dream <preset>" any other.
                    string preset = a[0] == "epilogue" ? "epilog_part1a_dream" : a[1];
                    Dreams d = Dreams.Instance;
                    if (d == null || d.dreaming) { Out("  already dreaming"); return; }
                    Core.forbidInputs = true;
                    p.halt();
                    d.wantToDream = true;
                    d.StartCoroutine(d.prepareDream(preset));
                    Out("  " + preset + " started");
                    return;
                }
                case "use":
                {
                    // "use <name>": walk up to the nearest active object of that name and press E on it
                    // (Item.activate: its onActivate triggers, a bed, a switch, a note).
                    Item best = null;
                    float bestD = float.MaxValue;
                    foreach (Item it in Resources.FindObjectsOfTypeAll<Item>())
                    {
                        if (it == null || !it.gameObject.activeInHierarchy || !it.gameObject.scene.IsValid()
                            || it.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        float dd = Flat(p.transform.position, it.transform.position);
                        if (dd < bestD) { bestD = dd; best = it; }
                    }
                    if (best == null)
                    {
                        // A scene action without an Item (a bed to lie in, a hole to climb into):
                        // CustomCursorAction, named by itself or its parent.
                        CustomCursorAction cca = null;
                        foreach (CustomCursorAction c in Resources.FindObjectsOfTypeAll<CustomCursorAction>())
                        {
                            if (c == null || !c.gameObject.activeInHierarchy || !c.gameObject.scene.IsValid())
                                continue;
                            string nm = c.name + "/" + (c.transform.parent != null ? c.transform.parent.name : "");
                            if (nm.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            float dd = Flat(p.transform.position, c.transform.position);
                            if (dd < bestD) { bestD = dd; cca = c; }
                        }
                        if (cca == null) { Out("  nothing to use named like " + a[1]); return; }
                        p.teleportTo(cca.transform.position + new Vector3(30f, 0f, 0f), p.transform.rotation);
                        p.selectedObject = cca.transform;
                        cca.activate();
                        Out("  used action " + cca.transform.parent?.name + "/" + cca.name + "@" + Pos(cca.transform.position));
                        return;
                    }
                    p.teleportTo(best.transform.position + new Vector3(30f, 0f, 0f), p.transform.rotation);
                    p.selectedObject = best.transform;
                    bool r = best.activate();
                    Out("  used " + best.name + "@" + Pos(best.transform.position) + " -> " + r);
                    return;
                }
                case "hit":
                {
                    // "hit <name> [damage]": this player's melee hit on the nearest creature of that
                    // name (switched off ones too, as a story dummy can be), from right beside it.
                    CharBase best = null;
                    float bestD = float.MaxValue;
                    foreach (CharBase cb in Resources.FindObjectsOfTypeAll<CharBase>())
                    {
                        if (cb == null || cb.gameObject == p.gameObject || !cb.gameObject.scene.IsValid()
                            || cb.GetComponent<RemotePlayerProxy>() != null
                            || cb.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        float dd = Flat(p.transform.position, cb.transform.position);
                        if (dd < bestD) { bestD = dd; best = cb; }
                    }
                    if (best == null)
                    {
                        // An object struck instead (a barricade, a crate): Item.getHit, as a swing does.
                        Item hitItem = null;
                        foreach (Item it in Resources.FindObjectsOfTypeAll<Item>())
                        {
                            if (it == null || !it.gameObject.activeInHierarchy || !it.gameObject.scene.IsValid()
                                || it.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            float dd = Flat(p.transform.position, it.transform.position);
                            if (dd < bestD) { bestD = dd; hitItem = it; }
                        }
                        if (hitItem == null) { Out("  nothing named like " + a[1]); return; }
                        p.teleportTo(hitItem.transform.position + new Vector3(40f, 0f, 0f), p.transform.rotation);
                        hitItem.getHit(a.Length > 2 ? (int)F(a[2]) : 30, p.transform);
                        Out("  hit object " + hitItem.name + "@" + Pos(hitItem.transform.position));
                        return;
                    }
                    p.teleportTo(best.transform.position + new Vector3(40f, 0f, 0f), p.transform.rotation);
                    best.getHit(a.Length > 2 ? F(a[2]) : 30f, p.transform, false, true, true);
                    Out("  hit " + best.name + "@" + Pos(best.transform.position) + " hp=" + Mathf.RoundToInt(best.health)
                        + " alive=" + best.alive + " active=" + best.gameObject.activeInHierarchy);
                    return;
                }
                case "skill":
                {
                    // "skill <PlayerSkills field> [0|1]": read or set a perk/trait flag (NightShadows...).
                    var f = AccessTools.Field(typeof(PlayerSkills), a[1]);
                    if (f == null) { Out("  no skill " + a[1]); return; }
                    if (a.Length > 2)
                        f.SetValue(p.skills, a[2] == "1");
                    Out("  " + a[1] + "=" + f.GetValue(p.skills));
                    return;
                }
                case "perk":
                case "useskill":
                {
                    // "perk <name> [0]": take (or drop) a level-up skill as the skills menu's confirm
                    // does (PlayerSkill.initialize). "useskill <name>": press its skill key (activate).
                    PlayerSkill sk = null;
                    foreach (PlayerSkill s in p.skills.progressionSkills)
                        if (s != null && string.Equals(s.gameObject.name, a[1], StringComparison.OrdinalIgnoreCase))
                            sk = s;
                    if (sk == null) { Out("  no skill " + a[1]); return; }
                    if (a[0] == "perk")
                        sk.initialize(destBool: a.Length < 3 || a[2] != "0");
                    else
                        sk.activate();
                    Out("  " + sk.gameObject.name + " chosen=" + sk.chosen + " used=" + sk.timesUsed + "/" + sk.maxUses
                        + " skills=" + p.skills.skills.Count + " invisible=" + p.invisible + " handling=" + p.handlingModifier
                        + " maxHp=" + Mathf.RoundToInt(p.maxHealth));
                    return;
                }
                case "effect":
                {
                    // "effect <CharacterEffectType> <seconds> [magnitude]": put an effect on this
                    // player, as a sensor's effects list does (bleeding, poison...).
                    // "effect <type> off" takes it off (what the Wolf trap's exit step does).
                    var type = (CharacterEffectType)Enum.Parse(typeof(CharacterEffectType), a[1], ignoreCase: true);
                    if (a[2] == "off")
                        p.effects.deleteThisTypeOfEffect(type);
                    else
                        p.effects.activate(type, F(a[2]), a.Length > 3 ? F(a[3]) : 0f);
                    Out("  " + type + (a[2] == "off" ? " off" : " on") + ", bleeding=" + p.bleeding + " hp=" + Mathf.RoundToInt(p.health));
                    return;
                }
                case "shadows":
                    // The Shadows perk's night step (a night scene's tryToSpawnShadow on this player).
                    p.tryToSpawnShadow();
                    Out("  shadow wave asked for");
                    return;
                case "shadowlist":
                {
                    // Every shadow here: id, owner, where, how close to its owner, alive, lit spot.
                    int n = 0;
                    foreach (ShadowCreature sc in UnityEngine.Object.FindObjectsOfType<ShadowCreature>())
                    {
                        if (sc == null)
                            continue;
                        n++;
                        Patches.ShadowSyncInfo info = sc.GetComponent<Patches.ShadowSyncInfo>();
                        int owner = info != null ? info.OwnerPlayerId : 0;
                        Transform ot = owner == 0 || owner == net.LocalPlayerId ? p.transform
                            : net.GetProxy(owner) != null ? net.GetProxy(owner).transform : null;
                        Out("  #" + (info != null ? info.ShadowId : -1) + " owner=p" + owner
                            + (sc.GetComponent<Patches.ProxyShadowController>() != null ? "(proxy)" : "")
                            + " @" + Pos(sc.transform.position) + " d=" + (ot != null ? Flat(ot.position, sc.transform.position).ToString("0", CultureInfo.InvariantCulture) : "?")
                            + " dist=" + sc.distanceToPlayer.ToString("0", CultureInfo.InvariantCulture)
                            + " dead=" + sc.dead + " lit=" + Core.isInLight(sc.transform.position, mustBeWalkable: false));
                    }
                    var cs = Singleton<CharacterSpawner>.Instance;
                    Out("  " + n + " shadow(s) spawned=" + (cs != null && cs.spawnedShadows) + " remove=" + (cs != null && cs.shadowsRemove)
                        + " amount=" + (cs != null ? cs.spawnedShadowsAmount : -1));
                    return;
                }
                case "hold":
                {
                    // "hold <type> [0]": take that item from the hotbar in hand and switch it on (a
                    // torch, flashlight, lantern), or off with 0.
                    int idx = -1;
                    for (int i = 0; i < p.Hotbar.slots.Count; i++)
                        if (p.Hotbar.slots[i] != null && !InvItemClass.isNull(p.Hotbar.slots[i].invItem)
                            && string.Equals(p.Hotbar.slots[i].invItem.type, a[1], StringComparison.OrdinalIgnoreCase))
                        { idx = i; break; }
                    if (idx < 0 && p.Inventory.haveItemAmountInPlayer(a[1]) > 0f)
                    {
                        // In the pack: onto the hotbar, as dragging it there would.
                        p.Inventory.removeItemAmount(a[1], 1);
                        p.Hotbar.addItemType(a[1], 1);
                        for (int i = 0; i < p.Hotbar.slots.Count; i++)
                            if (p.Hotbar.slots[i] != null && !InvItemClass.isNull(p.Hotbar.slots[i].invItem)
                                && string.Equals(p.Hotbar.slots[i].invItem.type, a[1], StringComparison.OrdinalIgnoreCase))
                            { idx = i; break; }
                    }
                    if (idx < 0) { Out("  no " + a[1] + " carried"); return; }
                    p.Hotbar.selectSlot(idx, noiseless: true, force: true);
                    InvItemClass held = p.Hotbar.slots[idx].invItem;
                    // An already selected slot (the item was put into it) is not selected again.
                    if (p.currentItem != held)
                    {
                        held.shouldBeActive = true;
                        p.switchToItem(held);
                    }
                    held.switchActive(a.Length < 3 || a[2] != "0");
                    Out("  holding " + held.type + " on=" + held.activated + " current=" + (InvItemClass.isNull(p.currentItem) ? "-" : p.currentItem.type));
                    return;
                }
                case "face":
                {
                    // "face <name> [away]": turn toward (or away from) the nearest object of that name, as the cursor would.
                    Transform best = null;
                    float bestD = float.MaxValue;
                    foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    {
                        if (t == null || t.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        float dd = Flat(p.transform.position, t.position);
                        if (dd < bestD) { bestD = dd; best = t; }
                    }
                    if (best == null) { Out("  nothing named like " + a[1]); return; }
                    Vector3 dir = best.position - p.transform.position;
                    if (a.Length > 2 && a[2] == "away")
                        dir = -dir;
                    p.transform.rotation = Quaternion.Euler(90f, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 0f);
                    Out("  facing " + best.name + " d=" + bestD.ToString("0", CultureInfo.InvariantCulture));
                    return;
                }
                case "lit":
                    // Is this player protected from shadows: in a light area / holding a protecting
                    // item (isInLight), and is the path node under it lit (Core.isInLight)?
                    Out("  isInLight=" + p.isInLight + " node=" + Core.isInLight(p.transform.position, mustBeWalkable: true)
                        + " hp=" + Mathf.RoundToInt(p.health) + " pos=" + Pos(p.transform.position));
                    return;
                case "pending":
                    Out("  pending:" + ClientEntityInterpolationService.DebugPending());
                    return;
                case "dstate":
                {
                    // Dream progress: session, completed presets, this player's dream flags.
                    Dreams d = Dreams.Instance;
                    Out("  session=" + DreamSession.Current + " preset=" + (DreamSession.PresetName ?? "-")
                        + " dreaming=" + (d != null && d.dreaming) + " local=" + (d != null && d.preset != null ? d.preset.name : "-")
                        + " outcome=" + (d != null ? d.outcome : "-")
                        + " completed=[" + string.Join(",", DreamSession.GetCompletedPresets() ?? new string[0]) + "]"
                        + " dead=" + FinalDreamsceneManager.IsLocalDead + " spectating=" + (DWMPHorde.Spectator.SpectatorModeController.Instance != null && DWMPHorde.Spectator.SpectatorModeController.Instance.IsSpectating)
                        + " hp=" + Mathf.RoundToInt(p.health) + " pos=" + Pos(p.transform.position)
                        + " forbid=" + Core.forbidInputs);
                    return;
                }
                case "credits":
                {
                    // The last ending page done (EpilogueOutcomes.goToCredits, on a holder that never Starts).
                    var holder = new GameObject("pilot_epilogue_outcomes");
                    holder.SetActive(false);
                    EpilogueOutcomes eo = holder.AddComponent<EpilogueOutcomes>();
                    AccessTools.Method(typeof(EpilogueOutcomes), "goToCredits").Invoke(eo, null);
                    Out("  pages done inEpilogue=" + p.inEpilogue + " ending=" + EpilogueNetHandlers.IsLocalInEpilogue());
                    return;
                }
                case "endnight":
                    // What walking out of the hideout does in the morning (Location.OnTriggerExit);
                    // a teleport fires no trigger exit. On a client it asks the host, as a walk-out would.
                    Singleton<Controller>.Instance.endAfterNight();
                    Out("  afterNight=" + Singleton<Controller>.Instance.isAfterNight
                        + " clockOn=" + Singleton<Controller>.Instance.DoUpdateTime);
                    return;
                case "doors":
                    ListNear(ListTracker<Door>.GetAll(), a.Length > 1 ? F(a[1]) : 20f,
                        d => "open=" + d.opened + " locked=" + (d.GetComponent<Padlock>() != null && d.GetComponent<Padlock>().locked));
                    return;
                case "door":
                {
                    Door d = Nearest(ListTracker<Door>.GetAll(), a.Length > 2 ? F(a[2]) : 6f);
                    if (d == null) { Out("  no door near"); return; }
                    if (a.Length > 1 && a[1] == "close")
                        d.close(p.transform);
                    else
                        d.open(p.transform.position, p.transform);
                    Out("  " + d.name + "@" + Pos(d.transform.position) + " open=" + d.opened);
                    return;
                }
                case "chars":
                {
                    int n = CharacterTracker.CopyAll(out Character[] buf);
                    var list = new List<Character>(n);
                    for (int i = 0; i < n; i++)
                        if (buf[i] != null && buf[i].gameObject != p.gameObject)
                            list.Add(buf[i]);
                    ListNear(list, a.Length > 1 ? F(a[1]) : 30f, c =>
                    {
                        CharacterTracker.TryGetStableId(c, out short id);
                        CharBase cb = c.GetComponent<CharBase>();
                        string tgt = c.target == null ? "-" : c.target == p.transform ? "me"
                            : c.target.GetComponent<RemotePlayerProxy>() is RemotePlayerProxy tp ? "p" + tp.PlayerId : c.target.name;
                        return "id=" + id + " alive=" + (cb == null || cb.alive) + " active=" + c.gameObject.activeInHierarchy
                            + " hp=" + (cb != null ? Mathf.RoundToInt(cb.health) : -1) + " target=" + tgt
                            + " beh=" + c.behaviour + " sleep=" + c.sleeping + " isActive=" + c.isActive
                            + " seen=" + c.canSeeEnemyFar + " enabled=" + c.enabled
                            + (CreatureSightState.Of(c) is InSightOfPlayer isp ? " sight=" + (isp.firedInSight ? "in" : "out") : "")
                            + (c.AIpath != null ? " path(move=" + c.AIpath.canMove + " search=" + c.AIpath.canSearch
                                + " on=" + c.AIpath.enabled + " to=" + (c.AIpath.target != null ? c.AIpath.target.name : Pos(c.AIpath.targetPos))
                                + " reached=" + c.AIpath.TargetReached + " v=" + Mathf.RoundToInt(c.GetComponent<Rigidbody>() != null ? c.GetComponent<Rigidbody>().velocity.magnitude : -1) + ")" : " nopath");
                    });
                    return;
                }
                case "attached":
                {
                    // Characters (switched off ones too) holding objects in their attachedGameObjects, and what.
                    int shown = 0, total = 0;
                    foreach (CharBase cb in Resources.FindObjectsOfTypeAll<CharBase>())
                    {
                        if (cb == null || !cb.gameObject.scene.IsValid() || cb.attachedGameObjects == null
                            || cb.attachedGameObjects.Count == 0)
                            continue;
                        total++;
                        if (shown++ >= 30)
                            continue;
                        var sb = new StringBuilder();
                        foreach (GameObject go in cb.attachedGameObjects)
                            sb.Append(' ').Append(go == null ? "<dead>" : go.name + (go.activeSelf ? "" : "(off)"));
                        Out("  " + cb.name + "@" + Pos(cb.transform.position) + (cb.gameObject.activeInHierarchy ? "" : "(off)") + " :" + sb);
                    }
                    Out("  " + total + " character(s) with attached objects");
                    return;
                }
                case "kill":
                {
                    if (net.Role == NetworkRole.Client) { Out("  host or offline only"); return; }
                    int n = CharacterTracker.CopyAll(out Character[] buf);
                    var list = new List<Character>(n);
                    for (int i = 0; i < n; i++)
                    {
                        Character c = buf[i];
                        CharBase cb = c != null ? c.GetComponent<CharBase>() : null;
                        if (c != null && cb != null && cb.alive && c.gameObject.activeInHierarchy
                            && c.GetComponent<RemotePlayerProxy>() == null && c.gameObject != p.gameObject)
                            list.Add(c);
                    }
                    if (a.Length > 2 && a[2] == "all")
                    {
                        // "kill <radius> all": every creature within it (a sweep's clean slate).
                        float r = F(a[1]);
                        int killed = 0;
                        foreach (Character c in list)
                            if (Flat(p.transform.position, c.transform.position) <= r)
                            {
                                c.die();
                                killed++;
                            }
                        Out("  killed " + killed);
                        return;
                    }
                    Character target = Nearest(list, a.Length > 1 ? F(a[1]) : 15f);
                    if (target == null) { Out("  nothing alive near"); return; }
                    target.die();
                    Out("  killed " + target.name + "@" + Pos(target.transform.position));
                    return;
                }
                case "spawn":
                {
                    // "spawn <type> [dist] [count] [attack]": creatures from Characters/ beside this
                    // player, as a night event's CharacterSpawner.spawnCharacterAround does (attack 1:
                    // attackPlayer, the host).
                    if (net.Role == NetworkRole.Client) { Out("  host or offline only"); return; }
                    float dist = a.Length > 2 ? F(a[2]) : 150f;
                    int count = a.Length > 3 ? int.Parse(a[3], CultureInfo.InvariantCulture) : 1;
                    bool attack = a.Length > 4 && a[4] == "1";
                    CharacterSpawner spawner = Singleton<CharacterSpawner>.Instance;
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 at = p.transform.position + new Vector3(dist, 0f, 40f * i);
                        Character c = spawner.spawnCharacterAround(p.gameObject, at, 1f, a[1], nocturnal: false, attackPlayer: attack);
                        if (c == null) { Out("  spawn " + a[1] + " failed"); return; }
                        CharacterTracker.TryGetStableId(c, out short sid);
                        Out("  spawned " + c.name + " id=" + sid + "@" + Pos(c.transform.position));
                    }
                    return;
                }
                case "aggro":
                {
                    // "aggro <name> <me|player id> [radius]": the nearest creature of that name attacks
                    // that player (Character.attackCharacter, what seeing them does).
                    float radius = a.Length > 3 ? F(a[3]) : 2000f;
                    Transform target = a[2] == "me" ? p.transform
                        : net.GetProxy(int.Parse(a[2], CultureInfo.InvariantCulture)) is RemotePlayerProxy tp ? tp.transform : null;
                    if (target == null) { Out("  no player " + a[2]); return; }
                    int n = CharacterTracker.CopyAll(out Character[] buf);
                    var list = new List<Character>(n);
                    for (int i = 0; i < n; i++)
                        if (buf[i] != null && buf[i].gameObject != p.gameObject && buf[i].GetComponent<RemotePlayerProxy>() == null
                            && buf[i].name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0)
                            list.Add(buf[i]);
                    Character who = Nearest(list, radius);
                    if (who == null) { Out("  no " + a[1] + " near"); return; }
                    who.attackCharacter(target);
                    Out("  " + who.name + "@" + Pos(who.transform.position) + " attacks " + target.name + " beh=" + who.behaviour);
                    return;
                }
                case "comps":
                {
                    // "comps <Type> [radius]": every live component of that game type near (gore,
                    // projectiles, decals...), switched off ones excluded.
                    Type t = AccessTools.TypeByName(a[1]);
                    if (t == null || !typeof(Component).IsAssignableFrom(t)) { Out("  no component type " + a[1]); return; }
                    var list = new List<Component>();
                    foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(t))
                        if (o is Component c && c != null)
                            list.Add(c);
                    ListNear(list, a.Length > 2 ? F(a[2]) : 400f, c => (c.transform.parent != null ? "in " + c.transform.parent.name : "root"));
                    Out("  " + list.Count + " " + t.Name + " in the scene");
                    return;
                }
                case "audio":
                    // "audio <seconds>": record every sound started (AudioController.PlayAudioItem).
                    _sounds.Clear();
                    _soundsUntil = Time.realtimeSinceStartup + F(a[1]);
                    Out("  recording sounds for " + a[1] + "s");
                    return;
                case "audiodump":
                {
                    // "audiodump [radius] [filter]": the recorded sounds near this player, by name.
                    float radius = a.Length > 1 ? F(a[1]) : float.MaxValue;
                    string filter = a.Length > 2 ? a[2] : null;
                    var byName = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    var where = new Dictionary<string, Vector3>();
                    foreach (KeyValuePair<string, Vector3> s in _sounds)
                    {
                        if (Flat(p.transform.position, s.Value) > radius
                            || (filter != null && s.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        byName.TryGetValue(s.Key, out int c);
                        byName[s.Key] = c + 1;
                        if (!where.ContainsKey(s.Key))
                            where[s.Key] = s.Value;
                    }
                    foreach (KeyValuePair<string, int> kv in byName)
                        Out("  " + kv.Key + " x" + kv.Value + " first@" + Pos(where[kv.Key]));
                    Out("  " + byName.Count + " sound(s) of " + _sounds.Count + " recorded");
                    return;
                }
                case "flag":
                {
                    Flags flags = Singleton<Flags>.Instance;
                    if (a.Length > 2)
                        flags.setFlag(a[1], a[2] == "1");
                    Out("  " + a[1] + "=" + flags.isFlagTrue(a[1]));
                    return;
                }
                case "desync":
                    DesyncCheck.RunSoon();
                    Out("  desync check queued");
                    return;
                case "shot":
                {
                    string file = Path.Combine(Dir, (a.Length > 1 ? a[1] : "shot") + ".png");
                    ScreenCapture.CaptureScreenshot(file);
                    Out("  screenshot " + file);
                    return;
                }
                case "find":
                case "goto":
                {
                    // Scene objects whose name contains the text (on demand only: a full scan).
                    string needle = a[1].ToLowerInvariant();
                    float radius = a.Length > 2 ? F(a[2]) : float.MaxValue;
                    Vector3 at = p.transform.position;
                    var hits = new List<KeyValuePair<float, Transform>>();
                    foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    {
                        if (t == null || t.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        float d = Flat(at, t.position);
                        if (d <= radius)
                            hits.Add(new KeyValuePair<float, Transform>(d, t));
                    }
                    hits.Sort((x, y) => x.Key.CompareTo(y.Key));
                    if (a[0] == "goto")
                    {
                        if (hits.Count == 0) { Out("  nothing named like " + needle); return; }
                        Transform t = hits[0].Value;
                        p.teleportTo(t.position + new Vector3(1.5f, 0f, 1.5f), p.transform.rotation);
                        Out("  went to " + t.name + "@" + Pos(t.position));
                        return;
                    }
                    for (int i = 0; i < hits.Count && i < 30; i++)
                        Out("  " + hits[i].Value.name + "@" + Pos(hits[i].Value.position) + " d="
                            + hits[i].Key.ToString("0", CultureInfo.InvariantCulture));
                    Out("  " + hits.Count + " match(es)");
                    return;
                }
                case "obj":
                {
                    // Scene objects named exactly so, switched off ones too: path, own and effective active.
                    int shown = 0;
                    foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                    {
                        if (t == null || !t.gameObject.scene.IsValid()
                            || !string.Equals(t.name, a[1], StringComparison.OrdinalIgnoreCase))
                            continue;
                        var path = new StringBuilder(t.name);
                        for (Transform q = t.parent; q != null; q = q.parent)
                            path.Insert(0, q.name + (q.gameObject.activeSelf ? "" : "(off)") + "/");
                        Out("  " + path + "@" + Pos(t.position) + " self=" + t.gameObject.activeSelf
                            + " inHierarchy=" + t.gameObject.activeInHierarchy);
                        shown++;
                    }
                    Out("  " + shown + " object(s)");
                    return;
                }
                case "gas":
                {
                    // "gas [n]": pour n puddles of gasoline in a row from the player (a can's trail).
                    int count = a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 1;
                    Vector3 at = p.transform.position;
                    for (int i = 0; i < count; i++)
                        Core.AddPrefab("Items/GasolineTrail", at + new Vector3(20f * i, 0f, 0f), Quaternion.Euler(90f, 0f, 0f), Core.ItemContainer);
                    Out("  poured " + count + " at " + Pos(at));
                    return;
                }
                case "ignite":
                case "liquids":
                {
                    // "ignite [radius]": light the nearest flammable puddle (a torch's touch).
                    // "liquids [radius]": the flammable puddles near, culled ones too.
                    var liquids = new List<Liquid>();
                    foreach (Liquid l in Resources.FindObjectsOfTypeAll<Liquid>())
                        if (l != null && l.flammable && l.gameObject.scene.IsValid())
                            liquids.Add(l);
                    float radius = a.Length > 1 ? F(a[1]) : 60f;
                    if (a[0] == "liquids")
                    {
                        ListNear(liquids, radius, l => "burning=" + l.burning + " active=" + l.gameObject.activeInHierarchy);
                        return;
                    }
                    Liquid liq = Nearest(liquids, radius);
                    if (liq == null) { Out("  no puddle near"); return; }
                    liq.startBurning();
                    Out("  lit " + liq.name + "@" + Pos(liq.transform.position) + " burning=" + liq.burning);
                    return;
                }
                case "lightshare":
                {
                    // Light2D components drawing into the same Mesh (each should own one).
                    var byMesh = new Dictionary<Mesh, List<Light2D>>();
                    foreach (Light2D l in UnityEngine.Object.FindObjectsOfType<Light2D>(true))
                    {
                        if (l == null || l._mesh == null)
                            continue;
                        if (!byMesh.TryGetValue(l._mesh, out List<Light2D> owners))
                            byMesh[l._mesh] = owners = new List<Light2D>(2);
                        owners.Add(l);
                    }
                    int shared = 0;
                    foreach (KeyValuePair<Mesh, List<Light2D>> kv in byMesh)
                    {
                        if (kv.Value.Count < 2)
                            continue;
                        shared++;
                        var names = new List<string>();
                        foreach (Light2D l in kv.Value)
                            names.Add((l.transform.root != null ? l.transform.root.name + "/" : "") + l.name);
                        Out("  shared " + kv.Key.name + ": " + string.Join(", ", names.ToArray()));
                    }
                    Out("  " + byMesh.Count + " light meshes, " + shared + " shared");
                    return;
                }
                case "events":
                {
                    // GameEvents under the current dream pad (or within a radius), numbered for "fire".
                    Dreams d = Dreams.Instance;
                    Transform pad = d != null && d.dreaming && d.dreamLocation != null ? d.dreamLocation.transform : null;
                    float radius = a.Length > 1 ? F(a[1]) : float.MaxValue;
                    _events.Clear();
                    foreach (GameEvents ge in UnityEngine.Object.FindObjectsOfType<GameEvents>(true))
                    {
                        if (ge == null)
                            continue;
                        if (pad != null ? !ge.transform.IsChildOf(pad) : Flat(p.transform.position, ge.transform.position) > radius)
                            continue;
                        _events.Add(ge);
                    }
                    for (int i = 0; i < _events.Count; i++)
                    {
                        GameEvents ge = _events[i];
                        var sb = new StringBuilder();
                        foreach (GameEvent e in ge.events)
                        {
                            if (e == null)
                                continue;
                            sb.Append(' ').Append(e.type);
                            if (!string.IsNullOrEmpty(e.Value))
                                sb.Append('=').Append(e.Value);
                            if (e.type == GameEvent.Type.gameObject)
                                sb.Append('/').Append(e.gameObjectModifyType);
                            else if (e.type == GameEvent.Type.modifyMainScript)
                                sb.Append('/').Append(e.mainScriptModify).Append(e.activeModifier ? "+" : "-");
                            else if (e.type == GameEvent.Type.tweenTime || e.type == GameEvent.Type.timeScale)
                                sb.Append('/').Append(e.magnitude.ToString("0.##", CultureInfo.InvariantCulture));
                            else if (e.type == GameEvent.Type.worldFlag)
                                sb.Append(e.activeModifier ? "+" : "-");
                        }
                        Out("  #" + i + " " + ge.name + "@" + Pos(ge.transform.position) + " fired=" + ge.fired
                            + " active=" + ge.gameObject.activeInHierarchy + " :" + sb);
                    }
                    Out("  " + _events.Count + " GameEvents" + (pad != null ? " on " + pad.name : ""));
                    return;
                }
                case "fire":
                {
                    // Fire a listed GameEvents as its trigger, dialogue or cutscene would.
                    int i = int.Parse(a[1], CultureInfo.InvariantCulture);
                    if (i < 0 || i >= _events.Count || _events[i] == null) { Out("  no event #" + i + " (run events first)"); return; }
                    _events[i].fire();
                    Out("  fired #" + i + " " + _events[i].name);
                    return;
                }
                case "dlg":
                {
                    // The open dialogue: list its options, pick one ("dlg 0"), or click on ("dlg next").
                    DialogueWindow w = Singleton<UI>.Instance.dialogueWindow;
                    if (w == null || !w.opened) { Out("  no dialogue open"); return; }
                    if (a.Length > 1 && a[1] == "next")
                        AccessTools.Method(typeof(DialogueWindow), "onInstantClick").Invoke(w, null);
                    else if (a.Length > 1)
                    {
                        int i = int.Parse(a[1], CultureInfo.InvariantCulture);
                        if (i < 0 || i >= w.menuOptions.Count) { Out("  no option " + i); return; }
                        w.menuOptions[i].getClicked(force: true);
                    }
                    var sb = new StringBuilder();
                    for (int i = 0; i < w.menuOptions.Count; i++)
                        if (w.menuOptions[i] != null)
                            sb.Append(" [").Append(i).Append("] ").Append(w.menuOptions[i].textMesh != null ? w.menuOptions[i].textMesh.text : w.menuOptions[i].name);
                    Out("  dialogue " + (w.currentDialogue != null ? w.currentDialogue.name : "-") + " opened=" + w.opened
                        + " decision=" + w.needsDecision + " finished=" + w.boardFinished + " :" + sb);
                    return;
                }
                case "inv":
                {
                    // The pack and hotbar, the level and the recipes known.
                    var sb = new StringBuilder();
                    foreach (Inventory inv in new[] { p.Hotbar, p.Inventory })
                    {
                        sb.Append(inv == p.Hotbar ? " hotbar:" : " | pack:");
                        for (int i = 0; i < inv.slots.Count; i++)
                        {
                            InvItemClass it = inv.slots[i] != null ? inv.slots[i].invItem : null;
                            if (!InvItemClass.isNull(it))
                                sb.Append(' ').Append(it.type).Append('x').Append(it.amount);
                        }
                    }
                    Out("  xp=" + p.experience + " recipes=" + (p.recipes != null ? p.recipes.Count : 0) + sb);
                    return;
                }
                case "oven":
                {
                    // Make the nearest oven this player's home, as picking Cook in its first talk
                    // does (DialogueWindow.close: setExperienceMachine, which lights it).
                    var ovens = new List<ExperienceMachine>(UnityEngine.Object.FindObjectsOfType<ExperienceMachine>());
                    ExperienceMachine em = Nearest(ovens, a.Length > 1 ? F(a[1]) : 15f);
                    if (em == null) { Out("  no oven near"); return; }
                    p.setExperienceMachine(em);
                    Out("  home " + em.name + "@" + Pos(em.transform.position) + " lit=" + em.isOn);
                    return;
                }
                case "locs":
                {
                    // Every location of the world, loaded or not ("locs" for the crawl's route).
                    WorldGenerator wg = Singleton<WorldGenerator>.Instance;
                    int n = 0;
                    if (wg != null && wg.locations != null)
                        foreach (Location l in wg.locations)
                        {
                            if (l == null)
                                continue;
                            Out("  loc " + Core.getTrueLocationName(l.name) + "@" + Pos(l.transform.position)
                                + " base=" + l.playerBase + " outside=" + l.isOutsideLocation);
                            n++;
                        }
                    Out("  " + n + " locations chapter=" + (wg != null ? wg.chapterID : -1));
                    return;
                }
                case "npcs":
                    ListNear(Talkable(), a.Length > 1 ? F(a[1]) : 300f,
                        n => "dialogues=" + n.characterDialogue.dialogues.Count);
                    return;
                case "talk":
                case "talkall":
                {
                    // Walk through NPC dialogues as a player would: every option once (show each
                    // asked-for item, gossip, special options), a different branch at each decision on
                    // each talk. "talk <name> [radius]" one NPC, "talkall [radius]" every one near.
                    bool all = a[0] == "talkall";
                    float radius = a.Length > (all ? 1 : 2) ? F(a[all ? 1 : 2]) : 300f;
                    List<NPC> near = Talkable();
                    Vector3 at = p.transform.position;
                    _talkQueue.Clear();
                    _talked = 0;
                    foreach (NPC n in near)
                        if (Flat(at, n.transform.position) <= radius
                            && (all || string.Equals(n.name, a[1], StringComparison.OrdinalIgnoreCase)))
                            _talkQueue.Add(n);
                    _talkQueue.Sort((x, y) => Flat(at, x.transform.position).CompareTo(Flat(at, y.transform.position)));
                    if (!all && _talkQueue.Count > 1)
                        _talkQueue.RemoveRange(1, _talkQueue.Count - 1);
                    Out("  talk queue " + _talkQueue.Count + (_talkQueue.Count == 0 ? " (talkall done n=0)" : ""));
                    return;
                }
                case "pads":
                case "padexits":
                {
                    // "pads [radius]": the doors into location pads near (GameEvents with a
                    // transportToOutsideLocation step), numbered for "fire". "padexits": the ways
                    // out of the pad the player is in (returnToWorld steps).
                    bool exits = a[0] == "padexits";
                    OutsideLocations outs = Singleton<OutsideLocations>.Instance;
                    Transform pad = null;
                    if (exits && outs != null && outs.playerInOutsideLocation
                        && outs.spawnedLocations.TryGetValue(outs.currentLocationName, out Location cur) && cur != null)
                        pad = cur.transform;
                    if (exits && pad == null) { Out("  not in a pad"); return; }
                    float radius = a.Length > 1 ? F(a[1]) : 700f;
                    GameEvent.Type want = exits ? GameEvent.Type.returnToWorld : GameEvent.Type.transportToOutsideLocation;
                    _events.Clear();
                    foreach (GameEvents ge in UnityEngine.Object.FindObjectsOfType<GameEvents>(true))
                    {
                        if (ge == null || ge.events == null)
                            continue;
                        if (exits ? !ge.transform.IsChildOf(pad) : Flat(p.transform.position, ge.transform.position) > radius)
                            continue;
                        string to = null;
                        foreach (GameEvent e in ge.events)
                            if (e != null && e.type == want)
                                to = e.Value ?? "";
                        if (to == null)
                            continue;
                        Out("  #" + _events.Count + " " + ge.name + "@" + Pos(ge.transform.position) + " → " + to);
                        _events.Add(ge);
                    }
                    Out("  " + _events.Count + (exits ? " pad exits" : " pad doors"));
                    return;
                }
                case "leavepad":
                {
                    // Back to the world from the pad the player is in (GameEvent returnToWorld).
                    OutsideLocations outs = Singleton<OutsideLocations>.Instance;
                    if (outs == null || !outs.playerInOutsideLocation) { Out("  not in a pad"); return; }
                    string from = outs.currentLocationName;
                    outs.returnToWorld();
                    Out("  left pad " + from);
                    return;
                }
                case "talkreset":
                    _visited.Clear();
                    Out("  dialogue options forgotten");
                    return;
                case "give":
                {
                    InvItemClass it = p.Inventory.addItemTypeToPlayer(a[1], a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 1, dropIfNoRoom: true);
                    Out("  give " + a[1] + " → " + (InvItemClass.isNull(it) ? "none" : it.type + "x" + it.amount));
                    return;
                }
                case "drop":
                {
                    // "drop <type>": throw the first stack of that item from the pack or hotbar on
                    // the ground, as the slot menu's Drop does (InvSlot.dropItem).
                    InvSlot slot = FindSlot(p, a[1]);
                    if (slot == null) { Out("  no " + a[1] + " carried"); return; }
                    string what = slot.invItem.type + "x" + slot.invItem.amount;
                    slot.dropItem();
                    Out("  dropped " + what + " at " + Pos(p.transform.position));
                    return;
                }
                case "pickup":
                {
                    // Pick up the nearest item lying on the ground (Item.getDroppedItem, a click on it).
                    var dropped = new List<Item>();
                    foreach (Item it in UnityEngine.Object.FindObjectsOfType<Item>())
                        if (it != null && it.isDroppedItem && it.GetComponent<Inventory>() != null
                            && it.GetComponent<Inventory>().slots.Count > 0 && !InvItemClass.isNull(it.GetComponent<Inventory>().slots[0].invItem))
                            dropped.Add(it);
                    Item item = Nearest(dropped, a.Length > 1 ? F(a[1]) : 30f);
                    if (item == null) { Out("  nothing on the ground near"); return; }
                    string what = item.GetComponent<Inventory>().slots[0].invItem.type + "x" + item.GetComponent<Inventory>().slots[0].invItem.amount;
                    Vector3 at = item.transform.position;
                    item.getDroppedItem();
                    Out("  picked up " + what + "@" + Pos(at));
                    return;
                }
                case "ground":
                {
                    // Items lying on the ground near.
                    var dropped = new List<Item>();
                    foreach (Item it in UnityEngine.Object.FindObjectsOfType<Item>())
                        if (it != null && it.isDroppedItem)
                            dropped.Add(it);
                    ListNear(dropped, a.Length > 1 ? F(a[1]) : 60f, it => Contents(it.GetComponent<Inventory>()));
                    return;
                }
                case "cont":
                case "loot":
                {
                    // "cont [radius]": the containers near and what is in them. "loot [radius]": take
                    // everything from the nearest one that has something (InvSlot.transferItemAllToPlayer).
                    bool take = a[0] == "loot";
                    var conts = new List<Item>();
                    foreach (Item it in UnityEngine.Object.FindObjectsOfType<Item>())
                        if (it != null && it.hasInventory && it.GetComponent<Inventory>() != null && it.GetComponent<Inventory>().invType == Inventory.InvType.itemInv
                            && !it.isDroppedItem
                            && (!take || Contents(it.GetComponent<Inventory>()).Length > 0))
                            conts.Add(it);
                    float radius = a.Length > 1 ? F(a[1]) : 60f;
                    if (!take)
                    {
                        ListNear(conts, radius, it => "[" + Contents(it.GetComponent<Inventory>()) + "]");
                        return;
                    }
                    Item c = Nearest(conts, radius);
                    if (c == null) { Out("  no container with items near"); return; }
                    string before = Contents(c.GetComponent<Inventory>());
                    foreach (InvSlot s in c.GetComponent<Inventory>().slots)
                        if (s != null && !InvItemClass.isNull(s.invItem))
                            s.transferItemAllToPlayer();
                    Out("  looted " + c.name + "@" + Pos(c.transform.position) + " [" + before + "] → [" + Contents(c.GetComponent<Inventory>()) + "]");
                    return;
                }
                case "save":
                    Singleton<SaveManager>.Instance.Save(doJson: true, doSaveProfile: true, force: true, forceSaveStatic: false,
                        showSavingIndicator: true, closeAndOpenStadiaSave: false);
                    Out("  saved day " + Singleton<Controller>.Instance.day);
                    return;
                case "chapter":
                {
                    // The bunker's leave event: save, then the next chapter (GameEvent transportPlayerToObject).
                    int ch = int.Parse(a[1], CultureInfo.InvariantCulture);
                    Singleton<SaveManager>.Instance.Save(doJson: true, doSaveProfile: true, force: true, forceSaveStatic: false,
                        showSavingIndicator: true, closeAndOpenStadiaSave: false);
                    Singleton<Controller>.Instance.generateChapter(ch, generateSave: true, loadChapterSave: true);
                    Out("  generateChapter " + ch);
                    return;
                }
                case "vsync":
                {
                    // A window parked on a hidden workspace gets no frame callbacks: with vsync on, a
                    // Wine client presents once a second. "vsync 0" frees it, capped at 60.
                    int v = int.Parse(a[1], CultureInfo.InvariantCulture);
                    QualitySettings.vSyncCount = v;
                    Application.targetFrameRate = v == 0 ? 60 : -1;
                    Out("  vSyncCount=" + QualitySettings.vSyncCount + " targetFrameRate=" + Application.targetFrameRate);
                    return;
                }
                case "say":
                    Out("  " + string.Join(" ", a, 1, a.Length - 1));
                    return;
                case "quit":
                    Out("  quitting");
                    Application.Quit();
                    return;
                default:
                    Out("  unknown command");
                    return;
            }
        }

        /// <summary>The first pack or hotbar slot holding an item of that type.</summary>
        private static InvSlot FindSlot(Player p, string type)
        {
            foreach (Inventory inv in new[] { p.Inventory, p.Hotbar })
                foreach (InvSlot s in inv.slots)
                    if (s != null && !InvItemClass.isNull(s.invItem)
                        && string.Equals(s.invItem.type, type, StringComparison.OrdinalIgnoreCase))
                        return s;
            return null;
        }

        private static string Contents(Inventory inv)
        {
            if (inv == null)
                return "";
            var sb = new StringBuilder();
            foreach (InvSlot s in inv.slots)
                if (s != null && !InvItemClass.isNull(s.invItem))
                    sb.Append(sb.Length > 0 ? " " : "").Append(s.invItem.type).Append('x').Append(s.invItem.amount);
            return sb.ToString();
        }

        /// <summary>Living NPCs in the loaded world that have something to say.</summary>
        private static List<NPC> Talkable()
        {
            var list = new List<NPC>();
            foreach (NPC n in UnityEngine.Object.FindObjectsOfType<NPC>())
            {
                if (n == null || n.characterDialogue == null || !n.wantsToTalk || !n.gameObject.activeInHierarchy)
                    continue;
                CharBase cb = n.GetComponent<CharBase>();
                if (cb != null && !cb.alive)
                    continue;
                list.Add(n);
            }
            return list;
        }

        /// <summary>One click of the running talk ("talk" / "talkall"), every 0.35 s.</summary>
        private static void TalkTick(float now)
        {
            Player p = Player.Instance;
            DialogueWindow w = Singleton<UI>.Instance.dialogueWindow;
            if (_talking == null)
            {
                if (w.opened)
                    return; // someone else's dialogue (a cutscene's) still up
                while (_talkQueue.Count > 0 && _talking == null)
                {
                    NPC next = _talkQueue[0];
                    _talkQueue.RemoveAt(0);
                    if (next != null && next.wantsToTalk && next.gameObject.activeInHierarchy)
                        _talking = next;
                }
                if (_talking == null)
                {
                    TalkDone();
                    return;
                }
                p.teleportTo(_talking.transform.position + new Vector3(12f, 0f, 0f), p.transform.rotation);
                GiveAsked(p, _talking);
                _talkAt = now;
                _talkSteps = 0;
                _talked++;
                Out("  talk " + _talking.name + "@" + Pos(_talking.transform.position));
                _talking.talkTo();
                return;
            }
            if (!w.opened)
            {
                if (_talkSteps == 0 && now - _talkAt < 4f)
                    return; // the window tweens open
                Out("  talk " + (_talking != null ? _talking.name : "?") + " ended after " + _talkSteps + " clicks");
                _talking = null;
                if (_talkQueue.Count == 0)
                    TalkDone();
                return;
            }
            _talkSteps++;
            if (_talkSteps > 200 || now - _talkAt > 150f)
            {
                Out("  talk " + _talking.name + " stuck in " + (w.currentDialogue != null ? w.currentDialogue.name : "menu") + ", closing");
                w.close();
                _talking = null;
                if (_talkQueue.Count == 0)
                    TalkDone();
                return;
            }
            if (p.inShop)
            {
                w.closeTrade();
                return;
            }
            if (w.displayingDialogue)
            {
                if (w.needsDecision && w.boardFinished && w.menuOptions.Count > 0)
                {
                    string key = _talking.name + "|" + (w.currentDialogue != null ? w.currentDialogue.name : "?");
                    _decisionTurns.TryGetValue(key, out int turn);
                    _decisionTurns[key] = turn + 1;
                    int pick = turn % w.menuOptions.Count;
                    Out("  decision " + key + " → " + pick + "/" + w.menuOptions.Count + " " + Label(w.menuOptions[pick]));
                    w.menuOptions[pick].getClicked(force: true);
                    return;
                }
                AccessTools.Method(typeof(DialogueWindow), "onInstantClick").Invoke(w, null);
                return;
            }
            // The options menu, or the "show item" list.
            Button exit = null;
            foreach (Button b in w.menuOptions)
            {
                if (b == null)
                    continue;
                string fn = b.function ?? "";
                if (fn == "exitDialogue" || fn == "exitItemsDialogue")
                {
                    exit = b;
                    continue;
                }
                if (fn == "showTrading")
                    continue;
                string key = _talking.name + "|" + fn + "|" + Label(b);
                if (!_visited.Add(key))
                    continue;
                Out("  option " + key);
                b.getClicked(force: true);
                return;
            }
            if (exit != null)
                exit.getClicked(force: true);
            else
                w.close();
        }

        private static void TalkDone() => Out("  talkall done n=" + _talked);

        private static string Label(Button b)
        {
            DialogueButton db = b.GetComponent<DialogueButton>();
            if (db != null && !string.IsNullOrEmpty(db.destDialogueName))
                return db.destDialogueName;
            return b.textMesh != null ? b.textMesh.text : b.name;
        }

        /// <summary>Hand the speaker one of every item the NPC's "show item" dialogues ask for.</summary>
        private static void GiveAsked(Player p, NPC n)
        {
            foreach (CharacterDialogue.Dialogue d in n.characterDialogue.dialogues)
            {
                if (d == null || d.type != CharacterDialogue.Dialogue.Type.item || d.disabled || string.IsNullOrEmpty(d.itemType))
                    continue;
                if (p.Inventory.getItemInPlayer(d.itemType) != null)
                    continue;
                if (Singleton<ItemsDatabase>.Instance.getItem(d.itemType, instantiate: false) == null)
                    continue;
                try
                {
                    p.Inventory.addItemTypeToPlayer(d.itemType, 1, dropIfNoRoom: true);
                    Out("  gave " + d.itemType + " for " + n.name);
                }
                catch (Exception ex)
                {
                    Out("  give " + d.itemType + " failed: " + ex.Message);
                }
            }
        }

        private static void Status(LanNetworkManager net)
        {
            Player p = Player.Instance;
            Controller c = Singleton<Controller>.Instance;
            string loc = p.whereAmI != null && p.whereAmI.bigLocation != null ? p.whereAmI.bigLocation.name : "-";
            var sb = new StringBuilder();
            sb.Append("  role=").Append(net.Role).Append(" id=").Append(net.LocalPlayerId)
                .Append(" pos=").Append(Pos(p.transform.position)).Append(" loc=").Append(loc)
                .Append(" hp=").Append(Mathf.RoundToInt(p.health)).Append('/').Append(Mathf.RoundToInt(p.maxHealth))
                .Append(" alive=").Append(p.alive)
                .Append(" day=").Append(c != null ? c.day : -1).Append(" time=").Append(c != null ? c.CurrentTime : -1)
                .Append(" dream=").Append(DreamSyncManager.IsDreamActive)
                .Append(" clockOn=").Append(c != null && c.DoUpdateTime)
                .Append(" timeScale=").Append(Time.timeScale.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" inOutsideLoc=").Append(Singleton<OutsideLocations>.Instance != null && Singleton<OutsideLocations>.Instance.playerInOutsideLocation)
                .Append(" afterNight=").Append(c != null && c.isAfterNight)
                .Append(" scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)
                .Append(" ending=").Append(EpilogueNetHandlers.IsLocalInEpilogue())
                .Append(" grid=").Append(Singleton<WorldGrid>.Instance != null && Singleton<WorldGrid>.Instance.currentGrid != null ? Singleton<WorldGrid>.Instance.currentGrid.name : "-");
            NightScenarios ns = Singleton<NightScenarios>.Instance;
            NightScenario sc = ns != null ? ns.currentScenario : null;
            sb.Append(" scenario=").Append(sc != null ? sc.name : "-")
                .Append(" event=").Append(sc != null && sc.currentEvent != null ? sc.currentEvent.name : "-")
                .Append(" home=").Append(p.experienceMachine != null ? Pos(p.experienceMachine.transform.position) + (p.experienceMachine.isOn ? " lit" : " unlit") : "-");
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
                if (proxy != null)
                    sb.Append(" | p").Append(proxy.PlayerId).Append('@').Append(Pos(proxy.transform.position))
                        .Append(" hp%=").Append(proxy.RemoteHealthPct)
                        .Append(proxy.CachedCharBase != null && !proxy.CachedCharBase.alive ? " dead" : "")
                        .Append(proxy.RemoteInEpilogue ? " ending" : "");
            Out(sb.ToString());
        }

        /// <summary>Distance on the ground plane (Darkwood's height axis is not the walking plane).</summary>
        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static T Nearest<T>(IList<T> items, float radius) where T : Component
        {
            Vector3 at = Player.Instance.transform.position;
            T best = null;
            float bestD = radius;
            for (int i = 0; i < items.Count; i++)
            {
                T it = items[i];
                if (it == null)
                    continue;
                float d = Flat(at, it.transform.position);
                if (d < bestD)
                {
                    bestD = d;
                    best = it;
                }
            }
            return best;
        }

        private static void ListNear<T>(IList<T> items, float radius, Func<T, string> describe) where T : Component
        {
            Vector3 at = Player.Instance.transform.position;
            int shown = 0;
            for (int i = 0; i < items.Count && shown < 40; i++)
            {
                T it = items[i];
                if (it == null)
                    continue;
                float d = Flat(at, it.transform.position);
                if (d > radius)
                    continue;
                Out("  " + it.name + "@" + Pos(it.transform.position) + " d=" + d.ToString("0.0", CultureInfo.InvariantCulture)
                    + " " + describe(it));
                shown++;
            }
            if (shown == 0)
                Out("  none within " + radius);
        }

        private static string Pos(Vector3 v)
            => v.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + v.z.ToString("0.0", CultureInfo.InvariantCulture);

        private static void Out(string line)
        {
            ModLog.Event(LogCat.Core, "[Pilot] " + line);
            try
            {
                File.AppendAllText(Path.Combine(Dir, "out.txt"),
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line + "\n");
            }
            catch (IOException) { }
        }
    }

    /// <summary>Pilot runs only: notes each sound started, for "audio" / "audiodump".</summary>
    [HarmonyPatch(typeof(AudioController), nameof(AudioController.PlayAudioItem))]
    internal static class PilotAudioTap
    {
        private static bool Prepare() => TestPilot.Active;

        private static void Postfix(AudioItem sndItem, Vector3 worldPosition, AudioObject __result)
        {
            if (sndItem == null)
                return;
            TestPilot.NoteSound(sndItem.Name, __result != null ? __result.transform.position : worldPosition);
        }
    }
}

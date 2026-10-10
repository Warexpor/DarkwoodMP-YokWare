using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;
using YokWare.VanillaMenu;

namespace DWMPHorde
{
    /// <summary>
    /// The screens behind MULTIPLAYER, in the look of the vanilla Profiles and Options menus:
    /// Multiplayer (what the player can do now), Host, Join, Settings, and the profile picker for
    /// the host's world. On the title screen they lead into a game; in the pause menu they run the
    /// session (invite, send the world again, restore the character, leave).
    /// </summary>
    internal static partial class MultiplayerScreens
    {
        private static VmScreen _root; // process-scoped: menu screens, rebuilt on show
        private static VmScreen _host; // process-scoped: menu screens
        private static VmScreen _join; // process-scoped: menu screens
        private static VmScreen _friends; // process-scoped: menu screens
        private static VmScreen _settings; // process-scoped: menu screens
        private static VmScreen _hostSettings; // process-scoped: menu screens
        private static VmScreen _picker; // process-scoped: menu screens
        private static VmSettingsPage _settingsPage; // process-scoped: Apply / Revert to default of Settings
        private static VmSettingsPage _hostSettingsPage; // process-scoped: Apply / Revert to default of Host settings
        private static string _pickerStatus; // process-scoped: menu status line
        private static float _pickerStatusUntil; // process-scoped: menu status line timer

        private static LanNetworkManager Net => ModRuntime.Network;
        private static NetworkRole Role => Net != null ? Net.Role : NetworkRole.Offline;

        private static void Ensure()
        {
            if (_root != null)
                return;
            _root = new VmScreen("Multiplayer") { Build = BuildRoot, Signature = RootSignature, Tick = TickStatus };
            _host = new VmScreen("MultiplayerHost") { Build = BuildHost, Parent = _root, Tick = TickStatus };
            _join = new VmScreen("MultiplayerJoin") { Build = BuildJoin, Signature = JoinSignature, Parent = _root, Tick = TickStatus };
            _friends = new VmScreen("MultiplayerSteamFriends") { Build = BuildFriends, Signature = JoinSignature, Parent = _join, Tick = TickStatus };
            _settings = new VmScreen("MultiplayerSettings") { Build = BuildSettings, Signature = SettingsSignature, Parent = _root };
            _hostSettings = new VmScreen("MultiplayerHostSettings") { Build = BuildHostSettings, Signature = HostSettingsSignature, Parent = _root };
            _settingsPage = new VmSettingsPage(_settings, SettingsEntries);
            _hostSettingsPage = new VmSettingsPage(_hostSettings, HostSettingsEntries);
            EnsureVoice();
            _picker = new VmScreen("MultiplayerWorldCopy") { Build = BuildPicker, Parent = _join, Tick = TickStatus, OnBack = PickerBack };
        }

        /// <summary>MULTIPLAYER in the title or pause stack.</summary>
        internal static void OpenRoot()
        {
            Ensure();
            ModLog.Event(LogCat.Session, "MULTIPLAYER menu opened (" + (GameScreen.AtTitle ? "title" : "pause") + ")");
            Vm.Open(_root);
        }

        /// <summary>A connect began outside the Join screen (Steam invite): show its progress.</summary>
        internal static void OpenJoinProgress()
        {
            Ensure();
            if (GameScreen.AtTitle && Vm.Menu != null && Vm.Menu.gameObject.activeInHierarchy)
                Vm.Open(_join);
        }

        internal static void OpenSlotPicker()
        {
            Ensure();
            Vm.Open(_picker);
        }

        /// <summary>
        /// Every frame: on the title screen the profile picker comes up by itself once the host's
        /// world has arrived, wherever the player is in the menu.
        /// </summary>
        internal static void AutoOpen()
        {
            if (!GameScreen.AtTitle || Net == null)
                return;
            var share = Net.WorldSaveShare;
            if (share == null || !share.IsAwaitingSlotPick || share.HasTerminalShareFailure || Role == NetworkRole.Offline)
                return;
            MainMenu menu = Vm.Menu;
            if (menu == null || !menu.gameObject.activeInHierarchy)
                return;
            Ensure();
            if (Vm.Current == _picker)
                return;
            ModLog.Event(LogCat.Session, "Host world downloaded — profile picker opened");
            Vm.Open(_picker);
        }

        // ------------------------------------------------------------------
        // Status line (under the heading of every screen)
        // ------------------------------------------------------------------

        private static tk2dTextMesh _status; // process-scoped: the shown screen's status label
        private static string _statusShown; // process-scoped: menu status line

        /// <summary>The status line sits between a screen's entries and its Back.</summary>
        private static void StatusLabel(VmBuilder b, float z = -125f)
        {
            _statusShown = null;
            _status = b.Label("", 0f, z, TextAnchor.MiddleCenter, Vm.Grey, 560);
            TickStatus();
        }

        private static void TickStatus()
        {
            if (_status == null)
                return;
            string line = StatusText();
            string shown = string.IsNullOrEmpty(line) ? "" : Loc.T(line);
            if (shown == _statusShown)
                return;
            _statusShown = shown;
            Vm.SetText(_status, shown);
        }

        private static string StatusText()
        {
            if (Vm.Current == _picker && !string.IsNullOrEmpty(_pickerStatus) && Time.realtimeSinceStartup < _pickerStatusUntil)
                return _pickerStatus;
            string flash = MainMenuMultiplayerInject.FlashLine;
            if (flash != null)
                return flash;
            var net = Net;
            if (net == null)
                return null;
            if (net.Role == NetworkRole.Host)
            {
                int others = net.ConnectedPlayerCount;
                string where = net.IsSteamSession ? "Hosting a Steam game" : "Hosting on the local network";
                if (MainMenuMultiplayerInject.HostWaitingForSave)
                    return where + " — choose a profile to play";
                string share = net.WorldSaveShare != null ? net.WorldSaveShare.ProgressText : null;
                if (!string.IsNullOrEmpty(share) && net.WorldSaveShare.IsBusy)
                    return share;
                return others == 1 ? where + " — 1 player joined" : where + " — " + others + " players joined";
            }
            return MainMenuMultiplayerInject.JoinStatusLine();
        }

        // ------------------------------------------------------------------
        // Multiplayer
        // ------------------------------------------------------------------

        private static string RootSignature()
        {
            var net = Net;
            var share = net?.WorldSaveShare;
            return GameScreen.AtTitle + "|" + Role + "|" + (net != null && net.IsSteamSession)
                + "|" + MainMenuMultiplayerInject.HostWaitingForSave + "|" + (share != null && share.IsAwaitingEnterWorld)
                + "|" + CanRestoreSelf(out _);
        }

        private static void BuildRoot(VmBuilder b)
        {
            b.Header("Multiplayer");
            StatusLabel(b);
            var items = new List<KeyValuePair<string, Action>>(6);
            var net = Net;
            NetworkRole role = Role;
            if (GameScreen.AtTitle)
            {
                if (role == NetworkRole.Offline)
                {
                    items.Add(Entry("Host", () => Vm.Open(_host)));
                    items.Add(Entry("Join", () => Vm.Open(_join)));
                }
                else if (role == NetworkRole.Host)
                {
                    items.Add(Entry("Choose a profile", ChooseHostProfile));
                    if (net != null && net.IsSteamSession)
                        items.Add(Entry("Invite friends", InviteFriends));
                }
                else
                {
                    var share = net?.WorldSaveShare;
                    if (share != null && share.IsAwaitingEnterWorld && !share.HasTerminalShareFailure)
                        items.Add(Entry("Enter world", MainMenuMultiplayerInject.EnterWorld));
                    else
                        items.Add(Entry("Join", () => Vm.Open(_join)));
                }
            }
            else
            {
                if (role == NetworkRole.Offline)
                    items.Add(Entry("Host this game", () => Vm.Open(_host)));
                if (role == NetworkRole.Host && net != null && net.IsSteamSession)
                    items.Add(Entry("Invite friends", InviteFriends));
                if (role == NetworkRole.Host)
                    items.Add(Entry("Send the world again", ResendWorld));
                if (role == NetworkRole.Client && CanRestoreSelf(out _))
                    items.Add(Entry("Restore my character", ConfirmRestoreSelf));
            }
            items.Add(Entry("Settings", () => Vm.Open(_settings)));
            // What a host decides for everyone: a client in a game plays by the host's choice.
            if (role != NetworkRole.Client)
                items.Add(Entry("Host settings", () => Vm.Open(_hostSettings)));
            if (role != NetworkRole.Offline)
                items.Add(Entry("Disconnect", ConfirmDisconnect));

            for (int i = 0; i < items.Count; i++)
                b.Item(items[i].Key, Vm.ItemTopZ - Vm.ItemStep * i, items[i].Value);
            b.Back();
            // A LAN host has nothing to hand out but this: the address the others type in.
            if (role == NetworkRole.Host && net != null && !net.IsSteamSession)
                b.Label(Loc.T("Your address") + ": " + LanAddressText(), 0f, -215f, TextAnchor.MiddleCenter, Vm.Grey, 560);
            b.Label(PluginInfo.Name + " " + PluginInfo.Version, 0f, -250f, TextAnchor.MiddleCenter, Vm.Dim);
        }

        private static string _lanAddress; // process-scoped: menu text, refreshed every few seconds
        private static float _lanAddressAt = -100f; // process-scoped: menu text timer

        /// <summary>This computer's local network addresses and the host port ("192.168.1.5:7788").</summary>
        private static string LanAddressText()
        {
            if (_lanAddress != null && Time.realtimeSinceStartup - _lanAddressAt < 5f)
                return _lanAddress;
            _lanAddressAt = Time.realtimeSinceStartup;
            var found = new List<string>(2);
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                        || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        System.Net.IPAddress ip = ua.Address;
                        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(ip))
                            continue;
                        string text = ip.ToString();
                        // 169.254.x.x: a card with no network behind it.
                        if (text.StartsWith("169.254.", StringComparison.Ordinal) || found.Contains(text))
                            continue;
                        if (found.Count < 2)
                            found.Add(text);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Session, "LAN address lookup failed: " + ex.Message);
            }
            int port = ModConfig.GetConnectPort();
            _lanAddress = found.Count == 0 ? "?:" + port : string.Join(" / ", found.ToArray()) + ":" + port;
            return _lanAddress;
        }

        private static KeyValuePair<string, Action> Entry(string english, Action a) => new KeyValuePair<string, Action>(english, a);

        private static void ChooseHostProfile()
        {
            MainMenu menu = Vm.Menu;
            Vm.CloseAll(restoreMenu: false);
            if (menu != null)
                menu.displayProfilesMenu();
        }

        private static void InviteFriends()
        {
            var net = Net;
            if (net == null || !net.IsSteamSession || string.IsNullOrEmpty(net.SteamLobbyIdText))
                return;
            Networking.Steam.SteamCoopTransport.CopyToClipboard(net.SteamLobbyIdText);
            net.InviteSteamFriends();
            MainMenuMultiplayerInject.Flash("Lobby id copied — invite friends in the Steam overlay");
        }

        private static void ResendWorld()
        {
            var net = Net;
            if (net == null || net.Role != NetworkRole.Host)
                return;
            if (!net.IsConnected || !net.IsHandshakeComplete)
            {
                MainMenuMultiplayerInject.Flash("Nobody has joined yet");
                return;
            }
            if (net.WorldSaveShare == null || net.WorldSaveShare.IsBusy)
            {
                MainMenuMultiplayerInject.Flash("The world is already being sent");
                return;
            }
            net.WorldSaveShare.ScheduleHostResend();
            MainMenuMultiplayerInject.Flash("Sending the world to the other players…");
        }

        private static void ConfirmDisconnect()
        {
            VmScreen s = Vm.Current;
            string q = Role == NetworkRole.Host
                ? "Stop hosting? The other players are moved to a new host when one can take over."
                : "Leave the game?";
            s?.Confirm(q, yes =>
            {
                if (!yes)
                    return;
                MainMenuMultiplayerInject.Disconnect();
                // Alone again in the pause menu: it pauses the game, as in single player. (A host
                // handing over stays online until the hand-off is done and keeps the co-op rule.)
                if (Role == NetworkRole.Offline && GameScreen.InPauseMenu)
                    Core.pause(false, 0f);
                Vm.Current?.Rebuild();
            });
        }

        // ------------------------------------------------------------------
        // Host
        // ------------------------------------------------------------------

        private static void BuildHost(VmBuilder b)
        {
            b.Header("Host");
            StatusLabel(b);
            bool steam = Networking.Steam.SteamCoopTransport.IsSteamReady(out _);
            b.Label(Loc.T(GameScreen.AtTitle
                    ? "Choose a profile next. Other players join once you are in the game."
                    : "Other players get a copy of this world when they join."),
                0f, 160f, TextAnchor.MiddleCenter, Vm.Grey, 560);
            b.Item("Local network", Vm.ItemTopZ - Vm.ItemStep * 1, () => StartHost(steam: false));
            b.Item("Steam", Vm.ItemTopZ - Vm.ItemStep * 2, () => StartHost(steam: true), enabled: steam);
            b.Back();
        }

        private static void StartHost(bool steam)
        {
            bool ok = steam ? MainMenuMultiplayerInject.HostSteam() : MainMenuMultiplayerInject.HostLan();
            if (!ok)
                return;
            if (GameScreen.AtTitle)
                ChooseHostProfile();
            else
                Vm.Open(_root);
        }

        // ------------------------------------------------------------------
        // Join (title screen)
        // ------------------------------------------------------------------

        private static string JoinSignature()
        {
            var share = Net?.WorldSaveShare;
            return Role + "|" + MainMenuMultiplayerInject.JoinPending
                + "|" + (share != null && share.IsAwaitingEnterWorld) + "|" + (share != null && share.HasTerminalShareFailure);
        }

        private static void BuildJoin(VmBuilder b)
        {
            b.Header("Join");
            StatusLabel(b);
            NetworkRole role = Role;
            bool busy = role != NetworkRole.Offline || MainMenuMultiplayerInject.JoinPending;
            if (!busy)
            {
                // Options-style rows; the Options menu sits 100 units above the centre.
                b.TextField("Address", 100f + 60f, () => ModConfig.ConnectAddress?.Value ?? "",
                    v => { if (ModConfig.ConnectAddress != null) ModConfig.ConnectAddress.Value = v; }, 64);
                b.TextField("Port", 100f + 20f, () => ModConfig.GetConnectPort().ToString(), SetPort, 5, accept: char.IsDigit);
                b.TextField("Password", 100f - 20f, () => ModConfig.HostPassword?.Value ?? "",
                    v => { if (ModConfig.HostPassword != null) ModConfig.HostPassword.Value = v; }, 64, masked: true);
                b.Item("Connect", 100f - 70f, MainMenuMultiplayerInject.JoinLan);

                bool steam = Networking.Steam.SteamCoopTransport.IsSteamReady(out _);
                b.Item("Steam friends", 100f - 130f, () => Vm.Open(_friends), enabled: steam);
                b.Back();
                return;
            }

            var share = Net?.WorldSaveShare;
            if (role == NetworkRole.Client && share != null && share.IsAwaitingEnterWorld && !share.HasTerminalShareFailure)
            {
                b.Item("Enter world", Vm.ItemTopZ - Vm.ItemStep, MainMenuMultiplayerInject.EnterWorld);
                b.Item("Disconnect", Vm.ItemTopZ - Vm.ItemStep * 2, ConfirmDisconnect);
            }
            else if (role == NetworkRole.Host)
            {
                b.Item("Choose a profile", Vm.ItemTopZ - Vm.ItemStep, ChooseHostProfile);
                b.Item("Disconnect", Vm.ItemTopZ - Vm.ItemStep * 2, ConfirmDisconnect);
            }
            else
            {
                if (role == NetworkRole.Offline || !Net.IsHandshakeComplete)
                    b.Item("Cancel", Vm.ItemTopZ - Vm.ItemStep, MainMenuMultiplayerInject.Disconnect);
                else
                    b.Item("Disconnect", Vm.ItemTopZ - Vm.ItemStep, ConfirmDisconnect);
            }
            b.Back();
        }

        // ------------------------------------------------------------------
        // Join > Steam friends
        // ------------------------------------------------------------------

        private const int MaxFriendRows = 3;

        /// <summary>
        /// Friends who are in a lobby of this game right now, each one click to join. Joining used
        /// to need the host's lobby id typed in, or an invite found in the Steam overlay.
        /// </summary>
        private static void BuildFriends(VmBuilder b)
        {
            b.Header("Steam friends");
            StatusLabel(b);
            if (Role != NetworkRole.Offline || MainMenuMultiplayerInject.JoinPending)
            {
                // The join is under way: its progress and buttons are the Join screen's.
                b.Item("Cancel", Vm.ItemTopZ - Vm.ItemStep, MainMenuMultiplayerInject.Disconnect);
                b.Back();
                return;
            }

            List<Networking.Steam.SteamCoopTransport.FriendLobby> lobbies =
                Networking.Steam.SteamCoopTransport.FindFriendLobbies(MaxFriendRows);
            float z = Vm.ItemTopZ;
            if (lobbies.Count == 0)
            {
                b.Label(Loc.T("No friend is hosting right now. Ask the host for an invite, or type in the lobby id."),
                    0f, z, TextAnchor.MiddleCenter, Vm.Grey, 560);
                z -= Vm.ItemStep;
            }
            for (int i = 0; i < lobbies.Count; i++)
            {
                ulong id = lobbies[i].LobbyId;
                b.Item(lobbies[i].Name, z, () => JoinFriendLobby(id));
                z -= Vm.ItemStep;
            }
            b.Item("Refresh", z, () => Vm.Current?.Rebuild());

            b.TextField("Steam lobby", -55f, () => ModConfig.SteamLobbyId?.Value ?? "",
                v => { if (ModConfig.SteamLobbyId != null) ModConfig.SteamLobbyId.Value = v; }, 24, accept: char.IsDigit,
                enabled: true);
            b.Item("Join the lobby", -95f, JoinTypedLobby);
            b.Back();
        }

        private static void JoinFriendLobby(ulong lobbyId)
        {
            if (ModConfig.SteamLobbyId != null)
                ModConfig.SteamLobbyId.Value = lobbyId.ToString();
            JoinTypedLobby();
        }

        private static void JoinTypedLobby()
        {
            MainMenuMultiplayerInject.JoinSteam();
            // The Join screen shows the progress, the cancel button and the world download.
            if (Role != NetworkRole.Offline || MainMenuMultiplayerInject.JoinPending)
                Vm.Open(_join);
        }

        private static void SetPort(string v)
        {
            if (ModConfig.ConnectPort == null)
                return;
            if (int.TryParse(v, out int p))
                ModConfig.ConnectPort.Value = Mathf.Clamp(p, ModConfig.MinPort, ModConfig.MaxPort);
        }

        // ------------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------------

        private static string SettingsSignature() => VoiceIndex().ToString();

        private static string HostSettingsSignature() => Role.ToString();

        private static readonly string[] YesNo = { "No", "Yes" };
        private static readonly string[] VoiceChoices = { "Off", "Push to talk", "Always on" };
        private static readonly string[] NameChoices = { "Off", "When pointed at", "Always" };
        private static readonly string[] NameValues = { "off", "pointed", "always" };
        private static readonly string[] LobbyChoices = { "Friends only", "Public", "Invite only" };
        private static readonly string[] LobbyValues = { "friends", "public", "private" };
        private static readonly string[] LootChoices = { "Off", "Grows with the party" };
        private static readonly string[] NightMonsterChoices = { "x1", "x1.5", "x2", "x3", "x4", "x5", "x7", "x10" };
        private static readonly float[] NightMonsterValues = { 1f, 1.5f, 2f, 3f, 4f, 5f, 7f, 10f };

        /// <summary>This player's own settings; voice has a screen of its own.</summary>
        private static void BuildSettings(VmBuilder b)
        {
            b.Header("Settings", 178f + 100f);
            float z = 100f + 80f;
            const float step = Vm.RowStep;

            b.TextField("Name", z, () => ModConfig.PlayerName?.Value ?? "Player",
                v => { if (ModConfig.PlayerName != null) ModConfig.PlayerName.Value = string.IsNullOrEmpty(v) ? "Player" : v; },
                Sync.PlayerNames.MaxLength);
            z -= step;
            b.Choice("Player names", z, NameChoices, NameIndex,
                i => { if (ModConfig.ShowPlayerNames != null) ModConfig.ShowPlayerNames.Value = NameValues[i]; });
            z -= step;
            b.Choice("Text chat", z, YesNo, () => ModConfig.ChatEnabled != null && ModConfig.ChatEnabled.Value ? 1 : 0,
                i => { if (ModConfig.ChatEnabled != null) ModConfig.ChatEnabled.Value = i == 1; });
            z -= step;
            // Its own screen: mode, key, microphone, levels and each player's volume.
            b.Name("Voice chat", z);
            b.Value(Loc.T(VoiceChoices[VoiceIndex()]) + " ...", z, () => Vm.Open(_voice));
            z -= step;
            b.Slider("Other players' steps", z, () => ModConfig.PeerMovementVolume?.Value ?? 0.85f,
                t => { if (ModConfig.PeerMovementVolume != null) ModConfig.PeerMovementVolume.Value = Mathf.Round(t * 20f) / 20f; });
            b.Return();
            _settingsPage.Buttons(b);
        }

        /// <summary>A config setting for a <see cref="VmSettingsPage"/> (null when not bound).</summary>
        /// <summary><paramref name="ask"/>: a change with consequences, confirmed when leaving unapplied.</summary>
        private static VmSetting Setting<T>(ModSetting<T> m, bool enabled = true, bool ask = false)
        {
            if (m == null)
                return null;
            return new VmSetting { Key = m.Id, Get = () => m.Value, Set = v => m.Value = (T)v, Default = m.Default, Enabled = enabled, Ask = ask };
        }

        private static IEnumerable<VmSetting> SettingsEntries()
        {
            // The name everyone sees; the rest only changes this player's own screen and ears.
            yield return Setting(ModConfig.PlayerName, ask: true);
            yield return Setting(ModConfig.ShowPlayerNames);
            yield return Setting(ModConfig.ChatEnabled);
            yield return Setting(ModConfig.PeerMovementVolume);
        }

        /// <summary>
        /// The rules, plus how the game is opened while not hosting yet (locked once online). A
        /// rule changed mid-game changes it for everyone at once, so leaving unapplied asks; before
        /// hosting nothing is live yet.
        /// </summary>
        private static IEnumerable<VmSetting> HostSettingsEntries()
        {
            bool offline = Role == NetworkRole.Offline;
            yield return Setting(ModConfig.FriendlyFireEnabled, ask: !offline);
            yield return Setting(ModConfig.LootShareModeSetting, ask: !offline);
            yield return Setting(ModConfig.DoubleItemsEnabled, ask: !offline);
            yield return Setting(ModConfig.NightMonsterMultiplier, ask: !offline);
            yield return Setting(ModConfig.VoiceAlertsEnemies, ask: !offline);
            yield return Setting(ModConfig.MaxPlayers, offline);
            yield return Setting(ModConfig.SteamLobbyType, offline);
            yield return Setting(ModConfig.ConnectPort, offline);
            yield return Setting(ModConfig.HostPassword, offline);
        }

        /// <summary>
        /// What the host decides for everyone. The rules apply at once, also mid-game; the way the
        /// game is opened (players, lobby, port, password) only before hosting.
        /// </summary>
        private static void BuildHostSettings(VmBuilder b)
        {
            b.Header("Host settings", 178f + 100f);
            float z = 100f + 80f;
            const float step = Vm.RowStep;
            bool offline = Role == NetworkRole.Offline;

            b.Choice("Friendly fire", z, YesNo, () => ModConfig.FriendlyFireEnabled == null || ModConfig.FriendlyFireEnabled.Value ? 1 : 0,
                i => { if (ModConfig.FriendlyFireEnabled != null) ModConfig.FriendlyFireEnabled.Value = i == 1; });
            z -= step;
            b.Choice("Extra loot", z, LootChoices, () => ModConfig.GetLootShareMode() == LootShareMode.Off ? 0 : 1, SetLoot);
            z -= step;
            b.Choice("Night monsters", z, NightMonsterChoices, NightMonsterIndex,
                i => { if (ModConfig.NightMonsterMultiplier != null) ModConfig.NightMonsterMultiplier.Value = NightMonsterValues[i]; });
            z -= step;
            b.Choice("Creatures hear voices", z, YesNo, () => ModConfig.VoiceAlertsEnemies == null || ModConfig.VoiceAlertsEnemies.Value ? 1 : 0,
                i => { if (ModConfig.VoiceAlertsEnemies != null) ModConfig.VoiceAlertsEnemies.Value = i == 1; });
            z -= step;
            b.Choice("Players", z, PlayerChoices, () => Mathf.Clamp((ModConfig.MaxPlayers?.Value ?? 8) - 2, 0, PlayerChoices.Length - 1),
                i => { if (ModConfig.MaxPlayers != null) ModConfig.MaxPlayers.Value = i + 2; },
                enabled: offline);
            z -= step;
            b.Choice("Steam lobby", z, LobbyChoices, LobbyIndex,
                i => { if (ModConfig.SteamLobbyType != null) ModConfig.SteamLobbyType.Value = LobbyValues[i]; },
                enabled: offline);
            z -= step;
            b.TextField("Port", z, () => ModConfig.GetConnectPort().ToString(), SetPort, 5, accept: char.IsDigit,
                enabled: offline);
            z -= step;
            b.TextField("Password", z, () => ModConfig.HostPassword?.Value ?? "",
                v => { if (ModConfig.HostPassword != null) ModConfig.HostPassword.Value = v; }, 64, masked: true,
                enabled: offline);
            b.Return();
            _hostSettingsPage.Buttons(b);
        }

        private static int NameIndex()
        {
            string v = ModConfig.ShowPlayerNames?.Value ?? "pointed";
            for (int i = 0; i < NameValues.Length; i++)
            {
                if (string.Equals(v, NameValues[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 1;
        }

        private static readonly string[] PlayerChoices = { "2", "3", "4", "5", "6", "7", "8" };

        private static int VoiceIndex()
        {
            if (ModConfig.VoiceEnabled == null || !ModConfig.VoiceEnabled.Value)
                return 0;
            return string.Equals(ModConfig.VoiceMode?.Value ?? "ptt", "ptt", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        private static void SetVoice(int i)
        {
            if (ModConfig.VoiceEnabled != null)
                ModConfig.VoiceEnabled.Value = i != 0;
            if (i != 0 && ModConfig.VoiceMode != null)
                ModConfig.VoiceMode.Value = i == 1 ? "ptt" : "open";
        }

        /// <summary>The listed step at or just below the current multiplier (a hand-edited 2.5 shows as x2).</summary>
        private static int NightMonsterIndex()
        {
            float v = CoopBalance.NightMonsterMultiplier;
            int best = 0;
            for (int i = 0; i < NightMonsterValues.Length; i++)
            {
                if (NightMonsterValues[i] <= v + 0.001f)
                    best = i;
            }
            return best;
        }

        private static int LobbyIndex()
        {
            string v = (ModConfig.SteamLobbyType?.Value ?? "friends").Trim().ToLowerInvariant();
            int i = Array.IndexOf(LobbyValues, v);
            return i < 0 ? 0 : i;
        }

        private static void SetLoot(int i)
        {
            if (ModConfig.LootShareModeSetting != null)
                ModConfig.LootShareModeSetting.Value = i == 0 ? "Off" : "ScaleWithPlayers";
            if (i == 1 && ModConfig.DoubleItemsEnabled != null)
                ModConfig.DoubleItemsEnabled.Value = true;
        }

        // ------------------------------------------------------------------
        // The profile that keeps the host's world (title screen, after the download)
        // ------------------------------------------------------------------

        private static void PickerFlash(string english)
        {
            _pickerStatus = english;
            _pickerStatusUntil = Time.realtimeSinceStartup + 6f;
        }

        private static void BuildPicker(VmBuilder b)
        {
            b.Header("The host's world");
            StatusLabel(b);
            b.Label(Loc.T("Choose the profile that keeps a copy of the host's world. It stays on this computer until you delete the profile."),
                0f, 150f, TextAnchor.MiddleCenter, Vm.Grey, 560);

            var share = Net?.WorldSaveShare;
            if (share == null)
            {
                b.Back();
                return;
            }
            ProfileSlotInfo[] slots = share.GetProfileSlotInfos();
            int preferred = ModConfig.PreferredCoopCopySlot != null ? ModConfig.PreferredCoopCopySlot.Value : 0;
            float z = 90f;
            for (int i = 0; i < slots.Length; i++)
            {
                ProfileSlotInfo s = slots[i];
                b.Name(Vm.Vanilla("Profile") + " " + s.Id, z, -40f);
                Button v = b.Value(SlotText(s, preferred), z, () => PickSlot(s), -10f);
                if (v != null && s.CampaignMismatchWithHost && v.textMesh != null)
                {
                    v.textMesh.color = Vm.Warn;
                    v.textMesh.Commit();
                }
                z -= Vm.RowStep;
            }
            b.Back();
        }

        private static string SlotText(ProfileSlotInfo s, int preferred)
        {
            string chDay = Vm.Vanilla("chapter") + " " + Mathf.Clamp(s.Chapter, 1, 10) + ", " + Vm.Vanilla("Day") + " " + s.Day;
            string text;
            if (s.IsEmpty)
                text = Loc.T("Empty");
            else if (s.IsCoopCopy)
                text = Loc.T("Co-op copy") + " · " + chDay
                    + (s.MatchesIncomingPackage ? " · " + Loc.T("same world") : "");
            else
                text = Loc.T("Your game") + " · " + chDay;
            if (s.CampaignMismatchWithHost)
                text += " · " + Loc.T("another campaign");
            if (s.Id == preferred && preferred >= 1)
                text += " · " + Loc.T("last used");
            return text;
        }

        private static void PickSlot(ProfileSlotInfo s)
        {
            if (s.IsEmpty)
            {
                Commit(s.Id);
                return;
            }
            string q = s.CampaignMismatchWithHost
                ? "Profile " + s.Id + " belongs to a different co-op campaign than this host.\nOverwrite will replace that save with the host world."
                : "Profile " + s.Id + " already has a save.\nOverwrite with the host world? This cannot be undone.";
            _picker.Confirm(q, yes => { if (yes) Commit(s.Id); }, "Overwrite", "Cancel");
        }

        private static void Commit(int slotId)
        {
            var share = Net?.WorldSaveShare;
            if (share == null)
                return;
            if (!share.TryCommitPermanentSlot(slotId, true, out string err))
            {
                PickerFlash(err ?? "Failed to save world copy");
                ModLog.Warn(LogCat.Save, "Join slot commit failed: " + (err ?? "?"));
                _picker.Rebuild();
                return;
            }
            if (ModConfig.PreferredCoopCopySlot != null)
                ModConfig.PreferredCoopCopySlot.Value = slotId;
            ModLog.Event(LogCat.Save, "Permanent co-op world committed to profile slot " + slotId);
            Vm.Open(_join);
        }

        private static void PickerBack()
        {
            _picker.Confirm("Leave the game?", yes =>
            {
                if (!yes)
                    return;
                MainMenuMultiplayerInject.Disconnect();
                Vm.Open(_root);
            });
        }

        // ------------------------------------------------------------------
        // Restore my character (client, in the world)
        // ------------------------------------------------------------------

        private static ClientStateBackupData _peekBackup; // process-scoped: 1 s read cache
        private static float _peekBackupAt; // process-scoped: read cache clock

        /// <summary>
        /// A client's own inventory, skills and position live in a local backup (the host's world
        /// share loads the host's body). The join restores it on its own; this is the way back when
        /// that missed.
        /// </summary>
        private static bool CanRestoreSelf(out ClientStateBackupData data)
        {
            data = null;
            if (GameScreen.AtTitle || Core.loadingGame || Player.Instance == null || Role != NetworkRole.Client)
                return false;
            if (Time.realtimeSinceStartup - _peekBackupAt >= 1f)
            {
                _peekBackupAt = Time.realtimeSinceStartup;
                _peekBackup = ClientStateBackup.LoadLocalSelfBackupFile();
            }
            data = _peekBackup;
            return data != null;
        }

        private static void ConfirmRestoreSelf()
        {
            _peekBackupAt = 0f;
            if (!CanRestoreSelf(out ClientStateBackupData peek))
                return;
            string q = Loc.T("Put back your character from the last backup?") + "\n"
                + Vm.Vanilla("Day") + " " + peek.Day + " · " + Loc.T("level") + " " + peek.CurrentLevel
                + " · " + Loc.T("items") + " " + (peek.InventoryItems?.Count ?? 0)
                + (string.IsNullOrEmpty(peek.Timestamp) ? "" : " · " + peek.Timestamp);
            _root.Confirm(q, yes => { if (yes) RestoreSelf(); });
        }

        private static void RestoreSelf()
        {
            _peekBackupAt = 0f;
            ClientStateBackupData data = ClientStateBackup.LoadLocalSelfBackupFile();
            if (data == null || Player.Instance == null)
            {
                MainMenuMultiplayerInject.Flash("No backup of your character yet");
                ModLog.Event(LogCat.Save, "No local self-backup found.");
                return;
            }
            float beforeHp = Player.Instance.health;
            int beforeLvl = Player.Instance.currentLevel;
            if (!ClientStateBackup.RestoreFromBackup(data))
            {
                MainMenuMultiplayerInject.Flash(WrongSaveWarning.Format("campaign mismatch, empty, or stale backup — restore refused"));
                return;
            }
            MainMenuMultiplayerInject.Flash("Character restored");
            ModLog.Event(LogCat.Save,
                "Applied local self-backup (before lvl=" + beforeLvl + " hp=" + beforeHp.ToString("F0") + ").");
        }
    }
}

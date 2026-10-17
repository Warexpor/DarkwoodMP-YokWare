using System;
using System.Collections.Generic;
using System.Text;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using UnityEngine;
using YokWare.VanillaMenu;

namespace DWMPHorde
{
    /// <summary>
    /// Multiplayer > Settings > Voice: how this player talks (off, push to talk, always on; the
    /// key; which microphone and how loud, with a live level meter) and how loud they hear
    /// everyone and each player (Players).
    /// </summary>
    internal static partial class MultiplayerScreens
    {
        private static VmScreen _voice; // process-scoped: menu screens
        private static VmScreen _voicePlayers; // process-scoped: menu screens
        private static VmSettingsPage _voicePage; // process-scoped: Apply / Revert to default of Voice
        private static VmSettingsPage _voicePlayersPage; // process-scoped: Apply / Revert to default of Players
        private static tk2dTextMesh _meter; // process-scoped: the live level row on the Voice screen
        private static string _meterShown; // process-scoped: last meter text, to rebuild the mesh only on change
        private static tk2dTextMesh[] _meterBars; // process-scoped: one fixed bar per meter slot on the Voice screen
        private static tk2dTextMesh _meterTalking; // process-scoped: "talking" after the meter, at a fixed place
        private static Color _meterLit = Color.white; // process-scoped: a filled bar's colour (the values column's)

        private const int MeterSlots = 18;
        private const int DeviceNameMax = 28;

        private static void EnsureVoice()
        {
            _voice = new VmScreen("MultiplayerVoice") { Build = BuildVoice, Signature = VoiceSignature, Parent = _settings, Tick = TickVoice };
            _voicePlayers = new VmScreen("MultiplayerVoicePlayers") { Build = BuildVoicePlayers, Signature = VoicePlayersSignature, Parent = _voice };
            _voicePage = new VmSettingsPage(_voice, VoiceEntries);
            _voicePlayersPage = new VmSettingsPage(_voicePlayers, VoicePlayerEntries);
        }

        private static IEnumerable<VmSetting> VoiceEntries()
        {
            // Whether and when this mic goes out to everyone; keys, device and volumes stay local.
            yield return Setting(ModConfig.VoiceEnabled, ask: true);
            yield return Setting(ModConfig.VoiceMode, ask: true);
            yield return Setting(ModConfig.VoicePttKey);
            yield return Setting(ModConfig.VoicePadTalkButton);
            yield return Setting(ModConfig.VoiceRadioTalkKey);
            yield return Setting(ModConfig.VoiceRadioPowerKey);
            yield return Setting(ModConfig.VoiceMicDevice);
            yield return Setting(ModConfig.VoiceMicVolume);
            yield return Setting(ModConfig.VoiceNoiseSuppression);
            yield return Setting(ModConfig.VoiceNoiseSuppressionStrength);
            yield return Setting(ModConfig.VoiceRadioWorldSounds);
            yield return Setting(ModConfig.VoiceVolume);
            yield return Setting(ModConfig.VoiceRadioVolume);
        }

        /// <summary>The players listed now, by the name their volume is kept under; default 1.</summary>
        private static IEnumerable<VmSetting> VoicePlayerEntries()
        {
            foreach (int id in OtherPlayerIds())
            {
                int pid = id;
                yield return new VmSetting
                {
                    Key = "player:" + Sync.PlayerNames.Shown(pid),
                    Get = () => VoicePlayerVolumes.Get(pid),
                    Set = v => VoicePlayerVolumes.Set(pid, (float)v),
                    Default = 1f,
                };
            }
        }

        private static string _voiceSig; // process-scoped: last Voice signature, rebuilt when its inputs change
        private static int _voiceSigIndex = -1; // process-scoped: voice mode it was built from
        private static string[] _voiceSigDevices; // process-scoped: device list it was built from
        private static int _voiceSigFrame = -1; // process-scoped: frame _voiceSigDevices was read

        /// <summary>Mode and device list. Unity's device list is read once a frame (TickVoice reuses it).</summary>
        private static string VoiceSignature()
        {
            int index = VoiceIndex();
            string[] devices = VoiceMic.Devices;
            _voiceSigFrame = Time.frameCount;
            if (_voiceSig == null || index != _voiceSigIndex || !SameStrings(devices, _voiceSigDevices))
                _voiceSig = index + "/" + string.Join("\n", devices);
            _voiceSigIndex = index;
            _voiceSigDevices = devices;
            return _voiceSig;
        }

        private static string[] DevicesThisFrame() =>
            _voiceSigFrame == Time.frameCount && _voiceSigDevices != null ? _voiceSigDevices : VoiceMic.Devices;

        private static bool SameStrings(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static readonly string[] OffOn = { "Off", "On" };
        private static readonly string[] PadTalkChoices = { "Off", "D-pad left", "D-pad right", "Left stick click", "Right stick click" };

        private static void BuildVoice(VmBuilder b)
        {
            b.Header("Voice", 178f + 140f);
            float z = 100f + 120f;
            // Fourteen rows: a little closer than vanilla's, to stay above Return.
            const float step = Vm.RowStep - 3f;

            b.Choice("Voice chat", z, VoiceChoices, VoiceIndex, SetVoice);
            z -= step;
            b.KeyField("Push to talk key", z, () => ModConfig.VoicePttKey?.Value ?? "V",
                v => { if (ModConfig.VoicePttKey != null) ModConfig.VoicePttKey.Value = v; }, enabled: VoiceIndex() == 1);
            z -= step;
            b.Choice("Controller talk button", z, PadTalkChoices, VoiceChatService.PadVoiceIndex,
                i => { if (ModConfig.VoicePadTalkButton != null) ModConfig.VoicePadTalkButton.Value = VoiceChatService.PadVoiceButtons[i]; },
                enabled: VoiceIndex() == 1);
            z -= step;
            b.KeyField("Radio talk key", z, () => ModConfig.VoiceRadioTalkKey?.Value ?? "Mouse1",
                v => { if (ModConfig.VoiceRadioTalkKey != null) ModConfig.VoiceRadioTalkKey.Value = v; });
            z -= step;
            b.KeyField("Radio on/off key", z, () => ModConfig.VoiceRadioPowerKey?.Value ?? "B",
                v => { if (ModConfig.VoiceRadioPowerKey != null) ModConfig.VoiceRadioPowerKey.Value = v; });
            z -= step;
            b.Choice("Radio picks up sounds", z, OffOn,
                () => ModConfig.VoiceRadioWorldSounds == null || ModConfig.VoiceRadioWorldSounds.Value ? 1 : 0,
                i => { if (ModConfig.VoiceRadioWorldSounds != null) ModConfig.VoiceRadioWorldSounds.Value = i == 1; });
            z -= step;
            b.Name("Microphone", z);
            Button mic = null;
            mic = b.Value(MicText(), z, () =>
            {
                NextMic();
                Vm.SetText(mic.textMesh, MicText());
            }, enabled: VoiceIndex() != 0);
            z -= step;
            // Volumes in dB: left end muted, middle as recorded, right end +24 dB (VolumeScale).
            b.Slider("Microphone volume", z, () => VolumeScale.ToSlider(ModConfig.VoiceMicVolume?.Value ?? 1f),
                t => { if (ModConfig.VoiceMicVolume != null) ModConfig.VoiceMicVolume.Value = VolumeScale.FromSlider(t); });
            z -= step;
            b.Choice("Noise suppression", z, OffOn,
                () => ModConfig.VoiceNoiseSuppression == null || ModConfig.VoiceNoiseSuppression.Value ? 1 : 0,
                i => { if (ModConfig.VoiceNoiseSuppression != null) ModConfig.VoiceNoiseSuppression.Value = i == 1; });
            z -= step;
            // Left end 0.25, right end 3, in steps of 0.25.
            b.Slider("Suppression strength", z,
                () => ((ModConfig.VoiceNoiseSuppressionStrength?.Value ?? ModConfig.DefaultNoiseSuppressionStrength) - RnNoise.MinStrength)
                    / (RnNoise.MaxStrength - RnNoise.MinStrength),
                t =>
                {
                    if (ModConfig.VoiceNoiseSuppressionStrength != null)
                        ModConfig.VoiceNoiseSuppressionStrength.Value = Mathf.Round(
                            (RnNoise.MinStrength + t * (RnNoise.MaxStrength - RnNoise.MinStrength)) * 4f) / 4f;
                });
            z -= step;
            b.Name("Microphone level", z);
            Button meter = b.Value("", z, null);
            _meter = meter != null ? meter.textMesh : null;
            _meterShown = null;
            BuildMeterBars(b, z);
            z -= step;
            b.Slider("Voice volume", z, () => VolumeScale.ToSlider(ModConfig.VoiceVolume?.Value ?? 1f),
                t => { if (ModConfig.VoiceVolume != null) ModConfig.VoiceVolume.Value = VolumeScale.FromSlider(t); });
            z -= step;
            b.Slider("Radio volume", z, () => VolumeScale.ToSlider(ModConfig.VoiceRadioVolume?.Value ?? 1f),
                t => { if (ModConfig.VoiceRadioVolume != null) ModConfig.VoiceRadioVolume.Value = VolumeScale.FromSlider(t); });
            z -= step;
            b.Name("Players", z);
            int others = OtherPlayerIds().Count;
            b.Value(others > 0 ? others + " ..." : Loc.T("Nobody yet"), z, () => Vm.Open(_voicePlayers), enabled: others > 0);
            b.Return();
            _voicePage.Buttons(b);
        }

        /// <summary>
        /// The level meter: one bar per slot, each its own text at a fixed place, spread over the
        /// width of the sliders below it. One string of '|' and '.' changed width as it filled (the
        /// menu font is not monospaced), so the row grew and its "talking" moved as one spoke.
        /// Empty slots are the same bar, dimmed.
        /// </summary>
        private static void BuildMeterBars(VmBuilder b, float z)
        {
            _meterBars = null;
            _meterTalking = null;
            if (_meter == null)
                return;
            _meterLit = _meter.color;
            MainMenu m = Vm.Menu;
            GameObject sliderSrc = m != null ? Vm.SliderSource(m) : null;
            OptionsSlider os = sliderSrc != null ? sliderSrc.GetComponent<OptionsSlider>() : null;
            float width = os != null ? os.xSize : 200f;
            float slot = width / MeterSlots;
            // Same line as the values column; each bar centred in its slot.
            TextAnchor left = _meter.anchor;
            TextAnchor centre = (TextAnchor)((int)left / 3 * 3 + 1);
            var bars = new tk2dTextMesh[MeterSlots];
            for (int i = 0; i < MeterSlots; i++)
            {
                bars[i] = b.Label("|", Vm.ValueX + slot * (i + 0.5f), z, centre, Vm.Dim);
                if (bars[i] == null)
                    return;
            }
            _meterBars = bars;
            _meterTalking = b.Label("", Vm.ValueX + width + slot, z, left, _meterLit);
            ShowMeterBars(false, 0, false);
        }

        private static void ShowMeterBars(bool shown, int lit, bool talking)
        {
            if (_meterBars == null)
                return;
            for (int i = 0; i < _meterBars.Length; i++)
            {
                tk2dTextMesh bar = _meterBars[i];
                if (bar == null)
                    continue;
                if (bar.gameObject.activeSelf != shown)
                    bar.gameObject.SetActive(shown);
                Color c = i < lit ? _meterLit : Vm.Dim;
                if (bar.color != c)
                {
                    bar.color = c;
                    bar.Commit();
                }
            }
            if (_meterTalking != null)
                Vm.SetText(_meterTalking, shown && talking ? Loc.T("talking") : "");
        }

        // process-scoped: what the meter text was made from (state 0 off, 1 no mic, 2 starting, 3 level)
        private static int _meterState = -1; // process-scoped: meter cache (rebuilt on change)
        private static int _meterLevel; // process-scoped: filled slots it was made from
        private static bool _meterOn; // process-scoped: talking flag it was made from
        private static string _meterLang; // process-scoped: Loc.Language it was made in
        private static readonly StringBuilder _meterSb = new StringBuilder(MeterSlots + 16); // process-scoped: reused meter text buffer

        /// <summary>Every frame on the Voice screen: keep the mic on and redraw the meter.</summary>
        private static void TickVoice()
        {
            VoiceChatService.MeterWantedUntil = Time.unscaledTime + 0.5f;
            if (_meter == null)
                return;
            int mode = VoiceIndex();
            int state, n = 0;
            bool on = false;
            if (mode == 0)
                state = 0;
            else if (!VoiceMic.Running)
                state = DevicesThisFrame().Length == 0 ? 1 : 2;
            else
            {
                state = 3;
                n = Mathf.Clamp(Mathf.RoundToInt(VoiceMic.Level * MeterSlots), 0, MeterSlots);
                // Always on: whether the gate hears speech; push to talk: whether the key is down.
                on = mode == 2 ? VoiceMic.GateOpen : VoiceChatService.Transmitting;
            }
            if (_meterShown != null && state == _meterState && n == _meterLevel && on == _meterOn
                && ReferenceEquals(Loc.Language, _meterLang))
                return;
            _meterLang = Loc.Language;
            _meterState = state;
            _meterLevel = n;
            _meterOn = on;
            ShowMeterBars(state == 3 && _meterBars != null, n, on);
            string shown;
            if (state == 0)
                shown = Loc.T("Voice chat is off");
            else if (state == 1)
                shown = Loc.T("No microphone found");
            else if (state == 2)
                shown = Loc.T("Starting...");
            else if (_meterBars != null)
                shown = "";
            else
            {
                // No bars built (menu sources missing): the old one-line meter.
                StringBuilder sb = _meterSb;
                sb.Length = 0;
                for (int i = 0; i < MeterSlots; i++)
                    sb.Append(i < n ? '|' : '.');
                if (on)
                    sb.Append("  ").Append(Loc.T("talking"));
                shown = sb.ToString();
            }
            if (shown == _meterShown)
                return;
            _meterShown = shown;
            Vm.SetText(_meter, shown);
        }

        private static string MicText()
        {
            string dev = ModConfig.VoiceMicDevice?.Value ?? "";
            if (string.IsNullOrEmpty(dev))
                return Loc.T("Default");
            if (Array.IndexOf(VoiceMic.Devices, dev) < 0)
                return Loc.T("Default") + " (" + Loc.T("not found") + ")";
            return dev.Length > DeviceNameMax ? dev.Substring(0, DeviceNameMax - 3) + "..." : dev;
        }

        /// <summary>Default, then each device Unity lists, then Default again.</summary>
        private static void NextMic()
        {
            if (ModConfig.VoiceMicDevice == null)
                return;
            string[] devices = VoiceMic.Devices;
            int i = Array.IndexOf(devices, ModConfig.VoiceMicDevice.Value ?? "");
            int next = i + 1;
            ModConfig.VoiceMicDevice.Value = next < devices.Length ? devices[next] : "";
        }

        // ------------------------------------------------------------------
        // Players: one volume per other player
        // ------------------------------------------------------------------

        private static List<int> OtherPlayerIds()
        {
            var ids = new List<int>();
            FillOtherPlayerIds(ids);
            return ids;
        }

        private static void FillOtherPlayerIds(List<int> ids)
        {
            ids.Clear();
            LanNetworkManager net = Net;
            if (net == null || !net.IsConnected)
                return;
            foreach (RemotePlayerProxy p in net.EnumerateRemoteProxies())
            {
                if (p != null && p.PlayerId > 0 && p.PlayerId != net.LocalPlayerId && !ids.Contains(p.PlayerId))
                    ids.Add(p.PlayerId);
            }
            ids.Sort();
        }

        private static readonly List<int> _sigIds = new List<int>(8); // process-scoped: reused per-frame id list
        private static readonly List<string> _sigNames = new List<string>(8); // process-scoped: names the signature was built from
        private static readonly List<int> _sigBuiltIds = new List<int>(8); // process-scoped: ids the signature was built from
        private static readonly StringBuilder _sigSb = new StringBuilder(128); // process-scoped: reused signature buffer
        private static string _voicePlayersSig; // process-scoped: last Players signature

        private static string VoicePlayersSignature()
        {
            List<int> ids = _sigIds;
            FillOtherPlayerIds(ids);
            bool same = _voicePlayersSig != null && ids.Count == _sigBuiltIds.Count;
            for (int i = 0; same && i < ids.Count; i++)
                same = ids[i] == _sigBuiltIds[i]
                    && string.Equals(Sync.PlayerNames.Shown(ids[i]), _sigNames[i], StringComparison.Ordinal);
            if (same)
                return _voicePlayersSig;
            _sigBuiltIds.Clear();
            _sigNames.Clear();
            StringBuilder sb = _sigSb;
            sb.Length = 0;
            foreach (int id in ids)
            {
                string name = Sync.PlayerNames.Shown(id);
                _sigBuiltIds.Add(id);
                _sigNames.Add(name);
                sb.Append(id).Append(':').Append(name).Append('\n');
            }
            _voicePlayersSig = sb.ToString();
            return _voicePlayersSig;
        }

        /// <summary>Seven rows at most (eight players, this one aside), as many as a vanilla Options page.</summary>
        private static void BuildVoicePlayers(VmBuilder b)
        {
            b.Header("Players", 178f + 100f);
            float z = 100f + 80f;
            const float step = Vm.RowStep;
            List<int> ids = OtherPlayerIds();
            if (ids.Count == 0)
                b.Label(Loc.T("No other players in this game."), 0f, z, TextAnchor.MiddleCenter, Vm.Grey);
            for (int i = 0; i < ids.Count && i < 7; i++)
            {
                int id = ids[i];
                b.Slider(Sync.PlayerNames.Shown(id), z, () => VolumeScale.ToSlider(VoicePlayerVolumes.Get(id)),
                    t => VoicePlayerVolumes.Set(id, VolumeScale.FromSlider(t)));
                z -= step;
            }
            b.Return();
            _voicePlayersPage.Buttons(b);
        }
    }
}

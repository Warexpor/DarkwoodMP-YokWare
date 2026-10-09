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
        private static tk2dTextMesh _meter; // process-scoped: the live level row on the Voice screen
        private static string _meterShown; // process-scoped: last meter text, to rebuild the mesh only on change

        private const int MeterSlots = 18;
        private const int DeviceNameMax = 28;

        private static void EnsureVoice()
        {
            _voice = new VmScreen("MultiplayerVoice") { Build = BuildVoice, Signature = VoiceSignature, Parent = _settings, Tick = TickVoice };
            _voicePlayers = new VmScreen("MultiplayerVoicePlayers") { Build = BuildVoicePlayers, Signature = VoicePlayersSignature, Parent = _voice };
        }

        private static string VoiceSignature() => VoiceIndex() + "/" + string.Join("\n", VoiceMic.Devices);

        private static void BuildVoice(VmBuilder b)
        {
            b.Header("Voice", 178f + 100f);
            float z = 100f + 80f;
            const float step = Vm.RowStep;

            b.Choice("Voice chat", z, VoiceChoices, VoiceIndex, SetVoice);
            z -= step;
            b.KeyField("Push to talk key", z, () => ModConfig.VoicePttKey?.Value ?? "V",
                v => { if (ModConfig.VoicePttKey != null) ModConfig.VoicePttKey.Value = v; }, enabled: VoiceIndex() == 1);
            z -= step;
            b.KeyField("Radio on/off key", z, () => ModConfig.VoiceRadioPowerKey?.Value ?? "B",
                v => { if (ModConfig.VoiceRadioPowerKey != null) ModConfig.VoiceRadioPowerKey.Value = v; });
            z -= step;
            b.Name("Microphone", z);
            Button mic = null;
            mic = b.Value(MicText(), z, () =>
            {
                NextMic();
                Vm.SetText(mic.textMesh, MicText());
            }, enabled: VoiceIndex() != 0);
            z -= step;
            b.Slider("Microphone volume", z, () => (ModConfig.VoiceMicVolume?.Value ?? 1f) / 2f,
                t => { if (ModConfig.VoiceMicVolume != null) ModConfig.VoiceMicVolume.Value = Mathf.Round(t * 2f * 20f) / 20f; });
            z -= step;
            b.Name("Microphone level", z);
            Button meter = b.Value("", z, null);
            _meter = meter != null ? meter.textMesh : null;
            _meterShown = null;
            z -= step;
            b.Slider("Voice volume", z, () => (ModConfig.VoiceVolume?.Value ?? 1f) / 2f,
                t => { if (ModConfig.VoiceVolume != null) ModConfig.VoiceVolume.Value = Mathf.Round(t * 2f * 20f) / 20f; });
            z -= step;
            b.Name("Players", z);
            int others = OtherPlayerIds().Count;
            b.Value(others > 0 ? others + " ..." : Loc.T("Nobody yet"), z, () => Vm.Open(_voicePlayers), enabled: others > 0);
            b.Return();
        }

        /// <summary>Every frame on the Voice screen: keep the mic on and redraw the meter.</summary>
        private static void TickVoice()
        {
            VoiceChatService.MeterWantedUntil = Time.unscaledTime + 0.5f;
            if (_meter == null)
                return;
            string shown;
            if (VoiceIndex() == 0)
                shown = Loc.T("Voice chat is off");
            else if (!VoiceMic.Running)
                shown = VoiceMic.Devices.Length == 0 ? Loc.T("No microphone found") : Loc.T("Starting...");
            else
            {
                int n = Mathf.RoundToInt(VoiceMic.Level * MeterSlots);
                var sb = new StringBuilder(MeterSlots + 16);
                for (int i = 0; i < MeterSlots; i++)
                    sb.Append(i < n ? '|' : '.');
                // Always on: whether the gate hears speech; push to talk: whether the key is down.
                bool on = VoiceIndex() == 2 ? VoiceMic.GateOpen : VoiceChatService.Transmitting;
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
            LanNetworkManager net = Net;
            if (net == null || !net.IsConnected)
                return ids;
            foreach (RemotePlayerProxy p in net.EnumerateRemoteProxies())
            {
                if (p != null && p.PlayerId > 0 && p.PlayerId != net.LocalPlayerId && !ids.Contains(p.PlayerId))
                    ids.Add(p.PlayerId);
            }
            ids.Sort();
            return ids;
        }

        private static string VoicePlayersSignature()
        {
            var sb = new StringBuilder();
            foreach (int id in OtherPlayerIds())
                sb.Append(id).Append(':').Append(Sync.PlayerNames.Shown(id)).Append('\n');
            return sb.ToString();
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
                b.Slider(Sync.PlayerNames.Shown(id), z, () => VoicePlayerVolumes.Get(id) / 2f,
                    t => VoicePlayerVolumes.Set(id, Mathf.Round(t * 2f * 20f) / 20f));
                z -= step;
            }
            b.Return();
        }
    }
}

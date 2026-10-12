using System;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A dream every player plays alone (the home dream, <c>dream_home</c>, while the host's
    /// <c>SoloHomeDream</c> is on). The party still goes in together and each player wakes by
    /// its own way out, but in between every machine plays its own copy of the dream as in
    /// single player: its events, doors, cupboards, items and lights are its own, nothing of it
    /// is sent, and the other dreamers are not there. The session (who is in it, who left, what
    /// the dream changed in the world) is shared as in any party dream.
    ///
    /// The copy is a personal pad like the host's prologue (<see cref="PersonalPads"/>): the sync
    /// code that leaves the prologue alone leaves it alone too.
    /// </summary>
    internal static class SeparateDream
    {
        internal const string HomePreset = "dream_home";

        /// <summary>The running session's dream is played alone (decided when it began).</summary>
        private static bool _sessionSolo; // reset-in: Reset
        private static string _sessionPreset; // reset-in: Reset

        /// <summary>The pad last looked at, and whether it is a solo dream's.</summary>
        private static Location _pad; // reset-in: Reset
        private static bool _padSolo; // reset-in: Reset

        internal static void Reset()
        {
            _sessionSolo = false;
            _sessionPreset = null;
            _pad = null;
            _padSolo = false;
        }

        /// <summary>The host's rule (a client reads the host's value): this dream is played alone.</summary>
        internal static bool IsSoloPreset(string presetName)
        {
            if (string.IsNullOrEmpty(presetName) || !SessionSettings.SoloHomeDream)
                return false;
            // A spawned dream pad is named "<preset>_done" (vanilla), and a clone "(Clone)".
            string name = presetName.EndsWith("(Clone)", StringComparison.Ordinal)
                ? presetName.Substring(0, presetName.Length - "(Clone)".Length).TrimEnd()
                : presetName;
            return string.Equals(Core.getTrueLocationName(name), HomePreset, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A session begins or learns its dream: whether it is played alone is decided now.</summary>
        internal static void OnSessionPreset(string presetName)
        {
            if (string.Equals(_sessionPreset, presetName, StringComparison.OrdinalIgnoreCase))
                return;
            _sessionPreset = presetName;
            _sessionSolo = IsSoloPreset(presetName);
            if (_sessionSolo)
                ModLog.Event(LogCat.Dream, "[SeparateDream] " + presetName + " is played alone: every player has its own");
        }

        /// <summary>The session is over (the pad may still stand for a few seconds, <see cref="Contains(Transform)"/>).</summary>
        internal static void OnSessionOver()
        {
            _sessionSolo = false;
            _sessionPreset = null;
        }

        /// <summary>The running session is a solo dream.</summary>
        internal static bool SessionIsSolo => _sessionSolo && DreamSession.IsActive;

        /// <summary>
        /// This machine's own copy of a solo dream, from its load until vanilla frees it (4 s after
        /// the wake-up, <c>Dreams.destroyDream</c>). Null otherwise.
        /// </summary>
        internal static Transform OwnPad
        {
            get
            {
                Dreams d = Dreams.Instance;
                Location loc = d != null ? d.dreamLocation : null;
                if (loc == null)
                {
                    _pad = null;
                    _padSolo = false;
                    return null;
                }
                // Decided once per pad, and only while its session runs: the pad can be listed
                // before its preset and session are (a no then is not kept).
                if (loc != _pad || !_padSolo)
                {
                    _pad = loc;
                    _padSolo = SessionIsSolo && (IsSoloPreset(loc.name) || (d.preset != null && IsSoloPreset(d.preset.name)));
                }
                return _padSolo ? loc.transform : null;
            }
        }

        /// <summary>This machine's player is in its own copy of a solo dream.</summary>
        internal static bool LocalActive
        {
            get
            {
                Dreams d = Dreams.Instance;
                return d != null && d.dreaming && OwnPad != null;
            }
        }

        /// <summary>
        /// On this machine's own copy of a solo dream: under its pad, or in its pad slot (what a
        /// dream spawns beside the pad), or, while the scene is still loading, under a location of
        /// that dream.
        /// </summary>
        internal static bool Contains(Transform t)
        {
            if (t == null)
                return false;
            Transform pad = OwnPad;
            if (pad != null)
                return t.IsChildOf(pad) || DreamSyncManager.IsInPadSlot(pad, t.position);
            if (!SessionIsSolo)
                return false;
            Location loc = t.GetComponentInParent<Location>(true);
            return loc != null && IsSoloPreset(loc.name);
        }

        internal static bool Contains(Vector3 pos)
        {
            Transform pad = OwnPad;
            return pad != null && DreamSyncManager.IsInPadSlot(pad, pos);
        }

        /// <summary>
        /// Another player's body at <paramref name="pos"/> is in this player's own copy of a solo
        /// dream, where it is not: it is in its own copy.
        /// </summary>
        internal static bool HidesBody(Vector3 pos) => LocalActive && Contains(pos);

        /// <summary>
        /// Sent by this machine's own play inside its copy, never world upkeep: nothing of it goes
        /// out while the player is in a solo dream (relays of other players' messages, sent while
        /// one is being applied, still go). The rest is held back by position
        /// (<see cref="PersonalPads"/>).
        /// </summary>
        internal static bool BlocksSend(NetMessageType type, NetworkRole role)
        {
            if (LanNetworkManager.IsApplyingRemoteState || !LocalActive)
                return false;
            if (role == NetworkRole.Client)
                return !ClientSendsInSolo(type);
            // The host: only what its own player does. The world it runs for the players already
            // awake (events, creatures, doors, drops) goes on, held back by position only.
            switch (type)
            {
                case NetMessageType.JournalItem:
                case NetMessageType.ThrowableSpawn:
                case NetMessageType.DreamItemPickup:
                case NetMessageType.DreamAudio:
                case NetMessageType.DreamPropCollider:
                case NetMessageType.PlayerScare:
                case NetMessageType.PlayerSound:
                case NetMessageType.PlayerAudio:
                case NetMessageType.PlayerFiredWeapon:
                case NetMessageType.ExamineObject:
                case NetMessageType.ContainerItem:
                case NetMessageType.LocationEnter:
                case NetMessageType.LocationExit:
                case NetMessageType.DragSync:
                case NetMessageType.CoHold:
                case NetMessageType.ItemPose:
                case NetMessageType.LockedUnlock:
                case NetMessageType.PadlockUnlock:
                case NetMessageType.MeleeWorldHit:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A client is no authority over the world, so in its solo dream it sends only what is
        /// about itself and the session: its body (hidden on the others' pads), its saves and
        /// character, the dream's session, chat and voice.
        /// </summary>
        private static bool ClientSendsInSolo(NetMessageType type)
        {
            switch (type)
            {
                case NetMessageType.Handshake:
                case NetMessageType.PlayerState:
                case NetMessageType.PlayerDied:
                case NetMessageType.PlayerEffectSync:
                case NetMessageType.PlayerLightState:
                case NetMessageType.PlayerAnimation:
                case NetMessageType.PlayerAnimLibrary:
                case NetMessageType.PlayerBurning:
                case NetMessageType.SaveSync:
                case NetMessageType.ClientStateBackup:
                case NetMessageType.NightDeathState:
                case NetMessageType.PlayerSkillsSync:
                case NetMessageType.PeerHasItem:
                case NetMessageType.DreamStartRequest:
                case NetMessageType.DreamEntered:
                case NetMessageType.DreamEnded:
                case NetMessageType.DreamLeft:
                case NetMessageType.FinalDreamsceneDeath:
                case NetMessageType.SleepEndRequest:
                case NetMessageType.AfterNightEndRequest:
                case NetMessageType.WorldRequest:
                case NetMessageType.ChapterShareAck:
                case NetMessageType.LocationPadSlotRequest:
                case NetMessageType.DesyncDetailRequest:
                case NetMessageType.DesyncReport:
                case NetMessageType.PauseMenuState:
                case NetMessageType.MapPinRequest:
                case NetMessageType.PlayerName:
                case NetMessageType.ChatMessage:
                case NetMessageType.VoiceData:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// What this machine's player plays on its own, apart from the shared world: the host's
    /// prologue (<see cref="PersonalPrologue"/>) and a solo dream's copy (<see cref="SeparateDream"/>).
    /// Nothing there is sent, nothing of it is taken from another machine, and it runs as in
    /// single player.
    /// </summary>
    internal static class PersonalPads
    {
        internal static bool Contains(Transform t) => PersonalPrologue.IsOnProloguePad(t) || SeparateDream.Contains(t);

        internal static bool Contains(Vector3 pos) => PersonalPrologue.IsAtProloguePad(pos) || SeparateDream.Contains(pos);

        /// <summary>This machine's player is on a personal pad now.</summary>
        internal static bool LocalOn => PersonalPrologue.LocalInPrologue || SeparateDream.LocalActive;

        /// <summary>This machine's own play on a personal pad: not sent.</summary>
        internal static bool BlocksSend(NetMessageType type, NetworkRole role)
            => (role == NetworkRole.Host && PersonalPrologue.HostBlocksSend(type)) || SeparateDream.BlocksSend(type, role);
    }
}

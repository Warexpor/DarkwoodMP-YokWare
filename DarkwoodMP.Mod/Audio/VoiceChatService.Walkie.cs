using System;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// This player's walkie as a device: switched on and off with its knob (config
    /// <c>VoiceRadioPowerKey</c>, only with the radio in hand), running on a 9V battery like the
    /// vanilla flashlight (its durability bar is the charge; the vanilla Reload key swaps in a
    /// <c>battery9v</c>), in hand or in a pocket, and how well a signal gets between two radios
    /// (distance, walls, underground).
    /// </summary>
    public static partial class VoiceChatService
    {
        /// <summary>A full battery lasts this long switched on and listening.</summary>
        private const float BatteryStandbySec = 1500f;
        /// <summary>Talking drains this much faster on top.</summary>
        private const float BatteryTalkSec = 480f;
        /// <summary>Below this share of charge the radio chirps a low-battery warning now and then.</summary>
        private const float LowBattery = 0.1f;
        private const float LowBatteryEverySec = 30f;

        /// <summary>Clear signal up to here (world units, the map is about 25000 across), gone at <see cref="RadioMaxRange"/>.</summary>
        private const float RadioClearRange = 3000f;
        private const float RadioMaxRange = 9000f;
        /// <summary>Each end inside a building counts as this much farther.</summary>
        private const float RadioInsideFactor = 1.35f;
        /// <summary>What gets through from or to underground.</summary>
        private const float RadioUndergroundShare = 0.15f;
        /// <summary>Below this signal the radio does not open at all.</summary>
        private const float RadioSquelchQuality = 0.03f;

        /// <summary>A radio in a pocket plays quieter and duller than one in hand.</summary>
        private const float PocketVolume = 0.55f;
        private const float PocketCutoff = 2000f;

        private static bool _radioOn = true; // process-scoped: the knob, kept across sessions like a real radio
        private static InvItemClass _walkieItem; // process-scoped: polled every 0.5 s
        private static KeyCode _powerKey = KeyCode.B; // process-scoped: config cache
        private static string _powerKeyText; // process-scoped: the setting text _powerKey was parsed from
        private static float _nextLowBattery; // process-scoped: warning pacing
        private static float _lastRefreshedCharge = -1f; // process-scoped: item bar redraw pacing

        /// <summary>This player's walkie, for PlayerState and this player's own hearing.</summary>
        internal static byte LocalWalkieState
        {
            get
            {
                UpdateLocalWalkie();
                Player p = Player.Instance;
                bool underground = p != null && p.whereAmI != null && p.whereAmI.inUndergroundLocation;
                if (!_localWalkie)
                    return WalkieStates.Make(WalkieStates.None, underground);
                if (!_radioOn || Charge() <= 0f)
                    return WalkieStates.Make(WalkieStates.Off, underground);
                return WalkieStates.Make(HoldingWalkie() ? WalkieStates.Hand : WalkieStates.Pocket, underground);
            }
        }

        /// <summary>This player's radio is on, charged and so receiving.</summary>
        private static bool LocalRadioLive => WalkieStates.Live(LocalWalkieState);

        private static string WalkieType => ModConfig.WalkieItemName?.Value ?? "walkie_talkie";

        private static bool HoldingWalkie()
        {
            try
            {
                Player p = Player.Instance;
                InvItemClass cur = p != null ? p.currentItem : null;
                return !InvItemClass.isNull(cur) && cur.type == WalkieType;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Battery charge 0..1 of the walkie carried (the one in hand first); 0 with none.</summary>
        private static float Charge()
        {
            InvItemClass item = ActiveWalkieItem();
            if (InvItemClass.isNull(item) || item.baseClass == null || item.baseClass.maxDurability <= 0f)
                return 0f;
            return Mathf.Clamp01(item.durability / item.baseClass.maxDurability);
        }

        private static InvItemClass ActiveWalkieItem()
        {
            if (HoldingWalkie())
                return Player.Instance.currentItem;
            return _walkieItem;
        }

        private static void UpdateLocalWalkie()
        {
            if (Time.unscaledTime < _nextWalkieCheck)
                return;
            _nextWalkieCheck = Time.unscaledTime + 0.5f;
            _localWalkie = false;
            _walkieItem = null;
            try
            {
                Player p = Player.Instance;
                if (p == null || Core.loadingGame || p.Inventory == null)
                    return;
                string name = WalkieType;
                if (string.IsNullOrEmpty(name))
                    return;
                _walkieItem = p.Inventory.getItemInPlayer(name);
                _localWalkie = !InvItemClass.isNull(_walkieItem);
            }
            catch { /* ignore */ }
        }

        /// <summary>Every frame in a game: the knob, the battery, the low-battery chirp.</summary>
        private static void TickWalkie(bool talking, bool receiving)
        {
            string keyText = ModConfig.VoiceRadioPowerKey?.Value ?? "B";
            if (!string.Equals(keyText, _powerKeyText, StringComparison.Ordinal))
            {
                _powerKeyText = keyText;
                try { _powerKey = (KeyCode)Enum.Parse(typeof(KeyCode), keyText, ignoreCase: true); }
                catch
                {
                    ModLog.Warn(LogCat.Audio, "Bad VoiceRadioPowerKey — using B");
                    _powerKey = KeyCode.B;
                }
            }

            // The knob is on the radio: it turns only with the radio in hand, while playing.
            if (Input.GetKeyDown(_powerKey) && HoldingWalkie() && WalkieTxAllowed())
            {
                _radioOn = !_radioOn;
                bool live = _radioOn && Charge() > 0f;
                PlayLocalRadioSound(_radioOn ? (live ? RadioSound.PowerOn : RadioSound.Dead) : RadioSound.PowerOff);
                ModLog.Event(LogCat.Audio, "[Voice] radio switched " + (_radioOn ? "on" : "off")
                    + " (battery " + Mathf.RoundToInt(Charge() * 100f) + "%)");
            }

            if (!_localWalkie || !_radioOn)
                return;
            InvItemClass item = ActiveWalkieItem();
            if (InvItemClass.isNull(item) || item.baseClass == null || item.baseClass.maxDurability <= 0f || item.durability <= 0f)
                return;

            // Game time: a paused game does not drain it.
            float max = item.baseClass.maxDurability;
            float perSec = max / BatteryStandbySec + (talking ? max / BatteryTalkSec : 0f) + (receiving ? max / (BatteryStandbySec * 2f) : 0f);
            item.durability = Mathf.Max(0f, item.durability - perSec * Time.deltaTime);
            float charge = item.durability / max;
            // The bar in the inventory redraws per whole percent, not every frame.
            if (Mathf.Abs(charge - _lastRefreshedCharge) >= 0.01f || item.durability <= 0f)
            {
                _lastRefreshedCharge = charge;
                try { item.refresh(); } catch { /* slot UI not up */ }
            }
            if (item.durability <= 0f)
            {
                PlayLocalRadioSound(RadioSound.Dead);
                ModLog.Event(LogCat.Audio, "[Voice] radio battery flat");
                return;
            }
            if (charge < LowBattery && Time.unscaledTime >= _nextLowBattery)
            {
                _nextLowBattery = Time.unscaledTime + LowBatteryEverySec;
                PlayLocalRadioSound(RadioSound.LowBattery);
            }
        }

        /// <summary>
        /// How well a signal gets from a talker's radio to a receiving one, 1 clear .. 0 none: by
        /// the distance between them, longer through buildings, barely at all underground.
        /// </summary>
        internal static float RadioQuality(Vector3 talker, Vector3 receiver, bool talkerInside, bool receiverInside,
            bool talkerUnderground, bool receiverUnderground)
        {
            float d = DistXz(talker, receiver);
            if (talkerInside) d *= RadioInsideFactor;
            if (receiverInside) d *= RadioInsideFactor;
            float q = 1f - Mathf.InverseLerp(RadioClearRange, RadioMaxRange, d);
            if (talkerUnderground) q *= RadioUndergroundShare;
            if (receiverUnderground) q *= RadioUndergroundShare;
            return q;
        }

        /// <summary>Whether a peer's PlayerState says they are underground.</summary>
        private static bool PeerUnderground(LanNetworkManager net, int id)
            => net != null && net.RemotePlayers.TryGetValue(id, out RemotePlayerState st) && st != null
               && WalkieStates.IsUnderground(st.WalkieState);

        private static bool LocalUnderground
        {
            get
            {
                Player p = Player.Instance;
                return p != null && p.whereAmI != null && p.whereAmI.inUndergroundLocation;
            }
        }
    }
}

using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Weather / clock / after-night time sync composed for 0.8.</summary>
    internal sealed class WorldWeatherTimeNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldWeatherTimeNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendWeatherSync() => SendWeatherSyncTo(-1);

        internal void SendWeatherSyncWithStrike(byte strike) => SendWeatherSyncTo(-1, strike);

        /// <summary>Send weather to one client (targetPlayerId &gt; 0) or all (targetPlayerId &lt;= 0).</summary>
        internal void SendWeatherSyncTo(int targetPlayerId) => SendWeatherSyncTo(targetPlayerId, 0);

        internal void SendWeatherSyncTo(int targetPlayerId, byte strike)
        {
            if (_net.Role != NetworkRole.Host) return;
            var rain = Singleton<Rain>.Instance;
            if (rain == null) return;

            var msg = new WeatherSyncMessage
            {
                Raining = rain.Raining,
                RainToday = rain.rainToday,
                TimeToStart = rain.timeToStart,
                LightningTime = rain.lightningTime,
                PreRainLightning = rain.preRainLightning,
                PreRainLightningTime = rain.preRainLightningTime,
                Duration = rain.duration,
                FogFadedOutToday = rain.fogFadedOutToday,
                FogIsActive = rain.fogIsActive,
                Strike = strike,
            };
            if (rain.timeToFadeInFog != null)
            {
                msg.TimeToFadeInFog_Hours = rain.timeToFadeInFog.time;
                msg.TimeToFadeInFog_Day = rain.timeToFadeInFog.day;
            }
            if (rain.timeToFadeOutFog != null)
            {
                msg.TimeToFadeOutFog_Hours = rain.timeToFadeOutFog.time;
                msg.TimeToFadeOutFog_Day = rain.timeToFadeOutFog.day;
            }

            _net.SendBulkOrAll(NetMessageType.WeatherSync, w => msg.Serialize(w), targetPlayerId);
        }

        internal void HandleWeatherSync(WeatherSyncMessage msg)
        {
            // Host is authoritative; only clients apply weather packets.
            if (_net.Role != NetworkRole.Client)
                return;

            var rain = Singleton<Rain>.Instance;
            if (rain == null) return;

            bool wasRaining = rain.Raining;
            bool wasFogActive = rain.fogIsActive;

            // Apply timers and flags first. Do not pre-set private raining or fogIsActive.
            // startRain() early-outs when raining is already true, which skipped visuals
            // when we wrote the field before calling the Raining setter.
            rain.rainToday = msg.RainToday;
            rain.timeToStart = msg.TimeToStart;
            rain.lightningTime = msg.LightningTime;
            rain.preRainLightning = msg.PreRainLightning;
            rain.preRainLightningTime = msg.PreRainLightningTime;
            rain.duration = msg.Duration;
            rain.fogFadedOutToday = msg.FogFadedOutToday;

            if (rain.timeToFadeInFog == null)
                rain.timeToFadeInFog = new TimeAndDay((int)msg.TimeToFadeInFog_Hours, msg.TimeToFadeInFog_Day);
            else
            {
                rain.timeToFadeInFog.time = (int)msg.TimeToFadeInFog_Hours;
                rain.timeToFadeInFog.day = msg.TimeToFadeInFog_Day;
            }
            if (rain.timeToFadeOutFog == null)
                rain.timeToFadeOutFog = new TimeAndDay((int)msg.TimeToFadeOutFog_Hours, msg.TimeToFadeOutFog_Day);
            else
            {
                rain.timeToFadeOutFog.time = (int)msg.TimeToFadeOutFog_Hours;
                rain.timeToFadeOutFog.day = msg.TimeToFadeOutFog_Day;
            }

            // Visual transitions via public API (startRain / stopRain / fog)
            if (msg.Raining != wasRaining)
            {
                // Inside a location pad the rain stays out of sight until return (and inside
                // an underground one vanilla startRain would not start it at all).
                if (msg.Raining && Patches.PadWeather.LocalInPad())
                    Patches.PadWeather.StartHidden(rain);
                else if (msg.Raining)
                    rain.Raining = true;
                else
                    rain.Raining = false;
            }

            // startRain may randomize lightningTime or timeToStart; re-assert host values
            rain.timeToStart = msg.TimeToStart;
            rain.lightningTime = msg.LightningTime;
            rain.duration = msg.Duration;
            rain.preRainLightning = msg.PreRainLightning;
            rain.preRainLightningTime = msg.PreRainLightningTime;

            if (msg.FogIsActive != wasFogActive)
            {
                if (msg.FogIsActive)
                    rain.startFog();
                else
                    rain.stopFog();
            }
            else
            {
                rain.fogIsActive = msg.FogIsActive;
            }

            if (msg.Strike != 0
                && Player.Instance != null
                && Player.Instance.whereAmI != null
                && !Player.Instance.whereAmI.inUndergroundLocation
                && !Patches.PadWeather.LocalInPad()
                && Singleton<CamMain>.Instance != null
                && Singleton<CamMain>.Instance.lightning != null)
            {
                if (msg.Strike == 2)
                    Singleton<CamMain>.Instance.lightning.strikeVeryFar();
                else
                    Singleton<CamMain>.Instance.lightning.strike();
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[WeatherSync] rain={msg.Raining} fog={msg.FogIsActive} today={msg.RainToday}");
        }

        /// <summary>
        /// Host→one peer (targetPlayerId &gt; 0) or all (≤ 0).
        /// Join path uses this so a new client does not wait up to TimeSyncInterval.
        /// </summary>
        internal void SendTimeSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected)
                return;

            var ctrl = Singleton<Controller>.Instance;
            var msg = new TimeSyncMessage
            {
                CurrentTime = ctrl != null ? ctrl.CurrentTime : 0,
                Day = ctrl != null ? ctrl.day : 1,
                IsAfterNight = ctrl != null && ctrl.isAfterNight,
                VillagersAway = Sync.NightVillage.Away
            };
            Dreams dreams = Dreams.Instance;
            msg.OverworldTime = Sync.PersonalPrologue.HostWorldTime(
                dreams != null && dreams.dreaming ? (int)dreams.timeCopy : msg.CurrentTime);
            msg.PrologueHold = (byte)Mathf.Clamp(Sync.PersonalPrologue.HoldCount, 0, 255);
            // Reliable: after-night transitions must not be dropped (client wrongly
            // reporting AfterNightActive=false can clear host morning freeze).
            if (targetPlayerId > 0)
            {
                _net.SendToPlayer(targetPlayerId, NetMessageType.TimeSync, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
            else
            {
                _net.Broadcast(NetMessageType.TimeSync, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
        }

        internal void HandleTimeSync(TimeSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;

            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            Sync.PersonalPrologue.ClientNoteHold(msg.PrologueHold);

            int prevDay = ctrl.day;
            float prevTime = ctrl.CurrentTime;
            bool wasAfterNight = ctrl.isAfterNight;

            // Apply full isAfterNight from host (true and false).
                // Do not call startAfterNight or endAfterNight; those spawn traders,
            // grant rep, and destroy NPCs; host owns that. Client only mirrors
            // freeze flag + timeFreeze VFX so PlayerState.AfterNightActive matches.
            if (msg.IsAfterNight && !ctrl.isAfterNight)
            {
                ctrl.isAfterNight = true;
                try
                {
                    // Vanilla's end-of-night effect is for the player at home; one out in the
                    // forest or inside a location at dawn gets no morning, only the flag.
                    if (Player.Instance != null && Player.Instance.effects != null
                        && Patches.MorningHideoutHold.LocalPositionInside())
                        ctrl.addAfterNightEffect();
                }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[TimeSync] addAfterNightEffect: " + ex.Message);
                }
            }
            else if (!msg.IsAfterNight && ctrl.isAfterNight)
            {
                ctrl.isAfterNight = false;
                try
                {
                    ctrl.removeAfterNightEffect();
                }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[TimeSync] removeAfterNightEffect: " + ex.Message);
                }
                // Host endAfterNight destroyed the trader; mirror the despawn without advancing time.
                if (wasAfterNight)
                    CleanupClientMorningTrader();
            }

            bool dreamClock = Core.EnteringDream
                || (Dreams.Instance != null && (Dreams.Instance.dreaming || Dreams.Instance.dreamPrepared || Dreams.Instance.switchingDream))
                || Sync.DreamSyncManager.IsDreamActive;

            int appliedTime = dreamClock ? msg.CurrentTime : msg.OverworldTime;
            ctrl.CurrentTime = appliedTime;
            ctrl.day = msg.Day;
            Sync.NightVillage.SetAway(msg.VillagersAway);

            // Host startDay full-heals + skill recharge is world-authority-side only.
            // Client must still get personal morning benefits when day rolls.

            if (msg.Day > prevDay)
                ApplyClientPersonalNewDay(prevDay, msg.Day);
            // One night passed (not a join catching up several days).
            if (msg.Day == prevDay + 1)
                ApplyClientSurvivedNight(ctrl, prevDay);

            if (!dreamClock)
                PlayClientNightCues(ctrl, (int)prevTime, appliedTime);


            // Clear soft invuln from suppressed startBeforeDay if still set.
            if (Player.Instance != null && Player.Instance.invulnerable
                && !msg.IsAfterNight && Core.isDay())
            {
                // Leave invuln if something else set it; only clear after morning settle.
                // Clear it only after the host reports a new day.
                if (msg.Day > prevDay)
                    Player.Instance.invulnerable = false;
            }

            // Do not call refreshTime() here. It would run day, trader, and
            // night-scenario logic on the client. Update clock UI and ambient only.
            if (CoopTimePolicy.ShouldUseRefreshTimeNoLogicOnClientSync)
            {
                try { ctrl.refreshTimeNoLogic(); }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[TimeSync] refreshTimeNoLogic: " + ex.Message);
                }
            }

            float delta = appliedTime - prevTime;
            bool dayChange = msg.Day != prevDay;
            bool afterNightFlip = msg.IsAfterNight != wasAfterNight;
            // Dream start sets Controller.CurrentTime = preset.time (often +hundreds).
            // Use ASCII "->" so log files never glue "1→1" into "11" / "417→800" into "417800".
            if (dayChange || afterNightFlip || Mathf.Abs(delta) >= 2f)
            {
                string tag = dreamClock ? "[TimeSync/dream] " : "[TimeSync] ";
                ModLog.Event(LogCat.Session,
                    tag + "client clock day " + prevDay + "->" + msg.Day
                    + " time " + prevTime.ToString("F0") + "->" + appliedTime.ToString("F0")
                    + " (d=" + delta.ToString("F1") + ")"
                    + " afterNight " + wasAfterNight + "->" + msg.IsAfterNight);
            }
            else if (ModRuntime.VerboseLogging)
            {
                ModRuntime.LegacyInfo($"[TimeSync] synced day={msg.Day} time={appliedTime} isAfterNight={msg.IsAfterNight} (no day-chain)");
            }

            // TimeSync can stomp day-ambient after startDreaming set preset.ambientColor.
            if (dreamClock && Dreams.Instance != null && Dreams.Instance.dreaming)
            {
                try { ctrl.updateAmbientLight(); }
                catch { /* non-fatal */ }
            }
        }

        /// <summary>
        /// The personal cues of vanilla <c>refreshTime</c>, which never runs on a client: "night is
        /// coming" for a player not at home, "light the oven" for one at home with the ward out,
        /// and the end-of-night sound. Vanilla fires on the exact minute; TimeSync can step over it.
        /// </summary>
        private static void PlayClientNightCues(Controller ctrl, int prevTime, int newTime)
        {
            Player p = Player.Instance;
            if (p == null || p.whereAmI == null)
                return;
            Location big = p.whereAmI.bigLocation;
            if (ctrl.isHardNight
                && CoopTimePolicy.LiveStepCrossedMinute(prevTime, newTime, (int)ctrl.nightTime - 130)
                && (big == null || !big.playerBase))
                p.displayMessage(Language.Get("Playermsg_nightComing", "UI"));
            if (ctrl.isHardNight
                && CoopTimePolicy.LiveStepCrossedMinute(prevTime, newTime, (int)ctrl.nightTime - 20)
                && big != null && big.playerBase && big.shadowWard != null && !big.shadowWard.activeInHierarchy)
                p.displayMessage(Language.Get("Playermsg_nightMustLightOven", "UI"));
            if (CoopTimePolicy.LiveStepCrossedMinute(prevTime, newTime, 1360))
                AudioController.Play("endOfNight_pre");
        }

        /// <summary>
        /// Personal half of host startDay: heal and skill recharge, with no world despawn or save.
        /// </summary>
        internal static void ApplyClientPersonalNewDay(int prevDay, int newDay)
        {
            Player p = Player.Instance;
            if (p == null) return;

            try
            {
                p.setHealth(p.maxHealth);
                if (p.skills != null)
                {
                    p.skills.rechargeUses();
                    p.skills.activateSkillsOnNewDay();
                }
                ModRuntime.LegacyInfo(
                    $"[DayNight] client personal new day {prevDay}→{newDay} (heal+skills)");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DayNight] client personal new day failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Vanilla <c>startBeforeDay</c> (suppressed on a client) sets <c>player_survivedNight</c> on a
        /// hard night for the player who lived to dawn; one that died skipped past it. The flag is
        /// this player's own (the trader greets by it), so the client sets it here for itself.
        /// </summary>
        internal static void ApplyClientSurvivedNight(Controller ctrl, int nightDay)
        {
            if (ctrl == null || !ctrl.isHardNight)
                return;
            if (!PerPlayerFlagPolicy.SurvivedNight(nightDay, DeathStateTracker.LocalNightDeathDay))
                return;
            Flags flags = Singleton<Flags>.Instance;
            if (flags == null)
                return;
            flags.setFlag("player_survivedNight", activeModifier: true);
            ModRuntime.LegacyInfo($"[DayNight] client survived night {nightDay} (player_survivedNight)");
        }

        /// <summary>
        /// Mirror host endAfterNight trader despawn on client (no CurrentTime++ / refreshTime).
        /// </summary>
        internal static void CleanupClientMorningTrader()
        {
            try
            {
                Player p = Player.Instance;
                if (p != null && p.whereAmI != null)
                {
                    p.whereAmI.checkWhereAmI();
                    if (p.whereAmI.bigLocation != null && p.whereAmI.bigLocation.trader != null)
                    {
                        UnityEngine.Object.Destroy(p.whereAmI.bigLocation.trader);
                        ModRuntime.LegacyInfo("[DayNight] client despawned morning trader (TimeSync clear)");
                        return;
                    }
                }

                var wg = Singleton<WorldGenerator>.Instance;
                if (wg == null || wg.locations == null) return;
                for (int i = 0; i < wg.locations.Count; i++)
                {
                    Location loc = wg.locations[i];
                    if (loc != null && loc.playerBase && loc.trader != null)
                    {
                        UnityEngine.Object.Destroy(loc.trader);
                        ModRuntime.LegacyInfo("[DayNight] client despawned morning trader via location scan");
                    }
                }
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DayNight] client trader cleanup: " + ex.Message);
            }
        }
    }
}

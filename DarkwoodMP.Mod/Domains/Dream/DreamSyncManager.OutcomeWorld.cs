using DWMPHorde.Networking;
using DWMPHorde.Patches;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The world events a dream's outcome fires when the party wakes (vanilla <c>Dreams.endDreaming</c>:
    /// fireWorldEvent / fireGameEvent effects). Every peer woke from the host's outcome (DreamEnded
    /// carries its name), so a client replays the outcome's world events as a replay of the host's
    /// world, the way an applied GameEventsFired runs. The host's own GameEventsFired fan-out does not
    /// carry them: repeatable ones (multipleFire) are never sent, and the "dream_"-named ones
    /// (onEndDream_won_oneChance) are dropped on both ends once nobody is dreaming.
    ///
    /// Every outcome world event in the dream presets (Resources/dreampresets; no preset uses
    /// fireGameEvent):
    /// <list type="bullet">
    /// <item>spawnWolfInCurrentHideout (church ruins, every outcome): Player.spawnWolfInCurrentHideout
    /// adds the Wolfman to the hideout around the player and sets the wolf flags. The host's creature
    /// and flags reach everyone through entity state and FlagSync; a client's run made a second,
    /// local-only Wolfman. Host only.</item>
    /// <item>onPlayerDeath_church_dream_01 (flag church_dream_escaped), doctor_afterTrapWon/Failed
    /// (the doctor's wagon NPCs, flags, cutscene), onEndDream_won/fail_oneChance (sluice door, rubble,
    /// levers), onDestroyEgg / onPlayerDeath of the ch2 underground (a door and a setActive whose
    /// targets are missing in the data): world state every peer mirrors. Replayed.</item>
    /// <item>onExitUndergroundBunkerDream (player_inBunkerUndergroundDream off, a per-player flag),
    /// addDivingOutEventAfterLoad (the dive out after the cellar dream), and the doctor events' body
    /// steps (lie down, FOV, its dialogue): the waking player's own. Replayed for a peer that dreamt.</item>
    /// </list>
    /// </summary>
    internal static partial class DreamSyncManager
    {
        /// <summary>The Player function behind spawnWolfInCurrentHideout (it spawns the Wolfman).</summary>
        private const string SpawnWolfFunction = "spawnWolfInCurrentHideout";

        /// <summary>Client: inside its own Dreams.endDreaming, whose outcome loop fires world events.</summary>
        private static bool _outcomeWorldReplay; // process-scoped: call-scoped, unwound by DreamEndPatch's Finalizer
        /// <summary>
        /// Client: which of the preset's outcome world events were already fired when the host's
        /// DreamEnded came in. A one-shot fired since then was latched by the host's own fan-out of
        /// this same wake-up (run here for the host's body, personal steps left out).
        /// </summary>
        private static readonly HashSet<int> _outcomeLatchedAtEnd = new HashSet<int>();
        private static bool _outcomeLatchSnapshot;

        internal static bool OutcomeWorldReplayActive => _outcomeWorldReplay;

        internal static void BeginOutcomeWorldReplay() => _outcomeWorldReplay = true;

        /// <summary>The wake-up's world events ran (or the session ended): drop the replay state.</summary>
        internal static void ClearOutcomeWorldReplay()
        {
            _outcomeWorldReplay = false;
            _outcomeLatchedAtEnd.Clear();
            _outcomeLatchSnapshot = false;
        }

        private static bool IsConnectedClient()
        {
            var net = ModRuntime.Network;
            return net != null && net.IsConnected && net.Role == NetworkRole.Client;
        }

        /// <summary>
        /// An outcome event the host alone runs. Character spawns inside a replay are already left
        /// out step by step (GameEventFireScopePatch); the Wolfman comes from a Player function.
        /// </summary>
        internal static bool IsHostOwnedOutcomeEvent(GameEvents ge)
        {
            if (ge == null || ge.events == null)
                return false;
            for (int i = 0; i < ge.events.Count; i++)
            {
                GameEvent e = ge.events[i];
                if (e != null && !e.disabled && e.type == GameEvent.Type.runFunction
                    && e.Value == SpawnWolfFunction)
                    return true;
            }
            return false;
        }

        /// <summary>The preset's own copy of a dream (Dreams.getPreset), or null.</summary>
        private static DreamPreset FindPreset(string presetName)
        {
            Dreams dreams = Dreams.Instance;
            if (dreams == null || string.IsNullOrEmpty(presetName))
                return null;
            if (dreams.preset != null && Core.getTrueLocationName(dreams.preset.name) == Core.getTrueLocationName(presetName))
                return dreams.preset;
            try { return dreams.getPreset(Core.getTrueLocationName(presetName)); }
            catch (KeyNotFoundException) { return null; }
        }

        private static GameEvents FindWorldEvent(string type)
        {
            Events events = Singleton<Events>.Instance;
            if (events == null || events.worldEvents == null || string.IsNullOrEmpty(type))
                return null;
            return events.worldEvents.TryGetValue(type, out GameEvents ge) ? ge : null;
        }

        private static GameEvents OutcomeGameEvents(DreamPreset.Outcome.Effect effect)
        {
            if (effect == null)
                return null;
            if (effect.type == DreamPreset.Outcome.Effect.Type.fireWorldEvent)
                return FindWorldEvent(effect.worldEventType);
            if (effect.type == DreamPreset.Outcome.Effect.Type.fireGameEvent)
            {
                var go = effect.destPrefab as GameObject;
                return go != null ? go.GetComponent<GameEvents>() : null;
            }
            return null;
        }

        internal static bool IsOutcomeWorldEffect(DreamPreset.Outcome.Effect effect)
            => effect != null
               && (effect.type == DreamPreset.Outcome.Effect.Type.fireWorldEvent
                   || effect.type == DreamPreset.Outcome.Effect.Type.fireGameEvent);

        /// <summary>
        /// Client, on the host's DreamEnded: note which outcome world events of this dream are
        /// already fired, before the host's wake-up can latch any of them here.
        /// </summary>
        internal static void NoteOutcomeWorldEventLatches(string presetName)
        {
            _outcomeLatchedAtEnd.Clear();
            _outcomeLatchSnapshot = false;
            if (!IsConnectedClient())
                return;
            DreamPreset preset = FindPreset(presetName);
            if (preset == null || preset.outcomes == null)
                return;
            _outcomeLatchSnapshot = true;
            for (int i = 0; i < preset.outcomes.Count; i++)
            {
                DreamPreset.Outcome oc = preset.outcomes[i];
                if (oc == null || oc.effects == null)
                    continue;
                for (int j = 0; j < oc.effects.Count; j++)
                {
                    GameEvents ge = OutcomeGameEvents(oc.effects[j]);
                    if (ge != null && ge.fired)
                        _outcomeLatchedAtEnd.Add(ge.GetInstanceID());
                }
            }
        }

        /// <summary>
        /// Client: run one outcome world event as a replay of the host's. <paramref name="personal"/>:
        /// this peer dreamt and wakes from it, so the event's steps on the waking body are its own.
        /// <paramref name="unlatch"/>: vanilla's fireGameEvent effect clears the latch before firing.
        /// </summary>
        internal static void ReplayOutcomeEvent(GameEvents ge, string label, bool personal, bool unlatch)
        {
            if (ge == null)
                return;
            if (IsHostOwnedOutcomeEvent(ge))
            {
                ModRuntime.LegacyInfo($"[DreamSync] outcome event '{label}' is the host's (spawns its creature) — not run here");
                return;
            }
            if (unlatch)
                ge.fired = false;
            else if (ge.fired && !ge.multipleFire)
            {
                // Latched before this wake-up (or no DreamEnded to compare with): vanilla's own fire
                // is a no-op then too.
                if (!_outcomeLatchSnapshot || _outcomeLatchedAtEnd.Contains(ge.GetInstanceID()))
                    return;
                ge.fired = false;
            }
            bool prevSuppress = GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer;
            if (!personal)
                GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer = true;
            try
            {
                using (new NetworkApplyGuard())
                    ge.fire();
            }
            finally
            {
                GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer = prevSuppress;
            }
            ModRuntime.LegacyInfo($"[DreamSync] outcome event '{label}' replayed (host's outcome{(personal ? "" : ", world only")})");
        }

        /// <summary>Client, inside its own endDreaming: vanilla's fireWorldEvent of the outcome loop.</summary>
        internal static void ReplayOutcomeWorldEvent(string type)
        {
            GameEvents ge = FindWorldEvent(type);
            if (ge != null)
                ReplayOutcomeEvent(ge, type, personal: true, unlatch: false);
        }

        /// <summary>
        /// Outcome world effects of a wake-up this peer runs itself (not vanilla endDreaming): the
        /// host or an offline peer fires them as vanilla does; a client replays the host's.
        /// </summary>
        private static void RunOutcomeWorldEffects(DreamPreset.Outcome outcome, bool personal)
        {
            if (outcome == null || outcome.effects == null)
                return;
            bool client = IsConnectedClient();
            for (int i = 0; i < outcome.effects.Count; i++)
            {
                DreamPreset.Outcome.Effect effect = outcome.effects[i];
                if (!IsOutcomeWorldEffect(effect))
                    continue;
                bool gameEvent = effect.type == DreamPreset.Outcome.Effect.Type.fireGameEvent;
                if (client)
                {
                    GameEvents ge = OutcomeGameEvents(effect);
                    ReplayOutcomeEvent(ge, gameEvent ? (ge != null ? ge.name : "?") : effect.worldEventType,
                        personal, unlatch: gameEvent);
                    continue;
                }
                if (gameEvent)
                {
                    GameEvents ge = OutcomeGameEvents(effect);
                    if (ge != null)
                    {
                        ge.fired = false;
                        ge.fire();
                    }
                }
                else if (!string.IsNullOrEmpty(effect.worldEventType))
                {
                    Singleton<Events>.Instance?.fireWorldEvent(effect.worldEventType);
                }
            }
        }

        /// <summary>
        /// Client whose pad never loaded when the dream ended (DreamEnded during its entry): it woke
        /// from nothing, but the world changed with the host's outcome. World steps only.
        /// </summary>
        private static void ReplayOutcomeWorldOnly(string presetName, string outcomeName)
        {
            try
            {
                if (IsConnectedClient() && !DreamSession.IsFailureCleanup(outcomeName))
                    RunOutcomeWorldEffects(ResolveEffectOutcome(FindPreset(presetName), outcomeName), personal: false);
            }
            finally
            {
                ClearOutcomeWorldReplay();
            }
        }

        /// <summary>
        /// The outcome whose effects a wake-up applies (ApplyOutcomeEffects): the named one; for
        /// allDead / reject / disconnect only playerDeath (never the default's reward); else
        /// "default", else the first.
        /// </summary>
        private static DreamPreset.Outcome ResolveEffectOutcome(DreamPreset preset, string outcomeName)
        {
            if (preset == null || preset.outcomes == null)
                return null;
            List<DreamPreset.Outcome> outcomes = preset.outcomes;
            for (int i = 0; i < outcomes.Count; i++)
                if (outcomes[i] != null && outcomes[i].name == outcomeName)
                    return outcomes[i];
            if (DreamSession.IsNonRewardOutcome(outcomeName))
            {
                if (outcomeName == "allDead" || outcomeName == "playerDeath")
                {
                    for (int i = 0; i < outcomes.Count; i++)
                        if (outcomes[i] != null && outcomes[i].name == "playerDeath")
                            return outcomes[i];
                }
                return null;
            }
            for (int i = 0; i < outcomes.Count; i++)
                if (outcomes[i] != null && outcomes[i].name == "default")
                    return outcomes[i];
            return outcomes.Count > 0 ? outcomes[0] : null;
        }

        /// <summary>
        /// A peer that died in the dream wakes with the death outcome's personal effects
        /// (DreamEndPatch), but the world changed with the party's outcome: its world events stay
        /// the party's, as on every other peer.
        /// </summary>
        internal static DreamPreset.Outcome WithPartyWorldEvents(DreamPreset.Outcome own, Dreams dreams, string partyOutcome)
        {
            if (own == null)
                return null;
            DreamPreset.Outcome party = ResolveOutcome(dreams, partyOutcome);
            if (party == null || ReferenceEquals(party, own))
                return own;
            var effects = new List<DreamPreset.Outcome.Effect>();
            if (own.effects != null)
                for (int i = 0; i < own.effects.Count; i++)
                    if (!IsOutcomeWorldEffect(own.effects[i]))
                        effects.Add(own.effects[i]);
            if (party.effects != null)
                for (int i = 0; i < party.effects.Count; i++)
                    if (IsOutcomeWorldEffect(party.effects[i]))
                        effects.Add(party.effects[i]);
            return new DreamPreset.Outcome
            {
                name = own.name,
                effects = effects,
                transition = own.transition,
                customEndTime = own.customEndTime,
                endTime = own.endTime,
                dontLieDown = own.dontLieDown
            };
        }
    }
}

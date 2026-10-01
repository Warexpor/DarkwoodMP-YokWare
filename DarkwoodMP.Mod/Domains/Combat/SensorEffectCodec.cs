using System;
using System.Collections.Generic;
using DWMPHorde.Logging;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Bridges vanilla <c>InvItemEffect</c> lists (<c>MeleeSensor.effects</c>) and <see cref="SensorEffectWire"/>.
    /// Receivers re-apply them through vanilla's own <c>CharacterEffects.activate(InvItemEffect)</c>, exactly
    /// where <c>MeleeSensor.OnTriggerEnter</c> does: after <c>getHit</c> on the hit Character / Player.
    /// </summary>
    internal static class SensorEffectCodec
    {
        private static readonly int MaxEffectType = (int)CharacterEffectType.forestSpiritWard;

        /// <summary>Snapshot of a sensor's effects for the wire; null when it carries none.</summary>
        internal static SensorEffectWire[] ToWire(List<InvItemEffect> effects)
        {
            if (effects == null || effects.Count == 0) return null;

            var list = new List<SensorEffectWire>(Math.Min(effects.Count, SensorEffectWire.MaxEffects));
            for (int i = 0; i < effects.Count && list.Count < SensorEffectWire.MaxEffects; i++)
            {
                InvItemEffect e = effects[i];
                if (e == null) continue; // vanilla skips null entries too
                list.Add(new SensorEffectWire
                {
                    Type = (byte)e.type,
                    TimeElapsed = e.timeElapsed,
                    Duration = e.duration,
                    Modifier = e.modifier,
                    Interval = e.interval,
                    StopsBleeding = e.stopsBleeding,
                    StopsPoison = e.stopsPoison,
                    HasPoisonOverlay = e.hasPoisonOverlay,
                    ActivateSound = e.activateSound
                });
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        /// <summary>
        /// Rebuilds each received effect and hands it to vanilla <c>CharacterEffects.activate</c>.
        /// Entries from a peer that are not a defined effect type or carry non-finite numbers are dropped
        /// (same spirit as the per-message damage clamp); nothing else is reinterpreted.
        /// </summary>
        internal static void Apply(CharacterEffects target, SensorEffectWire[] effects, string context)
        {
            if (effects == null || effects.Length == 0) return;
            if (target == null) return;

            int n = Math.Min(effects.Length, SensorEffectWire.MaxEffects);
            for (int i = 0; i < n; i++)
            {
                SensorEffectWire w = effects[i];
                if (w.Type > MaxEffectType
                    || !IsFinite(w.TimeElapsed) || !IsFinite(w.Duration)
                    || !IsFinite(w.Modifier) || !IsFinite(w.Interval)
                    || w.Duration < 0f)
                {
                    ModLog.WarnRate(LogCat.Combat, "sensorfx-bad:" + context,
                        "[SensorEffects] " + context + " dropped malformed effect type=" + w.Type);
                    continue;
                }

                var effect = new InvItemEffect
                {
                    type = (CharacterEffectType)w.Type,
                    timeElapsed = w.TimeElapsed,
                    duration = w.Duration,
                    modifier = w.Modifier,
                    interval = w.Interval,
                    stopsBleeding = w.StopsBleeding,
                    stopsPoison = w.StopsPoison,
                    hasPoisonOverlay = w.HasPoisonOverlay,
                    activateSound = w.ActivateSound
                };
                target.activate(effect);
            }
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}

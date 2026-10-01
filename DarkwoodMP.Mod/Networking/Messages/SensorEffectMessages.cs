namespace DWMPHorde.Networking
{
    /// <summary>
    /// One vanilla <c>InvItemEffect</c> carried by a melee sensor (<c>MeleeSensor.effects</c>), reduced to
    /// exactly the fields <c>CharacterEffect.initialize</c> reads. <c>startDelay</c> is copied by vanilla
    /// but never read anywhere, and <c>requirements</c> is not consulted by <c>CharacterEffects.activate</c>,
    /// so neither is sent. Unity-free so the wire layout can be round-tripped in tests.
    /// </summary>
    public struct SensorEffectWire
    {
        /// <summary>Hard cap per message. Vanilla weapons carry one or two; the receiver ignores extras.</summary>
        public const int MaxEffects = 8;

        private const byte FlagStopsBleeding = 1;
        private const byte FlagStopsPoison = 2;
        private const byte FlagHasPoisonOverlay = 4;

        /// <summary><c>CharacterEffectType</c> as its underlying value (0..33 in the shipped game).</summary>
        public byte Type;
        public float TimeElapsed;
        public float Duration;
        public float Modifier;
        public float Interval;
        public bool StopsBleeding;
        public bool StopsPoison;
        public bool HasPoisonOverlay;
        public string ActivateSound;

        public void Serialize(NetWriter w)
        {
            w.Put(Type);
            w.Put(TimeElapsed);
            w.Put(Duration);
            w.Put(Modifier);
            w.Put(Interval);
            byte flags = 0;
            if (StopsBleeding) flags |= FlagStopsBleeding;
            if (StopsPoison) flags |= FlagStopsPoison;
            if (HasPoisonOverlay) flags |= FlagHasPoisonOverlay;
            w.Put(flags);
            w.Put(ActivateSound ?? "");
        }

        public static SensorEffectWire Deserialize(NetReader r)
        {
            var e = new SensorEffectWire
            {
                Type = r.GetByte(),
                TimeElapsed = r.GetFloat(),
                Duration = r.GetFloat(),
                Modifier = r.GetFloat(),
                Interval = r.GetFloat()
            };
            byte flags = r.GetByte();
            e.StopsBleeding = (flags & FlagStopsBleeding) != 0;
            e.StopsPoison = (flags & FlagStopsPoison) != 0;
            e.HasPoisonOverlay = (flags & FlagHasPoisonOverlay) != 0;
            e.ActivateSound = r.GetString();
            return e;
        }

        /// <summary>Trailer: count byte, then that many effects. A null/empty list writes a single 0.</summary>
        public static void WriteList(NetWriter w, SensorEffectWire[] effects)
        {
            int count = effects == null ? 0 : effects.Length;
            if (count > MaxEffects) count = MaxEffects;
            w.Put((byte)count);
            for (int i = 0; i < count; i++)
                effects[i].Serialize(w);
        }

        /// <summary>Reads what <see cref="WriteList"/> wrote; null for an empty list.</summary>
        public static SensorEffectWire[] ReadList(NetReader r)
        {
            int count = r.GetByte();
            if (count == 0) return null;
            if (count > MaxEffects)
                throw new System.IO.InvalidDataException("sensor effect count " + count + " exceeds " + MaxEffects);
            var list = new SensorEffectWire[count];
            for (int i = 0; i < count; i++)
                list[i] = Deserialize(r);
            return list;
        }
    }
}

namespace DWMPHorde
{
    /// <summary>Why <see cref="PlayerTargetPolicy.Choose"/> picked what it picked.</summary>
    public enum PlayerTargetReason : byte
    {
        /// <summary>No player body to pick: the caller leaves the target alone.</summary>
        None,
        /// <summary>A body this creature is bound to (the bunker dream spirit's owner).</summary>
        Pinned,
        /// <summary>The current player target is still held.</summary>
        Keep,
        /// <summary>No player target before: the nearest sensed body.</summary>
        Acquire,
        /// <summary>The current player target was lost (or gone): the nearest sensed body.</summary>
        CurrentLost,
        /// <summary>The closer-enemy check moved to a body clearly nearer than the current one.</summary>
        ClearlyNearer,
    }

    /// <summary>One player body (the host or a stand-in) as one creature sees it.</summary>
    public struct PlayerTargetCandidate
    {
        /// <summary>Flat distance from the creature.</summary>
        public float Distance;
        /// <summary>Alive, not invisible, not ignored, not a night corpse.</summary>
        public bool Valid;
        /// <summary>The creature senses it right now (the caller's test: sight, smell, being seen).</summary>
        public bool Sensed;
    }

    /// <summary>
    /// Which player body a creature targets when there is more than one. Vanilla has one player, so a
    /// creature never has to choose between two; every player body is treated alike here (no host
    /// or stand-in preference). A creature acquires the nearest player it senses and keeps that one
    /// while it is valid and sensed (or was sensed a moment ago); it moves to another only when the
    /// current one is lost, or on vanilla's own closer-enemy check when the other is clearly nearer.
    /// With nobody else sensed the current target is never dropped here: losing it stays vanilla's
    /// (<c>lostEnemy</c>).
    /// </summary>
    public static class PlayerTargetPolicy
    {
        /// <summary>Another body must be nearer than this share of the current one's distance.</summary>
        public const float SwitchDistanceRatio = 0.75f;

        /// <summary>
        /// How long a body that drops out of sight still counts as held. Vanilla's sight check runs
        /// every 0.5-1 s, so one missed ray must not hand the creature to another player.
        /// </summary>
        public const float LostGraceSeconds = 2f;

        /// <summary>
        /// Minimum time between two "clearly nearer" moves. Vanilla's closer-enemy check runs every
        /// 2.5-3.5 s; any other caller asking for a switch window does not move faster than that.
        /// </summary>
        public const float MinSecondsBetweenSwitches = 2.5f;

        /// <summary>The current body is still held: valid, and sensed now or within the grace.</summary>
        public static bool CurrentHeld(bool valid, bool sensedNow, float secondsSinceSensed)
            => valid && (sensedNow || secondsSinceSensed <= LostGraceSeconds);

        /// <summary>
        /// Index of the body to target among <paramref name="count"/> candidates, or -1 for none.
        /// <paramref name="current"/> is the index of the player body targeted now (-1 for none),
        /// <paramref name="pinned"/> a body the creature is bound to (-1 for none),
        /// <paramref name="switchWindow"/> true on the closer-enemy check, the only time a held
        /// target gives way to a clearly nearer one.
        /// </summary>
        public static int Choose(
            PlayerTargetCandidate[] candidates, int count, int current, bool currentHeld, int pinned,
            bool switchWindow, float secondsSinceSwitch, out PlayerTargetReason reason)
        {
            reason = PlayerTargetReason.None;
            if (candidates == null || count <= 0)
                return -1;
            if (count > candidates.Length)
                count = candidates.Length;

            if (pinned >= 0 && pinned < count && candidates[pinned].Valid)
            {
                reason = PlayerTargetReason.Pinned;
                return pinned;
            }

            int best = -1;
            for (int i = 0; i < count; i++)
            {
                if (!candidates[i].Valid || !candidates[i].Sensed)
                    continue;
                if (best < 0 || candidates[i].Distance < candidates[best].Distance)
                    best = i;
            }

            bool hasCurrent = current >= 0 && current < count && candidates[current].Valid;
            if (hasCurrent && currentHeld)
            {
                if (switchWindow
                    && best >= 0
                    && best != current
                    && secondsSinceSwitch >= MinSecondsBetweenSwitches
                    && candidates[best].Distance < candidates[current].Distance * SwitchDistanceRatio)
                {
                    reason = PlayerTargetReason.ClearlyNearer;
                    return best;
                }
                reason = PlayerTargetReason.Keep;
                return current;
            }

            if (best >= 0)
            {
                reason = current >= 0 ? PlayerTargetReason.CurrentLost : PlayerTargetReason.Acquire;
                return best;
            }

            if (hasCurrent)
            {
                // Lost and nobody else sensed: keep it; vanilla's lostEnemy decides when to give up.
                reason = PlayerTargetReason.Keep;
                return current;
            }
            return -1;
        }
    }
}

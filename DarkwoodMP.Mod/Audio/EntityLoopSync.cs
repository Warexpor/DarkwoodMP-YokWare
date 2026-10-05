using System.Runtime.CompilerServices;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// A creature's CharacterSounds loop (breathing, buzzing, growl bed, sleeping) as state.
    /// The host puts the loop the creature's AI has on (vanilla's last playIdleLoop / destroySounds,
    /// heard on the host or not) into every entity snapshot as a slot of the creature's own loop
    /// fields; each client keeps its copy's loop equal to it. A missed
    /// message, a client walking up to a creature someone else woke, a late join or a body
    /// the client's WorldGrid toggled all heal on the next snapshot (at most the 1 s resync).
    /// On a host-synced copy the client's own playIdleLoop / destroySounds are blocked
    /// (<see cref="Patches.ClientCreatureLoopGuardPatch"/>): its frozen AI would pick the wrong loop.
    /// </summary>
    internal static class EntityLoopSync
    {
        private const float LoopFade = 0.5f; // vanilla playIdleLoop / destroySounds fade

        private static int _applying; // process-scoped: call-scoped, unwound by each Apply's finally

        /// <summary>True while this class itself starts or stops a client copy's loop.</summary>
        internal static bool Applying => _applying > 0;

        /// <summary>The vanilla loop fields playIdleLoop is called with (Character, Underwater, GameEvent).</summary>
        private static string SlotId(CharacterSounds s, byte slot)
        {
            switch (slot)
            {
                case 1: return s.idleLoop;
                case 2: return s.idleLoopAggressive;
                case 3: return s.escapingLoop;
                case 4: return s.sleeping;
                case 5: return s.aggressive;
                case 6: return s.defensive;
                default: return null;
            }
        }

        private const byte SlotCount = 6;

        private sealed class LoopIntent
        {
            public string Loop;
        }

        /// <summary>
        /// The loop vanilla's playIdleLoop / destroySounds last chose per creature, whether or not
        /// this machine could hear it. Weak keys: a destroyed creature's entry goes with it.
        /// </summary>
        private static readonly ConditionalWeakTable<CharacterSounds, LoopIntent> _intent = new ConditionalWeakTable<CharacterSounds, LoopIntent>(); // process-scoped: weak per-creature table; vanilla's loop choice is world state, not session state

        /// <summary>
        /// After vanilla playIdleLoop ran: record the loop it chose, by its own rules (an
        /// inactive body, underwater or underground leaves the current one; the wolfman's
        /// aggressive variant while chasing; an empty name stops it).
        /// </summary>
        internal static void NoteVanillaPlayIdleLoop(CharacterSounds s, string loopName)
        {
            if (s == null || !s.gameObject.activeInHierarchy)
                return;
            if (string.IsNullOrEmpty(loopName))
            {
                SetIntent(s, null);
                return;
            }
            if (!Patches.EntitySoundSyncHelper.VanillaWouldPlay(s))
                return;
            if (loopName == s.idleLoop && !string.IsNullOrEmpty(s.idleLoopAggressive))
            {
                Character ch = s.GetComponent<Character>();
                if (ch != null && ch.behaviour == Character.Behaviour.chasingTarget)
                    loopName = s.idleLoopAggressive;
            }
            SetIntent(s, loopName);
        }

        /// <summary>After vanilla destroySounds ran: the creature has no loop.</summary>
        internal static void NoteVanillaDestroySounds(CharacterSounds s)
        {
            if (s != null)
                SetIntent(s, null);
        }

        private static void SetIntent(CharacterSounds s, string loop)
        {
            _intent.GetOrCreateValue(s).Loop = loop;
        }

        /// <summary>
        /// Host: the slot of the loop this creature's AI has on, 0 for none. Taken from what
        /// vanilla asked for, not from the host's own AudioObject: the host culls a 2D loop far
        /// from the host player, and the client next to the creature must still hear it (its
        /// own cull decides). A creature with no recorded choice falls back to the live loop.
        /// </summary>
        internal static byte HostSlot(Character c)
        {
            CharacterSounds s = c != null ? c.sounds : null;
            if (s == null || !s.gameObject.activeInHierarchy)
                return 0;
            string id;
            if (_intent.TryGetValue(s, out LoopIntent intent))
            {
                id = intent.Loop;
            }
            else
            {
                AudioObject ao = s.idleAudioObject;
                if (!IsLive(s, ao))
                    return 0;
                id = ao.audioID;
            }
            if (string.IsNullOrEmpty(id))
                return 0;
            for (byte slot = 1; slot <= SlotCount; slot++)
            {
                if (string.Equals(SlotId(s, slot), id, System.StringComparison.Ordinal))
                    return slot;
            }
            return 0;
        }

        /// <summary>Client: make the copy's loop the host's (slot 0 stops it), with vanilla's fades.</summary>
        internal static void Apply(Character c, byte slot)
        {
            CharacterSounds s = c != null ? c.sounds : null;
            if (s == null)
                return;
            string want = SlotId(s, slot);
            if (string.IsNullOrEmpty(want))
                want = null;

            AudioObject cur = s.idleAudioObject;
            bool live = IsLive(s, cur);
            if (live && want != null && cur.audioID == want)
                return;
            if (!live && want == null)
            {
                Release(s, cur);
                return;
            }
            // An inactive copy cannot play (vanilla playIdleLoop returns too); a later snapshot retries.
            if (want != null && !s.gameObject.activeInHierarchy)
                return;

            _applying++;
            bool prevNet = TraverseHack.GetExplicitFlag();
            bool prevInside = TraverseHack.InsideCharacterSounds;
            TraverseHack.SetExplicitFlag(true);
            TraverseHack.InsideCharacterSounds = true;
            try
            {
                if (live)
                    cur.Stop(LoopFade);
                else
                    Release(s, cur);
                s.idleAudioObject = null;
                if (want == null)
                    return;

                // Reverb on the start comes from CharBase.isInside, which only checkGround refreshes.
                c.checkGround();
                AudioObject ao = AudioController.Play(want, s.transform);
                Logging.ModLog.TraceRate(Logging.LogCat.Audio, "loop:" + c.GetInstanceID(),
                    () => "[EntityLoop] " + c.name + " → " + want + (ao != null ? " playing" : " culled"), 1f);
                if (ao == null)
                    return; // culled (a 2D loop out of range); the next snapshot retries
                if (ao.GetComponent<LoopingAudioObject>() == null)
                    ao.gameObject.AddComponent<LoopingAudioObject>();
                ao.FadeIn(LoopFade);
                s.idleAudioObject = ao;
            }
            finally
            {
                TraverseHack.InsideCharacterSounds = prevInside;
                TraverseHack.SetExplicitFlag(prevNet);
                _applying--;
            }
        }

        /// <summary>Client: stop the copy's loop (left interest, despawned). The next snapshot restarts it.</summary>
        internal static void Stop(Character c)
        {
            if (c == null || c.sounds == null)
                return;
            Apply(c, 0);
        }

        /// <summary>
        /// AudioObjects are pooled: the held reference is ours only while it is parented to this
        /// creature. A finished or recycled one is dropped, never stopped.
        /// </summary>
        private static bool IsLive(CharacterSounds s, AudioObject ao)
        {
            return ao != null && ao.transform.parent == s.transform && !ao.isFadingOut
                && (ao.IsPlaying() || ao.IsPaused());
        }

        /// <summary>Drop a dead reference; a still-parented, silenced one (copy was deactivated) is stopped first.</summary>
        private static void Release(CharacterSounds s, AudioObject ao)
        {
            if (ao != null && ao.transform.parent == s.transform && !ao.isFadingOut)
                ao.Stop();
            s.idleAudioObject = null;
        }
    }
}

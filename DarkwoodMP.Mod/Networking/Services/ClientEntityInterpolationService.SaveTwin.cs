using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Matching a host creature to this client's own copy of it. Creatures of the shared save
    /// (worldgen free-roamers from WorldChunk.spawnChars, location and story characters, saved
    /// night spawns) carry the same SaveableObject id on every peer: the host sends it in the
    /// descriptor and the client finds the copy in vanilla's id dictionary
    /// (<c>SaveManager.uniqueIdDict</c>), inactive bodies on sleeping grid nodes included, with
    /// no scan. Only a creature with no copy here gets matched by position or a phantom.
    /// </summary>
    public static partial class ClientEntityInterpolationService
    {
        /// <summary>Save id → the host id whose descriptor carried it.</summary>
        private static readonly Dictionary<int, short> _saveIdOwners = new Dictionary<int, short>(64); // reset-in: Reset
        /// <summary>Host id the next position search is for (argument of <see cref="RejectOtherSaveTwin"/>).</summary>
        private static short _matchHostId; // process-scoped: call-scoped argument, set before each search

        /// <summary>
        /// A position candidate that is the save twin of another host body is not this one's. The
        /// same save id alone does not make it that body's twin: a location pad loaded after the
        /// world gives its bodies ids in this machine's own load order (the chapter-1 village's
        /// woodcutter got the id of a host villager elsewhere and was refused as that one's twin,
        /// so the client never showed him). As in <see cref="FindSaveTwin"/>, it is the other body's
        /// twin only under that body's name.
        /// </summary>
        private static readonly System.Predicate<Character> RejectOtherSaveTwin = c =>
        {
            int sid = SaveIdOf(c);
            if (sid <= 0 || !_saveIdOwners.TryGetValue(sid, out short owner) || owner == _matchHostId)
                return false;
            return !_descriptors.TryGetValue(owner, out EntityDescriptor d)
                || CharacterTracker.BaseNameEquals(c.name, d.Name);
        };

        private static void NoteSaveIdOwner(int saveId, short hostId)
        {
            if (saveId > 0)
                _saveIdOwners[saveId] = hostId;
        }

        private static void ForgetSaveIdOwner(short hostId)
        {
            if (_descriptors.TryGetValue(hostId, out EntityDescriptor d) && d.SaveId > 0
                && _saveIdOwners.TryGetValue(d.SaveId, out short owner) && owner == hostId)
                _saveIdOwners.Remove(d.SaveId);
        }

        /// <summary>The body's vanilla save id (0: unsaved, or a dream copy that is never saved).</summary>
        private static int SaveIdOf(Character c)
        {
            if (c == null) return 0;
            SaveableObject so = c.saveableObject;
            return so != null && so.assigned && !so.dontSave && so.uniqueId > 0 ? so.uniqueId : 0;
        }

        /// <summary>
        /// This client's copy of the host's save creature <paramref name="saveId"/>: the same id,
        /// the same name, in the same world (a dream pad copy shares its original's name). A copy
        /// that an earlier position match bound to another host body is taken back from it unless
        /// that body is the one with this save id.
        /// </summary>
        private static Character FindSaveTwin(short hostId, int saveId, string entityName, Vector3 hostPos)
        {
            if (saveId <= 0)
                return null;
            SaveManager sm = Singleton<SaveManager>.Instance;
            if (sm == null || sm.uniqueIdDict == null
                || !sm.uniqueIdDict.TryGetValue(saveId, out Transform t) || t == null)
                return null;
            Character c = t.GetComponent<Character>();
            if (c == null || !CharacterTracker.BaseNameEquals(c.name, entityName))
                return null;
            if (Player.Instance != null && c.gameObject == Player.Instance.gameObject)
                return null;
            if (SaveIdOf(c) != saveId)
                return null;
            if (!SamePresentationWorld(c.transform.position, hostPos))
                return null;
            if (CharacterTracker.TryGetStableId(c, out short bound) && bound != 0 && bound != hostId)
            {
                if (_descriptors.TryGetValue(bound, out EntityDescriptor d) && d.SaveId == saveId)
                    return null;
                UnbindHostId(bound, c);
            }
            return c;
        }

        /// <summary>Tag a local body as the copy of <paramref name="hostId"/> and wake it.</summary>
        private static void BindHostId(Character c, short hostId)
        {
            // A body asleep on an inactive grid node never started, so the tracker does not list it.
            CharacterTracker.Add(c);
            CharacterTracker.AssignId(c, hostId);
            _hostSyncedIds.Add(hostId);
            _everHostSyncedIds.Add(hostId);
            _spawnedPhantomIds.Remove(hostId);
            EnsureEntityAwake(c);
            ApplyHostLook(c, hostId);
        }

        /// <summary>
        /// The host's cosmetic look for a body bound to <paramref name="hostId"/>: its
        /// randomizers rolled here where this machine got the body, the host's where it was born.
        /// </summary>
        private static void ApplyHostLook(Character c, short hostId)
        {
            if (_descriptors.TryGetValue(hostId, out EntityDescriptor d))
                DWMPHorde.Sync.CosmeticRolls.ApplyCharacterKey(c, d.LookKey);
        }

        /// <summary>
        /// <paramref name="c"/> was bound to <paramref name="hostId"/> but is another host body's
        /// save twin: let it go. That host id matches again on its next snapshot.
        /// </summary>
        private static void UnbindHostId(short hostId, Character c)
        {
            CharacterTracker.ClearId(c);
            _hostSyncedIds.Remove(hostId);
            DropDrivenState(hostId);
            if (EntitySyncLog.On)
                EntitySyncLog.Event(() =>
                    "[ClientMatch] released " + c.name + " from id=" + hostId + " (another body's save twin)");
        }

        /// <summary>
        /// Move <paramref name="hostId"/> from <paramref name="old"/> (a phantom, or a body bound by
        /// position) to <paramref name="twin"/>. A phantom goes away; a real body stays, unbound.
        /// The driven state restarts so the twin snaps to the host pose.
        /// </summary>
        private static Character RebindToSaveTwin(Character old, Character twin, short hostId, string entityName)
        {
            if (ReferenceEquals(old, twin))
                return twin;
            bool wasPhantom = _spawnedPhantomIds.Contains(hostId);
            DropDrivenState(hostId);
            if (old != null)
            {
                Audio.EntityLoopSync.Stop(old);
                CharacterTracker.ClearId(old);
                if (wasPhantom && old.gameObject != null)
                    Object.Destroy(old.gameObject);
            }
            BindHostId(twin, hostId);
            if (EntitySyncLog.On)
                EntitySyncLog.Event(() =>
                    "[ClientMatch] replaced " + (wasPhantom ? "phantom" : "position match") + " → real "
                    + entityName + "(id=" + hostId + ")");
            return twin;
        }

        /// <summary>Forget the interpolation of a host id (its body changed): the next snapshot starts it over.</summary>
        private static void DropDrivenState(short hostId)
        {
            if (_states.TryGetValue(hostId, out EntityInterpState state))
            {
                ReleaseDrivenBody(state.CachedRb);
                _states.Remove(hostId);
            }
            _displayPositions.Remove(hostId);
            _displayRotations.Remove(hostId);
        }

        /// <summary>
        /// The local world or a location is still loading: a body the host streams may not exist
        /// here yet, so a pending match waits instead of spawning a phantom.
        /// </summary>
        private static bool IsLocalWorldLoading()
        {
            if (Core.loadingGame || !Core.worldGenFinished())
                return true;
            OutsideLocations ol = Singleton<OutsideLocations>.Instance;
            return ol != null && ol.loading;
        }
    }
}

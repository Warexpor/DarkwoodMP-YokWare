using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Spectator
{
    public sealed partial class SpectatorModeController : MonoBehaviour
    {
        /// <summary>
        /// A living player watching another (F4) leaves its body where it stood: the others see it
        /// there (<see cref="NetworkPositionOverride"/>), but on its own screen the real body goes
        /// under the watched player, hidden, because the world around the camera, the sounds and
        /// the rooms all follow the local player's position. So the spectator saw an empty spot
        /// where everyone else saw it standing. A copy of the player now stands there on its own
        /// screen, idle, facing the way it faced, for as long as it watches. A dead spectator has
        /// its body from <see cref="PlayerCorpses"/> instead.
        /// </summary>
        private GameObject _selfBody;

        private static bool LocalDown(Player player)
            => player == null || !player.alive || DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsLocalDead;

        /// <summary>On entering, before the real body is hidden (the copy takes its look as it is now).</summary>
        private void ShowSelfBody(Player player)
        {
            DropSelfBody();
            if (LocalDown(player))
                return;
            GameObject body = PlayerProxyBuilder.CreatePlayerClone(
                player, "SpectatorSelfBody", Vector3.zero, PlayerCloneKind.Remote, null);
            if (body == null)
                return;
            _selfBody = body;
            // Nothing meets it on this screen: the creatures here are the host's, and they go for
            // the body the others see, which the player's own game answers for.
            foreach (Collider col in body.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            // The player's light is the one the others see at the body; a second copy of it here
            // would light the spot twice over the real one following the camera.
            foreach (Light2D light in body.GetComponentsInChildren<Light2D>(true))
            {
                light.enabled = false;
                MeshRenderer mr = light.GetComponent<MeshRenderer>();
                if (mr != null)
                    mr.enabled = false;
            }
            foreach (AudioSource src in body.GetComponentsInChildren<AudioSource>(true))
            {
                src.Stop();
                src.enabled = false;
            }
            body.transform.position = player._transform.position;
            body.transform.rotation = player._transform.rotation;
            ModRuntime.LegacyInfo($"[Spectate] own body left standing at {body.transform.position}");
        }

        /// <summary>Every frame while watching: the copy goes once the player is down (its corpse takes over).</summary>
        private void TickSelfBody()
        {
            if (_selfBody != null && LocalDown(Player.Instance))
                DropSelfBody();
        }

        private void DropSelfBody()
        {
            if (_selfBody != null)
                Destroy(_selfBody);
            _selfBody = null;
        }
    }
}

using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Player FX / anim / dropped-item handlers composed for 0.8.</summary>
    internal sealed class PlayerFXNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal PlayerFXNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandlePlayerAnimation(PlayerAnimationMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;
            var animComp = proxy.GetComponent<SecondPlayerAnimController>();
            if (animComp == null) return;

            EntitySyncLog.Anim("player:rx",
                "[PlayerAnim] RX p" + playerId
                + (string.IsNullOrEmpty(msg.TorsoClip) ? "" : " torso=" + msg.TorsoClip)
                + (string.IsNullOrEmpty(msg.LegsClip) ? "" : " legs=" + msg.LegsClip), 0.4f);

            var prev = Sync.WorldPhysicsSyncService._suppressBroadcast;
            Sync.WorldPhysicsSyncService._suppressBroadcast = true;
            try
            {
                if (!string.IsNullOrEmpty(msg.TorsoClip))
                {
                    try
                    {
                        animComp.PlayTorso(msg.TorsoClip);
                    }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                // Don't restart walk legs while torso is vault/beartrap/etc. (vanilla hides legs).
                bool hideLegs = !string.IsNullOrEmpty(msg.TorsoClip)
                    && SecondPlayerAnimController.ShouldHideLegsForTorso(msg.TorsoClip);
                if (!hideLegs && !string.IsNullOrEmpty(msg.LegsClip))
                {
                    try
                    {
                        animComp.PlayLegs(msg.LegsClip);
                    }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                Sync.WorldPhysicsSyncService._suppressBroadcast = prev;
            }
        }

        internal void HandlePlayerAnimLibrary(PlayerAnimLibraryMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;
            if (string.IsNullOrEmpty(msg.LibraryName)) return;

            tk2dSpriteAnimator anim = proxy.GetComponent<tk2dSpriteAnimator>();
            if (anim == null) return;
            // Skip no-op applies; Resources.Load for every duplicate packet caused hitches.
            if (anim.Library != null
                && string.Equals(anim.Library.name, msg.LibraryName, System.StringComparison.Ordinal))
                return;

            var lib = Resources.Load(msg.LibraryName, typeof(tk2dSpriteAnimation)) as tk2dSpriteAnimation;
            if (lib == null)
            {
                ModRuntime.Log?.LogWarning("[AnimLib] library not found: " + msg.LibraryName);
                return;
            }

            var prev = Sync.WorldPhysicsSyncService._suppressBroadcast;
            Sync.WorldPhysicsSyncService._suppressBroadcast = true;
            try
            {
                HarmonyLib.Traverse.Create(anim).Property("Library").SetValue(lib);
                ModRuntime.Log?.LogDebug("[AnimLib] applied library: " + msg.LibraryName);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError("[AnimLib] failed to set library: " + ex);
            }
            finally
            {
                Sync.WorldPhysicsSyncService._suppressBroadcast = prev;
            }
        }

        /// <summary>
        /// Dispatches a player-specific message that was forwarded from another
        /// client. _net.CurrentReceivePlayerId has already been set to the original
        /// sender's PlayerId before this is called.
        /// </summary>
        internal void DispatchRemotePlayerForward(NetMessageType innerType, byte[] innerPayload)
        {
            switch (innerType)
            {
                case NetMessageType.PlayerLightState:
                    _net.PlayerLightFxHandlers.HandlePlayerLightState(
                        PlayerLightStateMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAudio:
                    _net.WorldFxHandlers.HandlePlayerAudio(
                        PlayerAudioMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerFiredWeapon:
                    HandlePlayerFiredWeapon(PlayerFiredWeaponMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAnimation:
                    HandlePlayerAnimation(PlayerAnimationMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerBurning:
                    _net.CombatFxHandlers.HandlePlayerBurning(
                        PlayerBurningMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerDied:
                    _net.CombatHandlers.HandlePlayerDied(
                        PlayerDiedMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerEffectSync:
                    _net.WorldProxyHandlers.HandlePlayerEffectSync(
                        PlayerEffectSyncMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAnimLibrary:
                    HandlePlayerAnimLibrary(PlayerAnimLibraryMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.ThrowableSpawn:
                    _net.CombatFxHandlers.HandleThrowableSpawn(
                        ThrowableSpawnMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.DreamEnded:
                    _net.DreamHandlers.HandleDreamEnded(DreamEndedMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.FinalDreamsceneDeath:
                    _net.CombatHandlers.HandleFinalDreamsceneDeath(
                        FinalDreamsceneDeathMessage.Deserialize(new NetReader(innerPayload)));
                    break;
            }
        }

        internal void HandleBulletImpact(BulletImpactMessage msg)
        {
            if (string.IsNullOrEmpty(msg.PrefabName)) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[BulletFX] HandleBulletImpact: {msg.PrefabName} pool={msg.PoolName} pos={pos}");

            bool isBlood = msg.PrefabName.IndexOf("Bloodsplat", System.StringComparison.OrdinalIgnoreCase) >= 0
                || msg.PrefabName.IndexOf("Shotsplat", System.StringComparison.OrdinalIgnoreCase) >= 0;

            // Wrap in ApplyingFromNetwork to prevent HitscanBloodPatch and similar
            // patches from re-forwarding this blood back to the sender.
            TraverseHack.ApplyingFromNetwork = true;
            try
            {
                if (string.IsNullOrEmpty(msg.PoolName))
                {
                    // worldSpace: true; blood must sit at the absolute world position (parent null).
                    Core.AddPrefab(msg.PrefabName, pos, rot, null, worldSpace: true);
                }
                else
                    Core.AddPooledPrefab(msg.PoolName, msg.PrefabName, pos, rot);
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }

            // Blood is visual-only; bullet_hit_1 is for walls / projectile impacts.
            if (!isBlood)
                AudioController.Play("bullet_hit_1", pos);
        }

        internal void HandlePlayerFiredWeapon(PlayerFiredWeaponMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) { ModRuntime.LegacyInfo($"[WeaponFire] handle: no proxy for player {playerId}"); return; }

            InvItem itemDef = null;
            try { itemDef = Singleton<ItemsDatabase>.Instance?.getItem(msg.ItemType, instantiate: false); }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[Network] getItem: " + ex.Message);
            }
            if (itemDef == null) { ModRuntime.LegacyInfo("[WeaponFire] handle: item not found: " + msg.ItemType); return; }
            if (!itemDef.isFirearm) { ModRuntime.LegacyInfo("[WeaponFire] handle: not a firearm: " + msg.ItemType); return; }

            Transform proxyT = proxy.transform;

            Vector3 muzzlePos = proxyT.position
                + proxyT.up * itemDef.muzzleOffset.y
                + proxyT.right * itemDef.muzzleOffset.x;
            Quaternion muzzleRot = Quaternion.Euler(90f, msg.AimY, 0f);

            ModRuntime.LegacyInfo("[WeaponFire] handle: spawning muzzle for " + msg.ItemType + " count=" + msg.ProjectileCount + " aimY=" + msg.AimY);

            if (itemDef.muzzlePrefab != null)
            {
                string name = itemDef.muzzlePrefab.name;
                if (!string.IsNullOrEmpty(name))
                    Core.AddPooledPrefab("FX", name, muzzlePos, muzzleRot);
            }

            if (itemDef.muzzleParticles != null)
            {
                string name = itemDef.muzzleParticles.name;
                if (!string.IsNullOrEmpty(name))
                    Core.AddPooledPrefab("FX", name, muzzlePos, muzzleRot);
            }

            if (!itemDef.noMuzzleFlash)
            {
                Core.AddPrefab("FX/Muzzle/PistolFlash", proxyT.position + proxyT.up, muzzleRot, null, worldSpace: true);
            }

            // Shot audio: local shooter plays parentless attackSound; peers need it here
            // (HandlePlayerFiredWeapon is VFX-only otherwise). ApplyingFromNetwork prevents
            // PlayerAudio re-forward loops.
            if (!string.IsNullOrEmpty(itemDef.attackSound))
            {
                Vector3 shotPos = proxyT.position;
                if (LocalAudioService.IsNearListener(shotPos, LocalAudioService.DefaultMaxAudioDistance))
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    try
                    {
                        var audioObj = AudioController.Play(itemDef.attackSound, shotPos, proxyT, 1f);
                        if (audioObj != null && audioObj.primaryAudioSource != null)
                        {
                            audioObj.primaryAudioSource.spatialBlend = 1f;
                            audioObj.primaryAudioSource.minDistance =
                                Mathf.Max(audioObj.primaryAudioSource.minDistance, LocalAudioService.DefaultMinSpatialDistance);
                            audioObj.primaryAudioSource.maxDistance =
                                Mathf.Max(audioObj.primaryAudioSource.maxDistance, 100f);
                            audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Linear;
                        }
                    }
                    finally { TraverseHack.ApplyingFromNetwork = false; }
                }
            }

            // Friendly-fire damage is applied only via ProxyDamagePatch (real collider hits
            // on the remote proxy → DamagePlayer / FriendlyFire). Cone damage here used to
            // double-apply with that path and could hit every peer incorrectly.
        }

        internal void HandleDroppedItemSpawn(DroppedItemSpawnMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Guid) || string.IsNullOrEmpty(msg.PrefabPath) || string.IsNullOrEmpty(msg.ItemType))
                return;
            if (Players.DroppedItemIdentifier.FindById(msg.Guid) != null)
                return;

            // Only the host's drops are authoritative. Client drops are local-only.
            // Receiving a client drop on the host would spawn a second copy,
            // causing item multiplication (both sides can pick up their copy).
            // Allow client drops through the GUID-based system (DroppedItemIdentifier +
            // LanNetworkManager.ConsumedDropGuids) prevents multiplication: when one player picks
            // up, the other player's copy is destroyed via DroppedItemPickupMessage.
            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[DroppedItemSpawn] unknown item type: " + msg.ItemType);
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            GameObject go = Core.AddPrefab(msg.PrefabPath, pos, rot, Core.ItemContainer);
            if (go == null) return;

            Inventory inv = go.GetComponent<Inventory>();
            if (inv == null || inv.slots == null || inv.slots.Count == 0) return;

            InvSlot slot = inv.slots[0];
            slot.inventory = inv;

            InvItemClass item = new InvItemClass(msg.ItemType, 1f, msg.Amount);
            if (item.baseClass == null)
            {
                ModRuntime.Log?.LogWarning("[DroppedItemSpawn] failed to assign baseClass for " + msg.ItemType);
                return;
            }

            slot.createItem(item);

            if (!InvItemClass.isNull(slot.invItem))
            {
                slot.invItem.durability = msg.Durability;
                if (slot.invItem.baseClass.hasAmmo)
                    slot.invItem.ammo = msg.Ammo;
            }

            Core.addToSaveable(go, isDynamic: true);
            Singleton<WorldGrid>.Instance?.registerToNode(go);

            var ident = go.AddComponent<Players.DroppedItemIdentifier>();
            ident.Id = msg.Guid;
            Players.DroppedItemIdentifier.Register(ident);

            ModRuntime.LegacyInfo("[DroppedItemSpawn] " + msg.ItemType + " x" + msg.Amount + " guid=" + msg.Guid);
        }

        /// <summary>Resets consumed GUID tracking (call on scene change / disconnect).</summary>
        internal void ResetConsumedDropGuids()
        {
            LanNetworkManager.ConsumedDropGuids.Clear();
        }

        internal void HandleDroppedItemPickup(DroppedItemPickupMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Guid)) return;

            // Mark consumed even if we already did (idempotent). Always try destroy
            // so a late packet still removes a lingering world copy.
            bool first = LanNetworkManager.ConsumedDropGuids.Add(msg.Guid);
            if (!first)
                ModRuntime.LegacyInfo("[DroppedItemPickup] already consumed: " + msg.Guid);

            var ident = Players.DroppedItemIdentifier.FindById(msg.Guid);
            if (ident == null || ident.gameObject == null) return;

            ModRuntime.LegacyInfo("[DroppedItemPickup] removing guid=" + msg.Guid);
            UnityEngine.Object.Destroy(ident.gameObject);
        }

    
    }
}

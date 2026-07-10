using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        /// <summary>BagId → DeathDrop (local + remote mirrors). Protocol 6+.</summary>
        private readonly Dictionary<string, DeathDrop> _spawnedDeathBags =
            new Dictionary<string, DeathDrop>(System.StringComparer.Ordinal);

        /// <summary>BagIds already fully looted — block late spawn retransmit / join races.</summary>
        private readonly HashSet<string> _lootedDeathBagIds =
            new HashSet<string>(System.StringComparer.Ordinal);

        /// <summary>Clear combat-side session maps on disconnect.</summary>
        internal void ResetCombatSessionState()
        {
            _spawnedDeathBags.Clear();
            _lootedDeathBagIds.Clear();
            _ffDebounce.Clear();
        }

        /// <summary>Register a bag under its stable BagId (local drop or remote spawn).</summary>
        internal void RegisterDeathBag(string bagId, DeathDrop drop)
        {
            if (string.IsNullOrEmpty(bagId) || drop == null) return;
            if (_lootedDeathBagIds.Contains(bagId)) return;
            _spawnedDeathBags[bagId] = drop;
        }

        internal void RegisterDeathBagLooted(string bagId)
        {
            if (!string.IsNullOrEmpty(bagId))
                _lootedDeathBagIds.Add(bagId);
        }

        internal bool IsDeathBagLooted(string bagId)
        {
            return !string.IsNullOrEmpty(bagId) && _lootedDeathBagIds.Contains(bagId);
        }

        internal void UnregisterDeathBag(string bagId)
        {
            if (!string.IsNullOrEmpty(bagId))
                _spawnedDeathBags.Remove(bagId);
        }

        /// <summary>Lookup by BagId; purges destroyed entries.</summary>
        internal DeathDrop FindDeathBagById(string bagId)
        {
            if (string.IsNullOrEmpty(bagId)) return null;
            if (!_spawnedDeathBags.TryGetValue(bagId, out DeathDrop drop))
            {
                // Component scan (local bags registered late, or dict cleared mid-session).
                foreach (DeathDrop dd in UnityEngine.Object.FindObjectsOfType<DeathDrop>(true))
                {
                    if (dd == null) continue;
                    if (string.Equals(Sync.DeathBagNetworkId.GetBagId(dd.gameObject), bagId, System.StringComparison.Ordinal))
                    {
                        _spawnedDeathBags[bagId] = dd;
                        return dd;
                    }
                }
                return null;
            }
            if (drop == null)
            {
                _spawnedDeathBags.Remove(bagId);
                return null;
            }
            return drop;
        }

        /// <summary>
        /// Per-message anti-grief clamp only. Do not rate-limit peer attacks:
        /// shotguns / multi-ray hitscan send legitimate bursts that a 50ms gate
        /// would drop (under-damage for client FF and PvE).
        /// </summary>
        private int SanitizePeerDamage(int reported, string context)
        {
            int max = Config.ModConfig.MaxPeerDamage != null ? Config.ModConfig.MaxPeerDamage.Value : 200;
            if (max < 1) max = 1;
            if (reported < 0) reported = 0;
            if (reported > max)
            {
                ModRuntime.LegacyInfo($"[{context}] clamped damage {reported} → {max}");
                return max;
            }
            return reported;
        }

        private void HandlePlayerAttack(PlayerAttackMessage msg)
        {
            if (_role != NetworkRole.Host)
            {
                ModRuntime.LegacyInfo($"[HandlePlayerAttack] rejected: not host (role={_role})");
                return;
            }

            int playerId = _currentReceivePlayerId;

            Character target = CharacterTracker.FindByStableId(msg.TargetNameHash);

            Vector3 attackPos = new Vector3(msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ);
            Vector3 targetPos = new Vector3(msg.TargetPosX, msg.TargetPosY, msg.TargetPosZ);

            if (target == null && !string.IsNullOrEmpty(msg.TargetName))
            {
                target = CharacterTracker.FindClosestByName(msg.TargetName, targetPos);
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[HandlePlayerAttack] fallback by name '{msg.TargetName}': found={target?.name ?? "null"} (searched near targetPos)");
            }

            if (target == null)
            {
                ModRuntime.LegacyInfo($"[HandlePlayerAttack] target null: nameHash={msg.TargetNameHash} name='{msg.TargetName}'");
                return;
            }

            float distSq = Vector3.SqrMagnitude(target.transform.position - attackPos);
            if (distSq > 350f * 350f)
            {
                ModRuntime.LegacyInfo($"[HandlePlayerAttack] target too far: dist={Mathf.Sqrt(distSq):F1} > 350 (target={target.name} pos={target.transform.position} attackPos={attackPos})");
                return;
            }

            if (!target.gameObject.activeSelf)
                target.gameObject.SetActive(true);
            if (!target.enabled)
                target.enabled = true;

            int damage = SanitizePeerDamage(msg.Damage, "HandlePlayerAttack");
            RemotePlayerProxy attackingProxy = GetProxy(playerId);
            Transform attackerT = attackingProxy != null
                ? attackingProxy.transform
                : (Player.Instance != null ? Player.Instance.transform : null);

            target.getHit(damage, attackerT, msg.CanCutInHalf, byPlayer: true, canInterrupt: true);

            ModRuntime.LegacyInfo($"[Attack] player {playerId} dealt {damage} to {target.name}(id={msg.TargetNameHash}) alive={target.alive} health={target.Health}");
        }

        private void HandleDamagePlayer(DamagePlayerMessage msg)
        {
            // Targeted delivery only (SendToPlayer). FF / environmental gates are
            // enforced by senders (ProxyDamagePatch vs ExplosionFriendlyFirePatch).
            if (_role != NetworkRole.Client) return;
            Player local = Player.Instance;
            if (local == null) return;

            local.getHit(msg.Damage, null, msg.CanCutInHalf, byPlayer: false, canInterrupt: true, showRedScreen: msg.ShowRedScreen);
        }

        private void HandlePlayerDied(PlayerDiedMessage msg)
        {
            Vector3 deathPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            bool isNight = msg.IsNight;
            int playerId = _currentReceivePlayerId;
            if (playerId <= 0)
            {
                ModRuntime.Log?.LogWarning("[Death] PlayerDied with invalid playerId — ignored");
                return;
            }

            ModRuntime.LegacyInfo($"[Death] Remote player {playerId} died at {deathPos}, isNight={isNight}");

            RemotePlayerProxy diedProxy = GetProxy(playerId);
            if (diedProxy != null)
            {
                CharBase cb = diedProxy.GetComponent<CharBase>();
                if (cb != null)
                {
                    cb.alive = false;
                    cb.Health = 0f;
                }
                foreach (Collider col in diedProxy.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;

                // Instant death pose — don't wait for next PlayerState anim tick.
                var anim = diedProxy.GetComponent<Players.SecondPlayerAnimController>();
                if (anim != null)
                    anim.PlayDeathClip("Death1");
            }

            if (isNight)
            {
                DeathStateTracker.OnRemoteNightDeath(playerId, deathPos);

                if (_role == NetworkRole.Host)
                {
                    if (!DeathStateTracker.TryResolveNightMorning("remote PlayerDied"))
                    {
                        ModRuntime.LegacyInfo(
                            $"[Death] Remote night death — waiting " +
                            $"(localDead={DeathStateTracker.LocalNightDeath} " +
                            $"remotes={DeathStateTracker.RemoteNightDeathCount}/{DeathStateTracker.TotalRemoteCount})");
                    }
                }
                else
                {
                    // If we are spectating the player who just died, retarget.
                    var spec = Spectator.SpectatorModeController.Instance;
                    if (spec != null && spec.IsSpectating)
                        ModRuntime.LegacyInfo($"[Death] Client saw remote night death of P{playerId}");
                }
                return;
            }

            DeathStateTracker.OnRemoteDayDeath(playerId);

            if (_role == NetworkRole.Host && Singleton<SaveManager>.Instance != null)
                Singleton<SaveManager>.Instance.Save(doJson: true);
        }

        private void HandleDeathBagSpawn(DeathBagSpawnMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            string bagId = msg.BagId;

            // Dedup: already have this bag (retransmit / late-join race).
            if (!string.IsNullOrEmpty(bagId))
            {
                if (_lootedDeathBagIds.Contains(bagId))
                {
                    ModRuntime.LegacyInfo($"[Death] Skip spawn — bag id={bagId} already looted");
                    return;
                }
                DeathDrop existing = FindDeathBagById(bagId);
                if (existing != null)
                {
                    ModRuntime.LegacyInfo($"[Death] Skip spawn — bag id={bagId} already present");
                    return;
                }
            }
            else
            {
                bagId = System.Guid.NewGuid().ToString("N");
            }

            ModRuntime.LegacyInfo($"[Death] Spawning death bag id={bagId} at {pos} water={msg.InWater}");

            string bagPrefab = msg.InWater ? "Objects/_Unique/deathDrop_water" : "Objects/_Unique/deathDrop";
            GameObject bagGO = Core.AddPrefab(bagPrefab,
                Core.getYPos(pos, PosType.items2),
                Quaternion.Euler(90f, 0f, 0f),
                Core.ItemContainer);
            if (bagGO == null)
            {
                ModRuntime.Log?.LogWarning("[Death] Failed to spawn death bag prefab");
                return;
            }

            DeathDrop deathDrop = bagGO.GetComponent<DeathDrop>();
            if (deathDrop != null)
                deathDrop.expAmount = msg.ExpAmount;

            Sync.DeathBagNetworkId.Ensure(bagGO, bagId, msg.InWater);
            if (deathDrop != null)
                RegisterDeathBag(bagId, deathDrop);

            Inventory bagInv = bagGO.GetComponent<Inventory>();
            if (bagInv != null)
            {
                bagInv.removeWhenEmpty = true;

                if (msg.ItemCount > 0 && msg.ItemTypes != null)
                {
                    bagInv.initSlots();
                    for (int i = 0; i < msg.ItemCount && i < msg.ItemTypes.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(msg.ItemTypes[i]))
                        {
                            bagInv.addSlot();
                            InvSlot slot = bagInv.getNextFreeSlot();
                            if (slot != null)
                            {
                                int amount = msg.ItemAmounts != null && i < msg.ItemAmounts.Length ? msg.ItemAmounts[i] : 1;
                                InvItemClass item = slot.createItem(msg.ItemTypes[i], amount);
                                if (item != null)
                                {
                                    if (msg.ItemDurabilities != null && i < msg.ItemDurabilities.Length)
                                        item.durability = msg.ItemDurabilities[i];
                                    if (msg.ItemAmmos != null && i < msg.ItemAmmos.Length && msg.ItemAmmos[i] > 0)
                                        item.ammo = msg.ItemAmmos[i];
                                }
                            }
                        }
                    }
                    bagInv.checkForActiveSwitches(force: true);
                }
            }
        }

        private void HandleDeathBagLooted(DeathBagLootedMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            DeathDrop found = null;

            // Primary: stable BagId
            if (!string.IsNullOrEmpty(msg.BagId))
            {
                _lootedDeathBagIds.Add(msg.BagId);
                found = FindDeathBagById(msg.BagId);
                if (found != null)
                    _spawnedDeathBags.Remove(msg.BagId);
            }

            // Fallback: XZ near position (legacy / missing id)
            if (found == null)
            {
                GameObject container = Core.ItemContainer;
                if (container != null)
                {
                    Vector2 posXZ = new Vector2(pos.x, pos.z);
                    foreach (Transform child in container.transform)
                    {
                        if (child == null) continue;
                        DeathDrop dd = child.GetComponent<DeathDrop>();
                        if (dd == null) continue;
                        Vector2 childXZ = new Vector2(child.position.x, child.position.z);
                        if (Vector2.Distance(childXZ, posXZ) < 2f)
                        {
                            found = dd;
                            break;
                        }
                    }
                }
            }

            if (found == null)
            {
                Vector2 posXZ = new Vector2(pos.x, pos.z);
                DeathDrop best = null;
                float bestDist = 2f;
                foreach (DeathDrop dd in UnityEngine.Object.FindObjectsOfType<DeathDrop>(true))
                {
                    if (dd == null) continue;
                    Vector2 ddXZ = new Vector2(dd.transform.position.x, dd.transform.position.z);
                    float d = Vector2.Distance(ddXZ, posXZ);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = dd;
                    }
                }
                found = best;
            }

            if (found != null)
            {
                string id = Sync.DeathBagNetworkId.GetBagId(found.gameObject);
                if (!string.IsNullOrEmpty(id))
                {
                    _lootedDeathBagIds.Add(id);
                    _spawnedDeathBags.Remove(id);
                }
                // Destroy under apply guard so Inventory.hide loot patch does not echo.
                using (new NetworkApplyGuard())
                {
                    UnityEngine.Object.Destroy(found.gameObject);
                }
                ModRuntime.LegacyInfo($"[Death] Destroyed death bag id={id ?? "?"} at {found.transform.position}");
            }
        }

        private void HandleNightDeathState(NightDeathStateMessage msg)
        {
            if (msg.AllDeadTrigger)
            {
                ModRuntime.LegacyInfo("[Death] All dead at night — exiting spectator for morning");

                if (Spectator.SpectatorModeController.Instance != null)
                {
                    Spectator.SpectatorModeController.Instance.ExitAndRespawn();
                }

                DeathStateTracker.Reset();
            }
        }

        private void HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            ModRuntime.LegacyInfo($"[FinalDreamscene] Received remote death notification for player {playerId}");

            RemotePlayerProxy diedProxy = GetProxy(playerId);
            if (diedProxy != null)
            {
                CharBase cb = diedProxy.GetComponent<CharBase>();
                if (cb != null)
                {
                    cb.alive = false;
                    cb.Health = 0f;
                }
                foreach (Collider col in diedProxy.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
                GetOrCreateState(playerId).IsDeadInDream = true;
            }

            Sync.FinalDreamsceneManager.OnRemoteDeathInDream(playerId);
        }

        // Friendly-fire debounce: ProxyDamagePatch + OnCollisionEnter can both
        // fire for one bullet, doubling damage for 3+ peer games.
        private readonly Dictionary<string, float> _ffDebounce = new Dictionary<string, float>();
        private const float FriendlyFireDebounceSec = 0.08f;

        private void HandleFriendlyFire(FriendlyFireMessage msg)
        {
            if (_role != NetworkRole.Host) return;
            if (!Config.ModConfig.FriendlyFireEnabled.Value) return;

            int victimPlayerId = msg.VictimPlayerId > 0 ? msg.VictimPlayerId : 0;
            Vector3 atkPos = new Vector3(msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ);
            int atkPlayerId = msg.AttackerPlayerId > 0 ? msg.AttackerPlayerId : _currentReceivePlayerId;

            // Never apply FF to self (attacker == victim) from a bad packet.
            if (victimPlayerId > 0 && atkPlayerId > 0 && victimPlayerId == atkPlayerId)
                return;

            int damage = SanitizePeerDamage(msg.Damage, "FriendlyFire");

            string debounceKey = atkPlayerId + "_" + victimPlayerId;
            float now = Time.time;
            if (_ffDebounce.TryGetValue(debounceKey, out float last) && now - last < FriendlyFireDebounceSec)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[FriendlyFire] debounced atk={atkPlayerId}→vic={victimPlayerId}");
                return;
            }
            _ffDebounce[debounceKey] = now;

            RemotePlayerProxy attackingProxy = GetProxy(atkPlayerId);
            Transform atkTransform = attackingProxy != null ? attackingProxy.transform : (Player.Instance?.transform);

            if (victimPlayerId == _localPlayerId || victimPlayerId == 0)
            {
                Player host = Player.Instance;
                if (host == null) return;
                host.getHit(damage, atkTransform, msg.CanCutInHalf, byPlayer: true, canInterrupt: true);
                ModRuntime.LegacyInfo($"[FriendlyFire] Host took {damage} damage from player {atkPlayerId}");

                Vector3 hitPoint = host.transform.position;
                Vector3 toHost = (host.transform.position - atkPos).normalized;
                float dist = Vector3.Distance(atkPos, host.transform.position) + 0.5f;
                if (Physics.Raycast(atkPos, toHost, out RaycastHit hostHit, dist, GameplayConstants.HitscanLayerMask))
                    hitPoint = hostHit.point;

                BroadcastFriendlyFireBlood(hitPoint, host.inWater, host.transform.eulerAngles.y);
            }
            else
            {
                ModRuntime.LegacyInfo($"[FriendlyFire] Forwarding {damage} damage from player {atkPlayerId} to victim player {victimPlayerId}");
                SendToPlayer(victimPlayerId, NetMessageType.DamagePlayer, w =>
                {
                    new DamagePlayerMessage
                    {
                        Damage = damage,
                        AttackerPosX = atkPos.x,
                        AttackerPosY = atkPos.y,
                        AttackerPosZ = atkPos.z,
                        CanCutInHalf = msg.CanCutInHalf,
                        ShowRedScreen = true
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);

                // Blood on victim proxy so all peers see the hit (not only host self-hit path).
                RemotePlayerProxy victimProxy = GetProxy(victimPlayerId);
                if (victimProxy != null)
                {
                    CharBase vicCb = victimProxy.GetComponent<CharBase>();
                    bool inWater = vicCb != null && vicCb.inWater;
                    BroadcastFriendlyFireBlood(victimProxy.transform.position, inWater, victimProxy.transform.eulerAngles.y);
                }
            }
        }

        private void BroadcastFriendlyFireBlood(Vector3 hitPoint, bool inWater, float baseRotY)
        {
            string bloodPrefab = inWater ? "FX/Bloodsplats/Shotsplat" : "FX/Bloodsplats/Shotsplat_stay";
            float rotY = baseRotY + UnityEngine.Random.Range(-40f, 40f);
            bool prevHack = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try { Core.AddPrefab(bloodPrefab, hitPoint, Quaternion.Euler(90f, rotY, 0f), null); }
            finally { TraverseHack.SetExplicitFlag(prevHack); }

            Broadcast(NetMessageType.BulletImpact, w => new BulletImpactMessage
            {
                PrefabName = bloodPrefab,
                PoolName = "",
                PosX = hitPoint.x,
                PosY = hitPoint.y,
                PosZ = hitPoint.z,
                RotX = 90f,
                RotY = rotY,
                RotZ = 0f
            }.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}

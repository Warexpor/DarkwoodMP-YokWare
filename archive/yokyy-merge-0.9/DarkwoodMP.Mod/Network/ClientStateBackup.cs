using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DarkwoodMP.GameLogic;
using UnityEngine;

namespace DarkwoodMP.Network;

/// <summary>
/// Per-player progression backup on host (Horde ClientStateBackup).
/// JsonUtility serializable DTOs — no Newtonsoft; LiteNetLib 1.3.5 unchanged.
/// </summary>
[Serializable]
public class ClientStateBackupData
{
    public int PlayerId;
    public string Timestamp = "";
    public int Day;
    public int GameTimeMinutes;
    public float PosX, PosY, PosZ;
    public float Health, Stamina;
    public int Experience, CurrentLevel;
    public int SkillPoints;
    public string InventoryCsv = ""; // type,amt,dur;...
    public string HotbarCsv = "";
    public string SkillsCsv = "";    // name,times;
    public string NightTraderRepCsv = ""; // name:rep;
}

public static class ClientStateBackup
{
    public static ClientStateBackupData Collect()
    {
        var data = new ClientStateBackupData
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            PlayerId = NetworkManager.Instance != null ? NetworkManager.Instance.LocalPlayerId : 0
        };
        try
        {
            var player = Player.Instance;
            if (player == null) return data;
            var pos = player.transform.position;
            data.PosX = pos.x; data.PosY = pos.y; data.PosZ = pos.z;
            try { data.Health = player.health; data.Stamina = player.stamina; } catch { }
            try
            {
                data.Experience = player.experience;
                data.CurrentLevel = player.currentLevel;
            }
            catch { }
            try
            {
                if (player.skills != null)
                    data.SkillPoints = player.skills.skillPoints;
            }
            catch { }

            var ctrl = UnityEngine.Object.FindObjectOfType<Controller>();
            if (ctrl != null)
            {
                data.Day = ctrl.day;
                data.GameTimeMinutes = ctrl.CurrentTime;
            }

            if (player.Inventory != null)
                data.InventoryCsv = EncodeInv(player.Inventory);
            try
            {
                var hotbar = player.GetType().GetField("hotbar")?.GetValue(player) as Inventory
                    ?? player.GetType().GetField("Hotbar")?.GetValue(player) as Inventory;
                if (hotbar != null) data.HotbarCsv = EncodeInv(hotbar);
            }
            catch { }

            // Night trader reps (per-player)
            try
            {
                var flags = Singleton<Flags>.Instance;
                if (flags != null)
                {
                    var sb = new StringBuilder();
                    foreach (var npc in UnityEngine.Object.FindObjectsOfType<NPC>())
                    {
                        if (npc == null || string.IsNullOrEmpty(npc.name)) continue;
                        if (npc.name.IndexOf("night", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var st = flags.getNPCState(npc.name);
                        if (st == null) continue;
                        if (sb.Length > 0) sb.Append(';');
                        sb.Append(npc.name.Replace(":", "_")).Append(':').Append(st.reputation);
                    }
                    data.NightTraderRepCsv = sb.ToString();
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ClientBackup] Collect: {ex.Message}");
        }
        return data;
    }

    public static void Restore(ClientStateBackupData data)
    {
        if (data == null) return;
        RemoteApply.Active = true;
        try
        {
            var player = Player.Instance;
            if (player == null) return;
            try
            {
                player.transform.position = new Vector3(data.PosX, data.PosY, data.PosZ);
                player.health = data.Health;
                player.stamina = data.Stamina;
            }
            catch { }
            try
            {
                player.experience = data.Experience;
                player.currentLevel = data.CurrentLevel;
                if (player.skills != null)
                    player.skills.skillPoints = data.SkillPoints;
            }
            catch { }

            if (player.Inventory != null && !string.IsNullOrEmpty(data.InventoryCsv))
                ApplyInv(player.Inventory, data.InventoryCsv);

            if (!string.IsNullOrEmpty(data.NightTraderRepCsv))
            {
                foreach (var e in data.NightTraderRepCsv.Split(';'))
                {
                    var sep = e.LastIndexOf(':');
                    if (sep <= 0) continue;
                    var name = e.Substring(0, sep);
                    if (!int.TryParse(e.Substring(sep + 1), out var rep)) continue;
                    try
                    {
                        var st = Singleton<Flags>.Instance?.getNPCState(name);
                        if (st != null) st.reputation = rep;
                    }
                    catch { }
                }
            }
            ModLogger.Msg($"[ClientBackup] Restored playerId={data.PlayerId}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ClientBackup] Restore: {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    public static string ToJson(ClientStateBackupData data) =>
        data == null ? "{}" : JsonUtility.ToJson(data);

    public static ClientStateBackupData FromJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonUtility.FromJson<ClientStateBackupData>(json); }
        catch { return null; }
    }

    public static string ProfileBackupDir()
    {
        try
        {
            var sm = Singleton<SaveManager>.Instance;
            if (sm != null && Core.currentProfile != null)
            {
                // Best-effort profile path
                var basePath = Application.persistentDataPath;
                return Path.Combine(basePath, "YokWareBackups", "prof" + Core.currentProfile.id);
            }
        }
        catch { }
        return Path.Combine(Application.persistentDataPath, "YokWareBackups");
    }

    public static void SaveFile(string json, int playerId)
    {
        try
        {
            var dir = ProfileBackupDir();
            Directory.CreateDirectory(dir);
            var name = playerId > 0 ? $"client_backup_p{playerId}.json" : "client_backup_self.json";
            File.WriteAllText(Path.Combine(dir, name), json ?? "{}");
            ModLogger.Msg($"[ClientBackup] Wrote {name}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ClientBackup] SaveFile: {ex.Message}");
        }
    }

    public static string LoadFile(int playerId)
    {
        try
        {
            var dir = ProfileBackupDir();
            var name = playerId > 0 ? $"client_backup_p{playerId}.json" : "client_backup_self.json";
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) && playerId > 0)
            {
                // legacy self
                path = Path.Combine(dir, "client_backup_self.json");
            }
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    public static void EmitToHost()
    {
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        var manager = NetworkManager.Instance;
        if (network == null || manager == null || !network.IsConnected) return;
        var data = Collect();
        data.PlayerId = manager.LocalPlayerId;
        var json = ToJson(data);
        // Local self backup always
        SaveFile(json, 0);
        // Base64 chunked ClientStateBackupChunk packets (Ironbark typed)
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        const int chunk = 1200;
        int total = Math.Max(1, (b64.Length + chunk - 1) / chunk);
        for (int i = 0; i < total; i++)
        {
            int len = Math.Min(chunk, b64.Length - i * chunk);
            var part = b64.Length == 0 ? "" : b64.Substring(i * chunk, len);
            network.SendReliableCritical(new Packets.ClientStateBackupChunkPacket
            {
                PlayerId = manager.LocalPlayerId,
                Index = i,
                Total = total,
                Part = part
            });
        }
        ModLogger.Msg($"[ClientBackup] Ironbark emitted {total} chunk(s) to host");
    }

    private static readonly Dictionary<int, string[]> _recvChunks = new Dictionary<int, string[]>();

    public static void HandleChunk(int playerId, int index, int total, string part)
    {
        if (!_recvChunks.TryGetValue(playerId, out var arr) || arr == null || arr.Length != total)
            arr = new string[total];
        if (index >= 0 && index < total)
            arr[index] = part ?? "";
        _recvChunks[playerId] = arr;
        for (int i = 0; i < total; i++)
            if (string.IsNullOrEmpty(arr[i])) return;
        // complete
        var sb = new StringBuilder();
        for (int i = 0; i < total; i++) sb.Append(arr[i]);
        _recvChunks.Remove(playerId);
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(sb.ToString()));
            SaveFile(json, playerId);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ClientBackup] HandleChunk: {ex.Message}");
        }
    }

    public static void TryRestoreLocal()
    {
        var json = LoadFile(0);
        if (string.IsNullOrEmpty(json)) return;
        var data = FromJson(json);
        if (data != null) Restore(data);
    }

    private static bool _restoreAttempted;

    /// <summary>
    /// Once per session after local player exists and world gen finished:
    /// re-apply last local self-backup (reconnect progression).
    /// </summary>
    public static void MaybeRestoreAfterSpawn()
    {
        if (_restoreAttempted) return;
        if (Player.Instance == null) return;
        try
        {
            if (!Core.worldGenFinished()) return;
        }
        catch { return; }
        _restoreAttempted = true;
        // Host keeps authority world inventory; clients rehydrate personal backup.
        var manager = NetworkManager.Instance;
        if (manager != null && manager.IsHost) return;
        TryRestoreLocal();
    }

    public static void ResetSession()
    {
        _restoreAttempted = false;
        _recvChunks.Clear();
    }

    private static string EncodeInv(Inventory inv)
    {
        var sb = new StringBuilder();
        try
        {
            var items = inv.getAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (InvItemClass.isNull(items[i])) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(items[i].type).Append(',').Append(items[i].amount)
                    .Append(',').Append(items[i].durability.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch { }
        return sb.ToString();
    }

    private static void ApplyInv(Inventory inv, string csv)
    {
        try
        {
            inv.clear();
            foreach (var e in csv.Split(';'))
            {
                var p = e.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[1], out var amt) || amt <= 0) continue;
                inv.addItemType(p[0], amt);
            }
        }
        catch { }
    }
}

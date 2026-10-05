using System;
using System.Collections.Generic;
using System.Globalization;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// What the desync check compares. Each section turns live state into <c>key → value</c>
    /// entries built the same way on the host and on a client; values hold only what should be
    /// equal across machines (no per-player state, nothing a client simulates on its own).
    /// </summary>
    internal static partial class DesyncCheck
    {
        /// <summary>Whose view a section is built for.</summary>
        private struct Ctx
        {
            public bool Host;
            public LanNetworkManager Net;
            /// <summary>The client the check is for (its own id on the client).</summary>
            public int FocusId;
            /// <summary>Where that client stands (host: its stand-in; client: its own body).</summary>
            public Vector3 FocusPos;
        }

        private sealed class Section
        {
            public byte Id;
            public string Name;
            /// <summary>Built around the focus player and sent inline; otherwise world-wide and hashed.</summary>
            public bool Focus;
            public Action<Ctx, List<KeyValuePair<string, string>>> Collect;
            public DesyncEntries.SameFn Same;
        }

        private static Ctx HostCtx(LanNetworkManager net, int focusId)
        {
            var ctx = new Ctx { Host = true, Net = net, FocusId = focusId };
            RemotePlayerProxy proxy = focusId > 0 ? net.GetProxy(focusId) : null;
            ctx.FocusPos = proxy != null ? proxy.transform.position : Vector3.zero;
            return ctx;
        }

        private static Ctx ClientCtx(LanNetworkManager net) => new Ctx
        {
            Host = false,
            Net = net,
            FocusId = net.LocalPlayerId,
            FocusPos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero
        };

        private static string Build(Section s, Ctx ctx)
        {
            var list = new List<KeyValuePair<string, string>>(64);
            try
            {
                s.Collect(ctx, list);
            }
            catch (Exception ex)
            {
                // A broken collector must not take the check down; its section reads as one entry.
                list.Clear();
                list.Add(new KeyValuePair<string, string>("!collector", ex.GetType().Name));
            }
            return DesyncEntries.Format(list);
        }

        private static Section Find(byte id)
        {
            for (int i = 0; i < Sections.Length; i++)
                if (Sections[i].Id == id)
                    return Sections[i];
            return null;
        }

        /// <summary>
        /// Around-the-player sections, in world units (a hideout room is a few hundred across;
        /// the screen shows about 1000): state streams to a client only near players.
        /// </summary>
        private const float NearRadius = 400f;
        private const float CreatureRadius = 600f;
        private const float ContainerRadius = 200f;
        /// <summary>
        /// Entries are collected this much past the radius and carry their distance, so an object
        /// right at the edge (the host measures from a stand-in a packet behind) is not a difference.
        /// </summary>
        private const float EdgeMargin = 60f;

        private static readonly Section[] Sections = // process-scoped: immutable table
        {
            new Section { Id = 1, Name = "Flags", Collect = CollectFlags },
            new Section { Id = 2, Name = "Npcs", Collect = CollectNpcs },
            new Section { Id = 3, Name = "Players", Focus = true, Collect = CollectPlayers, Same = SamePlayer },
            new Section { Id = 4, Name = "Doors", Focus = true, Collect = CollectDoors, Same = SameDoor },
            new Section { Id = 5, Name = "Creatures", Focus = true, Collect = CollectCreatures, Same = SameCreature },
            new Section { Id = 6, Name = "Traps", Focus = true, Collect = CollectTraps,
                Same = NearSame(NearRadius, "triggered", "active") },
            new Section { Id = 7, Name = "Generators", Focus = true, Collect = CollectGenerators, Same = SameGenerator },
            new Section { Id = 8, Name = "Pickups", Focus = true, Collect = CollectPickups, Same = NearSame(NearRadius, "item") },
            new Section { Id = 9, Name = "Drops", Collect = CollectDrops },
            new Section { Id = 10, Name = "Containers", Focus = true, Collect = CollectContainers, Same = SameContainer },
            new Section { Id = 11, Name = "Burning", Focus = true, Collect = CollectBurning, Same = NearSame(NearRadius) },
            new Section { Id = 12, Name = "Traders", Collect = CollectTraders, Same = SameTrader },
            new Section { Id = 13, Name = "Journal", Collect = CollectJournal },
            new Section { Id = 14, Name = "Night", Collect = CollectNight },
            new Section { Id = 15, Name = "World", Collect = CollectWorld },
        };

        // ------------------------------------------------------------ helpers

        private static void Add(List<KeyValuePair<string, string>> into, string key, string value)
            => into.Add(new KeyValuePair<string, string>(key, value));

        /// <summary>Position key, half-unit grid (scene objects sit at the same spot on every machine).</summary>
        private static string PosKey(Vector3 p)
            => Mathf.RoundToInt(p.x * 2f).ToString(CultureInfo.InvariantCulture) + ","
               + Mathf.RoundToInt(p.y * 2f).ToString(CultureInfo.InvariantCulture) + ","
               + Mathf.RoundToInt(p.z * 2f).ToString(CultureInfo.InvariantCulture);

        private static string B(bool v) => v ? "1" : "0";

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        private static string BaseName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            const string clone = "(Clone)";
            return name.EndsWith(clone, StringComparison.Ordinal) ? name.Substring(0, name.Length - clone.Length).TrimEnd() : name;
        }

        /// <summary>Value fields "a=1|b=2" → dictionary.</summary>
        private static Dictionary<string, string> Fields(string v)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(v))
                return d;
            foreach (string part in v.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                    d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return d;
        }

        private static bool Near(Dictionary<string, string> a, Dictionary<string, string> b, string field, int tol)
        {
            a.TryGetValue(field, out string x);
            b.TryGetValue(field, out string y);
            if (!int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ix)
                || !int.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iy))
                return string.Equals(x, y, StringComparison.Ordinal);
            return Math.Abs(ix - iy) <= tol;
        }

        private static bool SameField(Dictionary<string, string> a, Dictionary<string, string> b, string field)
        {
            a.TryGetValue(field, out string x);
            b.TryGetValue(field, out string y);
            return string.Equals(x, y, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------ sections

        /// <summary>Story flags (database flags), minus the per-player ones that stay local.</summary>
        private static void CollectFlags(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            Flags flags = Singleton<Flags>.Instance;
            if (flags == null || flags.flagsDict == null)
                return;
            foreach (KeyValuePair<string, Flags.Flag> kv in flags.flagsDict)
            {
                Flags.Flag f = kv.Value;
                if (f == null || PerPlayerFlagPolicy.IsPerPlayer(kv.Key))
                    continue;
                // Defaults are left out on both sides, so "never set" and "set false" agree.
                if (!f.isTrue && f.amount == 0)
                    continue;
                Add(into, kv.Key, B(f.isTrue) + ":" + I(f.amount));
            }
            Add(into, "~karma", I(flags.karmaPoints));
        }

        /// <summary>NPC state (alive or dead, reputation).</summary>
        private static void CollectNpcs(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            Flags flags = Singleton<Flags>.Instance;
            if (flags == null || flags.npcStates == null)
                return;
            // Same rule as ReputationSyncUtil.IsPerPlayerReputationNpcName, from the registry (no scene scan).
            var npcs = new List<NPC>(64);
            DesyncRegistry<NPC>.Snapshot(npcs);
            var nightTrader = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (NPC npc in npcs)
            {
                Character ch = npc.GetComponent<Character>();
                if (ch != null && !nightTrader.ContainsKey(npc.name))
                    nightTrader[npc.name] = ch.isNightTrader;
            }
            foreach (Flags.NPCState n in flags.npcStates)
            {
                if (n == null || string.IsNullOrEmpty(n.name))
                    continue;
                // Night traders keep a standing per player (ReputationSyncUtil); their death is shared.
                bool own = nightTrader.TryGetValue(n.name, out bool nt) ? nt : DialogApplyPolicy.IsPerPlayerReputationNpcName(n.name);
                string rep = own ? "own" : I(n.reputation);
                Add(into, n.name, "dead=" + B(n.dead) + "|rep=" + rep);
            }
        }

        /// <summary>
        /// Every player in the session as this machine sees it: the local player from its own body,
        /// the others from their stand-ins (fed by each owner's PlayerEffectSync).
        /// </summary>
        private static void CollectPlayers(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            Player local = Player.Instance;
            if (local != null)
            {
                float maxHp = local.maxHealth > 0f ? local.maxHealth : 1f;
                int hp = Mathf.Clamp(Mathf.RoundToInt(local.health / maxHp * 100f), 0, 100);
                CharBase cb = local.GetComponent<CharBase>();
                Add(into, "p" + I(ctx.Net.LocalPlayerId), PlayerValue(local.alive, hp,
                    cb != null && cb.poisoned, cb != null && cb.bleeding,
                    WorldProxyEffectNetHandlers.LearnedSkillNames(local)));
            }
            foreach (RemotePlayerProxy proxy in ctx.Net.GetAllProxies())
            {
                if (proxy == null || proxy.PlayerId <= 0)
                    continue;
                // Host: a third player still joining is not listed (the client may not have it yet).
                if (ctx.Host && proxy.PlayerId != ctx.FocusId && !ctx.Net.DesyncPeerSettled(proxy.PlayerId))
                    continue;
                CharBase cb = proxy.CachedCharBase;
                var skills = new List<string>(proxy.RemoteSkills);
                skills.Sort(StringComparer.Ordinal);
                Add(into, "p" + I(proxy.PlayerId), PlayerValue(cb == null || cb.alive, proxy.RemoteHealthPct,
                    proxy.RemotePoisoned, proxy.RemoteBleeding, string.Join("|", skills)));
            }
        }

        private static string PlayerValue(bool alive, int hp, bool poisoned, bool bleeding, string skills)
        {
            string[] s = string.IsNullOrEmpty(skills) ? new string[0] : skills.Split('|');
            Array.Sort(s, StringComparer.Ordinal);
            return "alive=" + B(alive) + "|hp=" + I(hp) + "|poison=" + B(poisoned) + "|bleed=" + B(bleeding)
                + "|skills=" + string.Join(",", s);
        }

        /// <summary>
        /// Health rides on 2 s keepalives: a few points apart is in flight, not drift. A player
        /// only the client lists is one the host left out while it joins.
        /// </summary>
        private static bool SamePlayer(string key, string host, string client)
        {
            if (host == null)
                return true;
            if (client == null)
                return false;
            var a = Fields(host);
            var b = Fields(client);
            return SameField(a, b, "alive") && SameField(a, b, "poison") && SameField(a, b, "bleed")
                && SameField(a, b, "skills") && Near(a, b, "hp", 10);
        }

        // ---------------------------------------------------- near the player

        /// <summary>XZ distance to the focus player, or -1 when it is past the radius plus margin.</summary>
        private static int NearDist(Ctx ctx, Vector3 p, float radius)
        {
            float dx = p.x - ctx.FocusPos.x;
            float dz = p.z - ctx.FocusPos.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            return d > radius + EdgeMargin ? -1 : Mathf.RoundToInt(d);
        }

        /// <summary>
        /// Compare the named fields exactly. One side missing is fine while the side that has it
        /// sees it past <paramref name="radius"/> (the edge band: the other side measured it out).
        /// </summary>
        private static DesyncEntries.SameFn NearSame(float radius, params string[] fields)
        {
            return (key, host, client) =>
            {
                if (host == null || client == null)
                    return OnlyInEdge(host ?? client, radius);
                var a = Fields(host);
                var b = Fields(client);
                for (int i = 0; i < fields.Length; i++)
                    if (!SameField(a, b, fields[i]))
                        return false;
                return true;
            };
        }

        private static bool OnlyInEdge(string present, float radius)
        {
            Fields(present).TryGetValue("d", out string d);
            return int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dist) && dist > radius;
        }

        /// <summary>Doors near the player: open, broken, barricade and health.</summary>
        private static void CollectDoors(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            IList<Door> doors = ListTracker<Door>.GetAll();
            for (int i = 0; i < doors.Count; i++)
            {
                Door d = doors[i];
                if (d == null || WorldQueryHelper.IsInactiveTwin(d, doors))
                    continue;
                Vector3 p = d.transform.position;
                int dist = NearDist(ctx, p, NearRadius);
                if (dist < 0)
                    continue;
                Add(into, BaseName(d.name) + "@" + PosKey(p),
                    "d=" + I(dist) + "|open=" + B(TraverseHack.ReadDoorOpened(d)) + "|destroyed=" + B(d.destroyed)
                    + "|barricaded=" + B(d.barricaded) + "|bstate=" + I(d.barricadeState) + "|hp=" + I(d.health)
                    + "|bhp=" + I(d.barricadeHealth) + "|blocked=" + B(d.blocked));
            }
        }

        /// <summary>A broken door's health means nothing (vanilla leaves whatever it had).</summary>
        private static bool SameDoor(string key, string host, string client)
        {
            if (host == null || client == null)
                return OnlyInEdge(host ?? client, NearRadius);
            var a = Fields(host);
            var b = Fields(client);
            foreach (string f in new[] { "open", "destroyed", "barricaded", "bstate", "blocked" })
                if (!SameField(a, b, f))
                    return false;
            a.TryGetValue("destroyed", out string destroyed);
            return destroyed == "1" || (SameField(a, b, "hp") && SameField(a, b, "bhp"));
        }

        /// <summary>
        /// Creatures near the player, by the host's id. The host lists every body it has there;
        /// a client lists its host-driven bodies and any visible body the host never claimed
        /// (a local leftover the player can see and the host does not know).
        /// </summary>
        private static void CollectCreatures(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            int n = CharacterTracker.CopyAll(out Character[] buf);
            var chars = new Character[n];
            Array.Copy(buf, chars, n);
            Player local = Player.Instance;
            for (int i = 0; i < n; i++)
            {
                Character c = chars[i];
                if (c == null || c.gameObject == null)
                    continue;
                if (local != null && c.gameObject == local.gameObject)
                    continue;
                Vector3 p = c.transform.position;
                int dist = NearDist(ctx, p, CreatureRadius);
                if (dist < 0)
                    continue;
                if (c.GetComponent<RemotePlayerProxy>() != null || c.name.StartsWith("RemotePlayer", StringComparison.Ordinal))
                    continue;
                if (NightVillage.IsHidden(c.gameObject))
                    continue;
                // TryGetStableId only: the check never mints an id (it must not change what it reads).
                if (!CharacterTracker.TryGetStableId(c, out short id))
                    id = 0;
                if (!ctx.Host && !c.gameObject.activeInHierarchy)
                    continue;
                CharBase cb = c.GetComponent<CharBase>();
                bool alive = cb == null || cb.alive;
                int hp = 100;
                if (cb != null && cb.maxHealth > 0f)
                    hp = Mathf.Clamp(Mathf.RoundToInt(cb.health / cb.maxHealth * 100f), 0, 100);
                // No id on the host: never streamed. No id on a client: a body the host does not drive.
                string key = id != 0 ? "c" + I(id) : (ctx.Host ? "unsent:" : "local:") + BaseName(c.name) + "@" + PosKey(p);
                Add(into, key, "d=" + I(dist) + "|name=" + BaseName(c.name) + "|alive=" + B(alive) + "|hp=" + I(hp)
                    + "|x=" + I(Mathf.RoundToInt(p.x)) + "|z=" + I(Mathf.RoundToInt(p.z)));
            }
        }

        /// <summary>
        /// Name and alive exact; health a few points apart and a body a few units behind are the
        /// stream in flight. A dead body is not compared for health or spot (corpses settle locally).
        /// </summary>
        private static bool SameCreature(string key, string host, string client)
        {
            if (host == null || client == null)
                return OnlyInEdge(host ?? client, CreatureRadius);
            var a = Fields(host);
            var b = Fields(client);
            if (!SameField(a, b, "name") || !SameField(a, b, "alive"))
                return false;
            a.TryGetValue("alive", out string alive);
            if (alive == "0")
                return true;
            if (!Near(a, b, "hp", 15))
                return false;
            // About a tenth of a second of a running creature: the stream's interpolation delay.
            return Near(a, b, "x", 60) && Near(a, b, "z", 60);
        }

        /// <summary>World traps near the player: sprung or set.</summary>
        private static void CollectTraps(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            var all = new List<Trigger>(256);
            DesyncRegistry<Trigger>.Snapshot(all);
            for (int i = 0; i < all.Count; i++)
            {
                Trigger t = all[i];
                if (t == null)
                    continue;
                Vector3 p = t.transform.position;
                int dist = NearDist(ctx, p, NearRadius);
                if (dist < 0 || !t.gameObject.activeInHierarchy || !TrapNetworkId.IsWorldTrap(t.gameObject))
                    continue;
                Add(into, BaseName(t.name) + "@" + PosKey(p),
                    "d=" + I(dist) + "|triggered=" + B(t.triggered) + "|active=" + B(t.active));
            }
        }

        private static void CollectGenerators(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            IList<Generator> gens = ListTracker<Generator>.GetAll();
            for (int i = 0; i < gens.Count; i++)
            {
                Generator g = gens[i];
                if (g == null)
                    continue;
                Vector3 p = g.transform.position;
                int dist = NearDist(ctx, p, NearRadius);
                if (dist < 0)
                    continue;
                Add(into, BaseName(g.name) + "@" + PosKey(p),
                    "d=" + I(dist) + "|on=" + B(g.isOn) + "|low=" + B(g.lowPower) + "|fuel=" + I(Mathf.RoundToInt(g.fuel)));
            }
        }

        /// <summary>Fuel burns locally between the host's resyncs (sent on a whole-unit change).</summary>
        private static bool SameGenerator(string key, string host, string client)
        {
            if (host == null || client == null)
                return OnlyInEdge(host ?? client, NearRadius);
            var a = Fields(host);
            var b = Fields(client);
            return SameField(a, b, "on") && SameField(a, b, "low") && Near(a, b, "fuel", 2);
        }

        /// <summary>
        /// Items lying in the world near the player that have no drop id (level pickups, loot
        /// thrown out of containers): the item and its stack.
        /// </summary>
        private static void CollectPickups(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            var all = new List<Item>(256);
            DesyncRegistry<Item>.Snapshot(all);
            for (int i = 0; i < all.Count; i++)
            {
                Item it = all[i];
                if (it == null || !it.isDroppedItem)
                    continue;
                Vector3 p = it.transform.position;
                int dist = NearDist(ctx, p, NearRadius);
                if (dist < 0 || !it.gameObject.activeInHierarchy || it.GetComponent<DroppedItemIdentifier>() != null)
                    continue;
                Inventory inv = it.GetComponent<Inventory>();
                InvItemClass stack = inv != null && inv.slots != null && inv.slots.Count > 0 ? inv.slots[0].invItem : null;
                Add(into, BaseName(it.name) + "@" + PosKey(p), "d=" + I(dist) + "|item=" + ItemText(stack));
            }
        }

        /// <summary>Player drops (they carry an id): which exist and what they hold, world-wide.</summary>
        private static void CollectDrops(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            var drops = new List<DroppedItemIdentifier>(32);
            DroppedItemIdentifier.CopyAll(drops);
            for (int i = 0; i < drops.Count; i++)
            {
                DroppedItemIdentifier d = drops[i];
                if (d == null || string.IsNullOrEmpty(d.Id) || d.gameObject == null)
                    continue;
                Inventory inv = d.GetComponent<Inventory>();
                InvItemClass stack = inv != null && inv.slots != null && inv.slots.Count > 0 ? inv.slots[0].invItem : null;
                Add(into, d.Id, ItemText(stack));
            }
        }

        /// <summary>One stack as text: type (recipe: what it teaches), count (ammo for a firearm).</summary>
        private static string ItemText(InvItemClass it)
        {
            if (InvItemClass.isNull(it))
                return "-";
            bool hasAmmo = it.baseClass != null && it.baseClass.hasAmmo;
            string type = it.isRecipe ? "recipe:" + it.recipeFor : it.type;
            return type + "x" + I(hasAmmo ? it.ammo : it.amount);
        }

        /// <summary>
        /// Client: containers whose contents came from the host (a full snapshot on open). Loot is
        /// rolled on the host and reaches a client only when it opens the container, so an
        /// unopened one is expected to differ and is not compared.
        /// </summary>
        private static readonly HashSet<int> _syncedContainers = new HashSet<int>(); // reset-in: Reset

        internal static void NoteContainerSynced(Inventory inv)
        {
            if (inv != null)
                _syncedContainers.Add(inv.GetInstanceID());
        }

        private static void CollectContainers(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            var all = new List<Inventory>(256);
            DesyncRegistry<Inventory>.Snapshot(all);
            var parts = new List<string>(16);
            for (int i = 0; i < all.Count; i++)
            {
                Inventory inv = all[i];
                if (inv == null || inv.slots == null)
                    continue;
                Vector3 p = inv.transform.position;
                int dist = NearDist(ctx, p, ContainerRadius);
                if (dist < 0 || !ContainerSyncHelpers.IsContainer(inv))
                    continue;
                Item asItem = inv.GetComponent<Item>();
                if (asItem != null && asItem.isDroppedItem)
                    continue; // a ground stack (Pickups / Drops), not a container
                if (inv.GetComponentInParent<Character>() != null)
                    continue; // a body's loot: the body lies where each machine's ragdoll left it
                parts.Clear();
                for (int s = 0; s < inv.slots.Count; s++)
                {
                    InvItemClass it = inv.slots[s] != null ? inv.slots[s].invItem : null;
                    if (!InvItemClass.isNull(it))
                        parts.Add(I(s) + ":" + ItemText(it));
                }
                string synced = ctx.Host ? "" : "|s=" + B(_syncedContainers.Contains(inv.GetInstanceID()));
                Add(into, BaseName(inv.name) + "@" + PosKey(p), "d=" + I(dist) + synced + "|items=" + string.Join(",", parts));
            }
        }

        private static bool SameContainer(string key, string host, string client)
        {
            if (host == null || client == null)
                return OnlyInEdge(host ?? client, ContainerRadius);
            var b = Fields(client);
            if (b.TryGetValue("s", out string synced) && synced != "1")
                return true;
            return SameField(Fields(host), b, "items");
        }

        /// <summary>Burning doors, windows and items near the player.</summary>
        private static void CollectBurning(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            var all = new List<Burn>(256);
            DesyncRegistry<Burn>.Snapshot(all);
            for (int i = 0; i < all.Count; i++)
            {
                Burn b = all[i];
                if (b == null)
                    continue;
                Vector3 p = b.transform.position;
                int dist = NearDist(ctx, p, NearRadius);
                if (dist < 0 || !b.isActiveAndEnabled)
                    continue;
                if (b.GetComponent<Player>() != null || b.GetComponent<RemotePlayerProxy>() != null
                    || b.GetComponent<Character>() != null)
                    continue; // bodies: Players / Creatures
                Add(into, BaseName(b.name) + "@" + PosKey(p), "d=" + I(dist));
            }
        }

        // ---------------------------------------------------------- world-wide

        /// <summary>Trader stock, per stack as the host sends it (TradeInventorySync).</summary>
        private static void CollectTraders(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            var all = new List<NPC>(256);
            DesyncRegistry<NPC>.Snapshot(all);
            var parts = new List<string>(16);
            for (int i = 0; i < all.Count; i++)
            {
                NPC npc = all[i];
                if (npc == null || !npc.trader || npc.inventory == null || string.IsNullOrEmpty(npc.name))
                    continue;
                TradeInventorySyncMessage m = TradeInventorySync.BuildMessage(npc);
                parts.Clear();
                for (int k = 0; k < m.ItemCount; k++)
                    parts.Add((m.IsRecipe[k] ? "recipe:" : "") + m.ItemTypes[k] + "x" + I(m.Amounts[k]));
                parts.Sort(StringComparer.Ordinal);
                // By name and spot: one trader can have several copies (story stages), and which of
                // them have woken differs per machine.
                Add(into, npc.name + "@" + PosKey(npc.transform.position), string.Join(",", parts));
            }
        }

        /// <summary>
        /// A trader copy only one machine has woken is not a difference (the registry holds what
        /// woke); the stock of one both have is (TradeInventorySync keeps it the host's).
        /// </summary>
        private static bool SameTrader(string key, string host, string client)
            => host == null || client == null || string.Equals(host, client, StringComparison.Ordinal);

        /// <summary>Journal notes, keys, items, entries and locations (shared; dream pages are gone after it).</summary>
        private static void CollectJournal(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            UI ui = Singleton<UI>.Instance;
            Journal j = ui != null ? ui.journal : null;
            if (j == null)
                return;
            // A dream's own pages (vanilla inDream) are that dreamer's until the dream ends.
            AddKeys(into, "note:", j.notesDict, n => n.inDream);
            AddKeys(into, "key:", j.keysDict, k => k.inDream);
            AddKeys(into, "item:", j.itemsDict, i => i.inDream);
            AddKeys(into, "entry:", j.journalEntriesDict, e => e.inDream);
            // Places inside a dream (sub_dream_…) are found by whoever walked that dream: each
            // player's own prologue, a dream the others sat out.
            if (j.locationsDict != null)
                foreach (KeyValuePair<string, string> kv in j.locationsDict)
                    if (kv.Key == null || kv.Key.IndexOf("dream_", StringComparison.OrdinalIgnoreCase) < 0)
                        Add(into, "loc:" + kv.Key, kv.Value ?? "");
        }

        private static void AddKeys<T>(List<KeyValuePair<string, string>> into, string prefix, Dictionary<string, T> dict,
            Func<T, bool> inDream) where T : class
        {
            if (dict == null)
                return;
            foreach (KeyValuePair<string, T> kv in dict)
                if (kv.Value == null || !inDream(kv.Value))
                    Add(into, prefix + kv.Key, "1");
        }

        /// <summary>Tonight's scenario and how far its events got (clients replay the host's).</summary>
        private static void CollectNight(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            NightScenarios ns = Singleton<NightScenarios>.Instance;
            NightScenario sc = ns != null ? ns.currentScenario : null;
            Add(into, "scenario", sc != null ? sc.name : "-");
            if (sc == null || sc.customEventAndInts == null)
                return;
            int current = -1;
            for (int i = 0; i < sc.customEventAndInts.Count; i++)
            {
                CustomEvent ce = sc.customEventAndInts[i] != null ? sc.customEventAndInts[i].customEvent : null;
                if (ce == null)
                    continue;
                if (ce == sc.currentEvent)
                    current = i;
                RandomEvent re = ce.theEvent;
                Add(into, "e" + I(i), "started=" + B(ce.started) + "|today=" + B(re != null && re.startedToday)
                    + "|disabled=" + B(re != null && re.disabled));
            }
            Add(into, "current", I(current));
        }

        private static void CollectWorld(Ctx ctx, List<KeyValuePair<string, string>> into)
        {
            Controller c = Singleton<Controller>.Instance;
            if (c != null)
            {
                Add(into, "workbench", I(c.workbenchLevel));
                Add(into, "afterNight", B(c.isAfterNight));
            }
            Rain r = Singleton<Rain>.Instance;
            if (r != null)
            {
                Add(into, "raining", B(r.Raining));
                Add(into, "rainToday", B(r.rainToday));
                Add(into, "fog", B(r.fogIsActive));
            }
        }
    }
}

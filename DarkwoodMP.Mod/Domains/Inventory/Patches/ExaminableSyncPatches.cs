using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Personal flavor / proximity narrative HUD (examine text, GameEvent displayMessage,
    /// and HelpMessage tutorials). Must stay on the observing local player — never leak
    /// to a peer who is elsewhere.
    ///
    /// Vanilla <c>GameEvents.fire</c> only <c>StartCoroutine</c>s delayed <c>GameEvent.fire</c>
    /// work; a try/finally around <c>fire()</c> cannot cover <c>Type.displayMessage</c> /
    /// HelpMessage after <c>delay</c>. Instead of a process-wide blacklist (which blanked
    /// local examine/help for up to 60s), <see cref="GameEventFireFlavorSourcePatch"/>
    /// pushes the GE <c>thisGO</c> while each delayed <c>MoveNext</c> runs, and
    /// <see cref="ShouldShow"/> re-checks <see cref="NearRange"/> against that transform.
    /// Local examine / non-GE HUD has no flavor source → unaffected. Chat / system tips
    /// use <see cref="BeginBypass"/>. Hard <see cref="SuppressCount"/> is host examine
    /// re-run (always hide). Reset clears on NetworkResetRegistry.
    /// </summary>
    internal static class PersonalFlavorHud
    {
        /// <summary>
        /// XZ range: "standing at the spot". 250 covered an entire hideout + yard and
        /// still leaked flavor across far peers; location volumes are much smaller.
        /// </summary>
        internal const float NearRange = 60f;

        /// <summary>When &gt; 0, flavor HUD is a no-op (host examine re-run). Setter clamps below 0.</summary>
        private static int _suppressCount;
        internal static int SuppressCount
        {
            get => _suppressCount;
            set => _suppressCount = value < 0 ? 0 : value;
        }

        private static int _bypassCount;
        private static readonly System.Collections.Generic.List<UnityEngine.GameObject> _flavorSources =
            new System.Collections.Generic.List<UnityEngine.GameObject>(8);

        /// <summary>Chat / system tips: show even while a far GE flavor source is active.</summary>
        internal static void BeginBypass() => _bypassCount++;
        internal static void EndBypass()
        {
            if (_bypassCount > 0) _bypassCount--;
        }

        /// <summary>
        /// Push the GameEvents GameObject for the duration of one <c>GameEvent.fire</c>
        /// MoveNext (including the post-delay action that calls display/HelpMessage).
        /// </summary>
        internal static void PushFlavorSource(UnityEngine.GameObject geGo) => _flavorSources.Add(geGo);

        internal static void PopFlavorSource()
        {
            if (_flavorSources.Count > 0)
                _flavorSources.RemoveAt(_flavorSources.Count - 1);
        }

        internal static bool ShouldShow
        {
            get
            {
                // Singleplayer: every flavor message is the player's own.
                bool connected = ModRuntime.Network != null && ModRuntime.Network.IsConnected;
                if (!connected) return true;
                if (_bypassCount > 0) return true;
                if (_suppressCount > 0) return false;
                if (_flavorSources.Count > 0)
                {
                    // Unity fake-null: destroyed GE after fire teardown — fail closed.
                    UnityEngine.GameObject go = _flavorSources[_flavorSources.Count - 1];
                    if (go == null) return false;
                    return IsListenerNear(go.transform.position);
                }
                return true;
            }
        }

        /// <summary>True when a real listen body exists and is within NearRange XZ of worldPos.</summary>
        internal static bool IsListenerNear(UnityEngine.Vector3 worldPos)
        {
            if (Player.Instance == null && UnityEngine.Camera.main == null)
                return false;
            return DWMPHorde.Audio.LocalAudioService.IsNearListenerXz(worldPos, NearRange);
        }

        /// <summary>
        /// Instant-hide a <see cref="CharacterMessage"/> for a far peer / suppressed examine.
        /// Must be used from Postfix (never Prefix-null): vanilla
        /// <c>GameEvent.fire</c> MoveNext does <c>displayMessage(...).texts = ...</c> with no
        /// null check — same class as HelpMessage / Hideout1_tutorial_02.
        /// </summary>
        internal static void HideCharacterMessage(CharacterMessage msg)
        {
            if (msg == null)
                return;
            try
            {
                if (msg.textMesh != null)
                    msg.textMesh.color = new UnityEngine.Color(
                        msg.textMesh.color.r, msg.textMesh.color.g, msg.textMesh.color.b, 0f);
                msg.longevity = 0.01f;
                msg.writing = false;
                msg.isWritingText = false;
                if (msg.waitAndDie != null)
                    msg.waitAndDie.longevity = 0.01f;
                // Destroy shortly — caller may still assign .texts / AssignDeathObjects
                // on this non-null ref in the same MoveNext step.
                UnityEngine.Object.Destroy(msg.gameObject, 0.05f);
            }
            catch (System.Exception)
            {
                // Unity teardown — GameEvent still holds a non-null ref.
            }
        }

        internal static void Reset()
        {
            _suppressCount = 0;
            _bypassCount = 0;
            _flavorSources.Clear();
        }
    }

    /// <summary>
    /// Examinable / story onExamine:
    /// Client keeps local HUD (<c>displayMessage</c> + local <c>DescriptionPool</c> draw)
    /// but must not fire <c>EventTrigger.onExamine</c> — one-shot GE is host-auth via
    /// <see cref="GameEventsFiredPatch"/>. Client Prefix sends <c>ExamineObject</c> request;
    /// host re-runs <c>examine()</c> for triggers + shared flags, with HUD suppressed so the
    /// host does not see the client's flavor text.
    ///
    /// <c>DescriptionPool</c> depletion stays per-peer presentation (no protocol bump for the
    /// drawn key). Examined / displayedDescriptionPool flags fan out so re-examine / pool
    /// one-shots latch consistently. HidingPlace is AI cabinet hideouts — host Character AI.
    /// </summary>
    internal static class ExaminableExamineSync
    {
        /// <summary>Host: suppress Player.displayMessage while applying a remote examine request.</summary>
        internal static int SuppressHostExamineHud
        {
            get => PersonalFlavorHud.SuppressCount;
            set => PersonalFlavorHud.SuppressCount = value;
        }
    }

    [HarmonyPatch(typeof(Examinable), "examine")]
    public static class ExaminableExaminePatch
    {
        private static void Prefix(Examinable __instance)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = ModRuntime.Network;
            if (net == null) return;

            // Client: ask host to run authoritative examine (triggers + flags).
            // Local examine still runs for personal HUD; onExamine triggers are blocked
            // in Core.sendTriggerInfo (see ExaminableOnExamineTriggerPatch).
            if (net.Role == NetworkRole.Client)
            {
                Vector3 p = __instance.transform.position;
                net.Send(NetMessageType.ExamineObject,
                    w => new ExamineObjectMessage
                    {
                        Action = ExamineObjectMessage.ActionRequest,
                        PosX = p.x,
                        PosY = p.y,
                        PosZ = p.z,
                        ObjectName = __instance.name ?? "",
                        Examined = __instance.examined,
                        DisplayedDescriptionPool = __instance.displayedDescriptionPool
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo($"[ExamineSync] client request {__instance.name} at {p}");
            }
        }

        private static void Postfix(Examinable __instance)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            // Host Apply of client ActionRequest runs examine() under RunHostWorldFanout
            // (IsApplyingRemoteState still held by ProcessInboundMessage). Must fan
            // ActionState so peers latch examined / displayedDescriptionPool. Prefix
            // still blocks Request re-send under apply. ActionState apply sets fields
            // only (no examine()), and host ignores inbound ActionState — no echo.
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active)
                return;

            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host) return;

            // Host (local or via request): fan out examined state to all clients.
            Vector3 p = __instance.transform.position;
            net.Broadcast(NetMessageType.ExamineObject,
                w => new ExamineObjectMessage
                {
                    Action = ExamineObjectMessage.ActionState,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    ObjectName = __instance.name ?? "",
                    Examined = __instance.examined,
                    DisplayedDescriptionPool = __instance.displayedDescriptionPool
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ExamineSync] host state {__instance.name} examined={__instance.examined} pool={__instance.displayedDescriptionPool}");
        }
    }

    /// <summary>
    /// Block client-local onExamine EventTriggers. ExamineObject request already asked the
    /// host to re-run examine for story GE; client one-shots would latch EventTrigger.fired
    /// without applying GE (GameEventsFiredPatch) and desync late-join assumptions.
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(bool) })]
    public static class ExaminableOnExamineTriggerPatch
    {
        private static bool Prefix(EventTrigger.Type triggerType)
        {
            return ExaminableOnExamineTrigger.Allow(triggerType);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(string), typeof(bool) })]
    public static class ExaminableOnExamineTriggerValuePatch
    {
        private static bool Prefix(EventTrigger.Type triggerType)
        {
            return ExaminableOnExamineTrigger.Allow(triggerType);
        }
    }

    internal static class ExaminableOnExamineTrigger
    {
        internal static bool Allow(EventTrigger.Type triggerType)
        {
            if (triggerType != EventTrigger.Type.onExamine) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client) return true;
            return false;
        }
    }

    /// <summary>
    /// While a delayed <c>GameEvent.fire</c> MoveNext runs (post-delay displayMessage /
    /// HelpMessage), expose that GE's <c>thisGO</c> so <see cref="PersonalFlavorHud.ShouldShow"/>
    /// can re-check proximity — no process-wide HUD blacklist.
    /// </summary>
    [HarmonyPatch]
    public static class GameEventFireFlavorSourcePatch
    {
        private static System.Reflection.FieldInfo _thisGoField; // process-scoped: reflection cache

        private static bool Prepare() => TargetMethod() != null;

        private static System.Reflection.MethodBase TargetMethod()
        {
            System.Type[] nested = typeof(GameEvent).GetNestedTypes(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            for (int i = 0; i < nested.Length; i++)
            {
                System.Type t = nested[i];
                if (t.Name.IndexOf("fire", System.StringComparison.Ordinal) < 0)
                    continue;
                if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(t))
                    continue;
                System.Reflection.MethodInfo m = AccessTools.Method(t, "MoveNext");
                if (m != null)
                    return m;
            }
            return null;
        }

        // __state: whether this call pushed a source, so the Finalizer pops exactly what it pushed
        // even if the session connected or dropped while the MoveNext was suspended.
        private static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (_thisGoField == null)
                _thisGoField = AccessTools.Field(__instance.GetType(), "thisGO");
            UnityEngine.GameObject go = _thisGoField != null
                ? _thisGoField.GetValue(__instance) as UnityEngine.GameObject
                : null;
            PersonalFlavorHud.PushFlavorSource(go);
            __state = true;
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                PersonalFlavorHud.PopFlavorSource();
        }
    }

    /// <summary>
    /// Suppress personal flavor HUD while a remote examine re-run or a far GameEvent
    /// delayed action is displaying — location hints must not appear for a peer who
    /// is not there. GE proximity is re-checked via <see cref="GameEventFireFlavorSourcePatch"/>.
    /// Postfix-hide (not Prefix-null): vanilla assigns <c>.texts</c> on the return without
    /// a null check (Batch 41 — remaining Hideout1_tutorial_02 MoveNext NRE after HelpMessage
    /// Postfix-hide alone).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.displayMessage),
        new[] { typeof(string), typeof(bool), typeof(bool) })]
    public static class ExaminableHostHudSuppressPatch
    {
        private static void Postfix(CharacterMessage __result)
        {
            if (PersonalFlavorHud.ShouldShow)
                return;
            PersonalFlavorHud.HideCharacterMessage(__result);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.displayMessage),
        new[] { typeof(string), typeof(UnityEngine.Vector3), typeof(float) })]
    public static class CoreDisplayMessagePosSuppressPatch
    {
        private static void Postfix(CharacterMessage __result)
        {
            if (PersonalFlavorHud.ShouldShow)
                return;
            PersonalFlavorHud.HideCharacterMessage(__result);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.displayMessage),
        new[] { typeof(string), typeof(UnityEngine.Transform), typeof(float), typeof(bool) })]
    public static class CoreDisplayMessageTransSuppressPatch
    {
        private static void Postfix(CharacterMessage __result)
        {
            if (PersonalFlavorHud.ShouldShow)
                return;
            PersonalFlavorHud.HideCharacterMessage(__result);
        }
    }


    [HarmonyPatch(typeof(UI), nameof(UI.displayHelpMessage), new[] { typeof(string) })]
    public static class UiDisplayHelpMessageSuppressPatch
    {
        /// <summary>
        /// Never skip the original: vanilla <c>GameEvent.fire</c> MoveNext assigns
        /// <c>helpMessage.actionToDisable</c> without a null check. Bool-Prefix false
        /// left <c>__result</c> null → NRE (Hideout1_tutorial_02). Create always, then
        /// hide for out-of-range peers so hints stay personal.
        /// </summary>
        private static void Postfix(HelpMessage __result)
        {
            if (PersonalFlavorHud.ShouldShow)
                return;
            if (__result == null)
                return;
            try
            {
                // Kill Awake fade-in immediately so far peers never flash the hint.
                if (__result.textMesh != null)
                    __result.textMesh.color = new UnityEngine.Color(1f, 1f, 1f, 0f);
                if (__result.background != null)
                    __result.background.color = new UnityEngine.Color(1f, 1f, 1f, 0f);
                if (__result.radial != null)
                    __result.radial.color = new UnityEngine.Color(1f, 1f, 1f, 0f);
                __result.longevity = 0.01f;
                __result.keyToHide = "";
                __result.hide();
            }
            catch (System.Exception)
            {
                // Unity teardown / missing UI — GameEvent still holds a non-null ref.
            }
        }
    }

    /// <summary>
    /// HidingPlace spawns AI on enable. In multiplayer clients already disable AI;
    /// skip client spawn so only host owns the hider character (avoids double Characters).
    /// </summary>
    [HarmonyPatch(typeof(HidingPlace), "OnEnable")]
    public static class HidingPlaceClientSpawnSuppressPatch
    {
        private static bool Prefix(HidingPlace __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            ModRuntime.LegacyInfo($"[HidingPlace] client suppressed OnEnable spawn on {__instance?.name}");
            return false;
        }
    }
}

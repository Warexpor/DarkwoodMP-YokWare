using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Personal narrative HUD (examine text, a scripted event's thought line, HelpMessage
    /// tutorials): it belongs to the player who set it off, never to a peer elsewhere.
    ///
    /// Vanilla <c>GameEvent.fire</c> is a coroutine and shows its text in a later step, after its
    /// delay. <see cref="EventCoroutineScope"/> carries who the event belongs to into each step;
    /// <see cref="GameEventFireFlavorSourcePatch"/> marks the step, and <see cref="ShouldShow"/>
    /// shows a thought or hint only when that player is this machine's (the same rule as the
    /// event's other personal steps, <see cref="GameEventPersonalActorPatch.ShouldSuppressPersonal"/>).
    /// It used to measure the event object's distance to the listener (60 units), but a location's
    /// event object usually sits away from its trigger volume, so the player who walked in lost the
    /// line too ("yesterday I barricaded that window" and the rest, on host and client alike).
    /// A speech bubble over a character or object is the world's and is not filtered.
    /// Chat / system tips use <see cref="BeginBypass"/>. Hard <see cref="SuppressCount"/> is the
    /// host's re-run of a client's examine (always hide). Reset clears on NetworkResetRegistry.
    /// </summary>
    internal static class PersonalFlavorHud
    {
        /// <summary>When &gt; 0, flavor HUD is a no-op (host examine re-run). Setter clamps below 0.</summary>
        private static int _suppressCount;
        internal static int SuppressCount
        {
            get => _suppressCount;
            set => _suppressCount = value < 0 ? 0 : value;
        }

        private static int _bypassCount;
        private static int _eventStepDepth;

        /// <summary>Chat / system tips: show even inside another player's event step.</summary>
        internal static void BeginBypass() => _bypassCount++;
        internal static void EndBypass()
        {
            if (_bypassCount > 0) _bypassCount--;
        }

        /// <summary>One <c>GameEvent.fire</c> step runs (including the post-delay text step).</summary>
        internal static void BeginEventStep() => _eventStepDepth++;

        internal static void EndEventStep()
        {
            if (_eventStepDepth > 0) _eventStepDepth--;
        }

        /// <summary>A thought line or hint on this player's screen.</summary>
        internal static bool ShouldShow
        {
            get
            {
                if (!Connected) return true;
                if (_bypassCount > 0) return true;
                if (_suppressCount > 0) return false;
                if (_eventStepDepth > 0)
                    return !GameEventPersonalActorPatch.ShouldSuppressPersonal();
                return true;
            }
        }

        /// <summary>A bubble over a character or object: only the host's examine re-run hides it.</summary>
        internal static bool ShouldShowBubble
            => !Connected || _bypassCount > 0 || _suppressCount == 0;

        private static bool Connected => ModRuntime.Network != null && ModRuntime.Network.IsConnected;

        /// <summary>
        /// Instant-hide a <see cref="CharacterMessage"/> for a far peer / suppressed examine.
        /// Must be used from Postfix (never Prefix-null): vanilla
        /// <c>GameEvent.fire</c> MoveNext does <c>displayMessage(...).texts = ...</c> with no
        /// null check — same class as HelpMessage / Hideout1_tutorial_02.
        /// Hidden at once, then retired next frame through vanilla <c>WaitAndDie.onDeath</c>,
        /// which takes it off its owner's <c>attachedGameObjects</c> and returns it to the
        /// pool. Destroying it instead left a dead entry in the player's list (every map open
        /// threw in <c>UI.hidePlayerUI</c> and the map never showed) and a dead pooled object.
        /// </summary>
        internal static void HideCharacterMessage(CharacterMessage msg)
        {
            if (msg == null)
                return;
            try
            {
                msg.writing = false;
                msg.gameObject.SetActive(false);
                Singleton<Controller>.Instance.StartCoroutine(RetireNextFrame(msg));
            }
            catch (System.Exception)
            {
                // Unity teardown — GameEvent still holds a non-null ref.
            }
        }

        // Next frame: the caller may still assign .texts / AssignDeathObjects in the same step.
        private static System.Collections.IEnumerator RetireNextFrame(CharacterMessage msg)
        {
            yield return null;
            if (msg == null || msg.waitAndDie == null)
                yield break;
            msg.texts?.Clear();
            msg.waitAndDie.fadeTime = 0f;
            msg.waitAndDie.pauseBeforeDying = 0f;
            msg.waitAndDie.onDeath();
        }

        internal static void Reset()
        {
            _suppressCount = 0;
            _bypassCount = 0;
            _eventStepDepth = 0;
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
            if (LanNetworkManager.IsApplyingRemoteState && !HostApplyGuard.Active)
                return;

            if (!NetGuard.Host(out var net)) return;

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
    /// Marks each <c>GameEvent.fire</c> step (including the post-delay displayMessage /
    /// HelpMessage step) so <see cref="PersonalFlavorHud.ShouldShow"/> applies the event owner's
    /// rule to the text it shows — no process-wide HUD blacklist.
    /// </summary>
    [HarmonyPatch]
    public static class GameEventFireFlavorSourcePatch
    {
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

        // __state: whether this call opened a step, so the Finalizer closes exactly what it opened
        // even if the session connected or dropped while the MoveNext was suspended.
        private static void Prefix(out bool __state)
        {
            __state = ModRuntime.Network != null && ModRuntime.Network.IsConnected;
            if (__state)
                PersonalFlavorHud.BeginEventStep();
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                PersonalFlavorHud.EndEventStep();
        }
    }

    /// <summary>
    /// Hide a thought line or hint that is not this player's: another player's event step, or the
    /// host's re-run of a client's examine. Postfix-hide (not Prefix-null): vanilla assigns
    /// <c>.texts</c> on the return without a null check (Hideout1_tutorial_02 MoveNext NRE).
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
            if (PersonalFlavorHud.ShouldShowBubble)
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
            if (PersonalFlavorHud.ShouldShowBubble)
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

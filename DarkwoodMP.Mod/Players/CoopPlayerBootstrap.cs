using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Initializes a co-op clone so Player.Update movement, anims, and interactions work.
    /// </summary>
    public static class CoopPlayerBootstrap
    {
        /// <summary>
        /// Wires internal fields/animators on a freshly instantiated Player clone so vanilla Update does not NRE.
        /// </summary>
        public static void RunLightweightStart(Player player, Player template)
        {
            if (player == null)
                return;

            Traverse t = Traverse.Create(player);

            Rigidbody rb = player.GetComponent<Rigidbody>();
            if (rb != null)
            {
                t.Property("Rigidbody").SetValue(rb);
                if (template != null && template.Rigidbody != null)
                    rb.constraints = template.Rigidbody.constraints;
            }

            t.Field("_transform").SetValue(player.transform);

            tk2dSpriteAnimator torso = player.GetComponent<tk2dSpriteAnimator>();
            if (torso != null)
            {
                t.Field("torsoAnimator").SetValue(torso);
                torso.IgnoreCameraVisibility = true;
            }

            AnimationTriggerListener animListener = player.GetComponent<AnimationTriggerListener>();
            if (animListener != null)
                t.Field("animationTriggerListenerComponent").SetValue(animListener);

            Transform legs = player.transform.Find("PlayerLegs");
            if (legs != null)
            {
                t.Field("legs").SetValue(legs.gameObject);
                tk2dSpriteAnimator legsAnimator = legs.GetComponent<tk2dSpriteAnimator>();
                t.Field("legsAnimator").SetValue(legsAnimator);

                Renderer legsRenderer = legs.GetComponent<Renderer>();
                if (legsRenderer != null)
                    legsRenderer.enabled = true;
            }

            BindLight(player, t, "PlayerFOVLogic", "FOVLogic");
            BindLight(player, t, "PlayerFOVLight", "FOVLight");
            BindLight(player, t, "PlayerFOVLightDot", "FOVDot");
            BindLight(player, t, "PlayerLightDot", "lightDot");
            BindLight(player, t, "Flashlight", "Flashlight");

            GameObject shadow = ResolveChild(player, "Shadow");
            if (shadow != null)
                t.Field("shadow").SetValue(shadow);

            BindAudioListenerStub(player, t);
            WireAnimationCallbacks(player);

            WhereAmI whereAmI = player.GetComponent<WhereAmI>();
            if (whereAmI != null)
                t.Field("whereAmI").SetValue(whereAmI);

            CharacterSounds sounds = player.GetComponent<CharacterSounds>();
            if (sounds != null)
                t.Field("sounds").SetValue(sounds);

            float yPos = Core.getYpos(PosType.player, randomize: false);
            t.Field("yPos").SetValue(yPos);

            // Disable flashlight on clone so only the original player controls it
            Light2D flash = t.Field("Flashlight").GetValue<Light2D>();
            if (flash != null)
                flash.gameObject.SetActive(false);

            WireSharedUiReferences(player, t, template);
            CopySafeDefaultsFromTemplate(player, t, template);
            ResetActionState(t);

            if (player.Inventory != null)
                player.Inventory.enabled = true;

            if (player.Hotbar != null)
                player.Hotbar.enabled = true;

            if (player.Crafting != null)
                player.Crafting.enabled = true;

            SanitizeInventorySlots(player);

            MethodInfo setLegsFps = AccessTools.Method(typeof(Player), "setLegsFPS");
            setLegsFps?.Invoke(player, new object[] { true });

            player.gameObject.SetActive(true);
        }

        private static void ResetActionState(Traverse t)
        {
            t.Field("performingAction").SetValue(false);
            t.Field("wantToJumpThroughWindow").SetValue(false);
            t.Field("jumping").SetValue(false);
            t.Field("switchingItem").SetValue(false);
            t.Field("gettingHit").SetValue(false);
            t.Field("attacking").SetValue(false);
            t.Field("aiming").SetValue(false);
            t.Field("dragging").SetValue(false);
            t.Field("startingDragging").SetValue(false);
            t.Field("endingDragging").SetValue(false);
            t.Field("crawling").SetValue(false);
            t.Field("running").SetValue(false);
            t.Field("inputMovement").SetValue(Vector3.zero);
            t.Field("inputRotation").SetValue(Vector3.zero);
        }

        private static void WireAnimationCallbacks(Player player)
        {
            AnimationTriggerListener listener = player.GetComponent<AnimationTriggerListener>();
            if (listener == null)
                return;

            MethodInfo triggerMethod = AccessTools.Method(typeof(AnimationTriggerListener), "animationTriggerListener");
            MethodInfo completedMethod = AccessTools.Method(typeof(Player), "animationCompletedListener");

            var triggerDelegate = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>)Delegate.CreateDelegate(
                typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>),
                listener,
                triggerMethod);

            var completedDelegate = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>)Delegate.CreateDelegate(
                typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>),
                player,
                completedMethod);

            tk2dSpriteAnimator torso = player.GetComponent<tk2dSpriteAnimator>();
            if (torso != null)
            {
                torso.AnimationEventTriggered = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>)Delegate.Combine(
                    torso.AnimationEventTriggered,
                    triggerDelegate);
                torso.AnimationCompleted = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>)Delegate.Combine(
                    torso.AnimationCompleted,
                    completedDelegate);
            }

            Transform legs = player.transform.Find("PlayerLegs");
            tk2dSpriteAnimator legsAnimator = legs != null ? legs.GetComponent<tk2dSpriteAnimator>() : null;
            if (legsAnimator != null)
            {
                legsAnimator.AnimationEventTriggered = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>)Delegate.Combine(
                    legsAnimator.AnimationEventTriggered,
                    triggerDelegate);
            }
        }

        private static void BindAudioListenerStub(Player player, Traverse t)
        {
            Transform audio = player.transform.Find("AudioListener");
            if (audio == null)
            {
                // Stub needed because vanilla code expects audioListener.gameObject to exist
                GameObject stub = new GameObject("AudioListenerStub");
                stub.transform.SetParent(player.transform, false);
                audio = stub.transform;
            }
            else
            {
                AudioListener listener = audio.GetComponent<AudioListener>();
                if (listener != null)
                    listener.enabled = false;
            }

            t.Field("audioListener").SetValue(audio.gameObject);
        }

        private static void WireSharedUiReferences(Player player, Traverse t, Player template)
        {
            // Share cursor and MouseText (UI elements), but NOT selectedObject fields
            // Each player needs their own interaction state to avoid conflicts
            if (template != null)
            {
                Traverse templateTraverse = Traverse.Create(template);
                t.Field("cursor").SetValue(templateTraverse.Field("cursor").GetValue());
                t.Field("MouseText").SetValue(templateTraverse.Field("MouseText").GetValue());

                // Copy shared UI bar references to prevent NRE in staminaRegen/updateStaminaBar/updateVars
                t.Field("staminaBar").SetValue(templateTraverse.Field("staminaBar").GetValue());
                t.Field("staminaBarBckg").SetValue(templateTraverse.Field("staminaBarBckg").GetValue());
                t.Field("staminaBarEnder").SetValue(templateTraverse.Field("staminaBarEnder").GetValue());
                t.Field("staminaBarNotEnoughIndicator").SetValue(templateTraverse.Field("staminaBarNotEnoughIndicator").GetValue());
                t.Field("healthBar").SetValue(templateTraverse.Field("healthBar").GetValue());
                t.Field("healthBarBckg").SetValue(templateTraverse.Field("healthBarBckg").GetValue());
                t.Field("healthBarEnder").SetValue(templateTraverse.Field("healthBarEnder").GetValue());
                t.Field("expBar").SetValue(templateTraverse.Field("expBar").GetValue());
                return;
            }

            if (Singleton<UI>.Instance != null)
            {
                Transform cursorTransform = Singleton<UI>.Instance.transform.Find("Cursor");
                if (cursorTransform != null)
                    t.Field("cursor").SetValue(cursorTransform.GetComponent<Cursor>());
            }

            GameObject mouseText = GameObject.Find("MouseText");
            if (mouseText != null)
                t.Field("MouseText").SetValue(mouseText);
        }

        private static void CopySafeDefaultsFromTemplate(Player player, Traverse t, Player template)
        {
            if (template == null)
                return;

            Traverse source = Traverse.Create(template);

            t.Field("fists").SetValue(source.Field("fists").GetValue());
            t.Field("hammer").SetValue(source.Field("hammer").GetValue());
            t.Field("defaultFOV").SetValue(source.Field("defaultFOV").GetValue());
            t.Field("currentDestFOV").SetValue(source.Field("currentDestFOV").GetValue());
            t.Field("currentFOV").SetValue(source.Field("currentFOV").GetValue());
            t.Field("walkSpeed").SetValue(source.Field("walkSpeed").GetValue());
            t.Field("runSpeed").SetValue(source.Field("runSpeed").GetValue());
            t.Field("relativeControls").SetValue(source.Field("relativeControls").GetValue());
            t.Field("invertControls").SetValue(source.Field("invertControls").GetValue());
            t.Field("freezeConstraints").SetValue(source.Field("freezeConstraints").GetValue());

            // Window jumping fields
            t.Field("touchingWindow").SetValue(source.Field("touchingWindow").GetValue());
            t.Field("touchedJumpableObject").SetValue(source.Field("touchedJumpableObject").GetValue());

            // Interaction fields
            t.Field("reach").SetValue(source.Field("reach").GetValue());
            t.Field("runSpeedModifier").SetValue(source.Field("runSpeedModifier").GetValue());
            t.Field("speedModifier").SetValue(source.Field("speedModifier").GetValue());

            if (t.Field("currentItem").GetValue() == null)
                t.Field("currentItem").SetValue(source.Field("currentItem").GetValue());

            if (player.Hotbar != null && template.Hotbar != null && player.Hotbar.slots.Count > 0)
                player.Hotbar.selectSlot(template.Hotbar.getSelectedSlotId());
        }

        /// <summary>
        /// Fixes null inventory/InvItemClass references on every slot so the clone does not NRE when accessing inventory UI.
        /// </summary>
        public static void SanitizeInventorySlots(Player player)
        {
            Inventory[] invs = player.GetComponents<Inventory>();
            foreach (Inventory inv in invs)
            {
                if (inv == null || inv.slots == null)
                    continue;

                for (int i = 0; i < inv.slots.Count; i++)
                {
                    InvSlot slot = inv.slots[i];
                    if (slot == null)
                        continue;

                    slot.inventory = inv;

                    if (!InvItemClass.isNull(slot.invItem))
                    {
                        slot.invItem.assignClass();
                        // Re-link the slot's back-reference so invItem operations don't NRE
                        Traverse.Create(slot.invItem).Field("_slot").SetValue(slot);
                    }
                }
            }
        }

        private static GameObject ResolveChild(Player player, string childName)
        {
            Transform child = player.transform.Find(childName);
            return child != null ? child.gameObject : null;
        }

        private static void BindLight(Player player, Traverse t, string childName, string fieldName)
        {
            Transform child = player.transform.Find(childName);
            if (child == null)
                return;

            Light2D light = child.GetComponent<Light2D>();
            if (light != null)
                t.Field(fieldName).SetValue(light);
        }
    }
}

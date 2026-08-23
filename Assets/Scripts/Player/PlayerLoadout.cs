using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Which set is in hand: 1 = melee (sword + shield), 2 = bow, 3 = torch (+ shield).
    ///
    /// One owner for the swap, so the two weapon scripts never both read the mouse.
    /// PlayerMelee and PlayerBow both bind LMB, and each already has its own
    /// "suppress until release" guard for a press consumed by something else — but
    /// neither can arbitrate against the other. This does, by ENABLING only the active
    /// one: the inactive script gets no Update at all, which is stronger than any
    /// amount of mutual input checking and can't drift as either grows.
    ///
    /// Hiding the viewmodels is separate from disabling the scripts, because
    /// ViewmodelCamera renders them through an overlay camera on their own layer —
    /// deactivating a root is what actually removes it from view.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerLoadout : MonoBehaviour
    {
        public enum Slot { Melee, Bow, Torch }

        [Header("Slots")]
        public KeyCode meleeKey = KeyCode.Alpha1;
        public KeyCode bowKey = KeyCode.Alpha2;
        public KeyCode torchKey = KeyCode.Alpha3;
        [Tooltip("What's in hand at spawn.")]
        public Slot startingSlot = Slot.Melee;

        [Header("Melee (sword)")]
        public PlayerMelee melee;
        [Tooltip("Viewmodel roots shown while melee is equipped. MAIN HAND ONLY now — put the SHIELD in Offhand Viewmodels below, or it will vanish when you draw the torch.")]
        public GameObject[] meleeViewmodels;

        [Header("Bow")]
        public PlayerBow bow;
        [Tooltip("Viewmodel roots shown while the bow is equipped. The bow is two-handed, so the off-hand set is hidden with it.")]
        public GameObject[] bowViewmodels;

        [Header("Torch")]
        [Tooltip("Owns the light. Left ENABLED in every slot on purpose — it reads no input, so there is nothing to arbitrate, and a disabled component could not hold the stowed ember.")]
        public PlayerTorch torch;
        [Tooltip("Viewmodel roots shown while the torch is equipped — the torch hand.")]
        public GameObject[] torchViewmodels;

        [Header("Off-hand")]
        [Tooltip("Viewmodel roots shown for every ONE-HANDED slot — the shield. Separate from the melee set so the shield survives a swap to the torch: sword+shield is fighting, torch+shield is exploring while still defended, and both keep the guard up.\n\nAUTHORING: move the shield hand out of Melee Viewmodels and into here. Left in the melee list it disappears the moment you draw the torch, which looks like the swap dropping it.")]
        public GameObject[] offhandViewmodels;

        [Header("Swap")]
        [Tooltip("Seconds before another swap is allowed — stops a held key flickering weapons.")]
        public float swapCooldown = 0.25f;
        [Tooltip("Block swapping mid-action (a swing, a bash, a drawn bow). Off lets a swap cancel whatever is playing, which is faster but can strand an animation.")]
        public bool blockWhileBusy = true;
        [Tooltip("Seconds to lower what is LEAVING before the weapons are exchanged. The exchange happens at the bottom of this motion, out of frame — that is what makes a swap read as putting one thing away and drawing another rather than one becoming the other.\n\nOnly what actually changes moves: sword→torch leaves the shield perfectly still, while melee→bow lowers sword AND shield because the bow takes both hands.\n\n0 restores the old instant swap.")]
        public float swapLowerTime = 0.16f;
        [Tooltip("Seconds for the arriving weapon to rise into rest. Eased out so it settles rather than snapping. Slightly longer than the lower reads as drawing being more deliberate than stowing.")]
        public float swapRaiseTime = 0.22f;
        [Tooltip("How far a stowed viewmodel drops, in CAMERA space — straight down the screen by default.\n\nNot local to the viewmodel: each carries its own authored orientation, so one offset sent the sword, shield and bow three different ways and a swap lowering two of them together had them visibly diverge. Not world either: world down ignores pitch, so looking up brought the weapon into your face and looking down buried it in the floor. Camera down is the same direction for all of them AND always off the bottom of the frame.\n\nMust clear the bottom of the frame — nothing is hidden during a swap, only moved out of view.")]
        public Vector3 swapLowerOffset = new Vector3(0f, -0.6f, 0f);

        [Tooltip("Log swaps and refusals.")]
        public bool debugLoadout = false;

        /// <summary>Fired after a successful swap.</summary>
        public event Action<Slot> OnSwapped;

        public Slot Current { get; private set; }

        PlayerCarry carry;
        float readyAt;

        // A weapon viewmodel SPAWNED AT RUNTIME by PlayerWeaponSlots. Held separately from
        // meleeViewmodels because the authored array is inspector data and a picked-up weapon
        // is not — and because the loadout must stay the single owner of viewmodel visibility.
        //
        // THIS EXISTS BECAUSE OF A REAL BUG. The first design required the weapon SOCKET to be
        // the thing listed in meleeViewmodels, so hiding the socket hid whatever was in it.
        // That works only if the authored weapon is also re-parented under the socket, and when
        // it was not, PlayerWeaponSlots spawned a sword the loadout had never heard of: the
        // loadout hid the authored sword, the spawned one stayed visible in every slot, and
        // both were on screen at once. A requirement that has to be satisfied in two places to
        // work is a requirement that will be half-satisfied — so the spawner now REPORTS what
        // it made instead.
        GameObject dynamicMeleeViewmodel;

        void Awake()
        {
            carry = GetComponent<PlayerCarry>();
            if (melee == null) melee = GetComponent<PlayerMelee>();
            if (bow == null) bow = GetComponent<PlayerBow>();
            if (torch == null) torch = GetComponent<PlayerTorch>();

            // The loadout is the only thing that knows which roots get deactivated, so it is
            // the only place this check can live.
            if (torch != null) torch.WarnIfLightIsNested(torchViewmodels);

            Current = startingSlot;
            Apply(Current, announce: false);
        }

        void Update()
        {
            if (Input.GetKeyDown(meleeKey)) TryEquip(Slot.Melee);
            else if (Input.GetKeyDown(bowKey)) TryEquip(Slot.Bow);
            else if (Input.GetKeyDown(torchKey)) TryEquip(Slot.Torch);
        }

        /// <summary>
        /// Hand the loadout the weapon viewmodel that is currently in the melee slot, so it can
        /// show and hide it with that slot. Pass null when the slot is emptied.
        ///
        /// Called by <see cref="PlayerWeaponSlots"/> on every equip, INCLUDING the one it
        /// applies at spawn — Awake order between the two components is undefined, and this
        /// re-applies the current slot immediately, so it is correct whichever ran first.
        /// </summary>
        public void SetMeleeViewmodel(GameObject root)
        {
            if (dynamicMeleeViewmodel == root) return;
            dynamicMeleeViewmodel = root;
            Apply(Current, announce: false);
        }

        public void TryEquip(Slot slot)
        {
            if (slot == Current || Time.time < readyAt) return;

            // Two-handed carry owns the hands entirely — the viewmodel is already
            // stowed, and re-showing it here would put a bow through a carried barrel.
            if (carry != null && carry.IsCarrying)
            {
                if (debugLoadout) Debug.Log("[Loadout] swap refused — carrying something.", this);
                return;
            }

            if (blockWhileBusy && IsBusy())
            {
                if (debugLoadout) Debug.Log("[Loadout] swap refused — mid-action.", this);
                return;
            }

            Slot previous = Current;
            Current = slot;
            readyAt = Time.time + swapCooldown;
            BeginSwap(previous, slot);
        }

        /// <summary>
        /// Lower what is leaving, exchange at the BOTTOM, then raise what is arriving.
        ///
        /// THE EXCHANGE HAPPENS OUT OF FRAME OR THE EFFECT IS POINTLESS — that is the entire
        /// reason `ViewmodelHolster.Lower` takes a callback rather than the caller waiting a fixed
        /// time. A timer would have to be kept in step with the animation by hand, and would show
        /// the swap the first time someone retuned one and not the other.
        ///
        /// ONLY WHAT CHANGES MOVES. The shield rides every one-handed slot, so sword→torch lowers
        /// the sword and raises the torch while the shield stays perfectly still; melee→bow lowers
        /// sword AND shield, because the bow takes both hands. Lowering everything unconditionally
        /// would make the shield bob for a swap it is not part of.
        ///
        /// INPUT SCRIPTS ARE DISABLED IMMEDIATELY, not at the exchange, so a lowering sword cannot
        /// still be swung. That is the same "disable before showing" ordering `Apply` already used
        /// to guarantee two weapons are never listening to the mouse at once — it just has to
        /// happen at the START of the transition now that the transition has a duration.
        /// </summary>
        void BeginSwap(Slot from, Slot to)
        {
            if (melee != null) melee.enabled = to == Slot.Melee;
            if (bow != null) bow.enabled = to == Slot.Bow;

            // A second swap mid-transition retargets rather than stacking: the pending exchange
            // is cancelled and rescheduled, so mashing 1-2-3 ends on the last key pressed instead
            // of running three overlapping sequences.
            CancelInvoke(nameof(FinishSwap));
            pendingExchange = to;

            if (swapLowerTime <= 0f) { FinishSwap(); return; }

            bool lowering = false;
            foreach (var go in VisibleRoots(from))
            {
                if (go == null || IsVisibleIn(go, to)) continue;   // stays on screen — leave it alone
                var h = ViewmodelHolster.EnsureOn(go);
                if (h == null) continue;
                h.Lower(swapLowerTime, swapLowerOffset);
                lowering = true;
            }

            // ONE timer for the whole set rather than a callback per holster. They share a
            // duration so they land together, and hanging the exchange off each one would swap
            // the loadout two or three times in the same frame.
            if (lowering) Invoke(nameof(FinishSwap), swapLowerTime);
            else FinishSwap();   // nothing to lower — a slot whose sets are all arriving
        }

        Slot? pendingExchange;

        void FinishSwap()
        {
            if (pendingExchange == null) return;
            Slot slot = pendingExchange.Value;
            pendingExchange = null;

            Apply(slot, announce: true);

            if (swapRaiseTime <= 0f) return;
            foreach (var go in VisibleRoots(slot))
            {
                if (go == null) continue;
                var h = ViewmodelHolster.EnsureOn(go);
                // Already at rest and not lowered = it never left, so leave it: raising a shield
                // that stayed in hand through a sword→torch swap would make it bob for no reason.
                if (h == null || (!h.Lowered && h.transform.localPosition == Vector3.zero)) continue;
                // Raise applies the low pose on this same call, before anything renders, so a set
                // shown by Apply a moment ago cannot flash at rest for a frame.
                h.Raise(0f, swapRaiseTime, swapLowerOffset);
            }
        }

        /// <summary>Every viewmodel root that should be on screen in <paramref name="slot"/>.</summary>
        System.Collections.Generic.IEnumerable<GameObject> VisibleRoots(Slot slot)
        {
            if (slot == Slot.Melee)
            {
                if (meleeViewmodels != null) foreach (var g in meleeViewmodels) yield return g;
                if (dynamicMeleeViewmodel != null) yield return dynamicMeleeViewmodel;
            }
            if (slot == Slot.Bow && bowViewmodels != null)
                foreach (var g in bowViewmodels) yield return g;
            if (slot == Slot.Torch && torchViewmodels != null)
                foreach (var g in torchViewmodels) yield return g;
            // The off-hand rides every ONE-HANDED slot; the bow takes both hands.
            if (slot != Slot.Bow && offhandViewmodels != null)
                foreach (var g in offhandViewmodels) yield return g;
        }

        bool IsVisibleIn(GameObject go, Slot slot)
        {
            foreach (var g in VisibleRoots(slot)) if (g == go) return true;
            return false;
        }

        bool IsBusy()
        {
            if (melee != null && melee.enabled && (melee.IsSwinging || melee.IsCharging || melee.IsBashing)) return true;
            if (bow != null && bow.enabled && bow.IsDrawing) return true;
            return false;
        }

        void Apply(Slot slot, bool announce)
        {
            // ONE FLAG PER SLOT, not `!isMelee`. The binary form was correct while there were
            // two slots and silently wrong the moment a third arrived — "not melee" stopped
            // meaning "bow", and the bow would have been enabled and visible in the torch slot.
            bool isMelee = slot == Slot.Melee;
            bool isBow = slot == Slot.Bow;
            bool isTorch = slot == Slot.Torch;

            // Disable the scripts BEFORE showing any set, so there's never a frame where two
            // are listening to the mouse.
            if (melee != null) melee.enabled = isMelee;
            if (bow != null) bow.enabled = isBow;

            // PlayerTorch is NOT toggled — it reads no input, and it has to keep driving the
            // light while stowed to hold the ember. It is told which state to ease toward.
            if (torch != null) torch.SetHeld(isTorch);

            SetActive(meleeViewmodels, isMelee);
            SetActive(bowViewmodels, isBow);
            SetActive(torchViewmodels, isTorch);

            // The runtime-spawned weapon rides the melee slot exactly as the authored array
            // does. Unity's == catches a destroyed instance, so a weapon replaced between
            // swaps needs no explicit clearing here.
            if (dynamicMeleeViewmodel != null && dynamicMeleeViewmodel.activeSelf != isMelee)
                dynamicMeleeViewmodel.SetActive(isMelee);

            // The shield rides every ONE-HANDED slot. The bow is the exception: it takes both
            // hands, so the off-hand set goes with it.
            SetActive(offhandViewmodels, !isBow);

            if (announce)
            {
                OnSwapped?.Invoke(slot);
                if (debugLoadout) Debug.Log($"[Loadout] equipped {slot}.", this);
            }
        }

        static void SetActive(GameObject[] roots, bool active)
        {
            if (roots == null) return;
            foreach (var go in roots)
                if (go != null && go.activeSelf != active) go.SetActive(active);
        }
    }
}

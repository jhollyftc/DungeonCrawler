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

            Current = slot;
            readyAt = Time.time + swapCooldown;
            Apply(slot, announce: true);
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

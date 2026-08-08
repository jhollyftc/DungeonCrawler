using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Which weapon set is in hand: 1 = melee (sword + shield), 2 = bow.
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
        public enum Slot { Melee, Bow }

        [Header("Slots")]
        public KeyCode meleeKey = KeyCode.Alpha1;
        public KeyCode bowKey = KeyCode.Alpha2;
        [Tooltip("What's in hand at spawn.")]
        public Slot startingSlot = Slot.Melee;

        [Header("Melee (sword + shield)")]
        public PlayerMelee melee;
        [Tooltip("Viewmodel roots shown while melee is equipped — typically the sword hand and the shield hand.")]
        public GameObject[] meleeViewmodels;

        [Header("Bow")]
        public PlayerBow bow;
        [Tooltip("Viewmodel roots shown while the bow is equipped.")]
        public GameObject[] bowViewmodels;

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

        void Awake()
        {
            carry = GetComponent<PlayerCarry>();
            if (melee == null) melee = GetComponent<PlayerMelee>();
            if (bow == null) bow = GetComponent<PlayerBow>();

            Current = startingSlot;
            Apply(Current, announce: false);
        }

        void Update()
        {
            if (Input.GetKeyDown(meleeKey)) TryEquip(Slot.Melee);
            else if (Input.GetKeyDown(bowKey)) TryEquip(Slot.Bow);
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
            bool isMelee = slot == Slot.Melee;

            // Disable the script BEFORE showing the other set, so there's never a frame
            // where both are listening to the mouse.
            if (melee != null) melee.enabled = isMelee;
            if (bow != null) bow.enabled = !isMelee;

            SetActive(meleeViewmodels, isMelee);
            SetActive(bowViewmodels, !isMelee);

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

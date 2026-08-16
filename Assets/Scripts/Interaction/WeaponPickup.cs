using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A weapon lying in the world, waiting to be taken. Deliberate pickup only: you look at
    /// it and press the interact key, never walk over it.
    ///
    /// NOT A <see cref="Carryable"/>, and that is a hard constraint rather than a preference.
    /// `Carryable.Interact()` hard-codes `PlayerCarry`, and `PlayerInteractor` stands down
    /// entirely while something is carried — so a weapon routed through the carry rig would be
    /// hauled around in both hands like a barrel and could never reach the loadout. This is its
    /// own IInteractable, which is exactly the gap CLAUDE.md's roadmap 26 flags with "a dropped
    /// weapon is NOT a Carryable".
    ///
    /// THE INSTANCE IS WHERE PER-COPY STATE BELONGS. The <see cref="WeaponDefinition"/> holds
    /// everything shared by every copy of this weapon — numbers, viewmodel, world prefab — and
    /// is immutable and shared with NPCs. Anything true of THIS sword and not of all iron
    /// swords (wear, an enchantment, a previous owner) goes on this component, which is the one
    /// thing a ScriptableObject structurally cannot carry.
    /// </summary>
    [DisallowMultipleComponent]
    public class WeaponPickup : MonoBehaviour, IInteractable
    {
        [Tooltip("Which weapon this is. Required — without it there is nothing to equip, and the pickup refuses rather than silently vanishing.")]
        public WeaponDefinition weapon;

        [Tooltip("Log pickups and refusals.")]
        public bool debugPickup = false;

        public string Prompt => weapon != null ? $"Pick up {weapon.Label}" : "Pick up weapon";

        public void Interact(Transform interactor)
        {
            if (weapon == null)
            {
                Debug.LogWarning($"[WeaponPickup] '{name}' has no WeaponDefinition assigned — " +
                                 "nothing to equip.", this);
                return;
            }

            var slots = interactor != null ? interactor.GetComponentInParent<PlayerWeaponSlots>() : null;
            if (slots == null)
            {
                Debug.LogWarning("[WeaponPickup] No PlayerWeaponSlots on the interactor — " +
                                 "add it to the player rig beside PlayerLoadout.", this);
                return;
            }

            if (debugPickup) Debug.Log($"[WeaponPickup] taking {weapon.Label}.", this);

            // The slot spawns the replaced weapon's world form itself, so this object is simply
            // consumed. Destroying LAST means a refusal upstream leaves the weapon on the floor
            // rather than deleting it into nothing.
            slots.Equip(weapon);
            Destroy(gameObject);
        }
    }
}

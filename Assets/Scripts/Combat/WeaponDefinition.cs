using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// One weapon: its combat numbers, its wielded viewmodel and its world pickup.
    ///
    /// THE SPLIT THAT MATTERS. A weapon exists in TWO forms that cannot be the same
    /// GameObject in this project, however tempting that is:
    ///
    ///   WORLD  — a normal-layer object with a Rigidbody and a collider, lying on the floor.
    ///   HELD   — a rig on the VIEWMODEL layer, rendered by an overlay camera that clears
    ///            depth, carrying `ViewmodelSway` (which captures a REST POSE at startup) and
    ///            `ViewmodelCollision`'s authored shoulder/tip anchors, and with NO collider
    ///            at all because the swing is its own cast.
    ///
    /// Re-parenting one object between those states means re-layering the whole hierarchy on
    /// every pickup and undoing it on every drop, toggling physics components, and re-capturing
    /// a rest pose — and anything added to the prefab later (an enchant VFX) silently renders
    /// through the wrong camera. So the identity of a weapon lives HERE, in data, and each form
    /// is its own authored prefab.
    ///
    /// SHARED WITH NPCs BY DESIGN (roadmap 27): the same asset is what an NpcEquipment would
    /// read to push stats into its own MeleeAttack, which is why the numbers live in a
    /// ScriptableObject rather than on the player's prefab. PER-INSTANCE state — a worn or
    /// enchanted copy of this weapon — belongs on <see cref="WeaponPickup"/>, which is the one
    /// thing an SO structurally cannot hold.
    /// </summary>
    [CreateAssetMenu(menuName = "Dungeon/Weapon Definition", fileName = "Weapon_")]
    public class WeaponDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Shown in the interact prompt: \"Pick up Iron Sword\". Falls back to the asset name when blank.")]
        public string displayName = "";

        [Header("Forms")]
        [Tooltip("The HELD rig, authored exactly like the sword already on the player: Viewmodel layer, ViewmodelSway, ViewmodelCollision anchors, NO collider and NO Rigidbody. Spawned under the weapon socket and posed procedurally by PlayerMelee.")]
        public GameObject viewmodelPrefab;
        [Tooltip("The WORLD form, dropped on the floor: normal layer, Rigidbody, collider, and a WeaponPickup pointing back at this asset. Spawned at the hand when the weapon is replaced.")]
        public GameObject worldPrefab;

        [Header("Combat")]
        [Tooltip("Damage per landed hit, before any Hitbox multiplier or victim mitigation.")]
        public float damage = 10f;
        [Tooltip("Reach of the swing sweep, in metres. NPC engageDistance must sit UNDER this or an attacker stops outside its own reach and swings at air.")]
        public float range = 1.6f;
        [Tooltip("Radius of the sweep capsule. Wider forgives aim; narrower rewards it.")]
        public float sweepRadius = 0.45f;
        [Tooltip("How far the sweep reaches ABOVE the origin height.")]
        public float sweepUpExtent = 0.3f;
        [Tooltip("How far the sweep reaches BELOW the origin height — this is what lets a swing connect with short enemies.")]
        public float sweepDownExtent = 1.3f;
        [Tooltip("Knockback in m/s applied to the victim's locomotion. A VELOCITY, not an impulse — do not reuse this number for IPushable.Push, which takes N·s and would read as props barely twitching or exploding purely by mass.")]
        public float knockback = 5f;
        [Tooltip("Poise chipped per hit. Enough repeated hits break poise and cause a major stagger.")]
        public float poiseDamage = 25f;

        [Tooltip("Move-speed multiplier while this weapon is held — a greatsword should cost mobility a dagger does not.\n\nHELD, not swung: it applies continuously, so weight is something you feel walking around rather than only at the moment of an attack. That is what makes choosing a heavy weapon a real trade instead of a pure upgrade, and it composes with the backpedal penalty, so retreating with a greatsword is genuinely committed.\n\nNOT pushed through ApplyTo — see the note there. This one describes the PLAYER's movement, not the blade's reach, so PlayerWeaponSlots requests it each frame instead. 1 = weightless.")]
        [Range(0.3f, 1f)] public float moveSpeedMultiplier = 1f;

        [Header("Timing")]
        [Tooltip("Seconds from input to the sweep. Ignored when MeleeAttack.sweepFromAnimationEvent is on, where the clip's impact frame decides instead.")]
        public float windup = 0.45f;
        [Tooltip("Seconds after the sweep before another attack is allowed.")]
        public float recovery = 0.8f;

        [Header("Swap audio")]
        [Tooltip("Played as this weapon is LIFTED into view — the draw. Several = free variation.\n\nPer weapon rather than one shared swap sound, because a greatsword coming off the back and a dagger clearing a sheath are different actions, and the swap is the moment the player is looking straight at the weapon.")]
        public AudioClip[] drawClips;
        [Tooltip("Played as this weapon LEAVES your hands, at the moment you let go.\n\nOptional, and often better left EMPTY: the world prefab's own ImpactAudio already makes the clatter when it lands, which is the sound most swaps want. Fill this only when the release itself should be audible — a scabbard, a heavy shift of weight — or the two stack into a double hit.")]
        public AudioClip[] dropClips;

        // NO impactSurface FIELD, deliberately. Roadmap 27 lists SurfaceType in the combat
        // payload, but today MeleeHitEffects resolves the surface from the VICTIM via
        // Surface.Of — what you hit, not what you hit it with — so a weapon-side surface would
        // have no consumer and would sit in the inspector doing nothing. Add it here at the
        // same time as the code that reads it, not before (§12: a setting whose failure is
        // indistinguishable from correct wiring is the most expensive kind of bug here).

        public string Label => string.IsNullOrWhiteSpace(displayName) ? name : displayName;

        /// <summary>
        /// Push this weapon's numbers into a <see cref="MeleeAttack"/>.
        ///
        /// TIMING AND SWEEP GEOMETRY TRAVEL WITH THE WEAPON, but the MASKS and the aim source
        /// deliberately do NOT: those describe the ATTACKER (which layers it may hit, where its
        /// aim originates), not the blade, and a weapon asset overwriting them would silently
        /// re-target whoever picked it up — the exact perspective bug MeleeAttack already
        /// carries a §12 note about.
        /// </summary>
        public void ApplyTo(MeleeAttack attack)
        {
            if (attack == null) return;
            attack.damage = damage;
            attack.range = range;
            attack.sweepRadius = sweepRadius;
            attack.sweepUpExtent = sweepUpExtent;
            attack.sweepDownExtent = sweepDownExtent;
            attack.knockback = knockback;
            attack.poiseDamage = poiseDamage;
            attack.windup = windup;
            attack.recovery = recovery;
        }
    }
}

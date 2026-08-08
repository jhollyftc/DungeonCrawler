using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Holding a guard up, and the tight window at the start of it that turns a block into a
    /// PARRY.
    ///
    /// Called GUARD, not Shield, deliberately. The shield's two behaviours and the eventual
    /// weapon parry with no shield are the same mechanism with different numbers — a tighter
    /// window and worse chip mitigation. Naming it after the shield would mean renaming it
    /// the day the weapon parry lands.
    ///
    /// THE PARRY WINDOW IS MEASURED FROM THE DEFENDER'S INPUT, not the attacker's swing: at
    /// the moment a blow lands, was the guard raised within the last `parryWindow` seconds?
    /// That is evaluated AT impact, so it never needs to know when a hit is going to
    /// arrive — which matters because NPC swings fire their sweep from an Animation Event and
    /// genuinely cannot publish an impact time in advance. The alternative framing ("parry if
    /// you press within Xms of the impact frame") would have forced attackers to predict
    /// themselves.
    ///
    /// Owns STATE and MITIGATION only. It does NOT pose the shield — PlayerMelee owns both
    /// hands and would fight it for the transform, the same one-owner rule as
    /// ViewmodelCollision and facingLockedFrame. PlayerMelee reads IsGuarding instead.
    /// </summary>
    [RequireComponent(typeof(Health))]
    [DisallowMultipleComponent]
    public class PlayerGuard : MonoBehaviour, IDamageMitigator
    {
        [Header("Input")]
        [Tooltip("Hold to raise the guard. RMB by default — heavy attacks moved to the middle mouse button to free it.")]
        public int guardMouseButton = 1;

        [Header("Block (holding)")]
        [Tooltip("Fraction of incoming damage a held block absorbs. 1 = immune, which you almost certainly don't want — chip damage is what stops turtling being a solution to everything.")]
        [Range(0f, 1f)] public float blockDamageReduction = 0.7f;
        [Tooltip("Fraction of the knockback a held block absorbs. Higher than the damage figure on purpose: being shoved while braced reads as weak, but you should still feel a heavy blow move you.")]
        [Range(0f, 1f)] public float blockImpulseReduction = 0.6f;
        [Tooltip("Multiplier on the blow's poiseDamage applied to YOUR poise when you block. Above 1 makes blocking cost more than being hit does — that is the pressure that makes holding block a losing strategy against a crowd, and the reason a guard BREAK exists at all.")]
        public float blockPoiseCost = 1.5f;

        [Header("Parry (timed)")]
        [Tooltip("Seconds after raising the guard during which a blow is PARRIED instead of blocked. The whole difficulty dial. ~0.2 is generous, ~0.12 is tight.")]
        public float parryWindow = 0.18f;
        [Tooltip("Fraction of damage a parry absorbs. 1 = a clean parry costs nothing, which is the usual reward for hitting the window.")]
        [Range(0f, 1f)] public float parryDamageReduction = 1f;
        [Tooltip("Seconds after a FAILED window (guard held past it) before raising the guard again can parry. Without this, tapping RMB repeatedly would spam parry windows and beat any attack by mashing.")]
        public float parryCooldown = 0.5f;

        [Header("Impact")]
        [Tooltip("What a blocked or parried blow appears to STRIKE. The hit landed on your shield, not on you, so the attacker's impact VFX/SFX should ring off this instead of spraying blood — set it to whatever your shield is made of. Reported to the attacker through Health.LastMitigation; no damage source needs to know guarding exists.")]
        public SurfaceType blockSurface = SurfaceType.Metal;

        [Header("Facing")]
        [Tooltip("Half-angle (deg) in front of you that the guard covers. A blow from outside it lands in full — you can't block what's behind you. 90 = anything from your front hemisphere.")]
        [Range(15f, 180f)] public float guardHalfAngle = 80f;

        [Tooltip("Log every block and parry with what it absorbed.")]
        public bool debugGuard = false;

        /// <summary>Guard is up right now.</summary>
        public bool IsGuarding { get; private set; }
        /// <summary>Still inside the parry window — for a shield-glint tell.</summary>
        public bool InParryWindow => IsGuarding && parryArmed && Time.time - raisedAt <= parryWindow;

        /// <summary>
        /// A blow was blocked: (world direction the blow travelled, damage absorbed). The
        /// direction is carried so feedback can be DIRECTIONAL — a shield jolted along the
        /// line the blow actually came from reads as an impact, whereas a symmetric wobble
        /// reads as a rumble effect.
        /// </summary>
        public event System.Action<Vector3, float> OnBlocked;
        /// <summary>A blow was parried: (world blow direction, who threw it).</summary>
        public event System.Action<Vector3, GameObject> OnParried;
        /// <summary>Poise ran out while guarding — the guard is broken open.</summary>
        public event System.Action OnGuardBroken;

        // Parry resolves before flat armour ever would.
        public int MitigationOrder => -100;

        Poise poise;
        PlayerMelee melee;
        MeleeAttack attack;     // for aimSource — the camera is where the player believes they face
        PlayerCarry carry;
        float raisedAt = -999f;
        float parryReadyAt;
        bool parryArmed;

        void Awake()
        {
            poise = GetComponent<Poise>();
            melee = GetComponent<PlayerMelee>();
            attack = GetComponent<MeleeAttack>();
            carry = GetComponent<PlayerCarry>();

            if (poise == null)
                Debug.LogWarning($"[Guard] {name}: no Poise component — blocking will cost nothing and can never be " +
                                 "broken, so holding guard becomes a free answer to everything. Add Poise to the player.", this);
        }

        void OnEnable()
        {
            if (poise != null) poise.OnPoiseBreak += HandlePoiseBreak;
        }

        void OnDisable()
        {
            if (poise != null) poise.OnPoiseBreak -= HandlePoiseBreak;
            IsGuarding = false;   // never leave the guard stuck up if the component is turned off
        }

        void Update()
        {
            bool wants = Input.GetMouseButton(guardMouseButton)
                         && Cursor.lockState == CursorLockMode.Locked;

            // Hands full, or mid-attack: you can't raise a shield you're not holding, and an
            // attack owns the arms until it resolves. Checked every frame rather than only on
            // press, so a swing STARTED while guarding also drops the guard.
            if (carry != null && carry.IsCarrying) wants = false;
            if (melee != null && (melee.IsSwinging || melee.IsCharging || melee.IsBashing)) wants = false;

            if (wants && !IsGuarding)
            {
                IsGuarding = true;
                raisedAt = Time.time;
                // Armed only if the cooldown has expired — otherwise the guard still goes up,
                // it just can't parry. Mashing gets you blocking, never free parries.
                parryArmed = Time.time >= parryReadyAt;
            }
            else if (!wants && IsGuarding)
            {
                IsGuarding = false;
                // A window that expired unused starts the cooldown. Releasing INSIDE the
                // window doesn't, so a feinted guard isn't punished.
                if (parryArmed && Time.time - raisedAt > parryWindow)
                    parryReadyAt = Time.time + parryCooldown;
                parryArmed = false;
            }
        }

        /// <summary>
        /// IDamageMitigator. Runs inside Health.TakeDamage, before any damage is applied.
        /// </summary>
        public Mitigation Mitigate(ref DamageInfo info)
        {
            if (!IsGuarding || !isActiveAndEnabled) return Mitigation.None;
            if (!IsInFront(info)) return Mitigation.None;

            bool parried = parryArmed && Time.time - raisedAt <= parryWindow;
            float before = info.amount;

            if (parried)
            {
                // Consume the window so ONE raise can't parry a whole volley — the reward is
                // for reading a specific attack, not for holding the button at the right time.
                parryArmed = false;
                parryReadyAt = Time.time + parryCooldown;

                info.amount *= 1f - Mathf.Clamp01(parryDamageReduction);
                info.impulse = 0f;
                info.poiseDamage = 0f;       // a clean parry costs the defender nothing

                PunishAttacker(info.instigator, parried: true);
                OnParried?.Invoke(info.direction, info.instigator);
                if (debugGuard)
                    Debug.Log($"[Guard] {name}: PARRIED {before:0.#} → {info.amount:0.#} " +
                              $"from {(info.instigator != null ? info.instigator.name : "the world")}.", this);
                return Mitigation.Of(DamageOutcome.Parried, blockSurface);
            }

            info.amount *= 1f - Mathf.Clamp01(blockDamageReduction);
            info.impulse *= 1f - Mathf.Clamp01(blockImpulseReduction);

            // The cost of blocking. Taken from poise and REMOVED from the blow, so a blocked
            // hit can break your guard but can't also stagger you through the normal poise
            // path twice over.
            float poiseCost = info.poiseDamage * Mathf.Max(0f, blockPoiseCost);
            info.poiseDamage = 0f;
            if (poise != null && poiseCost > 0f) poise.Chip(poiseCost, info);

            // Stop the incoming swing dead. The blade met a shield, so it should NOT follow
            // through as though it cut flesh — that follow-through is the main reason a block
            // currently reads as "nothing happened".
            PunishAttacker(info.instigator, parried: false);

            OnBlocked?.Invoke(info.direction, before - info.amount);
            if (debugGuard)
                Debug.Log($"[Guard] {name}: BLOCKED {before:0.#} → {info.amount:0.#} (poise -{poiseCost:0.#}).", this);
            return Mitigation.Of(DamageOutcome.Blocked, blockSurface);
        }

        /// <summary>
        /// Was the blow thrown from within the guard arc? Prefers the ATTACKER'S POSITION over
        /// info.direction: `direction` is the blow vector, which for a diagonal or upward
        /// slash can point well away from the line between us, and "can I see who hit me"
        /// is the question a player actually expects a shield to answer. Falls back to the
        /// blow direction for damage with no instigator (environment, traps).
        /// </summary>
        bool IsInFront(in DamageInfo info)
        {
            Vector3 toAttacker;
            if (info.instigator != null)
                toAttacker = info.instigator.transform.position - transform.position;
            else
                toAttacker = -info.direction;

            toAttacker.y = 0f;
            if (toAttacker.sqrMagnitude < 1e-4f) return true;   // on top of us — call it frontal

            // Aim from the CAMERA, not the body: in first person the camera is where the
            // player believes they are facing, and the body yaw follows it.
            Transform eye = attack != null && attack.aimSource != null ? attack.aimSource : transform;
            Vector3 forward = eye.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return true;

            return Vector3.Angle(forward.normalized, toAttacker.normalized) <= guardHalfAngle;
        }

        /// <summary>
        /// Make the attacker pay for being parried. Reaches them through the instigator, so
        /// nothing needed a return value and each attacker decides its own cost
        /// (MeleeAttack.Parried kills the swing; its own poise break supplies the stagger).
        /// </summary>
        void PunishAttacker(GameObject instigator, bool parried)
        {
            if (instigator == null) return;

            // Both halt the swing — a weapon stopped by a shield stops, whether or not you
            // timed it. Only the PARRY costs the attacker its footing.
            var attackerMelee = instigator.GetComponentInParent<MeleeAttack>();
            if (attackerMelee != null)
            {
                if (parried) attackerMelee.Parried();
                else attackerMelee.Blocked();
            }

            if (!parried) return;

            // Stagger via a POISE BREAK rather than a bespoke stun, so a parry reuses the
            // reaction NpcHitReactions already produces for one — and a creature with high
            // poise resists being parried, which is a free difficulty lever.
            var attackerPoise = instigator.GetComponentInParent<Poise>();
            if (attackerPoise != null) attackerPoise.Break();
        }

        void HandlePoiseBreak(DamageInfo info)
        {
            if (!IsGuarding) return;
            IsGuarding = false;
            parryArmed = false;
            parryReadyAt = Time.time + parryCooldown;
            OnGuardBroken?.Invoke();
            if (debugGuard) Debug.Log($"[Guard] {name}: GUARD BROKEN.", this);
        }
    }
}

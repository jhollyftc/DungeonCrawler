using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The player's held torch: a light source that is BRIGHT in the hand and an EMBER when
    /// stowed, so drawing a weapon costs you most of your light without blinding you.
    ///
    /// LIGHT ONLY, DELIBERATELY. There is no attack input here and no Update reading the
    /// mouse, which is why <see cref="PlayerLoadout"/> can leave this component enabled in
    /// every slot — it has nothing to arbitrate against PlayerMelee or PlayerBow. The loadout
    /// calls <see cref="SetHeld"/> instead of toggling `enabled`, and that distinction is what
    /// keeps the ember burning while the torch is put away: a disabled component could not ease
    /// the light down, let alone hold it there.
    ///
    /// THE LIGHT MUST NOT LIVE UNDER A TORCH VIEWMODEL ROOT. The loadout hides a slot by
    /// deactivating its roots (ViewmodelCamera renders them on their own layer, so hiding the
    /// GameObject is what actually removes them from view), and a light parented beneath one
    /// goes out with it — no ember, no warning, looking exactly like this component failing.
    /// Checked at startup rather than documented and hoped for; see the Awake warning.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerTorch : MonoBehaviour
    {
        [Header("Light")]
        [Tooltip("The player's torch light. Assign the Light itself, not the torch mesh — and keep it OUTSIDE the torch viewmodel roots, or hiding the viewmodel takes the ember with it (Awake warns if it is nested).")]
        public Light torchLight;

        [Header("In hand")]
        [Tooltip("Intensity while the torch is the equipped slot. 0 = capture whatever the Light is authored with at Awake, which is usually what you want.")]
        public float heldIntensity = 0f;
        [Tooltip("Range while the torch is the equipped slot. 0 = capture the authored value.")]
        public float heldRange = 0f;

        [Header("Stowed (the ember)")]
        [Tooltip("Intensity while something else is equipped, as a FRACTION of the held intensity. The torch is at your belt, not out — low enough that drawing a sword genuinely costs you light, high enough that a corridor stays navigable.")]
        [Range(0f, 1f)] public float stowedIntensityScale = 0.25f;
        [Tooltip("Range while stowed, as a fraction of the held range. Kept nearer 1 than the intensity: an ember should light a smaller area DIMLY rather than a tiny area brightly, and collapsing the range is what reads as the light being switched off.")]
        [Range(0f, 1f)] public float stowedRangeScale = 0.6f;

        [Header("Feel")]
        [Tooltip("Seconds to ease between held and stowed. A snap reads as a light switch; a short ease reads as a torch being raised or lowered.")]
        public float easeTime = 0.35f;

        [Tooltip("Log the captured values and every held/stowed transition.")]
        public bool debugTorch = false;

        /// <summary>Is the torch the equipped slot right now?</summary>
        public bool IsHeld { get; private set; } = true;

        // The authored values, captured ONCE and eagerly — the PlayerFov lesson. A lazily
        // captured base sampled while an ease is already in flight would adopt a partial value
        // as "full brightness", after which every subsequent stow measures from the wrong
        // number and the torch ratchets darker run by run.
        float baseIntensity, baseRange;

        // TorchFlicker OWNS Light.intensity outright: it caches a base in its own Awake and
        // rewrites the light from it EVERY Update, so anything assigning intensity directly is
        // silently discarded one frame later. Nothing on the player carries one today, but a
        // held torch is the single most likely thing to gain one, and the failure would present
        // as "the ember setting does nothing" with no error anywhere.
        TorchFlicker flicker;

        float t01 = 1f;      // 0 = fully stowed, 1 = fully held
        float target = 1f;

        void Awake()
        {
            // ASSIGN IT EXPLICITLY. An earlier version fell back to
            // GetComponentInChildren<Light>(true), which grabs the FIRST light anywhere under
            // the player — and that is very often a light belonging to a weapon viewmodel
            // rather than the torch. PlayerWeaponSlots destroys the old viewmodel on every
            // swap, so the captured reference died and Apply() threw a MissingReferenceException
            // every frame from then on. A greedy search that usually finds the right thing is
            // worse than no search at all, because it fails at a distance from its cause.
            if (torchLight == null)
            {
                Debug.LogWarning("[PlayerTorch] No Light assigned. Assign the torch's Light " +
                                 "explicitly — it is NOT auto-found, because the first light under " +
                                 "the player is usually a weapon's. Torch will swap viewmodels but " +
                                 "never light anything.", this);
                enabled = false;
                return;
            }

            flicker = torchLight.GetComponent<TorchFlicker>();

            baseIntensity = heldIntensity > 0f ? heldIntensity
                          : flicker != null ? flicker.BaseIntensity : torchLight.intensity;
            baseRange = heldRange > 0f ? heldRange : torchLight.range;

            if (debugTorch)
                Debug.Log($"[PlayerTorch] base intensity {baseIntensity:0.##}, range {baseRange:0.##}" +
                          $"{(flicker != null ? " (driving through TorchFlicker.SetBaseIntensity)" : "")}", this);

            Apply(1f);
        }

        void Update()
        {
            if (Mathf.Approximately(t01, target)) return;

            t01 = easeTime <= 0f
                ? target
                : Mathf.MoveTowards(t01, target, Time.deltaTime / easeTime);
            Apply(t01);
        }

        /// <summary>
        /// Raise or lower the torch. Called by <see cref="PlayerLoadout"/> on every swap,
        /// including the one it applies at spawn.
        /// </summary>
        public void SetHeld(bool held)
        {
            if (IsHeld == held) return;
            IsHeld = held;
            target = held ? 1f : 0f;
            if (debugTorch) Debug.Log($"[PlayerTorch] {(held ? "raised" : "stowed to ember")}.", this);
        }

        void Apply(float k)
        {
            // A destroyed Light still satisfies `!= null` on a raw C# reference but throws on
            // ACCESS, and this runs every frame — so one destroyed light becomes an exception
            // per frame forever. Unity's overloaded == catches it; stand down once rather than
            // spamming, since something outside has taken the light and it is not coming back.
            if (torchLight == null)
            {
                Debug.LogWarning("[PlayerTorch] The torch Light was destroyed — something else " +
                                 "owns that object's lifetime. Disabling so this does not throw " +
                                 "every frame; assign a Light that outlives weapon swaps.", this);
                enabled = false;
                return;
            }

            float intensity = Mathf.Lerp(baseIntensity * stowedIntensityScale, baseIntensity, k);
            float range = Mathf.Lerp(baseRange * stowedRangeScale, baseRange, k);

            // Through SetBaseIntensity when a flicker owns the light, so the pulse keeps
            // running around the new level instead of fighting it (§5's write-only-owner rule).
            if (flicker != null) flicker.SetBaseIntensity(intensity);
            else torchLight.intensity = intensity;
            torchLight.range = range;
        }

        /// <summary>
        /// Warn if the light is parented beneath something the loadout will deactivate.
        /// Called by PlayerLoadout once its roots are known — this component cannot check it
        /// alone, since it has no idea which GameObjects the loadout hides.
        /// </summary>
        public void WarnIfLightIsNested(GameObject[] roots)
        {
            if (torchLight == null || roots == null) return;
            foreach (var root in roots)
            {
                if (root == null || !torchLight.transform.IsChildOf(root.transform)) continue;
                Debug.LogWarning($"[PlayerTorch] The torch Light sits under the viewmodel root " +
                                 $"'{root.name}', which the loadout DEACTIVATES when the torch is " +
                                 $"stowed — so the ember goes out entirely and the stowed settings " +
                                 $"will look broken. Move the Light out from under it (the camera " +
                                 $"is a good parent).", this);
                return;
            }
        }
    }
}

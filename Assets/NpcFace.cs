using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Emotive face: jaw + eyebrow bones driven by layered sines for organic idle
    /// motion (the JawSineAnimation base), where the ANGLE RANGES are chosen by the
    /// NPC's mood and the jaw also opens to whatever it's vocalizing. A calm goblin
    /// has a slack, slow face; as it grows aware of you the brow furrows and the jaw
    /// tightens into a fast grind — the face becomes a readable, diegetic detection
    /// tell, the same role head-tracking plays.
    ///
    /// One-way and cheap, like NpcAnimatorDriver: it READS state (perception
    /// awareness, voice audio) and poses two bones in LateUpdate on top of whatever
    /// body animation is playing. Nothing depends on it; drop it on any rigged head.
    ///
    /// THE INSIGHT (from the original experiment): the min/max range IS the emotion.
    /// Narrow/raise the brow for surprise, lower/narrow it for anger; a wider, faster
    /// jaw reads as a snarl or teeth-grind. So a "mood" is just a set of ranges +
    /// speeds, and the face blends between them.
    ///
    /// EYES ride the same trick, no extra bones needed: the eye material is the
    /// ToonLit shader's rim-light effect turned into an iris — _BaseColor (Tint) is
    /// the pupil, _RimColor/_RimAmount are the sclera ("whites") color/brightness, and
    /// _RimThreshold is pupil SIZE (higher threshold = bigger pupil, less white showing
    /// — verified against the shader's rim math). Blended through the exact same
    /// mood/hit-shock system as jaw/eyebrow (MaterialPropertyBlock, so many goblins
    /// sharing one eye material don't each get a unique instance): calm eyes stay dark
    /// and soft, alertness tightens the pupil and brightens the whites, and full anger
    /// can push the pupil color to red (rage) while a hit blows the pupils wide (the
    /// physiological "big eyes" of shock) for a moment before easing back to the mood.
    ///
    /// [ExecuteAlways] so you can tune each expression's look in edit mode — with no
    /// perception in edit mode it holds the FIRST expression (author your neutral there).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class NpcFace : MonoBehaviour
    {
        [Serializable]
        public class Expression
        {
            public string name = "Calm";
            [Tooltip("Becomes active at/above this awareness (0..1). Sort ascending; the highest one the NPC has reached wins. The first (lowest) is the resting/neutral face and the edit-mode preview.")]
            [Range(0f, 1f)] public float minAwareness = 0f;
            [Tooltip("Eyebrow angle range (min, max local degrees). The RANGE is the expression — narrow/raise = surprise, low/narrow = anger.")]
            public Vector2 eyebrowRange = new Vector2(30f, 90f);
            [Tooltip("Jaw angle range (min, max local degrees). Wider = more mouth motion; a tight fast range reads as a grind.")]
            public Vector2 jawRange = new Vector2(90f, 155f);
            [Tooltip("Eyebrow idle speed. Faster = more agitated.")]
            public float eyebrowSpeed = 0.5f;
            [Tooltip("Jaw idle speed. Crank up with anger for a teeth-grind.")]
            public float jawSpeed = 0.5f;

            [Header("Eyes — ToonLit rim-light trick on the eye sphere")]
            [Tooltip("Pupil color (the eye material's Tint / _BaseColor). Blend calm → enraged through this — e.g. dark → red for rage.")]
            public Color pupilColor = Color.black;
            [Tooltip("Pupil SIZE (the eye material's _RimThreshold). HIGHER = BIGGER pupil (less white showing); LOWER = smaller pupil (more white) — verified against the shader's rim math, not a guess. Big for surprise/fear, small and tight for anger/focus.")]
            [Range(0f, 1f)] public float pupilSize = 0.2f;
            [Tooltip("Sclera ('whites') color — the eye material's _RimColor.")]
            public Color scleraColor = new Color(0.368f, 0.368f, 0.368f);
            [Tooltip("Sclera brightness — the eye material's _RimAmount. Higher = whites read brighter/wider (alert, wide-eyed); lower = sleepy/relaxed.")]
            [Range(0f, 1f)] public float scleraBrightness = 0.6f;
        }

        [Header("Bones")]
        public Transform jaw;
        public Transform eyebrow;
        [Tooltip("Local axis each bone rotates about. The original rig used X for both.")]
        public Vector3 jawAxis = Vector3.right;
        public Vector3 eyebrowAxis = Vector3.right;

        [Serializable]
        public struct EyeSlot
        {
            [Tooltip("Renderer whose mesh includes the eye submesh — usually the SAME renderer as the rest of the goblin body (eyes are typically just one material SLOT among several on one SkinnedMeshRenderer, not a separate GameObject).")]
            public Renderer renderer;
            [Tooltip("Material SLOT index (0-based, matches the order in the renderer's Materials list in the Inspector) that IS the eye material. -1 = auto-resolve from eyeMaterialAsset below by scanning this renderer's material list — leave it at -1 and just assign eyeMaterialAsset in the usual case.")]
            public int materialIndex;
        }

        [Header("Eyes")]
        [Tooltip("Each entry targets ONE material SLOT on a renderer via the INDEXED SetPropertyBlock overload. This matters: the non-indexed overload applies to the WHOLE renderer, so if eyes are one material slot on the same mesh as the goblin's skin, using it would tint the entire goblin (skin included) with the eye's pupil/sclera values — that's the 'whole mesh turns into the eye material' bug. The indexed overload touches only the eye submesh.")]
        public EyeSlot[] eyeSlots;
        [Tooltip("The eye Material asset (e.g. Goblin_Eyes) — used to AUTO-FIND each unresolved slot's materialIndex by scanning its renderer's material list, so you don't have to count slots by hand. Leave a slot's materialIndex at -1 to use this.")]
        public Material eyeMaterialAsset;

        [Header("Expressions (sorted by minAwareness)")]
        public List<Expression> expressions = new List<Expression>
        {
            new Expression { name = "Calm",       minAwareness = 0f,   eyebrowRange = new Vector2(30f, 90f),  jawRange = new Vector2(90f, 155f), eyebrowSpeed = 0.5f, jawSpeed = 0.5f,
                pupilColor = Color.black, pupilSize = 0.24f, scleraColor = new Color(0.368f, 0.368f, 0.368f), scleraBrightness = 0.55f },
            new Expression { name = "Suspicious", minAwareness = 0.4f, eyebrowRange = new Vector2(30f, 70f),  jawRange = new Vector2(95f, 140f), eyebrowSpeed = 0.7f, jawSpeed = 0.7f,
                pupilColor = Color.black, pupilSize = 0.16f, scleraColor = new Color(0.368f, 0.368f, 0.368f), scleraBrightness = 0.76f },
            new Expression { name = "Angry",      minAwareness = 0.8f, eyebrowRange = new Vector2(30f, 55f),  jawRange = new Vector2(105f, 150f), eyebrowSpeed = 1.0f, jawSpeed = 1.4f,
                pupilColor = new Color(0.55f, 0.03f, 0.03f), pupilSize = 0.08f, scleraColor = new Color(0.42f, 0.34f, 0.34f), scleraBrightness = 0.9f },
        };

        [Header("Mood blend")]
        [Tooltip("How fast the face eases between expressions (per second). A furrow shouldn't snap on.")]
        public float blendRate = 4f;

        [Header("Hit reaction (brief shock)")]
        [Tooltip("The face when struck — a flash of shock: raise/widen the brow, drop the jaw open, and blow the pupils WIDE (physiological surprise) with a brief bloodshot-white flash. Overrides the mood for a moment on every hit.")]
        public Expression hurtExpression = new Expression
        {
            name = "Hurt", eyebrowRange = new Vector2(70f, 95f), jawRange = new Vector2(120f, 150f),
            eyebrowSpeed = 0.2f, jawSpeed = 0.2f,
            pupilColor = Color.black, pupilSize = 0.4f, scleraColor = new Color(0.4f, 0.36f, 0.36f), scleraBrightness = 0.95f,
        };
        [Tooltip("Seconds the shocked face holds after a hit before easing back to the mood.")]
        public float hitReactionTime = 0.3f;
        [Tooltip("Blend rate INTO the shock (per second) — much faster than the mood blend, so a hit snaps the face rather than easing.")]
        public float hitBlendRate = 22f;

        [Header("Voice sync")]
        [Tooltip("The NPC's voice AudioSource (grunts/death cry). The jaw opens to its live amplitude, so it looks like it's making the sound. Left empty, NpcCombatAudio's source is used automatically.")]
        public AudioSource voiceSource;
        [Tooltip("How strongly the jaw opens to voice amplitude. 0 = no lip movement, just the idle sway.")]
        public float voiceJawGain = 6f;
        [Tooltip("Local jaw angle at FULLY OPEN. A grunt drives the jaw toward THIS, BEYOND the current mood's idle range — otherwise an angry clamped-shut jaw can't open to vocalize. Set it to your rig's wide-open jaw (may be higher OR lower than the idle range depending on the bone's orientation).")]
        public float jawOpenAngle = 160f;

        // Current (blended) ranges/speeds.
        float ebMin, ebMax, jMin, jMax, ebSpeed, jSpeed;
        bool initialized;

        // Current (blended) eye state, pushed to the material each frame.
        Color pupilColorCur, scleraColorCur;
        float pupilSizeCur, scleraBrightCur;
        MaterialPropertyBlock eyeBlock;
        static readonly int PropBaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int PropRimColor = Shader.PropertyToID("_RimColor");
        static readonly int PropRimAmount = Shader.PropertyToID("_RimAmount");
        static readonly int PropRimThreshold = Shader.PropertyToID("_RimThreshold");

        NpcPerception perception;
        Health health;
        float hurtUntil;
        readonly float[] sampleBuf = new float[256];

        const float WaveAmplitudeSum = 0.15f + 0.05f + 0.02f;

        void OnEnable()
        {
            perception = GetComponent<NpcPerception>();
            health = GetComponent<Health>();
            if (health != null) health.OnDamaged += HandleDamaged;
            if (voiceSource == null)
            {
                // Prefer the actual grunt/death-cry source; fall back to any source.
                var combat = GetComponent<NpcCombatAudio>();
                voiceSource = combat != null && combat.Source != null ? combat.Source : GetComponent<AudioSource>();
            }
            ResolveEyeSlots(warn: true);
            SnapToExpression(TargetExpression());
        }

        void OnDisable()
        {
            if (health != null) health.OnDamaged -= HandleDamaged;
        }

        void HandleDamaged(DamageInfo info) => hurtUntil = Time.time + hitReactionTime;

        void LateUpdate()
        {
            if (expressions.Count == 0) return;
            if (!initialized) SnapToExpression(TargetExpression());

            // A recent hit overrides the mood with the shock face, blended in fast
            // so it snaps; once the window passes it eases back to the mood. Shared by
            // jaw/eyebrow AND eyes, so a hit reads as one coherent flinch, not two.
            bool hurt = Application.isPlaying && Time.time < hurtUntil;
            Expression target = hurt ? hurtExpression : TargetExpression();
            float rate = hurt ? hitBlendRate : blendRate;
            float k = Application.isPlaying ? 1f - Mathf.Exp(-rate * Time.deltaTime) : 1f;

            if (jaw != null && eyebrow != null)
            {
                ebMin = Mathf.Lerp(ebMin, target.eyebrowRange.x, k);
                ebMax = Mathf.Lerp(ebMax, target.eyebrowRange.y, k);
                jMin = Mathf.Lerp(jMin, target.jawRange.x, k);
                jMax = Mathf.Lerp(jMax, target.jawRange.y, k);
                ebSpeed = Mathf.Lerp(ebSpeed, target.eyebrowSpeed, k);
                jSpeed = Mathf.Lerp(jSpeed, target.jawSpeed, k);

                float t = Time.time;

                // Idle jaw sways WITHIN the mood's range; the voice then pulls it toward
                // the fully-open angle, PAST that range — so an angry, near-clamped jaw
                // still opens to grunt. (Blending toward jawMax instead would cap the
                // opening at the mood's tight idle range — the bug this fixes.)
                float idleJaw = Mathf.Lerp(jMin, jMax, Wave01(t, jSpeed));
                float jawAngle = Mathf.Lerp(idleJaw, jawOpenAngle, VoiceOpen());

                float ebValue = Wave01(t, ebSpeed);

                jaw.localRotation = Quaternion.AngleAxis(jawAngle, jawAxis.normalized);
                eyebrow.localRotation = Quaternion.AngleAxis(Mathf.Lerp(ebMin, ebMax, ebValue), eyebrowAxis.normalized);
            }

            // Eyes blend through the SAME k (mood or hit-shock) as jaw/eyebrow — a hit
            // dilates the pupils in lockstep with the brow flying up, not on its own clock.
            pupilColorCur = Color.Lerp(pupilColorCur, target.pupilColor, k);
            pupilSizeCur = Mathf.Lerp(pupilSizeCur, target.pupilSize, k);
            scleraColorCur = Color.Lerp(scleraColorCur, target.scleraColor, k);
            scleraBrightCur = Mathf.Lerp(scleraBrightCur, target.scleraBrightness, k);
            ApplyEyes();
        }

        /// <summary>
        /// Push the blended pupil/sclera values to the eye material's SLOT via the
        /// INDEXED MaterialPropertyBlock overload — never renderer.material (would
        /// instantiate a unique material per NPC and break GPU instancing) and never
        /// the non-indexed SetPropertyBlock (applies to the WHOLE renderer, so a shared
        /// body+eyes mesh would have its skin tinted by the eye values too).
        /// </summary>
        void ApplyEyes()
        {
            if (eyeSlots == null || eyeSlots.Length == 0) return;
            ResolveEyeSlots();
            if (eyeBlock == null) eyeBlock = new MaterialPropertyBlock();

            for (int i = 0; i < eyeSlots.Length; i++)
            {
                Renderer r = eyeSlots[i].renderer;
                int idx = eyeSlots[i].materialIndex;
                if (r == null || idx < 0 || idx >= r.sharedMaterials.Length) continue;

                r.GetPropertyBlock(eyeBlock, idx);
                eyeBlock.SetColor(PropBaseColor, pupilColorCur);
                eyeBlock.SetColor(PropRimColor, scleraColorCur);
                eyeBlock.SetFloat(PropRimAmount, scleraBrightCur);
                eyeBlock.SetFloat(PropRimThreshold, pupilSizeCur);
                r.SetPropertyBlock(eyeBlock, idx);
            }
        }

        /// <summary>
        /// Verifies each slot's materialIndex actually points at eyeMaterialAsset, and
        /// re-scans/fixes it if not — SELF-VERIFYING rather than sentinel-based, because
        /// a freshly-added array element zero-initializes materialIndex to 0, which would
        /// silently look "already resolved" (and usually points at the SKIN slot, not the
        /// eyes) if we only trusted a -1-means-unresolved check. Cheap once correct (an
        /// index compare), so it's safe to call every frame — that also self-heals if you
        /// re-point the renderer/material live in the Inspector during edit-mode tuning.
        /// </summary>
        void ResolveEyeSlots(bool warn = false)
        {
            if (eyeMaterialAsset == null || eyeSlots == null) return;
            for (int i = 0; i < eyeSlots.Length; i++)
            {
                EyeSlot slot = eyeSlots[i];
                if (slot.renderer == null) continue;

                Material[] mats = slot.renderer.sharedMaterials;
                if (slot.materialIndex >= 0 && slot.materialIndex < mats.Length && mats[slot.materialIndex] == eyeMaterialAsset)
                    continue;   // already correct — no rescan needed

                int found = -1;
                for (int m = 0; m < mats.Length; m++)
                    if (mats[m] == eyeMaterialAsset) { found = m; break; }

                // Either way, WRITE BACK: on success the resolved index; on failure force
                // -1 so ApplyEyes skips this slot instead of silently writing eye values
                // into whatever stale/default index (often 0 = the skin slot) it had.
                slot.materialIndex = found;
                eyeSlots[i] = slot;
                if (found < 0 && warn)
                    Debug.LogWarning($"[NpcFace] Eye material '{eyeMaterialAsset.name}' not found on '{slot.renderer.name}'s material list — set this eye slot's Material Index by hand.", this);
            }
        }

        /// <summary>Three incommensurate sines → an organic 0..1 wave that never visibly loops.</summary>
        static float Wave01(float t, float speed)
        {
            float w = Mathf.Sin(t * speed * 0.8f) * 0.15f
                    + Mathf.Sin(t * speed * 2.3f) * 0.05f
                    + Mathf.Sin(t * speed * 5.7f) * 0.02f;
            return Mathf.Clamp01(0.5f + (w / WaveAmplitudeSum) * 0.5f);
        }

        float VoiceOpen()
        {
            if (!Application.isPlaying || voiceSource == null || !voiceSource.isPlaying || voiceJawGain <= 0f)
                return 0f;
            voiceSource.GetOutputData(sampleBuf, 0);
            float sum = 0f;
            for (int i = 0; i < sampleBuf.Length; i++) sum += sampleBuf[i] * sampleBuf[i];
            float rms = Mathf.Sqrt(sum / sampleBuf.Length);
            return Mathf.Clamp01(rms * voiceJawGain);
        }

        /// <summary>Highest expression whose awareness threshold the NPC has reached. Edit mode / no perception → the first.</summary>
        Expression TargetExpression()
        {
            float aware = Application.isPlaying && perception != null ? perception.Awareness01 : 0f;
            Expression best = expressions[0];
            foreach (var e in expressions)
                if (e.minAwareness <= aware && e.minAwareness >= best.minAwareness)
                    best = e;
            return best;
        }

        void SnapToExpression(Expression e)
        {
            ebMin = e.eyebrowRange.x; ebMax = e.eyebrowRange.y;
            jMin = e.jawRange.x; jMax = e.jawRange.y;
            ebSpeed = e.eyebrowSpeed; jSpeed = e.jawSpeed;
            pupilColorCur = e.pupilColor;
            pupilSizeCur = e.pupilSize;
            scleraColorCur = e.scleraColor;
            scleraBrightCur = e.scleraBrightness;
            initialized = true;
        }
    }
}

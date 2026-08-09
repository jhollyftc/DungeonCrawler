using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace DungeonGen
{
    /// <summary>
    /// Authoring for the per-torch crackle. Lives on TorchSettings and is copied onto the
    /// runtime pool, the same way TorchCullingManager's dials are.
    /// </summary>
    [System.Serializable]
    public class TorchAudioSettings
    {
        [Tooltip("Looping fire crackle. Several = the torches in one corridor don't sound cloned. Empty disables the whole system.")]
        public AudioClip[] loopClips;

        [Tooltip("Mixer group. Ambient/Proximity is the right home: a torch is a continuously-present POSITIONAL part of the room, not a physics impact and not a flat bed.")]
        public AudioMixerGroup mixerGroup;

        [Tooltip("How many torches can be heard AT ONCE. This is the whole voice-budget story: a loop holds its voice permanently, unlike a one-shot, so a source per torch would be 100+ permanent voices against a real budget of 32. Bounded by what you can perceive, exactly like maxShadowCasters.")]
        [Range(1, 16)] public int voices = 5;

        [Tooltip("Hearing range (m). Deliberately SHORT — a torch crackle is a PROXIMITY cue, and one audible across a room stops meaning 'you are near fire'. Also see the note on stealing: this doubles as the range at which a voice may be reassigned silently.")]
        public float maxDistance = 9f;
        [Tooltip("Full volume within this radius (m). IGNORED when a custom rolloff curve is used — Unity's custom curve spans 0..maxDistance on its own, so shape the flat near-field into the curve instead.")]
        public float minDistance = 1.5f;

        [Tooltip("Use the authored rolloff curve below instead of Unity's built-in linear falloff.")]
        public bool customRolloff = false;

        [Tooltip("Volume against distance: x = distance / maxDistance (so x=1 IS maxDistance), y = volume multiplier. The flat section at the left is the near field — widen it to keep a torch at full volume as you walk right past it, then let it fall away.\n\nIT MUST REACH 0 AT x=1. A voice is reassigned to a nearer torch at exactly that range, and the switch is inaudible ONLY because the source is silent there. A curve ending above zero puts a click on every steal, which will read as a bad loop point rather than a rolloff setting. Warned once at startup if it doesn't.")]
        public AnimationCurve rolloff = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.15f, 1f), new Keyframe(1f, 0f));
        [Range(0f, 1f)] public float volume = 0.5f;
        [Tooltip("Stereo spread. A little stops a torch collapsing to a hard point as you brush past it.")]
        [Range(0f, 180f)] public float spread = 35f;

        [Tooltip("Random pitch per voice, so two torches near each other don't comb-filter into a phasing whine.")]
        public Vector2 pitchRange = new Vector2(0.92f, 1.08f);

        [Tooltip("Seconds between reassignment passes. This does not need to be fast — torches don't move, and only the LISTENER's travel changes the answer.")]
        public float reassignInterval = 0.25f;

        [Tooltip("A nearer torch must beat the one holding a voice by this many metres before it steals it.\n\nWITHOUT THIS THE POOL FLAPS. Two torches at near-equal distance swap rank on the smallest movement, and a looping source jumping between them stutters audibly — the same oscillation NpcBrain's approachHysteresis and investigateRadius exist to stop. Shadow casters need no equivalent because a shadow popping between two distant torches is invisible; audio is not.")]
        public float stealMargin = 2f;
    }

    /// <summary>
    /// Per-torch fire crackle, from a POOL of voices reassigned to whichever torches are
    /// nearest — never one AudioSource per torch.
    ///
    /// WHY POOLED. A looping source holds its voice for as long as it plays, unlike a
    /// one-shot that frees its slot when the clip ends. A dungeon carries dozens to hundreds
    /// of torches, so a source per torch is 100+ permanent voices against a real budget of
    /// 32 — the same shape as the phantom `playOnAwake` voices (§10b), except these ones
    /// actually mix, so it would be worse. The pool bounds the cost by what can be PERCEIVED
    /// rather than by what exists, exactly as maxShadowCasters does for shadow-casting lights.
    ///
    /// WHY POSITIONAL AND NOT AN AMBIENT BED. A bed cannot pan. Walking a corridor past a
    /// sconce and hearing it swing across you and fall behind is the entire effect, and it is
    /// corridors — where torches are sparse enough to locate individually — that sell it.
    ///
    /// ONE CHILD GAMEOBJECT PER VOICE, because an AudioSource is positioned by its transform;
    /// a shared object cannot place several sounds. Same reason OneShotAudioPool is built that
    /// way.
    /// </summary>
    [DisallowMultipleComponent]
    public class TorchAudioPool : MonoBehaviour
    {
        public TorchAudioSettings settings = new TorchAudioSettings();

        readonly List<Vector3> torches = new List<Vector3>();

        class Voice
        {
            public AudioSource src;
            public int torch = -1;      // index into `torches`, -1 = idle
            public float sqrDist = float.MaxValue;
        }
        readonly List<Voice> voices = new List<Voice>();
        float timer;

        /// <summary>
        /// Called by TorchPlacer per torch. Positions are STATIC and cached — a torch never
        /// moves, so only the listener's travel changes which are nearest.
        /// </summary>
        public void Register(Vector3 worldPos) => torches.Add(worldPos);

        void Start()
        {
            if (settings.loopClips == null || settings.loopClips.Length == 0 || torches.Count == 0)
            {
                enabled = false;
                return;
            }

            WarnIfCurveDoesNotSilence();

            int n = Mathf.Min(settings.voices, torches.Count);
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject($"TorchVoice{i}");
                go.transform.SetParent(transform, false);

                var src = go.AddComponent<AudioSource>();
                src.clip = settings.loopClips[Random.Range(0, settings.loopClips.Length)];
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                // WHATEVER THE SHAPE, IT MUST REACH TRUE ZERO AT maxDistance. That is what
                // lets a voice be reassigned at that range with no fade machinery: the switch
                // happens while the source is already silent. Unity's LINEAR mode guarantees
                // it; a custom curve is the author's responsibility, hence the check below.
                // (Logarithmic is deliberately not offered — it never reaches zero, so it
                // would click on every steal.)
                src.minDistance = settings.minDistance;
                src.maxDistance = settings.maxDistance;
                if (settings.customRolloff && settings.rolloff != null && settings.rolloff.length > 0)
                {
                    src.rolloffMode = AudioRolloffMode.Custom;
                    // AFTER maxDistance: the curve's x axis is normalized against it, so
                    // setting the curve first would bake in the wrong range.
                    src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, settings.rolloff);
                }
                else
                {
                    src.rolloffMode = AudioRolloffMode.Linear;
                }
                src.spread = settings.spread;
                src.volume = settings.volume;
                src.pitch = Random.Range(settings.pitchRange.x, settings.pitchRange.y);
                src.priority = AudioPriority.AmbientPoint;
                AudioBus.Assign(src, settings.mixerGroup);

                voices.Add(new Voice { src = src });
            }
        }

        /// <summary>
        /// A custom rolloff that is still audible at maxDistance breaks the assumption the
        /// whole reassignment scheme rests on — that a steal happens in silence. The result is
        /// a click every time you walk past a torch, which presents as a bad loop point and
        /// sends you auditioning the clip rather than checking the curve. Named explicitly for
        /// that reason.
        /// </summary>
        void WarnIfCurveDoesNotSilence()
        {
            if (!settings.customRolloff || settings.rolloff == null || settings.rolloff.length == 0) return;
            float endVolume = settings.rolloff.Evaluate(1f);
            if (endVolume <= 0.001f) return;

            Debug.LogWarning(
                $"[TorchAudioPool] The rolloff curve is still at {endVolume:0.000} volume at maxDistance " +
                $"({settings.maxDistance}m) instead of 0. Voices are reassigned to nearer torches at exactly " +
                "that range on the assumption the source is silent there, so this will CLICK each time — " +
                "which sounds like a bad loop point, not a rolloff setting. Drag the curve's last key to 0.",
                this);
        }

        void Update()
        {
            timer -= Time.deltaTime;
            if (timer > 0f) return;
            timer = settings.reassignInterval;

            var listener = AudioCull.Listener;
            if (listener == null) return;
            Vector3 lp = listener.transform.position;

            float range = settings.maxDistance;
            float rangeSqr = range * range;

            // Refresh what the holders are worth now, and release any that have walked out of
            // range. Releasing at maxDistance is silent by construction (linear rolloff), so
            // no fade is needed.
            for (int i = 0; i < voices.Count; i++)
            {
                var v = voices[i];
                if (v.torch < 0) continue;
                v.sqrDist = (torches[v.torch] - lp).sqrMagnitude;
                if (v.sqrDist > rangeSqr) Release(v);
            }

            // Offer each in-range torch a voice: an idle one first, otherwise steal from the
            // WORST holder — but only if it wins by stealMargin, or the pool flaps between
            // near-equidistant torches and stutters.
            for (int t = 0; t < torches.Count; t++)
            {
                float d = (torches[t] - lp).sqrMagnitude;
                if (d > rangeSqr) continue;
                if (IsHeld(t)) continue;

                Voice idle = FindIdle();
                if (idle != null) { Assign(idle, t, d); continue; }

                Voice worst = FindWorst();
                if (worst == null) continue;

                // Compare in real metres: a squared-distance margin is not a distance and
                // would tighten as you move away, which is the opposite of what hysteresis
                // needs to do.
                float dm = Mathf.Sqrt(d);
                float wm = Mathf.Sqrt(worst.sqrDist);
                if (dm + settings.stealMargin < wm) Assign(worst, t, d);
            }
        }

        bool IsHeld(int torch)
        {
            for (int i = 0; i < voices.Count; i++) if (voices[i].torch == torch) return true;
            return false;
        }

        Voice FindIdle()
        {
            for (int i = 0; i < voices.Count; i++) if (voices[i].torch < 0) return voices[i];
            return null;
        }

        Voice FindWorst()
        {
            Voice worst = null;
            for (int i = 0; i < voices.Count; i++)
                if (voices[i].torch >= 0 && (worst == null || voices[i].sqrDist > worst.sqrDist))
                    worst = voices[i];
            return worst;
        }

        void Assign(Voice v, int torch, float sqrDist)
        {
            v.torch = torch;
            v.sqrDist = sqrDist;
            v.src.transform.position = torches[torch];
            if (!v.src.isPlaying)
            {
                // Start at a RANDOM point in the loop. Every voice starting at t=0 makes a
                // corridor of torches crackle in lockstep, which reads as one wide sound
                // rather than several separate fires.
                v.src.time = Random.Range(0f, Mathf.Max(0.01f, v.src.clip.length));
                v.src.Play();
            }
        }

        void Release(Voice v)
        {
            v.torch = -1;
            v.sqrDist = float.MaxValue;
            v.src.Stop();
        }
    }
}

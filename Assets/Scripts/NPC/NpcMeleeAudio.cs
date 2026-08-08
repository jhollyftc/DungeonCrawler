using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The WEAPON's sound as an NPC swings — the whoosh of a blade through air. The NPC
    /// counterpart to PlayerMeleeAudio riding PlayerMelee, and the same split as everywhere
    /// else: MeleeAttack stays about mechanics, audio only listens.
    ///
    /// Deliberately NOT in NpcCombatAudio, even though both make noise when a goblin fights.
    /// That component is the VOICE — NpcFace follows its source amplitude to open the jaw
    /// (§10), so anything played through it moves the goblin's mouth. A sword whoosh coming
    /// out of a goblin's face would be wrong, and the effort grunt that SHOULD move its mouth
    /// belongs there instead (NpcCombatAudio.PlayAttackVoice). One component per sound
    /// SOURCE, not per situation.
    ///
    /// 3D source, unlike PlayerMeleeAudio's 2D one: the player's weapon is their own arm, an
    /// NPC's is a position in the world you need to locate by ear.
    /// </summary>
    [RequireComponent(typeof(MeleeAttack))]
    [DisallowMultipleComponent]
    public class NpcMeleeAudio : MonoBehaviour
    {
        [Header("Source (auto-added 3D if empty)")]
        [Tooltip("Left empty, a 3D source (linear rolloff) is added at Awake. Kept separate from NpcCombatAudio's voice source so a whoosh never drives the jaw.")]
        [SerializeField] private AudioSource source;
        [Tooltip("Rolloff distance (m) for the swing. Shorter than the voice's 25m on purpose — you should hear a goblin shout across a room but only hear its blade when it's close enough to matter.")]
        [SerializeField] private float maxDistance = 14f;

        [Header("Clips")]
        [Tooltip("Swing whooshes. Several = free variation via the house no-repeat pick.")]
        [SerializeField] private AudioClip[] whooshClips;
        [Range(0f, 1f)][SerializeField] private float volume = 0.7f;
        [Tooltip("Random pitch spread per swing.")]
        [SerializeField] private Vector2 pitchRange = new Vector2(0.9f, 1.1f);

        [Header("Timing")]
        [Tooltip("ON: the whoosh fires from an ANIMATION EVENT (`AnimationWhoosh`) on the clip, so it tracks the blade exactly like the sweep does — the right choice once per-weapon clips land, since each has its own swing speed. OFF: fired `whooshDelay` seconds after the swing starts, which needs no clip authoring at all. Start OFF, switch ON if the whoosh drifts from the visible swing.")]
        [SerializeField] private bool whooshFromAnimationEvent = false;
        [Tooltip("Seconds after the swing starts before the whoosh plays, when NOT using the animation event. A whoosh at the very START of the windup reads wrong — the blade hasn't moved yet — so this should land it just BEFORE the impact frame.")]
        [SerializeField] private float whooshDelay = 0.25f;

        [Header("Crowd control")]
        [Tooltip("Don't play at all beyond this distance from the listener (m). At the target population a crowd of attackers is a wall of whooshes; culling by distance is cheaper and cleaner than letting rolloff fade dozens of inaudible voices, which still costs AudioSource slots.")]
        [SerializeField] private float cullDistance = 18f;

        MeleeAttack melee;
        int lastIndex = -1;

        void Awake()
        {
            melee = GetComponent<MeleeAttack>();

            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 1f;               // a swing happens WHERE THE GOBLIN IS
                source.rolloffMode = AudioRolloffMode.Linear;
            }
            source.maxDistance = maxDistance;
            source.playOnAwake = false;
        }

        void OnEnable()
        {
            melee.OnSwingStart += HandleSwingStart;
            melee.OnSwingCancelled += HandleSwingCancelled;
        }

        void OnDisable()
        {
            melee.OnSwingStart -= HandleSwingStart;
            melee.OnSwingCancelled -= HandleSwingCancelled;
            CancelInvoke(nameof(PlayWhoosh));
        }

        void HandleSwingStart()
        {
            if (whooshFromAnimationEvent) return;       // the clip will call AnimationWhoosh
            CancelInvoke(nameof(PlayWhoosh));
            Invoke(nameof(PlayWhoosh), Mathf.Max(0f, whooshDelay));
        }

        // A parried or interrupted swing never completes, so the blade never travels — the
        // whoosh must not arrive afterwards from a pending Invoke.
        void HandleSwingCancelled() => CancelInvoke(nameof(PlayWhoosh));

        /// <summary>
        /// ANIMATION EVENT target — put one on the clip a little before the impact frame,
        /// function name `AnimationWhoosh`. Only used when whooshFromAnimationEvent is on, so
        /// a clip carrying the event can still be driven by the timer without doubling up.
        /// </summary>
        public void AnimationWhoosh()
        {
            if (!whooshFromAnimationEvent) return;
            PlayWhoosh();
        }

        void PlayWhoosh()
        {
            if (source == null || whooshClips == null || whooshClips.Length == 0) return;

            // Distance cull. AudioListener rather than a cached player reference: the listener
            // is what actually decides audibility, and it survives the player rig being
            // rebuilt on a dungeon regenerate.
            if (cullDistance > 0f)
            {
                var listener = FindObjectOfType<AudioListener>();
                if (listener != null &&
                    (listener.transform.position - transform.position).sqrMagnitude > cullDistance * cullDistance)
                    return;
            }

            // Random NO-REPEAT pick, house convention — a two-clip set alternating at random
            // still repeats often enough to sound mechanical.
            int i = 0;
            if (whooshClips.Length > 1)
            {
                do { i = Random.Range(0, whooshClips.Length); }
                while (i == lastIndex);
            }
            lastIndex = i;
            if (whooshClips[i] == null) return;

            source.pitch = Random.Range(pitchRange.x, pitchRange.y);
            source.PlayOneShot(whooshClips[i], volume);
        }
    }
}

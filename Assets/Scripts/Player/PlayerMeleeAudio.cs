using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The player's own melee SOUND — the WHOOSH of the swing/thrust through air, played
    /// at the launch of each attack (PlayerMelee.OnAttackSwung). Its own component
    /// subscribed to the event, same split as NpcCombatAudio riding Health: PlayerMelee
    /// stays about motion, audio is a listener.
    ///
    /// This is the SWING sound only — the whoosh you hear whether or not you connect. The
    /// HIT sound (flesh splat, stone spark) is separate and surface-driven: it comes from
    /// MeleeHitEffects → SurfaceImpact when the sweep actually lands. Swing + impact layer
    /// naturally because they fire at different moments and from different systems.
    ///
    /// First-person weapon, so the source is 2D (spatialBlend 0) — it's YOUR arm, not a
    /// sound out in the world. House conventions: random no-repeat pick + pitch jitter so
    /// repeats don't sound stamped.
    /// </summary>
    [RequireComponent(typeof(PlayerMelee))]
    [DisallowMultipleComponent]
    public class PlayerMeleeAudio : MonoBehaviour
    {
        [Header("Source (auto-added 2D if empty)")]
        [Tooltip("Whoosh source. Left empty, a 2D source is added at Awake (first-person = your own arm, not a world position).")]
        [SerializeField] private AudioSource source;

        [Header("Whoosh clips (per attack kind)")]
        [Tooltip("Light-swing whooshes. Several = free variation.")]
        [SerializeField] private AudioClip[] lightWhoosh;
        [Tooltip("Heavy-swing whooshes — heavier, slower. Falls back to light if empty.")]
        [SerializeField] private AudioClip[] heavyWhoosh;
        [Tooltip("Shield-bash whooshes (the thrust). Falls back to heavy, then light, if empty.")]
        [SerializeField] private AudioClip[] bashWhoosh;

        [Header("Mix")]
        [Range(0f, 1f)][SerializeField] private float volume = 0.9f;
        [Tooltip("Random pitch spread on every whoosh.")]
        [SerializeField] private Vector2 pitchRange = new Vector2(0.94f, 1.06f);

        PlayerMelee melee;
        int lastLight = -1, lastHeavy = -1, lastBash = -1;

        void Awake()
        {
            melee = GetComponent<PlayerMelee>();
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 0f;   // 2D — it's the player's own weapon
            }
            source.playOnAwake = false;
        }

        void OnEnable() => melee.OnAttackSwung += HandleSwung;
        void OnDisable() => melee.OnAttackSwung -= HandleSwung;

        void HandleSwung(PlayerMelee.AttackKind kind)
        {
            switch (kind)
            {
                case PlayerMelee.AttackKind.Light:
                    PlayOneShot(lightWhoosh, ref lastLight);
                    break;
                case PlayerMelee.AttackKind.Heavy:
                    // Heavy → heavy clips, but a missing set still makes a sound.
                    if (Has(heavyWhoosh)) PlayOneShot(heavyWhoosh, ref lastHeavy);
                    else PlayOneShot(lightWhoosh, ref lastLight);
                    break;
                case PlayerMelee.AttackKind.Bash:
                    if (Has(bashWhoosh)) PlayOneShot(bashWhoosh, ref lastBash);
                    else if (Has(heavyWhoosh)) PlayOneShot(heavyWhoosh, ref lastHeavy);
                    else PlayOneShot(lightWhoosh, ref lastLight);
                    break;
            }
        }

        static bool Has(AudioClip[] clips) => clips != null && clips.Length > 0;

        void PlayOneShot(AudioClip[] clips, ref int lastIndex)
        {
            if (source == null || !Has(clips)) return;

            int i = 0;
            if (clips.Length > 1)
            {
                do { i = Random.Range(0, clips.Length); }
                while (i == lastIndex);
            }
            lastIndex = i;
            if (clips[i] == null) return;

            source.pitch = Random.Range(pitchRange.x, pitchRange.y);
            source.PlayOneShot(clips[i], volume);
        }
    }
}

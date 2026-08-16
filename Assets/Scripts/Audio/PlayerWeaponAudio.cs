using UnityEngine;
using UnityEngine.Audio;

namespace DungeonGen
{
    /// <summary>
    /// The sound of swapping weapons: the release as one leaves your hands, the draw as the
    /// next rises into view.
    ///
    /// CLIPS COME FROM THE WEAPON, not from here. A greatsword coming off the back and a dagger
    /// clearing a sheath are different actions, and the swap is the one moment the player is
    /// looking straight at the weapon — so `WeaponDefinition` carries them and this component
    /// owns only the source, the mix and the variation. Adding a weapon needs no change here.
    ///
    /// ITS OWN COMPONENT subscribed to events, the same split PlayerMeleeAudio makes against
    /// PlayerMelee: the slots component stays about swapping and never learns that audio exists.
    ///
    /// 2D, because it is YOUR hands rather than a sound out in the world — the same reasoning
    /// that makes PlayerMeleeAudio's whoosh 2D while an NPC's is positional. The CLATTER of the
    /// weapon hitting the floor is a different sound from a different source: that belongs to
    /// the dropped object's own ImpactAudio, out in the world where it landed.
    /// </summary>
    [RequireComponent(typeof(PlayerWeaponSlots))]
    [DisallowMultipleComponent]
    public class PlayerWeaponAudio : MonoBehaviour
    {
        [Header("Source (auto-added 2D if empty)")]
        [Tooltip("Left empty, a 2D source is added at Awake — first-person, so it is your own hands rather than a world position.")]
        [SerializeField] private AudioSource source;
        [Tooltip("Mixer group this routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, so an Output assigned in the inspector would cover only the authored case. An UNASSIGNED group bypasses the mixer entirely — straight to the listener, immune to every volume slider and invisible on every meter.")]
        [SerializeField] private AudioMixerGroup mixerGroup;

        [Header("Mix")]
        [Range(0f, 1f)][SerializeField] private float drawVolume = 0.9f;
        [Range(0f, 1f)][SerializeField] private float releaseVolume = 0.7f;
        [Tooltip("Random pitch spread, so repeated swaps don't sound stamped.")]
        [SerializeField] private Vector2 pitchRange = new Vector2(0.95f, 1.05f);

        PlayerWeaponSlots slots;
        int lastDraw = -1, lastRelease = -1;

        void Awake()
        {
            slots = GetComponent<PlayerWeaponSlots>();

            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 0f;   // 2D — your own hands
            }
            source.playOnAwake = false;
            // AND STOP IT. playOnAwake governs only a FUTURE start and cannot undo one already
            // underway — the engine acts on the authored flag before this runs. A source told to
            // play with no clip enters a playing state that NEVER completes: it reports isPlaying
            // forever while making no sound, holding a voice slot against a budget of ~32.
            source.Stop();

            // Route, not Assign: this source is OWNED by this component, so a null group leaves
            // whatever is already configured rather than clearing it. Assign is for POOLED
            // voices, which must be told their group on every acquisition or they inherit the
            // previous caller's — a footstep coming out of the Combat bus.
            AudioBus.Route(source, mixerGroup);
            source.priority = AudioPriority.PlayerAction;
        }

        void OnEnable()
        {
            if (slots == null) return;
            slots.OnWeaponReleased += PlayRelease;
            slots.OnWeaponDrawn += PlayDraw;
        }

        void OnDisable()
        {
            if (slots == null) return;
            slots.OnWeaponReleased -= PlayRelease;
            slots.OnWeaponDrawn -= PlayDraw;
        }

        void PlayDraw(WeaponDefinition weapon) => Play(weapon?.drawClips, drawVolume, ref lastDraw);
        void PlayRelease(WeaponDefinition weapon) => Play(weapon?.dropClips, releaseVolume, ref lastRelease);

        void Play(AudioClip[] clips, float volume, ref int last)
        {
            if (source == null || clips == null || clips.Length == 0) return;

            // No-repeat pick: with two or more clips, never the same one twice running. A weapon
            // with a single clip degrades to just playing it.
            int i = clips.Length == 1 ? 0 : Random.Range(0, clips.Length);
            if (clips.Length > 1 && i == last) i = (i + 1) % clips.Length;
            last = i;

            if (clips[i] == null) return;
            source.pitch = Random.Range(pitchRange.x, pitchRange.y);
            source.PlayOneShot(clips[i], volume);
        }
    }
}

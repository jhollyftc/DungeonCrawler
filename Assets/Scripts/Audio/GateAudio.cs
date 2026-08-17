using UnityEngine;
using UnityEngine.Audio;

namespace DungeonGen
{
    /// <summary>
    /// The sound of a gate moving — the whole reason levers are placed away from what they open.
    /// Pull a lever somewhere quiet, hear something heavy shift in the distance, go and find it.
    ///
    /// IT PLAYS AT THE GATE, NEVER AT THE LEVER. A clunk at your own hand carries no information;
    /// a portcullis grinding up behind you tells you where to go. `IGateLock.SoundOrigin` exists
    /// for exactly this, and this component lives on the gate.
    ///
    /// IT DELIBERATELY FIGHTS THE AUDIO SYSTEM'S DEFAULTS, and that is not an oversight.
    /// `AudioCull.TooFar` culls by distance and `AudioOcclusion` muffles through walls — both
    /// correct for ambience, both exactly wrong for a LANDMARK you are meant to hear from across
    /// the dungeon through solid rock. So this source gets a deliberately large `maxDistance` and
    /// opts out of occlusion. Anything else that wants to be heard through geometry on purpose
    /// belongs in the same category; it is not a general licence to skip occlusion.
    /// </summary>
    [DisallowMultipleComponent]
    public class GateAudio : MonoBehaviour
    {
        [Header("Source (auto-added 3D if empty)")]
        [Tooltip("Left empty, a 3D source is added at Awake. It must be POSITIONAL — the direction is the information.")]
        [SerializeField] private AudioSource source;
        [Tooltip("Mixer group. Set it HERE rather than on the AudioSource: the source is usually created at runtime, so an inspector Output would cover only the authored case. An unassigned group bypasses the mixer entirely.")]
        [SerializeField] private AudioMixerGroup mixerGroup;

        [Header("Clips")]
        [Tooltip("A gate starting to move — the grind of a portcullis, the clunk of a bolt drawing back. This is the cue the player acts on.")]
        [SerializeField] private AudioClip[] openClips;
        [Tooltip("A gate closing. Leave empty on a one-shot door lock, which never closes.")]
        [SerializeField] private AudioClip[] closeClips;
        [Tooltip("A locked door being shoved. Rate-limited by PhysicsDoor, not here — Push() fires every frame you lean on it.")]
        [SerializeField] private AudioClip[] rattleClips;

        [Header("Carry")]
        [Tooltip("Metres at which the gate becomes inaudible. LARGE on purpose — this is a landmark, not ambience, and the default rolloff would lose it two rooms away.")]
        [SerializeField] private float maxDistance = 60f;
        [Tooltip("Metres within which it plays at full volume.")]
        [SerializeField] private float minDistance = 4f;
        [Range(0f, 1f)][SerializeField] private float volume = 1f;
        [SerializeField] private Vector2 pitchRange = new Vector2(0.97f, 1.03f);

        int lastOpen = -1, lastClose = -1, lastRattle = -1;

        void Awake()
        {
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 1f;                       // 3D — the direction IS the cue
                source.rolloffMode = AudioRolloffMode.Linear;   // reaches zero, so nothing clicks at the edge
            }
            source.playOnAwake = false;
            // AND STOP IT. playOnAwake governs only a FUTURE start and cannot undo one already
            // underway — a source told to play with no clip enters a playing state that never
            // completes, reporting isPlaying forever while holding a voice slot.
            source.Stop();
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            AudioBus.Route(source, mixerGroup);
            source.priority = AudioPriority.WorldImpact;

            // OPT OUT OF OCCLUSION. Registering here would muffle the one sound whose whole job
            // is to travel through the dungeon. Left unregistered deliberately — see the class
            // summary before "fixing" this.

            // LOOKS BOTH UP AND DOWN THE HIERARCHY, so this can sit on the prefab ROOT or on the
            // moving part and work either way. Kit prefabs here are consistently a FRAME plus a
            // moving child — the door leaf, the portcullis bars — so `GetComponentInParent`
            // alone would find nothing when the component is placed on the frame, which is the
            // obvious place to put it. Searching parents first keeps the nearest owner winning
            // when a prefab nests several.
            portcullis = GetComponentInParent<Portcullis>() ?? GetComponentInChildren<Portcullis>(true);
            door = GetComponentInParent<PhysicsDoor>() ?? GetComponentInChildren<PhysicsDoor>(true);

            if (portcullis != null) portcullis.OnToggled += HandleToggled;
            if (door != null)
            {
                door.OnUnlocked += HandleUnlocked;
                door.OnLockedRattle += HandleRattle;
            }

            if (portcullis == null && door == null)
                Debug.LogWarning("[GateAudio] No Portcullis or PhysicsDoor found on this object, its " +
                                 "parents or its children — nothing will ever fire these clips.", this);
        }

        // CACHED RATHER THAN RE-LOOKED-UP. Unsubscribing via a fresh search is fragile: by
        // OnDestroy the hierarchy may already be partly torn down, and a search that returns a
        // different component (or none) leaves the original subscription dangling.
        Portcullis portcullis;
        PhysicsDoor door;

        void OnDestroy()
        {
            if (portcullis != null) portcullis.OnToggled -= HandleToggled;
            if (door != null)
            {
                door.OnUnlocked -= HandleUnlocked;
                door.OnLockedRattle -= HandleRattle;
            }
        }

        void HandleToggled(bool opening) => Play(opening ? openClips : closeClips, ref lastOpen, volume);
        void HandleUnlocked() => Play(openClips, ref lastOpen, volume);
        // Plays for ANYONE — the sound of a locked door being tried is worth hearing whoever
        // is trying it. Only the on-screen message is player-only (see DoorLock).
        void HandleRattle(float strength, bool fromPlayer) =>
            Play(rattleClips, ref lastRattle, Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(strength)) * volume);

        void Play(AudioClip[] clips, ref int last, float vol)
        {
            if (source == null || clips == null || clips.Length == 0) return;

            int i = clips.Length == 1 ? 0 : Random.Range(0, clips.Length);
            if (clips.Length > 1 && i == last) i = (i + 1) % clips.Length;
            last = i;
            if (clips[i] == null) return;

            source.pitch = Random.Range(pitchRange.x, pitchRange.y);
            source.PlayOneShot(clips[i], vol);
        }
    }
}

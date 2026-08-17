using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A wall lever that drives one gate somewhere else in the dungeon.
    ///
    /// THE SOUND PLAYS AT THE GATE, NEVER HERE, and that is the whole point of the feature. A
    /// clunk at your own hand tells you nothing; a portcullis grinding up somewhere behind you
    /// tells you where to go. `IGateLock.SoundOrigin` exists only to carry that.
    ///
    /// IT DELIBERATELY DOES NOT KNOW WHAT IT OPENS. Doors adapt through `DoorLock`, gates
    /// implement `IGateLock` directly, and adding a third kind of gate needs no change here.
    ///
    /// The link is set by the placer AFTER every gate exists — record-then-consume, the same
    /// shape KitSocketPlacer uses — so spawn order between the kit placer (doors) and the gate
    /// placer (portcullises) does not matter.
    /// </summary>
    [DisallowMultipleComponent]
    public class Lever : MonoBehaviour, IInteractable
    {
        [Tooltip("The gate this drives. Assigned by GatePlacer at generation; a lever with none is inert and says so.")]
        public MonoBehaviour gateBehaviour;   // an IGateLock — typed loosely so it serializes

        [Header("Handle")]
        [Tooltip("The part that swings when pulled. Leave empty for no visible movement.")]
        public Transform handle;
        [Tooltip("Degrees the handle throws.")]
        public float throwAngle = 55f;
        [Tooltip("Local axis the handle rotates about.")]
        public Vector3 handleAxis = Vector3.right;
        [Tooltip("Seconds the handle takes to move.")]
        public float throwTime = 0.25f;

        [Header("Audio")]
        [Tooltip("Left empty, a 3D source is added on first use — the ChestHinge pattern, so an unauthored lever still sounds.")]
        [SerializeField] private AudioSource audioSource;
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;
        [Tooltip("The handle being thrown to OPEN its gate.")]
        public AudioClip openClip;
        [Tooltip("The handle being thrown to CLOSE its gate. On a one-shot door lock this never plays — a lock does not re-lock — so it can be left empty there.")]
        public AudioClip closeClip;
        [Tooltip("Metres at which the lever's own clunk fades out. SHORT on purpose: this is the sound of the thing in your hand, and it must not compete with the GATE, which is the cue that actually carries information and reaches much further (see GateAudio).")]
        public float audioMaxDistance = 15f;

        [Tooltip("Log pulls and refusals.")]
        public bool debugLever = false;

        /// <summary>Fired when the lever is pulled, after the gate has been told.</summary>
        public event System.Action OnPulled;

        IGateLock gate;
        Quaternion restRot;
        float t01, target;

        void Awake()
        {
            gate = gateBehaviour as IGateLock;
            if (handle != null) restRot = handle.localRotation;
        }

        /// <summary>Called by the placer once every gate has spawned.</summary>
        public void Link(IGateLock target)
        {
            gate = target;
            gateBehaviour = target as MonoBehaviour;
        }

        void Update()
        {
            if (handle == null || Mathf.Approximately(t01, target)) return;
            t01 = throwTime <= 0f ? target : Mathf.MoveTowards(t01, target, Time.deltaTime / throwTime);
            float e = t01 * t01 * (3f - 2f * t01);
            handle.localRotation = restRot * Quaternion.AngleAxis(throwAngle * e, handleAxis.normalized);
        }

        /// <summary>
        /// Deliberately says nothing about WHAT the lever does or which way it will move it.
        ///
        /// The prompt used to read "Pull lever (opens)" / "(closes)", which quietly gave the
        /// whole thing away: it told the player a gate existed, that it was currently shut, and
        /// that this lever was the answer — before they had found any of it. The feature is built
        /// on pulling something and then HEARING where the consequence was; a prompt that
        /// announces the consequence in advance removes the moment it exists for.
        ///
        /// The gate's own sound is the feedback, and it is the only feedback that should exist.
        /// </summary>
        /// <summary>
        /// Is the linked gate still alive?
        ///
        /// CHECKED THROUGH THE MonoBehaviour, NOT THE INTERFACE. `gate` is an `IGateLock`, and
        /// an interface reference does NOT get Unity's overloaded `==` — a destroyed component
        /// still compares non-null through it, so `gate.Toggle()` would throw a
        /// MissingReferenceException rather than falling through to the "stuck" path. The
        /// UnityEngine.Object reference is the only one that answers this honestly.
        /// </summary>
        bool GateAlive => gate != null && gateBehaviour != null;

        public string Prompt => GateAlive ? "Pull lever" : "Rusted lever (stuck)";

        public void Interact(Transform interactor)
        {
            if (!GateAlive)
            {
                // Not an error worth throwing — a lever whose gate was dropped is harmless — but
                // silence would read as a broken interaction, so say something in-world.
                PlayerMessage.Show("The lever will not budge.");
                if (debugLever) Debug.LogWarning("[Lever] pulled with no linked gate.", this);
                return;
            }

            gate.Toggle();
            target = target > 0.5f ? 0f : 1f;   // handle mirrors the gate's state

            // READ THE GATE'S STATE, NOT THE HANDLE'S, so the clip matches what actually
            // happened. A one-shot DoorLock ignores a second pull, and picking the clip from the
            // handle's own flip would then play "closing" at a door that is still wide open.
            Play(gate.IsOpen ? openClip : closeClip);

            OnPulled?.Invoke();
            if (debugLever) Debug.Log($"[Lever] pulled — gate now {(gate.IsOpen ? "open" : "closed")}.", this);
        }

        /// <summary>
        /// The lever's own clunk, at the lever. Deliberately NOT the gate's sound — that plays at
        /// the gate through <see cref="GateAudio"/>, carries much further, and is the half that
        /// tells the player where to go.
        /// </summary>
        void Play(AudioClip clip)
        {
            if (clip == null) return;

            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.spatialBlend = 1f;                 // 3D — it is a thing in the world
                audioSource.maxDistance = audioMaxDistance;
                audioSource.playOnAwake = false;
                // playOnAwake governs only a FUTURE start; a source told to play with no clip
                // enters a state that never completes and holds a voice slot forever.
                audioSource.Stop();
                audioSource.priority = AudioPriority.PlayerAction;
            }

            AudioBus.Route(audioSource, mixerGroup);
            audioSource.PlayOneShot(clip);
        }
    }
}

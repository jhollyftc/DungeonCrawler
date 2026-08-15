using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Distance-culls the GENERATED GAMEOBJECT RENDERERS — doors, FullGameObject props,
    /// carryables, chests, grates.
    ///
    /// WHY THIS EXISTS: only the instanced path was ever distance-culled.
    /// `InstancedDungeonRenderer` packs visible instances by `renderDistance` because it owns
    /// its own submission; everything rendering as a real GameObject had nothing but Unity's
    /// FRUSTUM culling, so an object eighty metres down a corridor was still submitted every
    /// frame. Measured at depth 20, turning on the spot: 21.0ms frame against **2.8ms GPU** —
    /// CPU-bound on draw submission — with 2314 single-instance draws appearing purely because
    /// the sightline got longer. The instanced count barely moved between the two, which is what
    /// pointed here (§5).
    ///
    /// IT TOGGLES `Renderer.enabled`, NEVER `GameObject.SetActive`, and that is the whole
    /// safety argument. Deactivating the object would take out its collider, its `PhysicsDoor`,
    /// its `Carryable`, its lights and every script on it — and a `StaticCollider` prop IS its
    /// collider, so the world would become walkable-through at range. Worse for doors: the
    /// collider vanishing lets an NPC walk through a closed doorway, and re-activating could
    /// leave a body inside the door. Disabling only the renderer changes nothing behavioural:
    /// an NPC shoves an invisible door open exactly as it would a visible one, and it comes back
    /// at whatever angle it now sits.
    ///
    /// SEPARATE FROM NPC DORMANCY (roadmap 26), which caps the cost of AI rather than of draws
    /// and toggles `NpcLocomotion`. Each system owns a different property on a different
    /// component, so they compose without knowing about each other — the same one-owner-per-
    /// property discipline that keeps TorchFlicker and facingLockedFrame from fighting.
    /// </summary>
    [RequireComponent(typeof(DungeonVisualizer))]
    public class DungeonRendererCulling : MonoBehaviour
    {
        [Tooltip("Master switch. Turning it off at runtime restores every renderer it was managing, so it is safe to A/B in play mode.")]
        public bool cullingEnabled = true;

        [Tooltip("Metres beyond which a generated renderer stops drawing.\n\nSIZE IT TO THE FOG, not to taste: DungeonFogController washes distant geometry toward the room's fog colour, so past that point an object's disappearance is invisible. That is exactly why InstancedDungeonRenderer.renderDistance gets away with the same trick today. Too short and things pop in front of you; too long and it saves nothing.")]
        public float cullDistance = 60f;

        [Tooltip("Extra metres an object must come back INSIDE before it is drawn again. Without a band, anything sitting exactly at the boundary flickers on and off as you breathe — the same hysteresis NpcBrain's approach bands and TorchAudioPool's steal margin exist for.")]
        public float hysteresis = 6f;

        [Tooltip("How many renderers to test per frame. THE SLICING IS NOT OPTIONAL: testing every renderer each frame is itself the CPU cost this exists to remove, and reading transform.position is a managed-to-native transition per access — the exact profile that made Separation() 58% of NpcLocomotion until it was batched. A few frames of latency on a cull decision is invisible; a per-frame sweep of 3000 renderers is not.")]
        public int checksPerFrame = 250;

        [Tooltip("Generated roots to leave alone.\n\nDungeonMesh IS NOT OPTIONAL. DungeonMesher emits the ENTIRE shell as ONE GameObject with one renderer, so its transform sits at the origin and a distance test against it is meaningless — the whole dungeon would blink out the moment you walked 60m from world zero. Invisible in InstancedKit mode so it costs nothing anyway, but in the GeneratedMesh debug view culling it would look like the level had unloaded.\n\nNPCs are excluded because the win is small (Unity already skips their skinning off-screen) and distant NPCs are where the SkinnedMeshRenderer root-bone bounds bug lives — a second system toggling their visibility would muddy that. If NPC rendering ever costs anything, DORMANCY is the better lever, since that system already owns 'this NPC is not participating'.")]
        public string[] excludeRoots = { "DungeonNpcs", "DungeonMesh" };

        [Tooltip("Log how many renderers are tracked and how many are currently culled, once per second.")]
        public bool debugCulling = false;

        readonly List<Renderer> tracked = new List<Renderer>();
        // What WE last decided for each tracked renderer, parallel to `tracked`. Writing only on
        // a CHANGE keeps this off the native side almost entirely, and means a renderer some
        // other system switched off is not fought over every frame.
        readonly List<bool> lastVisible = new List<bool>();

        int cursor;
        int culledCount;
        Transform cam;
        float debugTimer;
        bool wasEnabled = true;

        /// <summary>Called by DungeonVisualizer at the end of BuildMesh, after every placer has run.</summary>
        public void Rebuild()
        {
            tracked.Clear();
            lastVisible.Clear();
            cursor = 0;
            culledCount = 0;

            var buffer = new List<Renderer>();
            foreach (Transform child in transform)
            {
                if (IsExcluded(child.name)) continue;

                buffer.Clear();
                child.GetComponentsInChildren(true, buffer);
                foreach (var r in buffer)
                {
                    // Only manage renderers that are ON right now. Anything already disabled is
                    // disabled for someone else's reason, and adopting it would mean switching
                    // it back on the moment the player walked near.
                    if (r == null || !r.enabled) continue;
                    tracked.Add(r);
                    lastVisible.Add(true);
                }
            }

            if (debugCulling)
                Debug.Log($"[Cull] tracking {tracked.Count} generated renderer(s) beyond {cullDistance}m " +
                          $"(+{hysteresis}m hysteresis), {checksPerFrame}/frame — " +
                          $"a full sweep every {Mathf.CeilToInt(tracked.Count / Mathf.Max(1f, checksPerFrame))} frame(s).", this);
        }

        bool IsExcluded(string rootName)
        {
            if (excludeRoots == null) return false;
            foreach (var n in excludeRoots)
                if (n == rootName) return true;
            return false;
        }

        void LateUpdate()
        {
            if (tracked.Count == 0) return;

            // Turning the switch off must UNDO the culling rather than merely stop updating it,
            // or half the dungeon stays invisible and looks like a bug in whatever changed next.
            if (cullingEnabled != wasEnabled)
            {
                wasEnabled = cullingEnabled;
                if (!cullingEnabled) { RestoreAll(); return; }
            }
            if (!cullingEnabled) return;

            if (cam == null)
            {
                // Re-resolved rather than cached once: the player is destroyed and respawned on
                // every regenerate, so a reference taken at build time goes stale on the first F1.
                Camera main = Camera.main;
                if (main == null) return;
                cam = main.transform;
            }

            Vector3 eye = cam.position;
            float off = cullDistance * cullDistance;
            float on = (cullDistance - Mathf.Max(0f, hysteresis));
            on *= on;

            int checks = Mathf.Min(checksPerFrame, tracked.Count);
            for (int i = 0; i < checks; i++)
            {
                if (cursor >= tracked.Count) cursor = 0;
                var r = tracked[cursor];

                if (r == null)
                {
                    // Destroyed under us — a barrel that broke, or a regenerate racing this.
                    // Swap-remove so the sweep stays O(1) and the cursor keeps its place.
                    int last = tracked.Count - 1;
                    tracked[cursor] = tracked[last]; tracked.RemoveAt(last);
                    lastVisible[cursor] = lastVisible[last]; lastVisible.RemoveAt(last);
                    continue;
                }

                // Squared distance, and the transform read is the reason this is sliced.
                float sq = (r.transform.position - eye).sqrMagnitude;
                bool visible = lastVisible[cursor] ? sq <= off : sq <= on;

                if (visible != lastVisible[cursor])
                {
                    r.enabled = visible;
                    lastVisible[cursor] = visible;
                    culledCount += visible ? -1 : 1;
                }
                cursor++;
            }

            if (!debugCulling) return;
            debugTimer -= Time.unscaledDeltaTime;
            if (debugTimer > 0f) return;
            debugTimer = 1f;
            Debug.Log($"[Cull] {culledCount}/{tracked.Count} generated renderer(s) culled beyond {cullDistance}m.", this);
        }

        void RestoreAll()
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i] != null) tracked[i].enabled = true;
                lastVisible[i] = true;
            }
            culledCount = 0;
        }

        // A disabled component must not leave half the dungeon invisible.
        void OnDisable() => RestoreAll();
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Retires fracture debris: sit for `lifetime`, then shrink away over
    /// `shrinkTime` and destroy.
    ///
    /// SHRINK, not the corpse-style sink through the floor. A body dies standing on a
    /// floor, so sinking works; debris does not — chunks scatter onto stairs, ledges,
    /// tables, or come to rest against a wall, and sinking assumes a floor directly
    /// beneath every one of them. Scaling is position-independent, so it looks right
    /// wherever a chunk ends up.
    ///
    /// Each chunk shrinks about ITS OWN pivot rather than the debris root: scaling the
    /// root would drag every chunk toward the origin, so a pile visibly slides inward
    /// as it disappears. Chunks also go kinematic first — shrinking a live collider
    /// makes it jitter and can drop it through the floor as it gets small.
    ///
    /// Added at runtime by DestructibleProp; nothing needs to author it.
    /// </summary>
    [DisallowMultipleComponent]
    public class DebrisCleanup : MonoBehaviour
    {
        [Tooltip("Seconds the debris lies there before it starts to go. Long enough that the player sees what broke.")]
        public float lifetime = 6f;
        [Tooltip("Seconds to shrink from full size to nothing.")]
        public float shrinkTime = 1.2f;

        IEnumerator Start()
        {
            yield return new WaitForSeconds(Mathf.Max(0f, lifetime));

            // Freeze BEFORE shrinking: a rigidbody whose collider is scaling toward zero
            // jitters against whatever it's resting on and can fall through it.
            foreach (var rb in GetComponentsInChildren<Rigidbody>(true))
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }

            // Shrink each chunk about its own VISUAL CENTRE, not its transform pivot.
            //
            // A fracture FBX exported as one object usually leaves every chunk's transform
            // at the SHARED prefab origin, with the per-chunk offset baked into the mesh
            // VERTICES — Blender showing separate origins doesn't survive that export. So
            // scaling localScale alone shrinks every chunk toward that one common point
            // and the pile visibly collapses inward (real bug).
            //
            // Correcting for it: a chunk's mesh centre sits at C = T + offset. Scaling by k
            // about T moves it to T + offset*k, so the transform is nudged by offset*(1-k)
            // to hold C still. Safe to drive transforms directly here because the bodies
            // were made kinematic above.
            var chunks = CollectChunks();
            int n = chunks.Count;
            var startScale = new Vector3[n];
            var startPos = new Vector3[n];
            var pivotOffset = new Vector3[n];

            for (int i = 0; i < n; i++)
            {
                Transform t0 = chunks[i];
                startScale[i] = t0.localScale;
                startPos[i] = t0.position;
                pivotOffset[i] = VisualCentre(t0) - t0.position;
            }

            float t = 0f;
            float dur = Mathf.Max(0.01f, shrinkTime);
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = 1f - Mathf.Clamp01(t / dur);
                for (int i = 0; i < n; i++)
                {
                    if (chunks[i] == null) continue;
                    chunks[i].localScale = startScale[i] * k;
                    chunks[i].position = startPos[i] + pivotOffset[i] * (1f - k);
                }
                yield return null;
            }

            Destroy(gameObject);
        }

        /// <summary>
        /// The independently-moving pieces. Rigidbodies are the real chunks once the burst
        /// has scattered them; falling back to renderers covers a fracture asset built
        /// without physics on each piece.
        /// </summary>
        List<Transform> CollectChunks()
        {
            var list = new List<Transform>();

            foreach (var rb in GetComponentsInChildren<Rigidbody>(true))
                if (rb != null) list.Add(rb.transform);

            if (list.Count == 0)
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                    if (r != null) list.Add(r.transform);

            if (list.Count == 0) list.Add(transform);   // nothing recognisable — scale the root
            return list;
        }

        /// <summary>
        /// World-space centre of what a chunk actually LOOKS like. Renderer bounds rather
        /// than the transform, because that's the whole point — the transform may sit at
        /// the shared prefab origin while the mesh lives somewhere else entirely.
        /// </summary>
        static Vector3 VisualCentre(Transform chunk)
        {
            var renderers = chunk.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return chunk.position;

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b.center;
        }
    }
}

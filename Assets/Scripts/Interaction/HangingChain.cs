using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Lengthens a hanging prop's chain at spawn so a cage drops as far below a TALL room's
    /// ceiling as it does below a single-storey one. Authored for the caged skeleton and the
    /// empty cage, which read perfectly at 3m and hang two storeys overhead at 6m.
    ///
    /// THE LINK PITCH IS 0.109m, SO ONE EXTRA STOREY IS ~28 LINKS — and that number is what
    /// decides the whole design. A 28-joint PhysX chain carrying a 30kg body stretches and
    /// jitters (the same competing-solver-terms problem `PhysicsDoor` documents), and the
    /// shrine set allows three cages per room. So the chain is SPLIT:
    ///
    ///   - the TOP section is STATIC and goes on the instanced path. A taut chain does not
    ///     move, and §5's rule is that only a MOVING part cannot be instanced — so this costs
    ///     no rigidbodies, no joints, and no draw calls however long the drop.
    ///   - the BOTTOM `dynamicLinks` are real Rigidbody + HingeJoint links, so the stretch
    ///     nearest the cage is genuinely loose and swings.
    ///   - the authored links and the cage below them are untouched and keep their feel.
    ///
    /// IT DERIVES ITS OWN STRUCTURE FROM THE JOINT GRAPH — no authored references, so there is
    /// nothing to half-assign (§12's "a requirement satisfied in two places will be
    /// half-satisfied"). The chain is exactly: the one hinge whose `connectedBody` is null,
    /// then whichever hinge connects to that body, and so on. The LAST body in that walk is
    /// the load; everything above it is a link. Pitch, link spacing and the alternating 90°
    /// link rotation all come from the authored links themselves, so retuning the art in
    /// Blender needs no change here.
    ///
    /// THE ROOT IS THE ANCHOR AND EVERYTHING UNDER IT IS THE HANGING ASSEMBLY. Extending means
    /// shifting every child down by N pitches and filling the vacated slots — which is why the
    /// point light and the audio source, both children at fixed local positions near the cage,
    /// follow it down for free rather than being left at the ceiling.
    ///
    /// OPT-IN, AND CURRENTLY ATTACHED TO NOTHING. The cage prefabs do not carry this component,
    /// so the feature is inert: `Extend` returns immediately for a prop without it. Add it to a
    /// prefab root to switch it on for that prop; remove it to switch it back off. Nothing else
    /// needs changing either way, which is deliberate — the physics half was judged marginal in
    /// play (see `dynamicLinks`) and this should be easy to walk away from.
    /// </summary>
    [DisallowMultipleComponent]
    public class HangingChain : MonoBehaviour
    {
        [Tooltip("How many of the ADDED links get a real Rigidbody and HingeJoint, counted from the BOTTOM of the added section (nearest the cage). The rest of the added length is static instanced mesh.\n\nThis is the swing dial, and it is the only one that costs anything. 0 is FULLY STABLE — the cage pivots on its authored short chain, exactly today's feel, just lower down. Higher makes the lower stretch floppier at one rigidbody and one joint each.\n\nFIELD RESULT: 10 was measured as too much — a chain that long carrying the 30kg cage pushes PhysX harder than the look is worth. Treat single digits as the usable range and 0 as the safe answer.")]
        [Range(0, 24)] public int dynamicLinks = 4;

        [Tooltip("Ceiling on how far the chain may be lengthened, in metres. A safety rail rather than a tuning dial: it stops a freakishly tall room paying out a chain of hundreds of links. 9m is three storeys.")]
        public float maxExtraDrop = 9f;

        [Tooltip("Log what was added and why. Chain extension happens once at generation and is invisible afterwards, so when a cage hangs at the wrong height there is otherwise nothing to look at.")]
        public bool debugChain = false;

        /// <summary>
        /// Lengthens the chain on a SPAWNED prop. Called from the placer's PropPlacement
        /// configure hook, which runs immediately after Instantiate and before the first
        /// physics step — the only window in which the joints can be re-anchored safely.
        ///
        /// Silently does nothing for a prop with no HangingChain, so the placer can call it on
        /// every ceiling prop without knowing which ones hang.
        /// </summary>
        public static void Extend(GameObject spawned, float extraDrop, InstancedDungeonRenderer instancer)
        {
            if (spawned == null || extraDrop <= 0.01f) return;
            var hc = spawned.GetComponent<HangingChain>();
            if (hc != null) hc.ExtendBy(extraDrop, instancer);
        }

        void ExtendBy(float extraDrop, InstancedDungeonRenderer instancer)
        {
            Transform root = transform;

            // ---- 1. Read the chain out of the joint graph -------------------------------
            if (!ResolveChain(root, out List<HingeJoint> chain))
            {
                Debug.LogWarning($"[HangingChain] '{name}' has no resolvable hinge chain (expected exactly one hinge with a null connectedBody, then a connected run below it). Not extending.", this);
                return;
            }
            // The last body is the LOAD (the cage); everything above it is a link.
            int linkCount = chain.Count - 1;
            if (linkCount < 1)
            {
                Debug.LogWarning($"[HangingChain] '{name}' has a load but no chain links to copy. Not extending.", this);
                return;
            }
            Transform firstLink = chain[0].transform;
            Transform template = chain[linkCount - 1].transform;   // deepest LINK, the clone source

            // ---- 2. Pitch and parity, both derived from the authored links ---------------
            // The anchor sits at the root's origin, so the first link is exactly one pitch
            // down. With two or more links the SPACING between them is the better measure —
            // it survives an artist offsetting the whole chain from the root.
            Vector3 pitch = linkCount >= 2
                ? chain[1].transform.localPosition - firstLink.localPosition
                : firstLink.localPosition;
            float pitchWorld = root.TransformVector(pitch).magnitude;
            if (pitchWorld <= 0.0001f)
            {
                Debug.LogWarning($"[HangingChain] '{name}' has coincident chain links, so the pitch is zero. Not extending.", this);
                return;
            }

            // Chain links alternate 90° about the chain axis. Read both poses off the
            // authored links rather than hardcoding the angle, so a differently-authored link
            // (a rope, a flat strap) still repeats correctly.
            Quaternion rotOdd = firstLink.localRotation;
            Quaternion rotEven = linkCount >= 2 ? chain[1].transform.localRotation : Quaternion.identity;
            Quaternion RotForSlot(int slot) => (slot & 1) == 1 ? rotOdd : rotEven;

            // ---- 3. How many links, and how they split ----------------------------------
            int add = Mathf.FloorToInt(Mathf.Min(extraDrop, maxExtraDrop) / pitchWorld);
            if (add <= 0) return;
            int dyn = Mathf.Clamp(dynamicLinks, 0, add);
            int stat = add - dyn;

            // ---- 4. Drop the whole assembly ---------------------------------------------
            // EVERY child moves, not just the chain: the point light and the audio source sit
            // at fixed local positions beside the cage, so shifting the lot keeps them with it
            // and needs no per-child special cases. The root itself stays at the ceiling — it
            // IS the anchor link.
            Vector3 shift = pitch * add;
            foreach (Transform child in root) child.localPosition += shift;

            // The authored links have moved down `add` slots, so their alternation has to be
            // re-derived or two neighbouring links end up lying in the same plane.
            for (int i = 0; i < linkCount; i++)
                chain[i].transform.localRotation = RotForSlot(add + i + 1);

            // ---- 5. Dynamic links fill the BOTTOM of the added section -------------------
            // TRANSFORM FIRST, THEN connectedBody. A HingeJoint with autoConfigureConnectedAnchor
            // computes its anchor from the CURRENT relative transforms at the moment the joint
            // is bound, so positioning after wiring would anchor every link where its template
            // used to be.
            Rigidbody previous = null;                      // null = anchored to the world
            for (int slot = stat + 1; slot <= add; slot++)
            {
                var clone = Instantiate(template.gameObject, root, false);
                clone.name = $"{template.name}_ext{slot}";
                clone.transform.localPosition = pitch * slot;
                clone.transform.localRotation = RotForSlot(slot);

                var joint = clone.GetComponent<HingeJoint>();
                var body = clone.GetComponent<Rigidbody>();
                if (joint == null || body == null)
                {
                    Debug.LogWarning($"[HangingChain] '{name}' link template has no HingeJoint/Rigidbody to copy. Not extending further.", this);
                    Destroy(clone);
                    break;
                }
                // Instantiate copies the template's connectedBody, which still points at the
                // ORIGINAL chain — references outside the copied hierarchy are not remapped.
                joint.connectedBody = previous;
                previous = body;
            }

            // Hand the authored chain over to the new bottom link. With dynamicLinks at 0 this
            // re-assigns null, which is what the assembly already had.
            chain[0].connectedBody = previous;

            // ---- 6. Force every joint to re-anchor --------------------------------------
            // A body-to-body anchor is stored in the CONNECTED body's local space, so links
            // that moved together are still correct — but the top link's anchor is in WORLD
            // space while it hangs off nothing, and that one is now stale by the full drop.
            // Rather than reason about which joints are affected (the answer changes with
            // dynamicLinks), re-assert auto-configure on all of them: the setter recomputes.
            // Getting this wrong does not error, it snaps the chain back to the ceiling.
            foreach (var j in root.GetComponentsInChildren<HingeJoint>(true))
            {
                if (!j.autoConfigureConnectedAnchor) continue;
                j.autoConfigureConnectedAnchor = false;
                j.autoConfigureConnectedAnchor = true;
            }

            // ---- 7. Static fill above them ----------------------------------------------
            int emitted = EmitStatic(root, template, pitch, RotForSlot, stat, instancer);

            if (debugChain)
                Debug.Log($"[HangingChain] '{name}' +{add} links ({stat} static, {dyn} dynamic) " +
                          $"= {add * pitchWorld:0.00}m of {extraDrop:0.00}m asked, pitch {pitchWorld:0.000}m." +
                          (emitted != stat ? $"  STATIC FILL FELL BACK TO {emitted} GameObjects (no instancer)." : ""), this);
        }

        /// <summary>
        /// The taut upper section. Instanced, because it never moves — one batch for every
        /// cage in the dungeon, since BatchKey is (mesh, submesh, material, castShadows) and
        /// knows nothing about which prop asked.
        ///
        /// Returns how many were emitted through the instancer; a shortfall means it fell back
        /// to plain GameObjects, which is correct in PrefabKit/greybox mode where there is no
        /// instancer at all and a missing chain would read as a bug.
        /// </summary>
        int EmitStatic(Transform root, Transform template, Vector3 pitch,
                       System.Func<int, Quaternion> RotForSlot, int count, InstancedDungeonRenderer instancer)
        {
            if (count <= 0) return 0;

            if (instancer == null)
            {
                for (int slot = 1; slot <= count; slot++)
                {
                    var clone = Instantiate(template.gameObject, root, false);
                    clone.name = $"{template.name}_fill{slot}";
                    clone.transform.localPosition = pitch * slot;
                    clone.transform.localRotation = RotForSlot(slot);
                    // Static: strip the physics so it cannot join the simulation.
                    if (clone.TryGetComponent(out HingeJoint j)) Destroy(j);
                    if (clone.TryGetComponent(out Rigidbody rb)) Destroy(rb);
                }
                return 0;
            }

            // Follow the ART rather than a new field: if the link is authored not to cast, the
            // filler does not either. Thin chain links inside a torch's 6-face cubemap are the
            // exact geometry roadmap 28a wants trimmed, so this must stay steerable.
            var mr = template.GetComponentInChildren<MeshRenderer>(true);
            bool casts = mr == null || mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;

            // AddInstance COMPOSES THE "PREFAB" ROOT'S OWN ROTATION AND SCALE INTERNALLY — the
            // same invariant PropInstancer carries (§5), and here the "prefab" is a live link
            // that is already rotated into the chain. So its world rotation has to be divided
            // back out of the placement, or every filler link is rotated twice. Scale is left
            // to the composed part matrix for the same reason.
            Quaternion templateInverse = Quaternion.Inverse(template.rotation);
            for (int slot = 1; slot <= count; slot++)
            {
                Vector3 pos = root.TransformPoint(pitch * slot);
                Quaternion rot = root.rotation * RotForSlot(slot) * templateInverse;
                instancer.AddInstance(template.gameObject, Matrix4x4.TRS(pos, rot, Vector3.one), casts);
            }
            return count;
        }

        /// <summary>
        /// Walks the hinge graph from the one joint anchored to the world down to the load.
        /// Exact rather than a search: a chain is defined by its joints, so nothing here has to
        /// guess from names, order or geometry.
        /// </summary>
        static bool ResolveChain(Transform root, out List<HingeJoint> chain)
        {
            chain = new List<HingeJoint>();
            var joints = root.GetComponentsInChildren<HingeJoint>(true);
            if (joints.Length == 0) return false;

            HingeJoint top = null;
            foreach (var j in joints)
            {
                if (j.connectedBody != null) continue;
                if (top != null) return false;   // two world anchors is not a single chain
                top = j;
            }
            if (top == null) return false;

            chain.Add(top);
            var current = top.GetComponent<Rigidbody>();
            while (current != null)
            {
                HingeJoint next = null;
                foreach (var j in joints)
                    if (j.connectedBody == current) { next = j; break; }
                if (next == null) break;
                chain.Add(next);
                current = next.GetComponent<Rigidbody>();
            }
            return chain.Count >= 2;   // at least one link plus the load
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// How a prop's mesh and function are split for rendering + gameplay.
    /// </summary>
    public enum PropTier
    {
        /// <summary>Mesh instanced, no GameObject. Pure static décor (rubble, bones, chains).</summary>
        StaticDecor,
        /// <summary>Mesh instanced + a GameObject carrying the prefab's colliders (and any
        /// non-Light/MeshRenderer components). Static obstacles, arches, gates.</summary>
        StaticCollider,
        /// <summary>Mesh instanced + a GameObject carrying colliders AND Lights/flicker/etc.
        /// The light stays individual (culled + animated); only the mesh batches. Torches.</summary>
        InstancedMeshWithLight,
        /// <summary>Whole prefab as a GameObject, nothing instanced. Moving/interactive
        /// props (doors, chests, levers) — few in number, so no draw-call concern.</summary>
        FullGameObject,
    }

    /// <summary>
    /// One placement of a prop: where and how it's oriented, plus a hook to
    /// configure the spawned functional GameObject (set light color, mark a
    /// door locked, attach a marker) when the tier produces one.
    /// </summary>
    public struct PropPlacement
    {
        public Vector3 position;      // world meters
        public Quaternion rotation;   // world (BEFORE the prefab's own root rotation is composed)
        public System.Action<GameObject> configure; // optional; receives the functional GameObject
    }

    /// <summary>
    /// The one place props become renderable. Splits any prefab into:
    ///   - MESH parts  -> InstancedDungeonRenderer (batched, chunked, distance-culled)
    ///   - FUNCTION    -> a lightweight GameObject holding colliders / lights / logic
    /// per the chosen PropTier. Every prop type (torches, arches, gates, and all
    /// future décor) routes through here, so batching + culling is automatic and
    /// adding a prop is "call PlaceProps", not "solve rendering again".
    ///
    /// The instancer's Commit() is idempotent/additive, so call this after the
    /// kit's own instancing and then Commit once more.
    /// </summary>
    public static class PropInstancer
    {
        public static void PlaceProps(
            InstancedDungeonRenderer instancer,
            GameObject prefab,
            IEnumerable<PropPlacement> placements,
            PropTier tier,
            float cellSize,       // unused directly; kept for signature symmetry with kit placers
            Transform functionalParent)
        {
            if (prefab == null) return;

            Quaternion rootRot = prefab.transform.rotation;

            foreach (var pl in placements)
            {
                // --- Mesh: instanced (all tiers except FullGameObject) ---
                if (tier != PropTier.FullGameObject && instancer != null)
                {
                    // Pass ONLY the placement rotation. AddInstance/BuildProto
                    // composes the prefab root's own rotation internally (same
                    // as every kit piece), so applying rootRot here too would
                    // double it — the bug that left instanced meshes mis-rotated
                    // while the functional GameObject (which applies rootRot
                    // once, below) sat correct.
                    Matrix4x4 m = Matrix4x4.TRS(pl.position, pl.rotation, Vector3.one);
                    instancer.AddInstance(prefab, m);
                }

                // --- Function: a GameObject, when the tier needs one ---
                bool needsGO =
                    tier == PropTier.FullGameObject ||
                    tier == PropTier.StaticCollider ||
                    tier == PropTier.InstancedMeshWithLight;

                if (needsGO)
                {
                    GameObject go = BuildFunctional(prefab, pl, rootRot, tier, functionalParent);
                    pl.configure?.Invoke(go);
                }
                else
                {
                    // StaticDecor: no GameObject at all, but still allow config
                    // (rare — e.g. registering a position). configure gets null.
                    pl.configure?.Invoke(null);
                }
            }
        }

        /// <summary>
        /// Builds the functional GameObject for a placement. For FullGameObject
        /// it's just Instantiate. For the split tiers it instantiates the prefab
        /// then strips the mesh renderers (those are instanced instead), keeping
        /// colliders always, and Lights only for InstancedMeshWithLight.
        /// </summary>
        static GameObject BuildFunctional(GameObject prefab, PropPlacement pl, Quaternion rootRot,
                                          PropTier tier, Transform parent)
        {
            var go = Object.Instantiate(prefab, pl.position, pl.rotation * rootRot, parent);

            if (tier == PropTier.FullGameObject)
                return go; // keep everything

            // Split tiers: the mesh is drawn by the instancer, so remove the
            // visual components from this GameObject to avoid double-drawing.
            bool keepLights = tier == PropTier.InstancedMeshWithLight;

            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                Object.Destroy(mr);
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                Object.Destroy(mf);

            if (!keepLights)
            {
                foreach (var li in go.GetComponentsInChildren<Light>(true))
                    Object.Destroy(li);
                foreach (var tf in go.GetComponentsInChildren<TorchFlicker>(true))
                    Object.Destroy(tf);
            }

            // Colliders are always kept (that's the whole point of the collider
            // tiers). Any custom logic components ride along untouched.
            return go;
        }
    }
}
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The grate over a crawlway mouth: press E to wrench it off, once, permanently.
    ///
    /// IT ALWAYS FALLS INTO THE OPEN CELL, whichever side you break it from. Standing in the
    /// room it drops toward you; standing in the bore it is shoved out ahead of you. Those read
    /// as opposite actions and are the same motion, which is worth stating because the obvious
    /// implementation — push it away from the player — is WRONG: from inside the pipe that
    /// drives the grate into the room correctly, but from the room it drives a heavy iron
    /// grating INTO the 1.5m passage you are about to crawl down, where it fits badly, blocks
    /// the way and cannot be pushed aside because you have no room to work. The bore is the one
    /// place it must never end up, so direction is a property of the MOUTH, not of the player.
    ///
    /// TIER: the mouth is placed as <see cref="PropTier.FullGameObject"/> because of this
    /// component. An instanced tier bakes the mesh into a static matrix and
    /// InstancedDungeonRenderer has NO REMOVAL PATH (§8), so a detached grate's mesh would stay
    /// welded across the opening while its collider fell away — the same rule carryables and
    /// destructibles follow. Mouths number in single digits per run, so the batching loss is
    /// nothing.
    ///
    /// THE FRAME IS NOT THE GRATE. The mouth prefab also carries the ring of collision that
    /// replaces the suppressed 3m wall quad (§5) — destroy that and you open a hole in the world
    /// either side of the opening. Only the child assigned to <see cref="grate"/> ever detaches.
    /// </summary>
    public class CrawlwayGrate : MonoBehaviour, IInteractable
    {
        [Header("Parts")]
        [Tooltip("The moving part ONLY — the bars that fall away. NOT the frame: the frame carries the collision that stands in for the 3m wall quad this mouth suppressed, and detaching it opens a hole in the rock either side of the opening.\n\nLeave empty to use this GameObject, which is right when the component sits on the grate child itself.")]
        public Transform grate;

        [Tooltip("Collider that blocks the passage while the grate is INTACT — typically a concave MeshCollider following the bars, so arrows fly through the gaps. Disabled when it breaks.")]
        public Collider blockingCollider;

        [Tooltip("Collider used once the grate is LOOSE. Author a disabled BoxCollider roughly the size of the bars and put it here.\n\nWHY IT MUST BE A DIFFERENT COLLIDER: PhysX rejects a concave MeshCollider on a non-kinematic Rigidbody, so the shape that lets arrows through the bars cannot be the shape that falls. Leaving it null auto-fits a box from the renderer bounds, which is fine — but authoring one is better, because you can inset it so the grate lies flat rather than balancing on a bounding box that includes the frame lugs.\n\nDELIBERATELY NOT 'flip the MeshCollider to convex at runtime', which is the obvious fix and is a BUILD-ONLY TRAP: cooking a hull needs mesh DATA, so a grate mesh without Read/Write Enabled cooks fine in the editor and fails in a player build. DestructibleProp carries the same warning after learning it the hard way. A primitive needs no cooking at all.")]
        public Collider brokenCollider;

        [Header("Break")]
        [Tooltip("Mass of the freed grate. Iron bars: heavy enough to land with a thud and not skitter.")]
        public float mass = 30f;
        [Tooltip("Shove along the mouth's outward direction (into the open cell) as it comes free, in m/s.")]
        public float outwardSpeed = 1.6f;
        [Tooltip("Extra tumble, in m/s, so it lands on a corner rather than sliding away flat.")]
        public float tumble = 2.2f;
        [Tooltip("Seconds before the freed grate is allowed to sleep. Purely so it settles rather than jittering against the frame it just left.")]
        public float settleDelay = 4f;

        [Header("Noise")]
        [Tooltip("How loud breaking it is to NPCs, 0-1. Wrenching an iron grate out of stone is one of the loudest things the player can deliberately do, and it SHOULD carry — a secret route you open by making a racket is a real trade, not a free one.")]
        [Range(0f, 1f)] public float breakLoudness = 0.85f;

        [Tooltip("Prompt shown by PlayerInteractor.")]
        public string prompt = "Wrench off the grate";

        /// <summary>Set by DungeonKitPlacer: world direction from the mouth into the OPEN cell.</summary>
        public Vector3 OutwardDirection { get; set; } = Vector3.forward;

        public bool IsOpen { get; private set; }

        public string Prompt => IsOpen ? null : prompt;

        public void Interact(Transform interactor)
        {
            if (IsOpen) return;
            Break();
        }

        /// <summary>
        /// Free the grate. Public so a future damage path (or an NPC that wants through) can
        /// call it without going via the interaction system.
        /// </summary>
        public void Break()
        {
            if (IsOpen) return;
            IsOpen = true;

            Transform part = grate != null ? grate : transform;

            // Off the frame first, so the physics body isn't fighting a parent transform that
            // the kit placed and never moves.
            part.SetParent(part.parent != null ? part.parent.parent : null, true);

            // COLLIDER SWAP BEFORE THE RIGIDBODY, and the order is not cosmetic: PhysX rejects a
            // concave MeshCollider on a non-kinematic body, so adding the Rigidbody while the
            // mesh collider is still live logs an error and leaves the grate colliding with
            // nothing — it falls through the floor. Disabling the blocker also clears the
            // passage before the body wakes, so the grate cannot wedge in the opening it just
            // left.
            SwapToDynamicCollider(part);

            var body = part.GetComponent<Rigidbody>();
            if (body == null) body = part.gameObject.AddComponent<Rigidbody>();
            body.mass = mass;
            body.isKinematic = false;
            body.useGravity = true;

            // Straight out into the open cell, plus a tumble. Velocity rather than a force so
            // the result does not depend on mass — a heavier grate should land harder, not
            // travel less far.
            Vector3 outward = OutwardDirection.sqrMagnitude > 0.001f
                ? OutwardDirection.normalized : part.forward;
            body.linearVelocity = outward * outwardSpeed;
            body.angularVelocity = Vector3.Cross(Vector3.up, outward) * tumble;
            body.sleepThreshold = 0f;
            CancelInvoke(nameof(AllowSleep));
            Invoke(nameof(AllowSleep), settleDelay);

            // A grate coming out of stone is loud, and this is the ONE place the crawlway system
            // touches the AI: opening a secret route announces you. Emitted through NoiseBus so
            // nothing here has to know NPCs exist (§10's mutually-ignorant emitters).
            NoiseBus.Emit(part.position, breakLoudness, part, Faction.Neutral);

            // ImpactAudio on the grate then handles the landing clang for free — it is speed
            // driven, so a grate that drops a foot ticks and one that is shoved clatters.
        }

        /// <summary>
        /// Retire the intact shape and bring up one a dynamic body may legally use.
        ///
        /// The two shapes answer different questions and cannot be the same collider. INTACT,
        /// the grate wants to follow the bars exactly, so a shot arrow passes between them —
        /// that means concave, which is only legal on static geometry. LOOSE, it needs a shape
        /// PhysX will simulate, which means convex or primitive. Once it is lying on the floor
        /// nobody is shooting through it, so a box loses nothing.
        /// </summary>
        void SwapToDynamicCollider(Transform part)
        {
            if (blockingCollider != null) blockingCollider.enabled = false;

            if (brokenCollider != null)
            {
                brokenCollider.enabled = true;
                return;
            }

            // Auto-fit from the RENDERER's local bounds. Safe in a build, unlike cooking a
            // convex hull: bounds are metadata and readable on any mesh, while vertex data is
            // what Read/Write Enabled gates.
            var box = part.gameObject.AddComponent<BoxCollider>();
            var renderer = part.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                // World bounds mapped back into the part's own space, so a rotated mouth still
                // fits its grate rather than getting an axis-aligned slab.
                Bounds b = renderer.bounds;
                box.center = part.InverseTransformPoint(b.center);
                Vector3 lossy = part.lossyScale;
                box.size = new Vector3(
                    b.size.x / Mathf.Max(0.0001f, Mathf.Abs(lossy.x)),
                    b.size.y / Mathf.Max(0.0001f, Mathf.Abs(lossy.y)),
                    b.size.z / Mathf.Max(0.0001f, Mathf.Abs(lossy.z)));
            }

            if (warnedAutoBox) return;
            warnedAutoBox = true;
            Debug.LogWarning(
                $"[Grate] '{name}' has no brokenCollider, so a BoxCollider was fitted from the renderer " +
                "bounds at runtime. That works, but author a disabled BoxCollider on the bars and assign " +
                "it instead: a bounds-fitted box includes the frame lugs, so the grate tends to land " +
                "balanced on a corner rather than lying flat.", this);
        }

        static bool warnedAutoBox;

        void AllowSleep()
        {
            Transform part = grate != null ? grate : transform;
            var body = part != null ? part.GetComponent<Rigidbody>() : null;
            if (body != null) body.sleepThreshold = 0.005f;
        }
    }
}

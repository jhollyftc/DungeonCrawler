using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DungeonGen
{
    /// <summary>
    /// The way OUT of a run's dungeon and into the next, deeper one — the placeholder
    /// for the eventual ladder/hatch. Authored as a prop in the EXIT room (see the
    /// authoring note below), interacted with via the existing PlayerInteractor/E path.
    ///
    /// It doesn't need new machinery: FirstPersonController's PgUp/PgDn debug keys
    /// already prove the route. `DungeonVisualizer.PendingDepth`/`PendingSeed` are
    /// statics consumed inside Generate() BEFORE the generator is built (depth drives
    /// room count and grid size), so they survive the scene reload; the player then
    /// spawns wherever DungeonPlayerSpawner.spawnRoomType points, which is Start.
    /// This component is just the diegetic trigger for that.
    ///
    /// SEED: `randomizeSeedOnGenerate` defaults to FALSE, so leaving the seed alone
    /// would regenerate the SAME layout one depth deeper. This always sets a seed. The
    /// default DERIVES it from the current one, so a whole multi-depth run reproduces
    /// from its first seed — the roguelike convention, and it keeps golden rule 4's
    /// "same (seed, depth) → same dungeon" useful across a run instead of only within
    /// one floor. Turn that off for a fresh unpredictable seed each descent.
    ///
    /// AUTHORING: put this on the prop prefab and add it to the EXIT room's PropSet as
    /// a Feature, guaranteed x1, with tier `PropTier.FullGameObject` — interactives must
    /// NOT use an instanced tier, or the mesh gets baked into a static matrix while only
    /// a collider GameObject is spawned (§8; the same rule that governs Carryables).
    /// </summary>
    [DisallowMultipleComponent]
    public class DungeonExitPortal : MonoBehaviour, IInteractable
    {
        [Header("Progression")]
        [Tooltip("Depths gained per use. 1 = the normal descent.")]
        public int depthIncrement = 1;
        [Tooltip("Refuse to go deeper than this. The portal reports itself as spent rather than silently doing nothing, so a test run has a visible end.")]
        public int maxDepth = 20;
        [Tooltip("Derive the next dungeon's seed from the current one, so an entire run replays from its FIRST seed. Off = a fresh random seed each descent (unpredictable, but you can't reproduce the run you just played).")]
        public bool deriveNextSeed = true;

        [Header("Presentation")]
        [Tooltip("Prompt shown by PlayerInteractor.")]
        public string prompt = "Descend";
        [Tooltip("Prompt once maxDepth is reached.")]
        public string spentPrompt = "The way down is sealed";
        [Tooltip("Seconds between interacting and the scene reloading — room for a sound or fade. 0 reloads instantly, which is jarring.")]
        public float activationDelay = 0.6f;

        [Tooltip("Log the depth/seed handoff. Very useful when testing progression — it prints the exact (seed, depth) that produced each dungeon in the chain.")]
        public bool debugPortal = true;

        /// <summary>Fired when used, before the reload — hook audio/fade/VFX here.</summary>
        public event Action<int> OnDescend;

        bool activating;   // guards a mashed E during activationDelay

        public string Prompt => AtMaxDepth ? spentPrompt : prompt;

        bool AtMaxDepth
        {
            get
            {
                var vis = FindObjectOfType<DungeonVisualizer>();
                return vis != null && vis.config.depth >= maxDepth;
            }
        }

        public void Interact(Transform interactor)
        {
            if (activating) return;

            var vis = FindObjectOfType<DungeonVisualizer>();
            if (vis == null)
            {
                Debug.LogWarning("[ExitPortal] No DungeonVisualizer in the scene — nothing to regenerate.", this);
                return;
            }

            int current = vis.config.depth;
            int next = Mathf.Min(current + Mathf.Max(1, depthIncrement), Mathf.Max(1, maxDepth));
            if (next == current)
            {
                if (debugPortal) Debug.Log($"[ExitPortal] Already at max depth {maxDepth}.", this);
                return;
            }

            activating = true;
            OnDescend?.Invoke(next);
            StartCoroutine(Descend(vis, next));
        }

        IEnumerator Descend(DungeonVisualizer vis, int nextDepth)
        {
            if (activationDelay > 0f) yield return new WaitForSeconds(activationDelay);

            // Consumed by Generate() before the generator is constructed.
            DungeonVisualizer.PendingDepth = nextDepth;
            DungeonVisualizer.PendingSeed = deriveNextSeed ? DeriveSeed(vis.seed, nextDepth) : NewSeed();

            if (debugPortal)
                Debug.Log($"[ExitPortal] depth {vis.config.depth} → {nextDepth}, " +
                          $"seed {vis.seed} → {DungeonVisualizer.PendingSeed}.", this);

            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        /// <summary>
        /// Mix the current seed with the next depth. Deterministic, so the whole run
        /// chains from one starting seed — and well-mixed, because a lazy `seed + depth`
        /// makes consecutive runs share most of their bits and start producing visibly
        /// similar dungeons.
        /// </summary>
        static int DeriveSeed(int seed, int depth)
        {
            unchecked
            {
                // uint throughout: the mixing constants are larger than int.MaxValue, and
                // >> on a uint is a LOGICAL shift — an arithmetic shift would drag sign
                // bits through the mix and weaken it. Cast back only at the end.
                uint h = (uint)seed ^ ((uint)depth * 0x9E3779B1u);
                h ^= h >> 16; h *= 0x85EBCA6Bu;
                h ^= h >> 13; h *= 0xC2B2AE35u;
                h ^= h >> 16;
                return (int)h;
            }
        }

        static int NewSeed() => UnityEngine.Random.Range(int.MinValue, int.MaxValue);
    }
}

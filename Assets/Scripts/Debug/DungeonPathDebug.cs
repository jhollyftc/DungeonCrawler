using UnityEngine;
using UnityEngine.AI;

namespace DungeonGen
{
    /// <summary>
    /// DEBUG: draws the walkable route to the Exit — either the full Start→Exit run or
    /// the player's current position→Exit.
    ///
    /// Deliberately pathed with NavMesh.CalculatePath rather than walking the room graph,
    /// because the room graph only says which rooms CONNECT; this says whether you can
    /// actually WALK there, through the real stairs and doorways. That distinction is the
    /// point of the tool: with Start on the lowest floor and Exit on the highest, the
    /// critical path is now the maximum vertical span on every seed, which is the hardest
    /// case for the stair-aware A* (roughly one staircase per level, each needing its
    /// 13-cell sealed envelope). A PARTIAL or INVALID path here means the exit is not
    /// reachable — reported in the line colour and a one-shot log, so a bad seed
    /// announces itself instead of being discovered by walking.
    ///
    /// Play-mode only, and it holds a runtime generator reference (same shape as
    /// DungeonFogController / DungeonMapper). The navmesh must be baked — DungeonNavBaker
    /// rebuilds it at the end of BuildMesh().
    /// </summary>
    [DisallowMultipleComponent]
    public class DungeonPathDebug : MonoBehaviour
    {
        public enum PathMode
        {
            /// <summary>The full run: Start room → Exit room. What the level designer cares about.</summary>
            StartToExit,
            /// <summary>Where the player is now → Exit. "Which way is out from here?"</summary>
            PlayerToExit,
        }

        [Header("Path")]
        public PathMode mode = PathMode.StartToExit;
        [Tooltip("Show/hide the path.")]
        public KeyCode toggleKey = KeyCode.P;
        public bool visible = true;
        [Tooltip("Seconds between recomputes. CalculatePath isn't free and the answer barely changes frame to frame; in PlayerToExit it also re-runs whenever you move far enough (see recomputeMoveDistance).")]
        public float refreshInterval = 0.5f;
        [Tooltip("In PlayerToExit, recompute early once the player has moved this far (m) since the last path — so the line keeps up when you're actually walking without paying for it while you stand still.")]
        public float recomputeMoveDistance = 2f;

        [Header("Look")]
        [Tooltip("Lift the line this far (m) off the floor so it doesn't z-fight the ground.")]
        public float heightOffset = 0.35f;
        public float lineWidth = 0.12f;
        [Tooltip("Colour for a COMPLETE path.")]
        public Color validColor = new Color(0.3f, 1f, 0.5f, 1f);
        [Tooltip("Colour for a PARTIAL or INVALID path — the exit can't be reached from here. This is the failure the tool exists to catch.")]
        public Color brokenColor = new Color(1f, 0.25f, 0.2f, 1f);
        [Tooltip("Colour when the route only works by CLIMBING A LADDER. Ladders are a scripted climb (LadderClimbZone), not navmesh, so no NavMeshAgent can follow it — meaning NPCs cannot reach you along this route. Worth seeing distinctly rather than lumping in with a normal path.")]
        public Color ladderRouteColor = new Color(1f, 0.85f, 0.25f, 1f);

        [Header("Particles (optional)")]
        [Tooltip("Optional ParticleSystem emitted along the route. MUST have Simulation Space = World (positions are emitted in world space). Left empty, only the line draws — the line needs no assets, so it always works.")]
        public ParticleSystem particles;
        [Tooltip("Spacing (m) between emitted particles along the path.")]
        public float particleSpacing = 1.5f;
        [Tooltip("Seconds between particle bursts along the path.")]
        public float particleInterval = 0.25f;

        DungeonVisualizer vis;
        Transform player;
        LineRenderer line;
        NavMeshPath path;

        float nextRefresh;
        float nextParticle;
        Vector3 lastPathOrigin;
        bool lastPathComplete = true;
        bool warnedThisDungeon;
        bool warnedLadderThisDungeon;
        DungeonGenerator cachedGen;

        /// <summary>False when the last computed route could NOT reach the exit.</summary>
        public bool ExitReachable => lastPathComplete;

        void Awake()
        {
            vis = GetComponent<DungeonVisualizer>();
            path = new NavMeshPath();
            BuildLine();
        }

        /// <summary>
        /// The line is built in code so the tool is drop-on-and-go — no prefab, no
        /// material to author. Uses whichever unlit shader this pipeline provides.
        /// </summary>
        void BuildLine()
        {
            var go = new GameObject("DungeonPathDebug_Line");
            go.transform.SetParent(transform, false);
            line = go.AddComponent<LineRenderer>();

            // Sprites/Default FIRST: it honours VERTEX colours, which is how a
            // LineRenderer's startColor/endColor reach the screen. URP/Unlit ignores
            // vertex colour entirely, so the line rendered plain white and the
            // valid/ladder/broken states were indistinguishable (real bug). The material
            // colour is also set explicitly in DrawSegments as a belt-and-braces.
            Shader sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color");
            if (sh != null) line.material = new Material(sh);

            line.widthMultiplier = lineWidth;
            line.useWorldSpace = true;
            line.numCapVertices = 4;
            line.numCornerVertices = 4;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.positionCount = 0;
        }

        void Update()
        {
            if (!Application.isPlaying || vis == null || vis.Generator == null) return;

            if (Input.GetKeyDown(toggleKey)) visible = !visible;
            if (!ReferenceEquals(vis.Generator, cachedGen))
            {
                cachedGen = vis.Generator;
                warnedThisDungeon = false;   // a new dungeon gets its own one-shot warnings
                warnedLadderThisDungeon = false;
                nextRefresh = 0f;
            }

            if (!visible)
            {
                if (line != null) line.enabled = false;
                return;
            }
            if (line != null) line.enabled = true;

            bool due = Time.time >= nextRefresh;
            if (!due && mode == PathMode.PlayerToExit && player != null)
                due = (player.position - lastPathOrigin).sqrMagnitude
                      > recomputeMoveDistance * recomputeMoveDistance;

            if (due)
            {
                nextRefresh = Time.time + Mathf.Max(0.05f, refreshInterval);
                Recompute();
            }

            if (particles != null && Time.time >= nextParticle && line.positionCount > 1)
            {
                nextParticle = Time.time + Mathf.Max(0.02f, particleInterval);
                EmitAlongPath();
            }
        }

        void Recompute()
        {
            var gen = cachedGen;
            Room exitRoom = FindRoom(gen, RoomType.Exit);
            if (exitRoom == null) { line.positionCount = 0; return; }

            Vector3 to = CellWorld(exitRoom.InteriorFloorCell);

            Vector3 from;
            if (mode == PathMode.PlayerToExit)
            {
                if (player == null)
                {
                    var fpc = FindObjectOfType<FirstPersonController>();
                    if (fpc == null) { line.positionCount = 0; return; }
                    player = fpc.transform;
                }
                from = player.position;
            }
            else
            {
                Room startRoom = FindRoom(gen, RoomType.Start);
                if (startRoom == null) { line.positionCount = 0; return; }
                from = CellWorld(startRoom.InteriorFloorCell);
            }
            lastPathOrigin = from;

            // Snap both ends onto the navmesh first — a room's nominal floor centre can
            // sit slightly off the baked surface, and CalculatePath silently fails rather
            // than snapping for you.
            if (!NavMesh.SamplePosition(from, out NavMeshHit a, vis.cellSize * 2f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(to, out NavMeshHit b, vis.cellSize * 2f, NavMesh.AllAreas))
            {
                line.positionCount = 0;
                Report(false, "could not sample the navmesh at one end (is the navmesh baked?)");
                return;
            }

            // 1) Straight navmesh route.
            if (TryNavPath(a.position, b.position, out var direct))
            {
                DrawSegments(new System.Collections.Generic.List<Vector3[]> { direct }, validColor);
                Report(true, "direct navmesh route");
                return;
            }

            // 2) Route THROUGH LADDERS. Ladders are a scripted climb (LadderClimbZone),
            // not navmesh geometry, so CalculatePath can never cross one — on a seed
            // whose only way up is a ladder the direct test above fails even though the
            // player can walk it. Hop navmesh islands via the generator's own ladder
            // list instead of declaring the exit unreachable.
            if (TryLadderRoute(a.position, b.position, out var viaLadders))
            {
                DrawSegments(viaLadders, ladderRouteColor);
                lastPathComplete = true;
                if (!warnedLadderThisDungeon)
                {
                    warnedLadderThisDungeon = true;
                    Debug.LogWarning(
                        $"[PathDebug] Route to the exit requires a LADDER ({viaLadders.Count} legs). " +
                        "Ladders are a scripted climb (LadderClimbZone), NOT navmesh, so no NavMeshAgent " +
                        "can follow it — NPCs cannot reach the exit along this route. Fixing that needs " +
                        $"NavMeshLinks at ladders plus off-mesh-link handling in NpcLocomotion. " +
                        $"Seed {vis.seed}, depth {vis.config.depth}.", this);
                }
                return;
            }

            // 3) Genuinely unreachable — draw whatever partial route exists so the dead
            // end is visible rather than the line just vanishing.
            NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path);
            DrawSegments(new System.Collections.Generic.List<Vector3[]> { path.corners }, brokenColor);
            Report(false, $"no route even through ladders (navmesh status {path.status})");
        }

        bool TryNavPath(Vector3 from, Vector3 to, out Vector3[] corners)
        {
            corners = null;
            path.ClearCorners();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path)) return false;
            if (path.status != NavMeshPathStatus.PathComplete) return false;
            corners = path.corners;
            return true;
        }

        /// <summary>
        /// Breadth-first hop across navmesh islands joined by ladders: nodes are the
        /// source, the exit, and each ladder's foot and head; an edge is a COMPLETE
        /// navmesh path, plus the free climb linking a ladder's own foot to its head.
        /// Fewest-ladders wins. Ladder counts are tiny (only drop-ins that failed to get
        /// an interior stair), so the pairwise path queries stay cheap.
        /// </summary>
        bool TryLadderRoute(Vector3 from, Vector3 to, out System.Collections.Generic.List<Vector3[]> segments)
        {
            segments = null;
            var ladders = cachedGen.Ladders;
            if (ladders.Count == 0) return false;

            // Node layout: 0 = source, 1 = exit, then foot/head per ladder.
            int nodeCount = 2 + ladders.Count * 2;
            var pos = new Vector3[nodeCount];
            pos[0] = from;
            pos[1] = to;
            for (int i = 0; i < ladders.Count; i++)
            {
                var lad = ladders[i];
                Vector3 foot = CellWorld(lad.BaseCell);

                // The head is the HALLWAY cell through the doorway (+WallDir), NOT the
                // room-side threshold. That threshold sits in the room's open vertical
                // volume with no floor under it, so it carries no navmesh — and sampling
                // it snapped back DOWN to the room floor, putting both ladder nodes on
                // the same island so the link bought nothing. WallDir is -door.Direction,
                // so +WallDir steps out of the room onto the corridor floor you actually
                // arrive on. (Real bug: ladders were silently never routed through.)
                Vector3 head = CellWorld(lad.BaseCell + Vector3Int.up * lad.HeightCells + lad.WallDir);

                // Tight sample radius for the same reason — a generous one can snap a
                // point onto a DIFFERENT storey, which is exactly the failure above.
                float r = vis.cellSize * 0.5f;
                if (NavMesh.SamplePosition(foot, out NavMeshHit fh, r, NavMesh.AllAreas)) foot = fh.position;
                if (NavMesh.SamplePosition(head, out NavMeshHit hh, r, NavMesh.AllAreas)) head = hh.position;
                pos[2 + i * 2] = foot;
                pos[3 + i * 2] = head;
            }

            var prev = new int[nodeCount];
            var prevCorners = new Vector3[nodeCount][];
            var seen = new bool[nodeCount];
            for (int i = 0; i < nodeCount; i++) prev[i] = -1;

            var queue = new System.Collections.Generic.Queue<int>();
            queue.Enqueue(0);
            seen[0] = true;

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (cur == 1) break;

                for (int next = 0; next < nodeCount; next++)
                {
                    if (seen[next] || next == cur) continue;

                    Vector3[] corners;
                    if (IsSameLadder(cur, next))
                        corners = new[] { pos[cur], pos[next] };   // the climb itself
                    else if (!TryNavPath(pos[cur], pos[next], out corners))
                        continue;

                    seen[next] = true;
                    prev[next] = cur;
                    prevCorners[next] = corners;
                    queue.Enqueue(next);
                }
            }

            if (!seen[1]) return false;

            segments = new System.Collections.Generic.List<Vector3[]>();
            for (int v = 1; v != 0; v = prev[v]) segments.Insert(0, prevCorners[v]);
            return true;
        }

        /// <summary>The foot and head of ONE ladder — connected by climbing, not by navmesh.</summary>
        static bool IsSameLadder(int a, int b)
        {
            if (a < 2 || b < 2) return false;
            return (a - 2) / 2 == (b - 2) / 2;
        }

        void DrawSegments(System.Collections.Generic.List<Vector3[]> segments, Color color)
        {
            int total = 0;
            foreach (var s in segments) if (s != null) total += s.Length;

            line.positionCount = total;
            int w = 0;
            foreach (var s in segments)
            {
                if (s == null) continue;
                for (int i = 0; i < s.Length; i++) line.SetPosition(w++, s[i] + Vector3.up * heightOffset);
            }

            line.startColor = color;
            line.endColor = color;
            line.widthMultiplier = lineWidth;

            // Drive the MATERIAL colour too, not just the vertex colours — whether
            // vertex colour is honoured depends on the shader we happened to find, and
            // the state readout has to be legible regardless of which one that was.
            var mat = line.material;
            if (mat != null)
            {
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            }
        }

        /// <summary>
        /// One warning per dungeon when the exit isn't reachable — the thing worth
        /// catching. Kept to once so a broken seed doesn't flood the console every
        /// refresh, and re-armed when the dungeon regenerates.
        /// </summary>
        void Report(bool ok, string detail)
        {
            lastPathComplete = ok;
            if (ok || warnedThisDungeon) return;

            warnedThisDungeon = true;
            Debug.LogWarning(
                $"[PathDebug] EXIT NOT REACHABLE ({mode}) — {detail}. With Start on the lowest floor " +
                $"and Exit on the highest, the critical path is the full vertical span, which is the " +
                $"hardest case for the stair-aware pathfinder. Seed {vis.seed}, depth {vis.config.depth}.", this);
        }

        void EmitAlongPath()
        {
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            float spacing = Mathf.Max(0.1f, particleSpacing);

            for (int i = 1; i < line.positionCount; i++)
            {
                Vector3 p0 = line.GetPosition(i - 1);
                Vector3 p1 = line.GetPosition(i);
                float seg = Vector3.Distance(p0, p1);
                int steps = Mathf.Max(1, Mathf.RoundToInt(seg / spacing));
                for (int s = 0; s < steps; s++)
                {
                    ep.position = Vector3.Lerp(p0, p1, s / (float)steps);
                    particles.Emit(ep, 1);
                }
            }
        }

        static Room FindRoom(DungeonGenerator gen, RoomType type)
        {
            foreach (var r in gen.Rooms)
                if (r.Type == type) return r;
            return null;
        }

        /// <summary>Same cell→world convention the spawner, brain and fog controller use.</summary>
        Vector3 CellWorld(Vector3Int cell) =>
            vis.transform.position + new Vector3(cell.x + 0.5f, cell.y, cell.z + 0.5f) * vis.cellSize;
    }
}

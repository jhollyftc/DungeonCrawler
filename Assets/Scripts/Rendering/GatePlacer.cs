using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Spawns portcullises, locks the doors the generator chose, mounts the levers, and links
    /// each lever to its gate.
    ///
    /// RECORD-THEN-CONSUME, the `KitSocketPlacer` pattern. Every gate is spawned and registered
    /// into an index→gate map FIRST, and levers resolve their target in a second pass once all of
    /// them exist. That makes spawn order between this placer and `DungeonKitPlacer` (which owns
    /// the doors) irrelevant — otherwise a lever for a door would have to be spawned after the
    /// kit and a lever for a portcullis after this, and the ordering would be an invisible
    /// dependency waiting to break.
    ///
    /// RUNS BEFORE `TorchPlacer`. A lever occupies a wall face and must CLAIM it, or a sconce
    /// lands on top of the thing the player is meant to find — the same most-constrained-first
    /// rule that puts `RecessPropPlacer` where it is.
    ///
    /// ITS ROOTS MUST BE IN `DungeonVisualizer.GeneratedRoots`, or every regenerate stacks
    /// another dungeon's gates on the last one.
    /// </summary>
    public static class GatePlacer
    {
        /// <summary>Root name the navmesh baker must exclude — a portcullis baked closed walls
        /// its corridor off permanently, even once raised.</summary>
        public const string PortcullisRootName = "DungeonPortcullises";
        public const string LeverRootName = "DungeonLevers";

        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        /// <summary>Destroy that also works when generation is run from the editor's context
        /// menu, where deferred Destroy never runs.</summary>
        static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        /// <summary>
        /// Can a lever mount on this face?
        ///
        /// Three separate refusals, and the middle one is the whole reason this exists: the wall
        /// must be SOLID, the asset that landed there must accept WALL-MOUNTED things, and
        /// nothing may have claimed it already. `WallPropsAllowed` is deliberately not
        /// `PropsAllowed` — a recessed window is perfectly happy to have rubble on the floor in
        /// front of it while refusing anything bolted to its face, and collapsing the two would
        /// force the author to give up the floor to protect the wall.
        ///
        /// A CORRIDOR CELL HAS ONLY TWO SOLID FACES, so this rejects far more often than it
        /// looks: one wall refusing mounts and one already carrying an authored kit-socket torch
        /// is enough to rule the whole cell out. That is why the generator offers several
        /// candidate CELLS rather than trusting one.
        /// </summary>
        static bool FaceUsable(DungeonGenerator gen, WallFaceRegistry wallFaces, Vector3Int cell, Vector3Int dir)
        {
            Vector3Int wall = cell + dir;
            if (gen.Grid.InBounds(wall) && gen.Grid[wall] != CellType.Empty) return false;   // not solid
            if (wallFaces == null) return true;

            int idx = gen.Grid.Index(cell);
            return wallFaces.WallPropsAllowed(idx, dir) && !wallFaces.IsClaimed(idx, dir);
        }

        public static void Build(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                 WallFaceRegistry wallFaces = null)
        {
            if (gen.Gates.Count == 0) return;

            var gateRoot = new GameObject(PortcullisRootName);
            gateRoot.transform.SetParent(parent, false);
            var leverRoot = new GameObject(LeverRootName);
            leverRoot.transform.SetParent(parent, false);

            if (kit.leverPrefab == null)
            {
                Debug.LogWarning("[Gates] No leverPrefab in the kit — every gate would be unopenable, " +
                                 "so none are applied. The generator still sited them; fill the slot.");
                return;
            }

            // ---- Pass 1: the gates themselves.
            var gates = new Dictionary<int, IGateLock>();
            // THE SPAWNED ROOT, tracked separately from the component. A Portcullis may be
            // authored on a CHILD (the bars are the obvious place to put it), and destroying
            // `component.gameObject` then removes only that child — leaving the frame and its
            // collider standing in the corridor. Which is exactly how a neutralised gate can go
            // on blocking the way.
            var gateRoots = new Dictionary<int, GameObject>();
            var gateLevers = new Dictionary<int, List<GameObject>>();
            var doorMarkers = parent.GetComponentsInChildren<DungeonDoorMarker>(true);

            for (int i = 0; i < gen.Gates.Count; i++)
            {
                GateSpec g = gen.Gates[i];
                if (g.Kind == GateKind.LockedDoor)
                {
                    // Matched by CELL rather than by a door index threaded through the kit
                    // placer: the marker already carries hallwayCell and direction, so no extra
                    // wiring — and no second source of truth to fall out of step.
                    DungeonDoorMarker hit = null;
                    foreach (var m in doorMarkers)
                        if (m.hallwayCell == g.Cell && m.direction == g.Axis) { hit = m; break; }

                    if (hit == null)
                    {
                        Debug.LogWarning($"[Gates] Locked door at {g.Cell} has no spawned door to lock " +
                                         "— the kit's door slot for that opening is probably empty.");
                        continue;
                    }

                    // SEARCH THE CHILDREN. `DungeonDoorMarker` is added to the spawned ROOT, but
                    // a door prefab is normally a frame with the swinging LEAF beneath it, and
                    // PhysicsDoor lives on the leaf. GetComponent on the root therefore found
                    // nothing — and because the gate was then never registered, pass 2 also
                    // skipped every lever for it. The symptom was a door that stayed unlocked
                    // with candidate markers drawn and no lever anywhere, which points at lever
                    // siting rather than at the door lookup that actually failed.
                    var pd = hit.GetComponentInChildren<PhysicsDoor>(true);
                    if (pd == null)
                    {
                        Debug.LogWarning($"[Gates] Door at {g.Cell} has no PhysicsDoor anywhere in its " +
                                         "hierarchy, so it cannot be locked. Locking is a kinematic " +
                                         "rigidbody state; a scripted HingedDoor would need its own adapter.");
                        continue;
                    }
                    // On the SAME object as the PhysicsDoor, not the marker's root: DoorLock
                    // requires it, and it subscribes to that door's rattle event.
                    gates[i] = pd.gameObject.AddComponent<DoorLock>();
                }
                else
                {
                    if (kit.portcullisPrefab == null)
                    {
                        Debug.LogWarning("[Gates] No portcullisPrefab in the kit — corridor gates skipped.");
                        continue;
                    }

                    // Base-origin and floor-aligned, standing in an open cell — it replaces no
                    // wall, so globalVisualOffset does NOT apply (the crawlway tube convention,
                    // not the mouth one).
                    Quaternion rot = Quaternion.LookRotation((Vector3)g.Axis, Vector3.up);
                    Vector3 pos = new Vector3(g.Cell.x + 0.5f, g.Cell.y, g.Cell.z + 0.5f) * cellSize
                                + rot * kit.portcullisOffset + parent.position;

                    var go = Object.Instantiate(kit.portcullisPrefab, pos,
                                                rot * kit.portcullisPrefab.transform.rotation,
                                                gateRoot.transform);
                    var pc = go.GetComponentInChildren<Portcullis>(true);
                    if (pc == null)
                    {
                        Debug.LogWarning("[Gates] portcullisPrefab has no Portcullis component — it will " +
                                         "stand there permanently closed with no way to raise it.", go);
                        continue;
                    }
                    gates[i] = pc;
                    gateRoots[i] = go;

                    // CLAIM BOTH JAMBS. The kit has already picked a flat wall for these faces
                    // (gen.IsPortcullisJamb reaches the wall pick), but nothing yet stops a torch
                    // or a banner mounting on one — and the gate is pressed flat against them, so
                    // anything there is inside the bars. Claimed rather than merely denied, so
                    // TorchPlacer's IsClaimed guard refuses them independently of the flags.
                    if (wallFaces != null)
                    {
                        Vector3Int perp = new Vector3Int(g.Axis.z, 0, g.Axis.x);
                        int idx = gen.Grid.Index(g.Cell);
                        foreach (var side in new[] { perp, -perp })
                        {
                            wallFaces.Record(idx, side, allowProps: false, allowWallProps: false, allowTorch: false);
                            wallFaces.Claim(idx, side);
                        }
                    }
                }
            }

            // ---- Pass 2: levers, now that every gate exists.
            //
            // ONE PER SIDE from several candidates. The generator offers alternatives precisely
            // because it cannot see which wall asset landed on a face, so the first one that
            // survives the registry wins and the rest are ignored.
            int levers = 0;
            var nearPlaced = new HashSet<int>();
            foreach (var g in gen.Gates)
            {
                bool tookNear = false, tookFar = false;
                for (int si = 0; si < g.Levers.Count; si++)
                {
                    var spec = g.Levers[si];
                    if (!gates.TryGetValue(spec.GateIndex, out var target)) continue;
                    if (spec.NearSide ? tookNear : tookFar) continue;

                    // THE GENERATOR CANNOT KNOW WHICH WALL ASSET LANDED HERE. It sites levers
                    // from grid solidity alone, because the registry does not exist until the kit
                    // has emitted — so a lever could be mounted on a recessed window, a candle
                    // niche or barred relief, floating over the recess or buried in it.
                    //
                    // Resolved at PLACEMENT, where the registry does exist: keep the authored
                    // face if it is legal, otherwise try the cell's other faces before giving up.
                    // Staying in the SAME CELL preserves the path-distance band the generator
                    // enforced, which moving to a neighbour would quietly break.
                    Vector3Int wallDir = spec.WallDir;
                    if (!FaceUsable(gen, wallFaces, spec.Cell, wallDir))
                    {
                        bool found = false;
                        foreach (var alt in HDirs)
                        {
                            if (alt == spec.WallDir) continue;
                            if (!FaceUsable(gen, wallFaces, spec.Cell, alt)) continue;
                            wallDir = alt;
                            found = true;
                            break;
                        }
                        // Not a warning: the generator deliberately offers alternatives, so a
                        // rejected face is the normal path, not a fault. Only running out of ALL
                        // of them matters, and that is reported once per gate below.
                        if (!found) continue;
                    }

                    // On the wall face, facing out into the cell — the WallMounted convention.
                    Quaternion rot = Quaternion.LookRotation(-(Vector3)wallDir, Vector3.up);
                    Vector3 face = new Vector3(spec.Cell.x + 0.5f + wallDir.x * 0.5f,
                                               spec.Cell.y,
                                               spec.Cell.z + 0.5f + wallDir.z * 0.5f) * cellSize;
                    Vector3 pos = face
                                + Vector3.up * kit.leverMountHeight
                                - (Vector3)wallDir * kit.leverWallGap
                                + parent.position;

                    var go = Object.Instantiate(kit.leverPrefab, pos,
                                                rot * kit.leverPrefab.transform.rotation,
                                                leverRoot.transform);
                    var lever = go.GetComponentInChildren<Lever>(true);
                    if (lever == null)
                    {
                        Debug.LogWarning("[Gates] leverPrefab has no Lever component — it is scenery.", go);
                        continue;
                    }
                    lever.Link(target);

                    // CLAIM THE FACE so no torch or banner mounts over it. Recorded as denied for
                    // props too: a crate snapped in front of a lever hides the one thing the
                    // player is hunting for.
                    if (wallFaces != null)
                    {
                        wallFaces.Record(gen.Grid.Index(spec.Cell), wallDir,
                                         allowProps: false, allowWallProps: false, allowTorch: false);
                        wallFaces.Claim(gen.Grid.Index(spec.Cell), wallDir);
                    }
                    if (spec.NearSide) { tookNear = true; nearPlaced.Add(spec.GateIndex); }
                    else tookFar = true;

                    // Write back which candidate was actually BUILT, so the gizmo can show the
                    // lever rather than every face that was considered. LeverSpec is a struct,
                    // so the list entry has to be replaced, not mutated.
                    if (!gateLevers.TryGetValue(spec.GateIndex, out var lst))
                        gateLevers[spec.GateIndex] = lst = new List<GameObject>();
                    lst.Add(go);

                    spec.Placed = true;
                    spec.WallDir = wallDir;   // the face finally used, which may not be the one sited
                    g.Levers[si] = spec;
                    levers++;
                }
            }

            // ---- Pass 3: a gate with no NEAR lever must not be left armed.
            //
            // THIS IS THE SOFTLOCK GUARD, and its absence shipped one. The generator guarantees a
            // near-side lever EXISTS — a gate that cannot site one is dropped — but the placer
            // can still reject every candidate face, and the old code merely warned and carried
            // on. That left a portcullis across the only route out of Start with its single
            // lever on the far side: reachable only by passing the gate it opens.
            //
            // A gate is optional content and connectivity is not, so the gate yields. Neutralised
            // rather than merely opened, so nothing can toggle it shut again later.
            for (int i = 0; i < gen.Gates.Count; i++)
            {
                if (!gates.TryGetValue(i, out var gate) || nearPlaced.Contains(i)) continue;

                Debug.LogWarning($"[Gates] {gen.Gates[i].Label} at {gen.Gates[i].Cell} could not place a " +
                                 "NEAR-side lever on any candidate face — every one is unusable (not solid, " +
                                 "refuses wall mounts, or already claimed). Leaving the way OPEN rather than " +
                                 "shipping a gate that can only be opened from the far side.");

                if (gate is Portcullis)
                {
                    // THE TRACKED ROOT, not the component's GameObject. The Portcullis may be
                    // authored on a child (the bars are the natural place for it), and killing
                    // that child leaves the frame and its collider standing across the corridor
                    // — a "neutralised" gate that still blocks the way.
                    Kill(gateRoots.TryGetValue(i, out var root) ? root : null);
                }
                else if (gate is DoorLock dl)
                {
                    // UNLOCK BEFORE DESTROYING. DoorLock.Awake already made the door kinematic,
                    // and removing the component does not undo that — it would leave a door
                    // permanently immovable with nothing left in the scene explaining why.
                    dl.Toggle();
                    Kill(dl);
                }

                // And the far-side lever, which now drives a gate that no longer exists. Left
                // alone it would be a lever the player finds, pulls, and gets nothing from —
                // worse than no lever, because it reads as broken rather than absent.
                if (gateLevers.TryGetValue(i, out var orphans))
                    foreach (var lever in orphans) Kill(lever);
            }

            // PER-GATE COUNTS, because the total answers nothing. "2 gates, 3 levers" leaves the
            // only question that matters unanswered — WHICH side is the missing one on. A gate
            // with near 1 / far 0 is correct and openable; a gate with near 0 is a softlock and
            // should already have been neutralised above. Reporting the totals alone meant
            // walking the dungeon to find out which had happened.
            var sb = new System.Text.StringBuilder();
            sb.Append($"[Gates] {gates.Count} gate(s) armed, {levers} lever(s):");
            for (int i = 0; i < gen.Gates.Count; i++)
            {
                if (!gates.ContainsKey(i)) continue;
                int n = 0, f = 0;
                foreach (var lv in gen.Gates[i].Levers)
                {
                    if (!lv.Placed) continue;
                    if (lv.NearSide) n++; else f++;
                }
                // The near REGION size beside the lever counts: a near side of two or three cells
                // means the gate sits almost on top of Start, and a lever "on your side" then has
                // nowhere to be but the few tiles behind you.
                sb.Append($"\n  {gen.Gates[i].Label} @{gen.Gates[i].Cell} — near {n}, far {f}" +
                          $"  [near side {gen.Gates[i].NearCells.Count} cells, " +
                          $"cuts off {gen.Gates[i].CutOffCells}, origin {gen.Gates[i].ReachOrigin}]" +
                          (f == 0 ? "  (no far lever: fine, it is optional — it only matters for one-way "
                                  + "arrivals and for reopening a gate toggled shut from the other side)"
                                  : ""));
            }
            Debug.Log(sb.ToString());
        }
    }
}

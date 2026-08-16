using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// One recess to furnish. A prison and an alcove are the SAME generator primitive — both
    /// come out of `RecessFits`, both are validated dead-end pockets hanging off one hallway
    /// cell — so they are furnished by one pass rather than two that would drift.
    ///
    /// Only three pieces of geometry are needed, which is exactly why the generalisation is
    /// cheap: the cells, the direction that gives the pocket a back/left/right frame, and the
    /// mouth.
    /// </summary>
    public readonly struct RecessTarget
    {
        public readonly HashSet<Vector3Int> Cells;
        /// <summary>Corridor -> recess. The frame every Feature anchor needs.</summary>
        public readonly Vector3Int Direction;
        /// <summary>The doorway tile. Depth 0; a Feature sorts to the far end, away from here.</summary>
        public readonly Vector3Int MouthCell;
        /// <summary>
        /// Cells that may take DECOR but never a BLOCKING tier. Null = no restriction (alcoves).
        /// For a prison this holds the mouth, because that is where the bars or door are.
        /// </summary>
        public readonly HashSet<Vector3Int> NoBlocking;
        /// <summary>
        /// Contents for THIS recess, resolved from a 0..1 roll the placer supplies from its own
        /// stream. A delegate rather than a field so the shared placer never learns about
        /// AlcoveKind: an alcove target closes over its kind, a prison target ignores the kind
        /// entirely, and both still get their weighted per-cell variety from one code path.
        /// </summary>
        public readonly System.Func<float, PropSet> Contents;

        public RecessTarget(HashSet<Vector3Int> cells, Vector3Int direction, Vector3Int mouthCell,
                            System.Func<float, PropSet> contents, HashSet<Vector3Int> noBlocking = null)
        {
            Cells = cells; Direction = direction; MouthCell = mouthCell;
            Contents = contents; NoBlocking = noBlocking;
        }
    }

    /// <summary>
    /// Fills carved RECESSES — hallway alcoves and prison closets — with authored contents.
    /// Their cells already have walls, floor and ceiling from the corridor/prison kit paths;
    /// this pass supplies only what makes a pocket a statue nook or an occupied cell rather
    /// than a dead end.
    ///
    /// WHY ITS OWN PASS rather than an extension of HallwayPropPlacer or RoomPropPlacer:
    /// - RoomPropPlacer hard-gates on `grid[c] == CellType.Room` and needs zones, an entrance
    ///   axis and a centroid. A recess has none of those.
    /// - HallwayPropPlacer scatters ONE global set across every corridor cell. A recess needs
    ///   per-kind content and a hero prop, and alcove cells are deliberately excluded from that
    ///   pass so generic debris can't bury the thing the alcove exists to show. (Prison cells
    ///   were never in it — they aren't CellType.Hallway.)
    ///
    /// It reuses everything else unchanged: PropSet/PropEntry authoring, PropAnchor, PropTier,
    /// PropInstancer, PropSnap, and the WallFaceRegistry claim system.
    ///
    /// NO FLOOD FILL, deliberately — but the reason is narrower than it looks. A dead-end
    /// pocket cannot SEVER the dungeon, so a blocking prop inside it is safe. It can however
    /// SEAL ITSELF, and a crate in the doorway of a barred cell is indistinguishable from a
    /// generation bug. `RecessTarget.NoBlocking` is what buys the exemption: reserve the mouth,
    /// and the flood-fill genuinely has nothing left to check. Do NOT instead add these cells to
    /// the hallway pass's BFS "for safety" — that would let a blocking prop veto itself for no
    /// reason.
    ///
    /// RUNS BEFORE TorchPlacer (see DungeonVisualizer). A recess has about three wall faces and
    /// one authored hero prop, making it the most constrained consumer of wall real estate in
    /// the dungeon — §8's most-constrained-first rule — and it must claim its feature face
    /// before torches get to pick.
    /// </summary>
    public static class RecessPropPlacer
    {
        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        static int DirIdx(Vector3Int d) => d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : 3;

        // Own salt BLOCK PER CALLER, never a shared counter (golden rule 4): tuning prisons must
        // not reshuffle every alcove, and vice versa. Offsets within a block are fixed, so the
        // two callers differ only by base. 12002/12003/12005 are the hallway pass; 110xx rooms.
        const int AlcoveSaltBase = 12100;
        const int PrisonSaltBase = 12300;
        // A salt BLOCK per caller, never a shared counter — tuning chambers must not reshuffle
        // every alcove and prison in the dungeon (rule 4).
        const int ChamberSaltBase = 12500;
        const int FeatureOffset = 1;
        const int WallOffset = 2;
        const int ScatterOffset = 3;
        const int CeilingOffset = 4;
        const int VariantOffset = 5;   // which contents set this recess gets

        /// <summary>Hallway alcoves, contents by AlcoveKind (RoomStyle.alcoveStyles).</summary>
        public static GameObject BuildAlcoves(DungeonGenerator gen, RoomStyle style, float cellSize,
                                              Transform parent, InstancedDungeonRenderer instancer,
                                              WallFaceRegistry wallFaces = null)
        {
            // A crawlway mouth may land in an alcove — deliberately, since a grate at the back
            // of a collapsed dig is the best entrance the feature can have. But the alcove's own
            // hero prop stands against that same back wall, so without this the recess gets a
            // statue planted over its grate. Same NoBlocking mechanism a prison uses for its
            // doorway tile: décor is still welcome there, only colliders are refused.
            var crawlMouths = new HashSet<Vector3Int>();
            foreach (var cw in gen.Crawlways)
            {
                foreach (var m in cw.Mouths) crawlMouths.Add(m.OpenCell);
            }

            var targets = new List<RecessTarget>();
            foreach (var a in gen.Alcoves)
            {
                var kind = a.Kind;   // captured per target, so the placer never sees AlcoveKind
                HashSet<Vector3Int> noBlocking = null;
                foreach (var c in a.Cells)
                    if (crawlMouths.Contains(c)) (noBlocking ??= new HashSet<Vector3Int>()).Add(c);

                targets.Add(new RecessTarget(a.Cells, a.Direction, a.MouthCell,
                                             roll => style != null ? style.AlcoveProps(kind, roll) : null,
                                             noBlocking));
            }
            return Build(gen, style, cellSize, parent, instancer, wallFaces,
                         "DungeonAlcoveProps", "Alcoves", AlcoveSaltBase, targets);
        }

        /// <summary>
        /// Prison closets, contents from RoomStyle.prisonProps. Same machinery as alcoves; the
        /// only difference is the MOUTH RESERVATION — a blocking prop on the doorway tile would
        /// fight the bars or the door's swing, and in a 1x1 cell it would wall the cell shut.
        /// Decor is still allowed there (straw on the threshold is fine).
        /// </summary>
        public static GameObject BuildPrisons(DungeonGenerator gen, RoomStyle style, float cellSize,
                                              Transform parent, InstancedDungeonRenderer instancer,
                                              WallFaceRegistry wallFaces = null)
        {
            var targets = new List<RecessTarget>();
            foreach (var p in gen.Prisons)
            {
                // The mouth is the only cell a hinged prison door can sweep INTO — an outward
                // swing sweeps the hallway cell, which isn't ours to reserve — so reserving it
                // covers the bars, the door and the swing arc together.
                // A MANHOLE TILE IS REMOVED FROM THE FOOTPRINT, not merely refused blocking
                // props. Its floor slab is suppressed, so anything placed there — a collider
                // crate or a scatter of straw alike — hangs in mid-air over the drain. §12's
                // category rule: a prison cell with a hole in it passes every "is this a prison
                // floor cell" test the placer makes, and fails only the thing nobody wrote down.
                HashSet<Vector3Int> cells = p.Cells;
                foreach (var c in p.Cells)
                    if (gen.IsManholeOpening(c))
                    {
                        cells = new HashSet<Vector3Int>(p.Cells);
                        cells.RemoveWhere(gen.IsManholeOpening);
                        break;
                    }

                var noBlocking = new HashSet<Vector3Int> { p.MouthCell };
                targets.Add(new RecessTarget(cells, p.Direction, p.MouthCell,
                                             roll => style != null ? style.PrisonProps(roll) : null,
                                             noBlocking));
            }
            return Build(gen, style, cellSize, parent, instancer, wallFaces,
                         "DungeonPrisonProps", "Prisons", PrisonSaltBase, targets);
        }

        /// <summary>
        /// Sewer chambers, contents from RoomStyle.chamberProps. The THIRD caller of this pass,
        /// and the one that confirms the generalisation was right: a chamber comes out of
        /// RecessFits exactly as a prison and an alcove do, so it arrives already shaped as a
        /// RecessTarget and needed no new machinery at all.
        ///
        /// Its Direction points AWAY from the tube (bore → chamber), so the authored frame reads
        /// the way it should: WallSide.Back is the far wall you see on entering, and
        /// FeatureFacing.Outward looks back at the grate you crawled in through.
        /// </summary>
        public static GameObject BuildChambers(DungeonGenerator gen, RoomStyle style, float cellSize,
                                               Transform parent, InstancedDungeonRenderer instancer,
                                               WallFaceRegistry wallFaces = null)
        {
            var targets = new List<RecessTarget>();
            foreach (var cw in gen.Crawlways)
            {
                // The entry tile is the only way out of a sealed room, and it is the tightest
                // cell in it. A crate there does not clutter the chamber, it entombs whatever
                // the chamber was built to hold.
                foreach (var ch in cw.Chambers)
                {
                    var noBlocking = new HashSet<Vector3Int> { ch.MouthCell };
                    targets.Add(new RecessTarget(ch.Cells, ch.Dir, ch.MouthCell,
                                                 roll => style != null ? style.ChamberProps(roll) : null,
                                                 noBlocking));
                }
            }
            return Build(gen, style, cellSize, parent, instancer, wallFaces,
                         "DungeonChamberProps", "Chambers", ChamberSaltBase, targets);
        }

        static GameObject Build(DungeonGenerator gen, RoomStyle style, float cellSize, Transform parent,
                                InstancedDungeonRenderer instancer, WallFaceRegistry wallFaces,
                                string rootName, string label, int saltBase, List<RecessTarget> targets)
        {
            var root = new GameObject(rootName);
            root.transform.SetParent(parent, false);
            if (style == null || targets.Count == 0) return root;

            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;

            var featureStream = new HashStream(Vector3Int.zero, saltBase + FeatureOffset);
            var wallStream = new HashStream(Vector3Int.zero, saltBase + WallOffset);
            var scatterStream = new HashStream(Vector3Int.zero, saltBase + ScatterOffset);
            var ceilingStream = new HashStream(Vector3Int.zero, saltBase + CeilingOffset);
            var variantStream = new HashStream(Vector3Int.zero, saltBase + VariantOffset);

            GameObject Pick(PropSet.PropEntry e, HashStream s) =>
                (e.prefabs == null || e.prefabs.Length == 0) ? null : e.prefabs[s.Next() % e.prefabs.Length];

            // Takes the ENTRY, not just a tier, so the per-room emissive tint resolves here
            // rather than at each call site. A recess belongs to no room — an alcove is typed
            // Hallway, a prison is its own CellType — so it passes a null Room and PropTint
            // resolves the style's DEFAULT torch colour, matching the walls around it (§7).
            void Place(PropSet.PropEntry e, GameObject prefab, Vector3 pos, Quaternion rot)
            {
                PropTier t = instancer != null ? e.tier : PropTier.FullGameObject;
                PropTint.Resolve(e, style, null, out var tintFrom, out var tintTo);
                PropInstancer.PlaceProps(instancer, prefab,
                    new[] { new PropPlacement { position = pos, rotation = rot } },
                    t, cellSize, root.transform,
                    replaceMat: tintFrom, withMat: tintTo);
            }

            Vector3 CellCentre(Vector3Int c) =>
                new Vector3((c.x + 0.5f) * cellSize, c.y * cellSize, (c.z + 0.5f) * cellSize) + parent.position;

            int total = 0;

            // Recesses in a stable order so the streams are reproducible.
            var ordered = new List<RecessTarget>(targets);
            ordered.Sort((a, b) =>
            {
                var x = a.MouthCell; var y = b.MouthCell;
                return x.x != y.x ? x.x.CompareTo(y.x) : x.z != y.z ? x.z.CompareTo(y.z) : x.y.CompareTo(y.y);
            });

            foreach (var recess in ordered)
            {
                // THE VARIANT ROLL IS UNCONDITIONAL and happens before any early-out, so the
                // stream advances once per recess no matter how the pool is authored. Rolling
                // it lazily — only when a pool has variants — would make the draw count depend
                // on content, and adding a second variant to one kind would reshuffle every
                // recess after it (the lesson already recorded at DungeonGenerator's prison
                // and alcove stages).
                float variantRoll = variantStream.Next01();
                PropSet set = recess.Contents?.Invoke(variantRoll);
                if (set == null || set.entries == null || set.entries.Count == 0) continue;

                // Cells of THIS recess, deepest first — so a Feature lands at the back and
                // scatter fills forward toward the mouth.
                var cells = new List<Vector3Int>(recess.Cells);
                cells.Sort((a, b) =>
                {
                    int da = Depth(a, recess), db = Depth(b, recess);
                    if (da != db) return db.CompareTo(da);
                    return a.x != b.x ? a.x.CompareTo(b.x) : a.z != b.z ? a.z.CompareTo(b.z) : a.y.CompareTo(b.y);
                });
                if (cells.Count == 0) continue;

                var usedFloor = new HashSet<Vector3Int>();
                var usedCeiling = new HashSet<Vector3Int>();

                // A RECESS RESOLVES ONCE, AT ITS MOUTH. It is one to a handful of cells — small
                // enough that a gradient across it would be invisible and a boundary through it
                // would just look wrong. Same treatment rooms get, for the same reason;
                // corridors are the only pass that varies per cell.
                //
                // Appended after the authored entries, so every base entry draws first and base
                // placement is unchanged whether regions exist or not.
                var recessEntries = new List<PropSet.PropEntry>(set.entries);
                var regionOf = new Dictionary<PropSet.PropEntry, int>();
                gen.Regions.AppendEntries(recess.MouthCell, recessEntries, regionOf);

                foreach (var e in recessEntries)
                {
                    if (e.prefabs == null || e.prefabs.Length == 0) continue;
                    float regionMult = gen.Regions.MultiplierAt(regionOf, e, recess.MouthCell);

                    // Recesses (prisons and alcoves) come out of RecessFits, which carves
                    // 1-tall pockets - same reasoning as the corridor check.
                    if (e.minRoomHeightCells > 1) continue;

                    // BLOCKING TIERS ARE ALLOWED AT ANY DEPTH, including a 1x1 recess. There was
                    // a guard here refusing them in shallow alcoves on the grounds that "there is
                    // nowhere to stand" — wrong, and it contradicted the no-flood-fill note
                    // above: a recess is a DEAD END, nothing routes through it, so a collider
                    // inside one cannot sever or narrow anything. A recessed statue you cannot
                    // walk into is the whole point of a 1x1 nook, and forcing it to StaticDecor
                    // made the player walk through the statue.
                    //
                    // The only real risk is a prop whose collider spills OUT into the corridor,
                    // and that is an authoring concern (prop size and wallGap), not something a
                    // tier check can catch.
                    //
                    // The ONE exception is NoBlocking (a prison's doorway tile) — see the class
                    // note. Passed down as a predicate rather than filtered out of `cells` up
                    // front, because DECOR is still welcome there; only colliders are refused.
                    bool CanBlock(Vector3Int c) =>
                        recess.NoBlocking == null || !recess.NoBlocking.Contains(c);

                    // KEEP THE DOORWAY CLEAR, when the entry asks for it. The exemptions below
                    // reason that the ceiling and wall planes cannot block a doorway, which
                    // holds for a banner and fails for anything that HANGS DOWN — a caged
                    // skeleton swinging in the one tile you must walk through reads as a
                    // generation fault rather than as decor.
                    //
                    // Filtered out of the cell list rather than expressed as a CanBlock
                    // predicate, because this is not about colliders: the point is that the
                    // prop must not BE there at all, whatever tier it is.
                    List<Vector3Int> entryCells = cells;
                    if (e.avoidEntranceCell)
                    {
                        entryCells = new List<Vector3Int>(cells.Count);
                        foreach (var c in cells)
                            if (c != recess.MouthCell) entryCells.Add(c);
                    }

                    switch (e.anchor)
                    {
                        case PropAnchor.Feature:
                            total += PlaceFeature(e, recess, entryCells, cellSize, CellCentre, Pick, Place,
                                                  featureStream, usedFloor, grid, wallFaces, CanBlock);
                            break;

                        case PropAnchor.WallMounted:
                            // No floor occupancy, so NoBlocking doesn't apply — a banner over the
                            // doorway blocks nothing.
                            total += PlaceWallMounted(e, recess, cellSize, CellCentre, Pick, Place,
                                                      wallStream, grid, wallFaces, Open, regionMult);
                            break;

                        case PropAnchor.CeilingHung:
                            // Ceiling plane: also exempt, for the same reason.
                            total += PlaceScatterLike(e, recess, entryCells, cellSize, CellCentre, Pick, Place,
                                                      ceilingStream, usedCeiling, grid, wallFaces, Open,
                                                      ceiling: true, CanBlock: null, chanceMult: regionMult);
                            break;

                        default: // FloorScatter, and anything room-only degrades to scatter
                            total += PlaceScatterLike(e, recess, entryCells, cellSize, CellCentre, Pick, Place,
                                                      scatterStream, usedFloor, grid, wallFaces, Open,
                                                      ceiling: false, CanBlock: CanBlock, chanceMult: regionMult);
                            break;
                    }
                }
            }

            if (total > 0)
                Debug.Log($"[{label}] {ordered.Count} recess(es), {total} prop(s) placed.");
            return root;
        }

        /// <summary>A tier that occupies space rather than merely decorating it.</summary>
        static bool Blocking(PropTier t) => t != PropTier.StaticDecor;

        /// <summary>How many cells INTO the recess this cell sits (mouth = 0).</summary>
        static int Depth(Vector3Int c, RecessTarget a)
        {
            Vector3Int rel = c - a.MouthCell;
            return Mathf.Abs(rel.x * a.Direction.x + rel.z * a.Direction.z);
        }

        /// <summary>
        /// The hero prop. This is why a recess carries Direction: it supplies the same
        /// back/left/right frame a room's entrance axis does, so the existing Feature authoring
        /// (WallSide.Back, FeatureFacing.Outward) means exactly what an author expects — Back is
        /// the far wall, Outward looks out at the corridor.
        /// </summary>
        static int PlaceFeature(PropSet.PropEntry e, RecessTarget a, List<Vector3Int> cells, float cellSize,
                                System.Func<Vector3Int, Vector3> CellCentre,
                                System.Func<PropSet.PropEntry, HashStream, GameObject> Pick,
                                System.Action<PropSet.PropEntry, GameObject, Vector3, Quaternion> Place,
                                HashStream s, HashSet<Vector3Int> usedFloor,
                                Grid3D<CellType> grid, WallFaceRegistry wallFaces,
                                System.Func<Vector3Int, bool> CanBlock)
        {
            // Deepest free cell — cells is already sorted deepest-first, so a hero prop lands at
            // the back and the mouth is the LAST thing it would fall back to. In a 1x1 prison
            // that fallback is the mouth, which is exactly when the CanBlock guard matters.
            bool needsSpace = Blocking(e.tier);
            Vector3Int? target = null;
            foreach (var c in cells)
            {
                if (usedFloor.Contains(c)) continue;
                if (needsSpace && CanBlock != null && !CanBlock(c)) continue;
                target = c;
                break;
            }
            if (!target.HasValue) return 0;

            GameObject prefab = Pick(e, s);
            if (prefab == null) return 0;

            Vector3Int cell = target.Value;

            // Outward = look back down the corridor you came from; Inward = face the dead end.
            Vector3 face = e.featureFacing == FeatureFacing.Inward ? (Vector3)a.Direction : -(Vector3)a.Direction;
            Quaternion rot = Quaternion.LookRotation(face.normalized) * Quaternion.Euler(0f, e.featureYaw, 0f);

            Vector3 pos = CellCentre(cell);
            if (e.snapToWall)
            {
                // Push back against the far wall if there is one behind this cell.
                Vector3Int behind = cell + a.Direction;
                if (!(grid.InBounds(behind) && grid[behind] != CellType.Empty))
                    pos += (Vector3)a.Direction * (cellSize * 0.5f - e.wallGap);
            }

            Place(e, prefab, pos, rot);
            if (!e.sharesTile) usedFloor.Add(cell);

            // Claim the back face so a torch can't land on top of the idol.
            Vector3Int back = cell + a.Direction;
            if (wallFaces != null && !(grid.InBounds(back) && grid[back] != CellType.Empty))
                wallFaces.Claim(grid.Index(cell), a.Direction);

            return 1;
        }

        static int PlaceWallMounted(PropSet.PropEntry e, RecessTarget a, float cellSize,
                                    System.Func<Vector3Int, Vector3> CellCentre,
                                    System.Func<PropSet.PropEntry, HashStream, GameObject> Pick,
                                    System.Action<PropSet.PropEntry, GameObject, Vector3, Quaternion> Place,
                                    HashStream s, Grid3D<CellType> grid, WallFaceRegistry wallFaces,
                                    System.Func<Vector3Int, bool> Open, float chanceMult = 1f)
        {
            var faces = new List<(Vector3Int c, Vector3Int d)>();
            foreach (var c in a.Cells)
                foreach (var d in HDirs)
                {
                    if (Open(c + d)) continue;   // not a wall
                    if (wallFaces != null && (!wallFaces.PropsAllowed(grid.Index(c), d) ||
                                              wallFaces.IsClaimed(grid.Index(c), d))) continue;
                    faces.Add((c, d));
                }
            if (faces.Count == 0) return 0;

            int salt = s.Next();
            faces.Sort((x, y) => DungeonKitPlacer.Hash(x.c, salt + DirIdx(x.d))
                                 .CompareTo(DungeonKitPlacer.Hash(y.c, salt + DirIdx(y.d))));

            int placed = 0, want = e.guaranteed ? e.count : int.MaxValue;
            foreach (var (c, d) in faces)
            {
                if (placed >= want) break;
                if (!e.guaranteed)
                {
                    if (e.maxPerRoom > 0 && placed >= e.maxPerRoom) break;
                    if (s.Next01() >= e.chancePerCell * chanceMult) continue;
                }
                GameObject prefab = Pick(e, s);
                if (prefab == null) continue;

                float h = e.mountHeight + (e.mountHeightJitter > 0f ? (s.Next01() - 0.5f) * 2f * e.mountHeightJitter : 0f);
                Vector3 tan = new Vector3(-d.z, 0f, d.x);
                float latRange = e.subCellJitter * (cellSize * 0.5f - 0.3f);
                Vector3 pos = CellCentre(c)
                              + (Vector3)d * (cellSize * 0.5f - e.wallGap)
                              + tan * ((s.Next01() - 0.5f) * 2f * latRange)
                              + Vector3.up * h;
                Quaternion rot = Quaternion.LookRotation(-(Vector3)d)
                                 * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);
                Place(e, prefab, pos, rot);
                wallFaces?.Claim(grid.Index(c), d);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Floor scatter and ceiling props share almost all their logic here; only the height
        /// and the used-set differ. A recess is nearly all inside corners, so snapToInsideCorner
        /// works especially well and comes free from PropSnap.
        /// </summary>
        static int PlaceScatterLike(PropSet.PropEntry e, RecessTarget a, List<Vector3Int> cells, float cellSize,
                                    System.Func<Vector3Int, Vector3> CellCentre,
                                    System.Func<PropSet.PropEntry, HashStream, GameObject> Pick,
                                    System.Action<PropSet.PropEntry, GameObject, Vector3, Quaternion> Place,
                                    HashStream s, HashSet<Vector3Int> used,
                                    Grid3D<CellType> grid, WallFaceRegistry wallFaces,
                                    System.Func<Vector3Int, bool> Open, bool ceiling,
                                    System.Func<Vector3Int, bool> CanBlock, float chanceMult = 1f)
        {
            int salt = s.Next();
            // Filtered BEFORE the hash sort, so a refused cell doesn't consume a chance roll and
            // the reservation can't silently thin the scatter's effective density.
            bool needsSpace = Blocking(e.tier);
            var order = new List<Vector3Int>();
            foreach (var c in cells)
            {
                if (!e.sharesTile && used.Contains(c)) continue;
                if (needsSpace && CanBlock != null && !CanBlock(c)) continue;
                order.Add(c);
            }
            order.Sort((x, y) => DungeonKitPlacer.Hash(x, salt).CompareTo(DungeonKitPlacer.Hash(y, salt)));

            bool insideCorner = e.snapToInsideCorner;
            bool wantsWall = !insideCorner && (e.snapToWall ||
                             e.facing == FacingRule.FaceAwayFromNearestWall ||
                             e.facing == FacingRule.AlignWithWall);

            int placed = 0, want = e.guaranteed ? e.count : int.MaxValue;
            foreach (var c in order)
            {
                if (placed >= want) break;

                Vector3Int ca = default, cb = default;
                if (insideCorner && !PropSnap.TryInsideCorner(grid, c, null, false, s.Next(), out ca, out cb))
                    continue;
                if (!e.guaranteed)
                {
                    if (e.maxPerRoom > 0 && placed >= e.maxPerRoom) break;
                    if (s.Next01() >= e.chancePerCell * chanceMult) continue;
                }

                Vector3Int? wallDir = null;
                if (wantsWall)
                {
                    var solids = new List<Vector3Int>();
                    foreach (var d in HDirs)
                    {
                        if (Open(c + d)) continue;
                        if (e.snapToWall && wallFaces != null && !wallFaces.PropsAllowed(grid.Index(c), d)) continue;
                        solids.Add(d);
                    }
                    if (solids.Count > 0) wallDir = solids[solids.Count == 1 ? 0 : s.Next() % solids.Count];
                    if (e.snapToWall && !wallDir.HasValue) continue;
                }

                // CellCentre sits at the cell's FLOOR (parent offset included). A ceiling prop
                // hangs one cell higher — add the delta rather than rebuilding the vector, or
                // the parent offset gets dropped. Alcoves are single-story, like corridors.
                Vector3 baseCentre = CellCentre(c);
                if (ceiling) baseCentre.y += cellSize;

                float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                Vector3 pos;
                Quaternion rot;
                if (insideCorner)
                {
                    pos = baseCentre + PropSnap.CornerOffset(ca, cb, cellSize, e.wallGap);
                    rot = Quaternion.LookRotation(PropSnap.CornerFacing(ca, cb).normalized)
                          * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);
                }
                else if (e.snapToWall && wallDir.HasValue)
                {
                    Vector3 tan = new Vector3(-wallDir.Value.z, 0f, wallDir.Value.x);
                    pos = baseCentre + (Vector3)wallDir.Value * (cellSize * 0.5f - e.wallGap)
                          + tan * ((s.Next01() - 0.5f) * 2f * range);
                    rot = Yaw(e, s, wallDir, a);
                }
                else
                {
                    pos = baseCentre + new Vector3((s.Next01() - 0.5f) * 2f * range, 0f,
                                                   (s.Next01() - 0.5f) * 2f * range);
                    rot = Yaw(e, s, wallDir, a);
                }

                GameObject prefab = Pick(e, s);
                if (prefab == null) continue;
                Place(e, prefab, pos, rot);
                if (!e.sharesTile) used.Add(c);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Scatter yaw. FaceEntrance is meaningful here where it isn't in a corridor — a
        /// recess HAS an entrance, so it maps to looking back out at the hallway.
        /// </summary>
        static Quaternion Yaw(PropSet.PropEntry e, HashStream s, Vector3Int? wallDir, RecessTarget a)
        {
            Vector3 dir = Vector3.zero;
            if (e.facing == FacingRule.FaceAwayFromNearestWall && wallDir.HasValue)
                dir = -(Vector3)wallDir.Value;
            else if (e.facing == FacingRule.AlignWithWall && wallDir.HasValue)
            {
                dir = Vector3.Cross(Vector3.up, -(Vector3)wallDir.Value);
                if (s.Next() % 2 == 1) dir = -dir;
            }
            else if (e.facing == FacingRule.FaceEntrance)
                dir = -(Vector3)a.Direction;

            Quaternion baseRot = dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity;
            return baseRot * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);
        }
    }
}

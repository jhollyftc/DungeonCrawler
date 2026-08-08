using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

namespace DungeonGen
{
    /// <summary>
    /// Fills the PropSockets authored on KIT pieces — a fireplace wall's fire, candles inside a
    /// recess, a chandelier on a ceiling boss, rubble in a cracked floor tile.
    ///
    /// Same authoring as prop sockets (§8): an empty child transform with PropSocket on it, its
    /// own transform IS the child's pose. What a socket buys over the prop system is an EXACT
    /// AUTHORED POSITION ON A SPECIFIC PIECE — the prop placers choose positions by zone,
    /// chance and spacing, which can never say "on the mantel of this fireplace" or "in the
    /// niche of this recess".
    ///
    /// Runs off the SocketSite list DungeonKitPlacer.Enumerate records rather than spawning
    /// inline, for two reasons: `place` cannot express PropTier (a fireplace VFX must be
    /// FullGameObject, a candle StaticDecor), and one pass then serves both the PrefabKit and
    /// InstancedKit paths. Same record-then-consume shape as FeatureFaces feeding NearWallAsset.
    ///
    /// RUNS BEFORE TorchPlacer, so a socket torch claims its face and enters the spacing buckets
    /// before any computed torch is chosen — §8's most-constrained-first rule again.
    /// </summary>
    public static class KitSocketPlacer
    {
        // Own salt, never a shared counter (golden rule 4): tuning this pass must not shift the
        // room (110xx), hallway (12002/3/5) or alcove (121xx) placements.
        const int SocketSalt = 12201;

        public static GameObject Build(DungeonGenerator gen, DungeonKit kit,
                                       List<DungeonKitPlacer.SocketSite> sites,
                                       float cellSize, Transform parent,
                                       InstancedDungeonRenderer instancer = null,
                                       RoomStyle style = null,
                                       WallFaceRegistry wallFaces = null,
                                       TorchSettings torches = null)
        {
            var root = new GameObject("DungeonKitSockets");
            root.transform.SetParent(parent, false);
            if (sites == null || sites.Count == 0) return root;

            var stream = new HashStream(Vector3Int.zero, SocketSalt);
            // One source for the flame property name, shared with TorchPlacer so an authored
            // sconce and a computed one cannot tint through different property paths.
            string flameProp = torches != null ? torches.flameColorProperty : "Color";
            var socketCache = new Dictionary<GameObject, PropSocket[]>();
            int placed = 0, blockedOnFloor = 0;

            // Stable order — the sites come out of a grid scan, but sorting means a change to
            // that scan order cannot reshuffle the stream.
            sites.Sort((a, b) =>
            {
                if (a.cell.x != b.cell.x) return a.cell.x.CompareTo(b.cell.x);
                if (a.cell.z != b.cell.z) return a.cell.z.CompareTo(b.cell.z);
                if (a.cell.y != b.cell.y) return a.cell.y.CompareTo(b.cell.y);
                return DirKey(a.faceDir).CompareTo(DirKey(b.faceDir));
            });

            foreach (var site in sites)
            {
                PropSocket[] sockets = SocketsOf(site.prefab, socketCache);
                if (sockets.Length == 0) continue;

                // The piece's ACTUAL rendered world pose, offset included. A socket is authored
                // in the mesh's frame, and kit meshes carry globalVisualOffset — composing from
                // the un-offset pose drops every child a half-cell (golden rule 2).
                Vector3 piecePos = site.posCells * cellSize + site.offset + parent.position;
                Transform pieceRoot = site.prefab.transform;
                Matrix4x4 pieceWorld =
                    Matrix4x4.TRS(piecePos, site.rot * pieceRoot.rotation, pieceRoot.lossyScale)
                    * pieceRoot.worldToLocalMatrix;

                // Room palette for this piece, resolved once. Falls back to the corridor colour,
                // same source the fog and flame VFX read (§7), so nothing can drift.
                Room room = gen.RoomAt(site.cell);
                Color palette = style != null
                    ? (room != null ? style.For(room.Type).torchColor : style.defaultTorchColor)
                    : Color.white;

                foreach (var s in sockets)
                {
                    if (s.childPrefabs == null || s.childPrefabs.Length == 0) continue;
                    if (stream.Next01() >= s.fillChance) continue;
                    GameObject child = s.childPrefabs[stream.Next() % s.childPrefabs.Length];
                    if (child == null) continue;

                    PropTier tier = s.childTier;

                    // FLOOR SOCKETS MAY NOT BLOCK. Sockets spawn outside RoomPropPlacer's
                    // occupancy system, so nothing flood-fills after them — a blocking child on
                    // a floor tile could sit in a doorway or pinch a room in two with nothing to
                    // catch it. Walls and ceilings are structurally incapable of this, which is
                    // why the guard is floors-only. Demoted rather than skipped, so the piece
                    // still appears; warned so it does not pass unnoticed.
                    if (site.isFloor && tier != PropTier.StaticDecor)
                    {
                        blockedOnFloor++;
                        tier = PropTier.StaticDecor;
                    }

                    Matrix4x4 m = pieceWorld * s.transform.localToWorldMatrix;
                    Quaternion childRot = m.rotation
                        * Quaternion.Euler(0f, (stream.Next01() - 0.5f) * 2f * s.yawJitter, 0f);
                    Vector3 jitter = new Vector3(
                        (stream.Next01() - 0.5f) * 2f * s.positionJitter, 0f,
                        (stream.Next01() - 0.5f) * 2f * s.positionJitter);
                    Vector3 childPos = (Vector3)m.GetColumn(3) + childRot * jitter;

                    // A socket torch is an AUTHORED torch: claim the face so no wall prop lands
                    // on it, and register it so TorchPlacer's spacing treats it as already
                    // placed and keeps computed torches away.
                    if (s.countsAsTorch && site.faceDir != Vector3Int.zero && wallFaces != null)
                    {
                        wallFaces.Claim(gen.Grid.Index(site.cell), site.faceDir);
                        wallFaces.RecordTorch(site.cell, site.faceDir);
                    }

                    // Tint resolves by WHAT THE CHILD IS, not by a second authoring flag:
                    //  - emissive material  -> swap the room's cached variant (a candle glows
                    //    per-room with no Light and, at StaticDecor, no GameObject either)
                    //  - a Light            -> tint the light and its flame VFX
                    // The emissive swap has to happen HERE because the kit's own swap lives in
                    // DungeonVisualizer's kit callback and only covers kit pieces — a socket
                    // child goes through PropInstancer and would otherwise keep its authored
                    // colour, which looks right in a warm corridor and wrong in a blue shrine.
                    //
                    // The material to replace is the SOCKET'S, falling back to the kit's. A socket
                    // child has its own glow material by nature — a candle's wax-and-flame material
                    // is not the walls' emissive material — and AddInstance replaces only the exact
                    // material it is handed. Keying this off kit.emissiveMaterial alone meant the
                    // swap matched nothing and every socket child rendered at its authored colour,
                    // in rooms and corridors alike.
                    Material replaceMat = null, withMat = null;
                    Material tintSrc = s.tintMaterial != null ? s.tintMaterial : kit.emissiveMaterial;
                    if (s.tintToRoomPalette && style != null && tintSrc != null)
                    {
                        replaceMat = tintSrc;
                        withMat = EmissiveMaterialVariants.Get(
                            tintSrc, palette * kit.emissiveIntensity, kit.emissiveProperty);
                    }

                    bool tintLights = s.tintToRoomPalette && style != null;
                    PropInstancer.PlaceProps(instancer, child,
                        new[] { new PropPlacement
                        {
                            position = childPos,
                            rotation = childRot,
                            configure = tintLights ? (System.Action<GameObject>)(go => TintLights(go, palette, flameProp)) : null,
                        } },
                        instancer != null ? tier : PropTier.FullGameObject,
                        // NAMED, not positional. PlaceProps takes `bool castShadows` before the
                        // material pair, and UnityEngine.Object defines an implicit operator bool
                        // (the thing that makes `if (obj)` work) — so passing Materials
                        // positionally COMPILES, silently binding replaceMat to castShadows and
                        // shifting the pair one slot. Name them and the shift cannot happen.
                        cellSize, root.transform,
                        replaceMat: replaceMat, withMat: withMat);
                    placed++;
                }
            }

            if (blockedOnFloor > 0)
                Debug.LogWarning($"[KitSockets] {blockedOnFloor} socket child(ren) on FLOOR pieces were " +
                                 "demoted to StaticDecor. Floor sockets bypass the prop system's " +
                                 "occupancy flood-fill, so a blocking child there could seal a doorway " +
                                 "or split a room. Author floor sockets as StaticDecor.");
            if (placed > 0)
                Debug.Log($"[KitSockets] {placed} socket child(ren) placed on {sites.Count} kit piece(s).");
            return root;
        }

        static int DirKey(Vector3Int d) => d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : d.z < 0 ? 3 : 4;

        static PropSocket[] SocketsOf(GameObject prefab, Dictionary<GameObject, PropSocket[]> cache)
        {
            if (cache.TryGetValue(prefab, out var s)) return s;
            s = prefab.GetComponentsInChildren<PropSocket>(true);
            // Hierarchy order is stable, but sort by name anyway — belt and braces for the
            // stream's determinism, same as RoomPropPlacer.FillSockets.
            if (s.Length > 1) System.Array.Sort(s, (a, b) => string.CompareOrdinal(a.name, b.name));
            cache[prefab] = s;
            return s;
        }

        /// <summary>
        /// Tint a spawned child's light and flame to the room's torch colour. Mirrors what
        /// TorchPlacer does for a computed torch, so an authored sconce and a computed one in
        /// the same room cannot burn different colours.
        /// </summary>
        static void TintLights(GameObject go, Color palette, string flameProperty)
        {
            // StaticDecor produces NO GameObject, and PlaceProps still invokes configure with null
            // there (so a decor placement can register a position). A light-tinting hook therefore
            // has to tolerate null rather than assume a GameObject exists — the emissive material
            // swap above is what carries per-room colour for the GameObject-less tier.
            if (go == null) return;

            var light = go.GetComponentInChildren<Light>(true);
            if (light != null) light.color = palette;

            var flame = go.GetComponentInChildren<VisualEffect>(true);
            if (flame != null)
            {
                // Same exposed-property contract TorchPlacer uses (TorchSettings.flameColorProperty,
                // "Color" by default): the graph's colour-over-life gradient owns the SHAPE —
                // bright core fading to smoke — and the palette owns only the hue. A missing
                // property is not an error; the flame simply keeps its authored colour.
                int id = Shader.PropertyToID(flameProperty);
                if (flame.HasVector4(id)) flame.SetVector4(id, palette);
            }
        }
    }
}

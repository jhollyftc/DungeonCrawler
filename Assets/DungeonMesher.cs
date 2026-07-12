using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DungeonGen
{
    /// <summary>
    /// Turns the carved grid into a walkable greybox mesh: the dungeon is
    /// treated as carved out of solid rock, so we emit only interior surfaces
    /// (faces between an open cell and a solid/out-of-bounds neighbor).
    /// Stairs get sloped ramps with side skirts. Three submeshes (room /
    /// hallway / stair) so each gets its own material tint.
    /// Geometry is built in cell units; the returned object is scaled by cellSize.
    /// </summary>
    public static class DungeonMesher
    {
        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int( 1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int( 0, 0, 1),
            new Vector3Int( 0, 0,-1),
        };

        public static GameObject Build(DungeonGenerator gen, float cellSize, Transform parent, float wallMargin = 0f,
                                       bool includeStairRamps = true)
        {
            var grid = gen.Grid;
            // wallMargin is authored in meters; geometry here is built in cell
            // units and scaled by cellSize afterward, so convert once.
            float marginCells = wallMargin / cellSize;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new[] { new List<int>(), new List<int>(), new List<int>(), new List<int>() }; // room, hallway, stair, prison

            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;

            void AddTri(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                // Self-correcting winding: flip if the face would point away from `normal`.
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f)
                    (b, c) = (c, b);
                int i = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c);
                norms.Add(normal); norms.Add(normal); norms.Add(normal);
                tris[sub].Add(i); tris[sub].Add(i + 1); tris[sub].Add(i + 2);
            }

            void AddQuad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                AddTri(sub, a, b, c, normal);
                AddTri(sub, a, c, d, normal);
            }

            // ---- Cell faces ----
            for (int i = 0; i < grid.Length; i++)
            {
                CellType t = grid[i];
                if (t == CellType.Empty) continue;

                Vector3Int c = grid.Position(i);
                Vector3 o = c; // cell min corner, cell units
                int sub = t == CellType.Room ? 0
                        : t == CellType.Hallway ? 1
                        : t == CellType.Prison ? 3
                        : 2;

                // Floor — skip stair cells: StairLower gets a ramp, StairUpper is open air.
                if ((t == CellType.Room || t == CellType.Hallway || t == CellType.Prison) && !Open(c + Vector3Int.down))
                    AddQuad(sub,
                        o, o + new Vector3(1, 0, 0), o + new Vector3(1, 0, 1), o + new Vector3(0, 0, 1),
                        Vector3.up);

                // Ceiling — StairLower's "above" is its own StairUpper cell, always open.
                if (t != CellType.StairLower && !Open(c + Vector3Int.up))
                    AddQuad(sub,
                        o + new Vector3(0, 1, 0), o + new Vector3(1, 1, 0),
                        o + new Vector3(1, 1, 1), o + new Vector3(0, 1, 1),
                        Vector3.down);

                // Walls against solid neighbors, normal pointing into the open cell.
                // Inset toward the open cell by marginCells so the collider
                // sits flush with the kit's decorative wall relief instead of
                // flush with the (invisible) nominal grid boundary.
                foreach (var d in HDirs)
                {
                    if (Open(c + d)) continue;
                    Vector3 fmin = FaceMin(c, d, c.y) - (Vector3)d * marginCells;
                    Vector3 span = new Vector3(Mathf.Abs(d.z), 0, Mathf.Abs(d.x)); // face runs perpendicular to d
                    AddQuad(sub,
                        fmin, fmin + span, fmin + span + Vector3.up, fmin + Vector3.up,
                        -(Vector3)d);
                }
            }

            // ---- Stair ramps ----
            // Approximate walking surface (smooth incline) for stair cells.
            // Skipped once the kit provides real stair prefabs with their own
            // authored (stepped) collider — see includeStairRamps callers —
            // so the two don't disagree about where the player's feet land.
            if (includeStairRamps)
            {
                var seen = new HashSet<Stair>();
                foreach (var stair in gen.Stairs.Values)
                {
                    if (!seen.Add(stair)) continue; // 4 cells map to the same record

                    Vector3Int E = stair.Entry;
                    Vector3Int cd = stair.Dir;
                    Vector3Int t1 = E + cd, t2 = E + cd * 2;
                    float yB = E.y, yT = E.y + 1;
                    Vector3 perp = new Vector3(Mathf.Abs(cd.z), 0, Mathf.Abs(cd.x));

                    Vector3 lowMin  = FaceMin(t1, -cd, yB); // ramp foot: face shared with the entry cell
                    Vector3 highMin = FaceMin(t2,  cd, yT); // ramp top: face shared with the exit cell
                    Vector3 highMinB = new Vector3(highMin.x, yB, highMin.z);

                    // Sloped walking surface across both tread cells.
                    AddQuad(2, lowMin, lowMin + perp, highMin + perp, highMin, Vector3.up);

                    // Triangular side skirts so you can't see under the ramp from the side.
                    AddTri(2, lowMin, highMinB, highMin, -perp);
                    AddTri(2, lowMin + perp, highMinB + perp, highMin + perp, perp);

                    // Back wall under the top edge (visible if the cell past the ramp
                    // at the lower level happens to be open).
                    AddQuad(2, highMinB, highMinB + perp, highMin + perp, highMin, (Vector3)(Vector3Int)cd);
                }
            }

            // ---- Mesh + GameObject ----
            var mesh = new Mesh { name = "DungeonMesh" };
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.subMeshCount = 4;
            for (int s = 0; s < 4; s++) mesh.SetTriangles(tris[s], s);
            mesh.RecalculateBounds();

            var go = new GameObject("DungeonMesh");
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * cellSize;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[]
            {
                MakeMat(new Color(0.75f, 0.55f, 0.5f)),   // rooms
                MakeMat(new Color(0.55f, 0.6f, 0.75f)),   // hallways
                MakeMat(new Color(0.55f, 0.75f, 0.55f)),  // stairs
                MakeMat(new Color(0.45f, 0.4f, 0.5f)),    // prisons
            };
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return go;
        }

        /// <summary>Min corner of the face of `cell` pointing toward `f`, at height y.</summary>
        static Vector3 FaceMin(Vector3Int cell, Vector3Int f, float y)
        {
            float x = cell.x + (f.x > 0 ? 1f : 0f);
            float z = cell.z + (f.z > 0 ? 1f : 0f);
            return new Vector3(x, y, z);
        }

        static Material MakeMat(Color c)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.color = c;
            return m;
        }
    }
}

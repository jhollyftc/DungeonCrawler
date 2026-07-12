using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>Undirected edge between two point indices. Always stored A &lt; B.</summary>
    public struct DEdge : IEquatable<DEdge>
    {
        public readonly int A, B;
        public DEdge(int a, int b) { A = Math.Min(a, b); B = Math.Max(a, b); }
        public bool Equals(DEdge o) => A == o.A && B == o.B;
        public override bool Equals(object o) => o is DEdge e && Equals(e);
        public override int GetHashCode() => A * 486187739 + B;
    }

    /// <summary>
    /// 3D Delaunay tetrahedralization via incremental Bowyer-Watson.
    /// All circumsphere math in doubles. Assumes input points are in
    /// "general position" — callers should jitter grid-aligned points.
    /// Output is the unique edge set (that's all the dungeon pipeline needs;
    /// the tetrahedra themselves are discarded).
    /// </summary>
    public static class Delaunay3D
    {
        struct Tet
        {
            public int A, B, C, D;
            public double CX, CY, CZ, RSq; // circumsphere center + radius²

            public bool CircumsphereContains(double x, double y, double z)
            {
                double dx = x - CX, dy = y - CY, dz = z - CZ;
                double dSq = dx * dx + dy * dy + dz * dz;
                // Relative epsilon: near-boundary points count as inside, which
                // errs toward re-triangulating rather than leaving slivers.
                return dSq <= RSq * (1.0 + 1e-9);
            }
        }

        readonly struct Face : IEquatable<Face>
        {
            public readonly int A, B, C; // sorted ascending
            public Face(int a, int b, int c)
            {
                // 3-element sort
                if (a > b) (a, b) = (b, a);
                if (b > c) (b, c) = (c, b);
                if (a > b) (a, b) = (b, a);
                A = a; B = b; C = c;
            }
            public bool Equals(Face o) => A == o.A && B == o.B && C == o.C;
            public override bool Equals(object o) => o is Face f && Equals(f);
            public override int GetHashCode() => (A * 486187739 + B) * 486187739 + C;
        }

        public static List<DEdge> Triangulate(IReadOnlyList<Vector3> points)
        {
            var edges = new List<DEdge>();
            int n = points.Count;
            if (n < 2) return edges;
            if (n == 2) { edges.Add(new DEdge(0, 1)); return edges; }

            // Working point list: input points + 4 super-tetrahedron vertices at the end.
            var pts = new (double x, double y, double z)[n + 4];
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                pts[i] = (points[i].x, points[i].y, points[i].z);
                minX = Math.Min(minX, pts[i].x); maxX = Math.Max(maxX, pts[i].x);
                minY = Math.Min(minY, pts[i].y); maxY = Math.Max(maxY, pts[i].y);
                minZ = Math.Min(minZ, pts[i].z); maxZ = Math.Max(maxZ, pts[i].z);
            }

            // Super-tet: regular tetrahedron around the bounding-sphere center,
            // scaled far beyond any circumsphere the real points can produce.
            double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5, cz = (minZ + maxZ) * 0.5;
            double ext = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            double s = Math.Max(ext, 1.0) * 100.0;
            int s0 = n, s1 = n + 1, s2 = n + 2, s3 = n + 3;
            pts[s0] = (cx + s, cy + s, cz + s);
            pts[s1] = (cx - s, cy - s, cz + s);
            pts[s2] = (cx - s, cy + s, cz - s);
            pts[s3] = (cx + s, cy - s, cz - s);

            var tets = new List<Tet> { MakeTet(pts, s0, s1, s2, s3) };

            // Reused scratch collections.
            var badIndices = new List<int>();
            var faceCount = new Dictionary<Face, int>();

            for (int p = 0; p < n; p++)
            {
                var (px, py, pz) = pts[p];

                badIndices.Clear();
                for (int t = 0; t < tets.Count; t++)
                    if (tets[t].CircumsphereContains(px, py, pz))
                        badIndices.Add(t);

                if (badIndices.Count == 0)
                {
                    // Should be impossible while the super-tet encloses everything.
                    Debug.LogError($"[Delaunay3D] point {p} found no containing circumsphere — input likely degenerate.");
                    continue;
                }

                // Boundary of the cavity = faces belonging to exactly one bad tet.
                faceCount.Clear();
                foreach (int t in badIndices)
                {
                    var tet = tets[t];
                    Bump(faceCount, new Face(tet.A, tet.B, tet.C));
                    Bump(faceCount, new Face(tet.A, tet.B, tet.D));
                    Bump(faceCount, new Face(tet.A, tet.C, tet.D));
                    Bump(faceCount, new Face(tet.B, tet.C, tet.D));
                }

                // Remove bad tets (descending index order so swaps don't invalidate).
                badIndices.Sort();
                for (int i = badIndices.Count - 1; i >= 0; i--)
                {
                    int t = badIndices[i];
                    tets[t] = tets[tets.Count - 1];
                    tets.RemoveAt(tets.Count - 1);
                }

                // Re-triangulate: connect the new point to every boundary face.
                foreach (var kv in faceCount)
                    if (kv.Value == 1)
                        tets.Add(MakeTet(pts, kv.Key.A, kv.Key.B, kv.Key.C, p));
            }

            // Collect edges from tets that don't touch a super-tet vertex.
            var edgeSet = new HashSet<DEdge>();
            foreach (var t in tets)
            {
                if (t.A >= n || t.B >= n || t.C >= n || t.D >= n) continue;
                edgeSet.Add(new DEdge(t.A, t.B));
                edgeSet.Add(new DEdge(t.A, t.C));
                edgeSet.Add(new DEdge(t.A, t.D));
                edgeSet.Add(new DEdge(t.B, t.C));
                edgeSet.Add(new DEdge(t.B, t.D));
                edgeSet.Add(new DEdge(t.C, t.D));
            }
            edges.AddRange(edgeSet);
            return edges;
        }

        static void Bump(Dictionary<Face, int> d, Face f)
        {
            d.TryGetValue(f, out int c);
            d[f] = c + 1;
        }

        static Tet MakeTet((double x, double y, double z)[] pts, int a, int b, int c, int d)
        {
            var p0 = pts[a]; var p1 = pts[b]; var p2 = pts[c]; var p3 = pts[d];

            // Edge vectors from p0.
            double ax = p1.x - p0.x, ay = p1.y - p0.y, az = p1.z - p0.z;
            double bx = p2.x - p0.x, by = p2.y - p0.y, bz = p2.z - p0.z;
            double cx = p3.x - p0.x, cy = p3.y - p0.y, cz = p3.z - p0.z;

            double aSq = ax * ax + ay * ay + az * az;
            double bSq = bx * bx + by * by + bz * bz;
            double cSq = cx * cx + cy * cy + cz * cz;

            // det = a · (b × c). Near zero => the four points are (almost) coplanar.
            double bcX = by * cz - bz * cy;
            double bcY = bz * cx - bx * cz;
            double bcZ = bx * cy - by * cx;
            double det = ax * bcX + ay * bcY + az * bcZ;

            var tet = new Tet { A = a, B = b, C = c, D = d };

            if (Math.Abs(det) < 1e-12)
            {
                // Degenerate sliver: give it an infinite circumsphere so the next
                // inserted point is guaranteed to destroy it.
                tet.CX = p0.x; tet.CY = p0.y; tet.CZ = p0.z;
                tet.RSq = double.PositiveInfinity;
                return tet;
            }

            // Circumcenter offset from p0:
            // o = ( |a|²(b×c) + |b|²(c×a) + |c|²(a×b) ) / (2 det)
            double caX = cy * az - cz * ay;
            double caY = cz * ax - cx * az;
            double caZ = cx * ay - cy * ax;
            double abX = ay * bz - az * by;
            double abY = az * bx - ax * bz;
            double abZ = ax * by - ay * bx;

            double inv = 0.5 / det;
            double ox = (aSq * bcX + bSq * caX + cSq * abX) * inv;
            double oy = (aSq * bcY + bSq * caY + cSq * abY) * inv;
            double oz = (aSq * bcZ + bSq * caZ + cSq * abZ) * inv;

            tet.CX = p0.x + ox;
            tet.CY = p0.y + oy;
            tet.CZ = p0.z + oz;
            tet.RSq = ox * ox + oy * oy + oz * oz;
            return tet;
        }
    }
}

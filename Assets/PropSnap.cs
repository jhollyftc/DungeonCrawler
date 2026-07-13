using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Shared prop-snapping geometry used by both RoomPropPlacer and
    /// HallwayPropPlacer so the two never diverge. Currently: inside-corner
    /// detection (a cell where two PERPENDICULAR walls meet — a concave
    /// corner) and the diagonal offset that tucks a prop into it.
    /// </summary>
    public static class PropSnap
    {
        static readonly Vector3Int R = new Vector3Int(1, 0, 0);
        static readonly Vector3Int L = new Vector3Int(-1, 0, 0);
        static readonly Vector3Int F = new Vector3Int(0, 0, 1);
        static readonly Vector3Int B = new Vector3Int(0, 0, -1);

        /// <summary>
        /// If `cell` is an inside corner — solid in at least one X direction
        /// AND one Z direction — returns those two wall directions (a = X
        /// wall, b = Z wall). `pick` chooses deterministically when a cell
        /// touches walls on multiple sides (a 1-wide dead-end pocket).
        /// requirePropsAllowed (with wallFaces) skips faces flagged no-props,
        /// but callers currently pass FALSE: a corner prop occupies only the
        /// tiny corner, and `allowPropsInFront` is really about keeping a
        /// wall's FACE clear of snapped props — a different intent that
        /// shouldn't also veto corner cobwebs.
        /// </summary>
        public static bool TryInsideCorner(Grid3D<CellType> grid, Vector3Int cell,
                                           WallFaceRegistry wallFaces, bool requirePropsAllowed, int pick,
                                           out Vector3Int a, out Vector3Int b)
        {
            a = default; b = default;
            bool Wall(Vector3Int d)
            {
                Vector3Int p = cell + d;
                bool solid = !(grid.InBounds(p) && grid[p] != CellType.Empty);
                if (!solid) return false;
                if (requirePropsAllowed && wallFaces != null && !wallFaces.PropsAllowed(grid.Index(cell), d))
                    return false;
                return true;
            }

            bool xr = Wall(R), xl = Wall(L), zf = Wall(F), zb = Wall(B);
            bool hasX = xr || xl, hasZ = zf || zb;
            if (!hasX || !hasZ) return false;

            // Pick one X wall and one Z wall; `pick` decorrelates the choice
            // when both sides are solid.
            a = xr && xl ? ((pick & 1) == 0 ? R : L) : (xr ? R : L);
            b = zf && zb ? ((pick & 2) == 0 ? F : B) : (zf ? F : B);
            return true;
        }

        /// <summary>World offset from a cell center that tucks a prop into the
        /// corner formed by walls a and b, sitting `wallGap` off each.</summary>
        public static Vector3 CornerOffset(Vector3Int a, Vector3Int b, float cellSize, float wallGap)
        {
            Vector3 diag = (Vector3)a + (Vector3)b; // points diagonally at the corner
            return diag * (cellSize * 0.5f - wallGap);
        }

        /// <summary>Facing direction for a corner prop: diagonally out of the
        /// corner into the open space.</summary>
        public static Vector3 CornerFacing(Vector3Int a, Vector3Int b) => -((Vector3)a + (Vector3)b);
    }
}

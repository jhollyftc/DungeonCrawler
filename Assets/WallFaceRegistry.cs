using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Per-face placement restrictions authored on RoomStyle.WallAsset
    /// (allowPropsInFront / allowTorch) — the start of the shared "wall real
    /// estate" system. Which wall asset lands on a face is only decided
    /// inside DungeonKitPlacer's emission (hash picks + capped reservations),
    /// so the kit placer RECORDS restricted faces here as it emits walls, and
    /// TorchPlacer / RoomPropPlacer QUERY it afterward. Build order in
    /// DungeonVisualizer guarantees walls emit before torches and props.
    ///
    /// A face is keyed by (open cell index, direction toward the solid wall)
    /// — the same convention TorchPlacer's slots and RoomPropPlacer's wall
    /// picks already use. Only restricted faces are stored; unknown faces
    /// (kit generic walls, GeneratedMesh mode) allow everything.
    ///
    /// Also tracks CLAIMED faces (a torch or wall-mounted prop took this
    /// face) so the two don't overlap — one occupant per face. TorchPlacer
    /// claims as it accepts; RoomPropPlacer's WallMounted pass skips claimed
    /// faces and claims its own.
    /// </summary>
    public class WallFaceRegistry
    {
        readonly HashSet<long> noProps = new HashSet<long>();
        readonly HashSet<long> noTorch = new HashSet<long>();
        readonly HashSet<long> claimed = new HashSet<long>();

        static long Key(int cellIndex, Vector3Int dir)
        {
            int di = dir.x > 0 ? 0 : dir.x < 0 ? 1 : dir.z > 0 ? 2 : 3;
            return (long)cellIndex * 4 + di;
        }

        public void Record(int cellIndex, Vector3Int dir, bool allowProps, bool allowTorch)
        {
            long key = Key(cellIndex, dir);
            if (!allowProps) noProps.Add(key);
            if (!allowTorch) noTorch.Add(key);
        }

        public bool PropsAllowed(int cellIndex, Vector3Int dir) => !noProps.Contains(Key(cellIndex, dir));
        public bool TorchAllowed(int cellIndex, Vector3Int dir) => !noTorch.Contains(Key(cellIndex, dir));

        /// <summary>Mark a face as occupied by a torch or wall-mounted prop.</summary>
        public void Claim(int cellIndex, Vector3Int dir) => claimed.Add(Key(cellIndex, dir));
        public bool IsClaimed(int cellIndex, Vector3Int dir) => claimed.Contains(Key(cellIndex, dir));
    }
}

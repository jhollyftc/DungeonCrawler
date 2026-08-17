using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>Which kind of gate a lever drives. Kept on the spec so one lever type serves both.</summary>
    public enum GateKind { LockedDoor, Portcullis }

    /// <summary>
    /// One lever: a wall face somewhere in the dungeon that drives a gate.
    ///
    /// NEAR AND FAR ARE NOT INTERCHANGEABLE. The NEAR lever sits in the component reachable from
    /// Start with its gate closed, and it is what makes the gate openable at all — the correctness
    /// condition the whole feature rests on. The FAR lever sits in the component the gate cuts off,
    /// where it can never be reached first; it exists for the one-way routes the dungeon already
    /// has (manholes; elevated doors that fail to get a ladder), and for a toggling portcullis it
    /// is what makes toggling SAFE — whichever side you are on, you can always raise it again.
    /// </summary>
    public struct LeverSpec
    {
        /// <summary>Open cell the lever is seen from.</summary>
        public Vector3Int Cell;
        /// <summary>Cell -> wall. The face the lever mounts on.</summary>
        public Vector3Int WallDir;
        /// <summary>Index into the generator's Gates list.</summary>
        public int GateIndex;
        /// <summary>Reachable from Start with the gate closed.</summary>
        public bool NearSide;

        /// <summary>
        /// Did a lever actually get BUILT here?
        ///
        /// The generator offers several candidate faces per side, because it cannot see which
        /// wall asset the kit will land on one — so most entries in this list are alternatives
        /// that were never used. Without this flag the gizmo draws every candidate as though it
        /// were a lever, which reads as "the lever is missing" when you walk to one and find
        /// bare wall. Written back by GatePlacer.
        /// </summary>
        public bool Placed;
    }

    /// <summary>
    /// One gate: either an existing MST door made lockable, or a portcullis dividing a corridor.
    ///
    /// ONE TYPE FOR BOTH KINDS because the LEVER machinery, the reachability rules and the
    /// softlock invariant are identical; only the siting differs. Splitting them would mean two
    /// copies of the part that is dangerous to get wrong.
    /// </summary>
    public class GateSpec
    {
        public GateKind Kind;

        /// <summary>LockedDoor: index into <see cref="DungeonGenerator.Doors"/>. Portcullis: -1.</summary>
        public int DoorIndex = -1;

        /// <summary>Portcullis: the corridor cell the gate stands in. LockedDoor: the threshold cell.</summary>
        public Vector3Int Cell;

        /// <summary>Portcullis: the corridor's run axis, so the gate faces across it. Unused for doors.</summary>
        public Vector3Int Axis;

        /// <summary>Both levers. Always exactly two once the gate survives siting; a gate that
        /// cannot site its NEAR lever is dropped rather than shipped.</summary>
        public List<LeverSpec> Levers = new List<LeverSpec>();

        /// <summary>Steps from Start's room, used to order gates so lever dependencies form a DAG.</summary>
        public int DepthFromStart;

        /// <summary>
        /// The cells still reachable from Start with this gate SHUT — the generator's own idea of
        /// "your side of it".
        ///
        /// KEPT SO IT CAN BE DRAWN. Which side is near is the one thing that decides whether a
        /// gate is openable or a softlock, and it is invisible from the numbers: a lever is
        /// reported as "near 1" whether that judgement was right or wrong. Rendering the region
        /// turns "is the generator's near side the side I am standing on" from a guess into a
        /// glance.
        /// </summary>
        public HashSet<Vector3Int> NearCells = new HashSet<Vector3Int>();

        /// <summary>Where the reachability walk started — Start's interior floor cell. Drawn with
        /// the region, because a near side computed from the WRONG origin looks perfectly
        /// self-consistent and is the failure the region alone cannot show.</summary>
        public Vector3Int ReachOrigin;

        /// <summary>
        /// How many cells this gate actually severs from Start, EXCLUDING its own.
        ///
        /// The number that says whether the gate means anything. A cut of 0 is a gate with a
        /// route around it, which gates nothing and — worse — makes the whole dungeon count as
        /// the "near side", so its lever can be sited past it. Reported in the log so a gate that
        /// severs two cells is visibly different from one that severs half the run.
        /// </summary>
        public int CutOffCells;

        /// <summary>World-space label for the gizmo.</summary>
        public string Label => Kind == GateKind.Portcullis ? "portcullis" : "locked door";
    }
}

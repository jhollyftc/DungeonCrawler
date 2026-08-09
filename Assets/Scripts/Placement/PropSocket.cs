using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A child-prop attachment point authored on a prop prefab: an empty
    /// child transform positioned/oriented where the child belongs (a chair
    /// socket sits at the seat spot, facing the table). The socket's
    /// transform IS the child's logical pose; yaw/position jitter is small
    /// visual variation on top — a chair may be 5° off, never 130°.
    ///
    /// Resolved by RoomPropPlacer after each parent placement, reading this
    /// component from the PREFAB ASSET (décor-tier parents never spawn a
    /// GameObject, so the socket data can't come from an instance). Children
    /// are NOT parented to the parent at runtime: logical composition,
    /// physical independence — each child is its own PropInstancer placement
    /// so batching stays intact (tables batch with tables, chairs with
    /// chairs). Chain depth caps at parent → child → grandchild.
    ///
    /// ALSO USED ON KIT PIECES — walls, ceilings, floors — resolved by KitSocketPlacer with
    /// the same authoring: a fireplace wall carries a socket where its fire belongs, a recess
    /// wall carries candle sockets inside the recess, a ceiling boss carries the chandelier's
    /// hook. One concept for both, because "an authored attachment point on a prefab" is the
    /// same idea whether the host is a table or a wall.
    ///
    /// THE COST OF A SOCKET IS ITS childTier, and that matters far more on kit pieces than on
    /// props because kit pieces repeat: StaticDecor is mesh-only and batches, so hundreds of
    /// socketed candles are nearly free; FullGameObject spawns an object per instance, which is
    /// fine for a fireplace VFX (capped 1/room by the wall reservation system) and a framerate
    /// cliff on a wall used 500 times.
    /// </summary>
    public class PropSocket : MonoBehaviour
    {
        [Tooltip("Pool of child prefabs this socket may spawn (deterministic hash-pick).")]
        public GameObject[] childPrefabs;
        [Tooltip("Tier for the spawned child. Blocking tiers participate in room occupancy (flood-fill checked).")]
        public PropTier childTier = PropTier.StaticDecor;
        [Tooltip("Chance this socket spawns a child at all — 0.75 leaves the occasional chair missing.")]
        [Range(0f, 1f)] public float fillChance = 0.75f;
        [Tooltip("Extra local yaw jitter applied to the child (degrees, +/-).")]
        public float yawJitter = 5f;
        [Tooltip("Small local position jitter (meters, +/-, in the socket's XZ plane).")]
        public float positionJitter = 0.05f;

        [Header("Kit sockets (walls, ceilings, floors)")]
        [Tooltip("This socket's child is a LIGHT SOURCE that should count as one of the room's torches. It claims its wall face so no wall prop lands on top, and it is fed to TorchPlacer's spacing BEFORE thinning — so the computed torches keep their distance from it rather than putting one 1.5m away. Authored torches therefore DISPLACE computed ones instead of adding to them, which keeps a room's brightness matching its palette. Ignored on prop sockets (a chair has no wall face to claim).")]
        public bool countsAsTorch = false;
        [Tooltip("Tint this child to the ROOM'S TORCH COLOUR, so a shrine's fittings burn its cold blue rather than default orange — the same palette that drives its torches, fog and flame VFX (§7).\n\nResolved by what the child actually IS: a child with a Light gets the light and its flame VFX tinted; a child using the kit's emissive material gets the room's cached emissive VARIANT swapped in (which is how a candle can glow per-room with no Light at all, and no GameObject if it is StaticDecor). A child that is neither is unaffected.\n\nDefault OFF: most sockets hold shields, banners and rubble, where tinting would be wrong.")]
        public bool tintToRoomPalette = false;
        [Tooltip("The child's OWN emissive material, when tintToRoomPalette is on and the child glows without a Light. That material is swapped for the room's cached tinted variant.\n\nSet this whenever the child has its own glow material — a candle's wax-and-flame material is NOT the kit's wall-emissive material, and the swap only replaces the exact material it is given. Leave empty and the kit's emissiveMaterial is used, which is correct only for a child that literally shares the walls' emissive material.\n\nIgnored unless tintToRoomPalette is on. A child that glows via a Light needs nothing here.")]
        public Material tintMaterial;
        [Tooltip("How brightly this child's emissive glows, independent of the room's colour. 0 = inherit the kit's global Emissive Intensity, which is the old behaviour.\n\nThis is the socket counterpart to a PropSet entry's tintIntensity, and it exists for the same reason: one emissive material and one room palette should be able to serve a dim shelf candle AND a blazing brazier. Without it every socketed glow in the dungeon shared a single global brightness.\n\nEmission only BLOOMS above 1 (§5), so values under 1 read as tinted-but-flat rather than dim. Ignored unless tintToRoomPalette is on.")]
        public float tintIntensity = 0f;

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.9f, 0.9f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.12f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.35f);
        }
    }
}

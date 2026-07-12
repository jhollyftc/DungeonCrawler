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

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.9f, 0.9f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.12f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.35f);
        }
    }
}

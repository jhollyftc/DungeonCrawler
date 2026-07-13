using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Marks a trigger volume as climbable. Author on the ladder prefab: a
    /// trigger BoxCollider covering the ladder's climbable front (extend it
    /// ~0.5m above the top opening so the player keeps climb control while
    /// cresting the lip), with this component on the same GameObject or a
    /// parent. FirstPersonController probes for it each frame — while inside,
    /// gravity is off and W/S climb up/down.
    ///
    /// Survives the instanced-prop split: PropInstancer's StaticCollider tier
    /// strips renderers but keeps colliders and custom components.
    /// </summary>
    public class LadderClimbZone : MonoBehaviour
    {
        void OnDrawGizmosSelected()
        {
            var box = GetComponentInChildren<BoxCollider>();
            if (box == null) return;
            Gizmos.color = new Color(0.9f, 0.75f, 0.2f, 0.9f);
            Gizmos.matrix = box.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
        }
    }
}

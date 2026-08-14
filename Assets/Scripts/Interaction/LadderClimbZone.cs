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
        /// <summary>
        /// World direction the CLIMBER must face — from the ladder toward the wall it is
        /// mounted on. Set by DungeonKitPlacer.BuildLadders from the generator's WallDir.
        ///
        /// SET BY THE PLACER RATHER THAN READ OFF transform.forward, for the same reason
        /// CrawlwayGrate.OutwardDirection is: the ladder is instantiated as
        /// `rot * prefab.transform.rotation`, so the root's forward only equals the mount
        /// direction if the prefab happens to have identity rotation. Deriving it would make
        /// the facing rule silently depend on how the FBX was exported, and the failure — a
        /// ladder you can only climb while facing some arbitrary compass direction — would look
        /// like a bug in the facing check rather than in the asset.
        ///
        /// Zero means UNSET, and the facing rule stands down entirely rather than guessing.
        /// That keeps a hand-placed ladder in a test scene climbable from any angle instead of
        /// unclimbable from all of them.
        /// </summary>
        public Vector3 FaceDirection { get; set; }

        public bool HasFacing => FaceDirection.sqrMagnitude > 0.001f;

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

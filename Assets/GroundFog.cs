using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Keeps a ground-fog ParticleSystem on the floor the player is standing on, and
    /// tints it from the room's torch palette. Put this on the DungeonVisualizer next to
    /// DungeonFogController.
    ///
    /// BILLBOARDS, not planes. Floor-parallel planes failed structurally: a plane hugging
    /// the floor has only the floor behind it (~20cm), so a soft-particle fade has no
    /// depth range to work across and every intersection with a wall, crate or goblin
    /// stays a hard line at any setting — and viewed from eye height stacked layers
    /// accumulate into a pool with a visible waterline. Camera-facing quads meet geometry
    /// at a grazing angle and have no edge-on orientation, so both problems go away by
    /// construction rather than by tuning.
    ///
    /// The SYSTEM is authored in the editor, deliberately. Emission rate, size, lifetime
    /// and shape are art decisions that want live preview, and setting particle modules
    /// blind from code is a poor trade. This owns only the two things the editor can't
    /// know: which floor the player is on, and what colour the room is.
    ///
    /// TINT reads RenderSettings.fogColor, which DungeonFogController already steers from
    /// the torch palette — so ground fog can't disagree with the distance fog or the
    /// torchlight, and there's no second copy of the room-lookup logic (§7).
    /// </summary>
    [DisallowMultipleComponent]
    public class GroundFog : MonoBehaviour
    {
        [Header("System")]
        [Tooltip("The fog ParticleSystem, authored in the editor with a material using Dungeon/GroundFog. Recommended starting point: Simulation Space = WORLD (so puffs stay put as you walk), a wide Box shape emitter, large size, long lifetime, low emission rate, Renderer Alignment = View.")]
        public ParticleSystem system;

        [Header("Placement")]
        [Tooltip("Height (m) above the floor the emitter sits at. Ankle-to-knee reads best.")]
        public float baseHeight = 0.3f;
        [Tooltip("Keep the emitter centred on the player horizontally. With World simulation space the puffs stay behind as you move, so the emitter only needs to follow to keep spawning around you.")]
        public bool followPlayer = true;

        [Header("Floor change")]
        [Tooltip("Floor difference (m) that counts as changing level. Below this it's noise in the cell lookup, not a storey.")]
        public float floorChangeThreshold = 0.5f;
        [Tooltip("Clear the existing puffs when the player changes floor. Without this, world-space particles from the floor below hang in the air behind you.")]
        public bool clearOnFloorChange = true;

        [Header("Tint")]
        [Tooltip("Take the colour from RenderSettings.fogColor, which DungeonFogController drives from the room's torch palette.")]
        public bool tintFromFogColor = true;
        [Tooltip("How far to follow the fog colour. Below 1 keeps some of the material's own character so ground mist isn't literally the distance fog.")]
        [Range(0f, 1f)] public float tintBlend = 0.8f;
        [Tooltip("Brightness after the blend — ground fog usually wants to sit a little darker than distance fog, which is lit by everything.")]
        [Range(0f, 2f)] public float tintBrightness = 0.9f;

        DungeonVisualizer vis;
        Transform player;
        Renderer systemRenderer;
        MaterialPropertyBlock block;
        Material sourceMaterial;
        float currentFloorY;
        bool haveFloor;

        static readonly int TintID = Shader.PropertyToID("_Tint");

        void Awake()
        {
            vis = GetComponent<DungeonVisualizer>();
            block = new MaterialPropertyBlock();

            if (system == null)
            {
                Debug.LogWarning("[GroundFog] No ParticleSystem assigned — nothing to place or tint.", this);
                enabled = false;
                return;
            }

            systemRenderer = system.GetComponent<Renderer>();
            if (systemRenderer != null) sourceMaterial = systemRenderer.sharedMaterial;
        }

        void LateUpdate()
        {
            if (!Application.isPlaying || system == null) return;

            if (player == null)
            {
                var fpc = FindObjectOfType<FirstPersonController>();
                if (fpc == null) return;
                player = fpc.transform;
            }

            PlaceEmitter();
            if (tintFromFogColor) ApplyTint();
        }

        void PlaceEmitter()
        {
            // Floor height from the player's CELL, not their world Y — standing on a stair
            // tread or a crate would otherwise lift the fog with them.
            float floorY = player.position.y;
            if (vis != null && vis.Generator != null)
            {
                Vector3Int cell = Vector3Int.FloorToInt(
                    (player.position - vis.transform.position) / vis.cellSize);
                floorY = vis.transform.position.y + cell.y * vis.cellSize;
            }

            bool changedFloor = haveFloor && Mathf.Abs(floorY - currentFloorY) >= floorChangeThreshold;
            currentFloorY = floorY;
            haveFloor = true;

            // Snapped, not eased. A world-space emitter's existing puffs stay where they
            // were, so there's no sheet of fog to be seen sliding vertically — the thing
            // that forced a fade-out on the plane version. Only the SPAWN point moves.
            Vector3 p = system.transform.position;
            if (followPlayer) p = new Vector3(player.position.x, 0f, player.position.z);
            p.y = floorY + baseHeight;
            system.transform.position = p;

            // Puffs left behind on the previous floor would otherwise hang in mid-air at
            // the wrong height, visible through a doorway or over a ledge.
            if (changedFloor && clearOnFloorChange)
                system.Clear(true);
        }

        void ApplyTint()
        {
            if (systemRenderer == null || sourceMaterial == null) return;

            Color c = Color.Lerp(sourceMaterial.GetColor(TintID), RenderSettings.fogColor, tintBlend)
                      * tintBrightness;

            // Property block rather than touching the material, so this never instantiates
            // a per-renderer copy of the shared fog material.
            systemRenderer.GetPropertyBlock(block);
            block.SetColor(TintID, c);
            systemRenderer.SetPropertyBlock(block);
        }
    }
}

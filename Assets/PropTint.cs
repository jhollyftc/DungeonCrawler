using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Resolves the per-room emissive material swap for a PROP placement — the prop-side
    /// counterpart to the kit's tinting (§5/§7), so a candle sitting on a shelf burns the
    /// same colour as the room's torches, fog and kit emissives rather than its authored one.
    ///
    /// WHY A MATERIAL SWAP AND NOT A PROPERTY BLOCK: nothing renders through an instanced
    /// prop's MeshRenderer, so a MaterialPropertyBlock has no renderer to attach to. See
    /// EmissiveMaterialVariants — this is only the "which material, which colour" decision;
    /// that class owns the caching that keeps the batch count bounded by the PALETTE rather
    /// than by prop count.
    ///
    /// ONE resolver shared by every prop placer (room, hallway, alcove) precisely because
    /// they would otherwise drift: the only thing that differs between them is whether a
    /// room is in play, and a corridor resolving to a different colour than the kit shell
    /// around it is exactly the kind of mismatch this system exists to prevent.
    /// </summary>
    public static class PropTint
    {
        /// <summary>
        /// The swap for an entry's own placements. `room` null = corridor/alcove, which takes
        /// the style's DEFAULT torch colour — the same fallback DungeonVisualizer's kit
        /// callback uses for a cell in no room, so prop and wall can't disagree.
        /// Both outputs are null when the entry doesn't opt in, which callers pass straight
        /// through (PlaceProps treats a null pair as "no swap").
        /// </summary>
        public static void Resolve(PropSet.PropEntry e, RoomStyle style, Room room,
                                   out Material replaceMat, out Material withMat)
        {
            if (e == null || !e.tintToRoomPalette)
            {
                replaceMat = null;
                withMat = null;
                return;
            }
            Resolve(e.tintMaterial, e.tintIntensity, style, room, out replaceMat, out withMat);
        }

        /// <summary>
        /// The swap for a SOCKET CHILD hanging off an entry (a candle on a candelabra). The
        /// socket carries its own material and opt-in — the child is a different prefab with
        /// a different material from its parent, which is the whole reason PropSocket has
        /// those fields at all — while INTENSITY comes from the parent entry, since brightness
        /// is a property of the fitting as a whole rather than of each candle on it.
        /// </summary>
        public static void Resolve(PropSocket s, float intensity, RoomStyle style, Room room,
                                   out Material replaceMat, out Material withMat)
        {
            if (s == null || !s.tintToRoomPalette)
            {
                replaceMat = null;
                withMat = null;
                return;
            }
            Resolve(s.tintMaterial, intensity, style, room, out replaceMat, out withMat);
        }

        static void Resolve(Material tintMaterial, float intensity, RoomStyle style, Room room,
                            out Material replaceMat, out Material withMat)
        {
            replaceMat = null;
            withMat = null;
            if (tintMaterial == null || style == null) return;

            Color palette = room != null ? style.For(room.Type).torchColor : style.defaultTorchColor;
            replaceMat = tintMaterial;
            // Emission only BLOOMS above 1 (§5), and the palette is LDR — intensity is what
            // lifts it into HDR range, exactly as kit.emissiveIntensity does for kit pieces.
            // Kept per-ENTRY rather than borrowed from the kit so a dim shelf candle and a
            // blazing brazier can share one material and one palette at different brightness.
            withMat = EmissiveMaterialVariants.Get(tintMaterial, palette * intensity);
        }
    }
}

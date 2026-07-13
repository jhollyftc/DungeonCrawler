using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Inspector drawer for PropSet entries: a summary foldout header
    /// ("Throne — Feature · Back Center") instead of "Element N", and only
    /// the fields that actually apply to the chosen anchor — Feature entries
    /// don't show scatter density, scatter entries don't show wall sides,
    /// wallGap only appears when snapToWall is on, etc. Pure editor UX; the
    /// serialized data is untouched.
    /// </summary>
    [CustomPropertyDrawer(typeof(PropSet.PropEntry))]
    public class PropSetEntryDrawer : PropertyDrawer
    {
        const float Pad = 2f;

        // Single source of truth for which fields show, in order — OnGUI and
        // GetPropertyHeight must agree or rows overlap.
        static IEnumerable<string> VisibleFields(SerializedProperty prop)
        {
            yield return "label";
            yield return "prefabs";
            yield return "anchor";
            yield return "tier";

            var anchor = (PropAnchor)prop.FindPropertyRelative("anchor").enumValueIndex;
            bool snap = prop.FindPropertyRelative("snapToWall").boolValue;

            switch (anchor)
            {
                case PropAnchor.Feature:
                    // Features place once; count/chance don't apply. Position
                    // is exact; jitter/yawRange don't apply (featureYaw does).
                    yield return "featurePositionMode";
                    var mode = (FeaturePositionMode)prop.FindPropertyRelative("featurePositionMode").enumValueIndex;
                    if (mode == FeaturePositionMode.WallSide)
                    {
                        yield return "featureWallSide";
                        yield return "featureSpot";
                        yield return "snapToWall";
                        if (snap) yield return "wallGap";
                    }
                    yield return "featureFacing";
                    yield return "featureYaw";
                    break;

                case PropAnchor.CeilingHung:
                    yield return "guaranteed";
                    if (prop.FindPropertyRelative("guaranteed").boolValue)
                    {
                        yield return "count";
                    }
                    else
                    {
                        yield return "chancePerCell";
                        yield return "maxPerRoom";
                    }
                    yield return "ceilingLayout";
                    bool grid = (CeilingLayout)prop.FindPropertyRelative("ceilingLayout").enumValueIndex == CeilingLayout.Grid;
                    if (grid) yield return "gridStride";
                    yield return "preferredZones";
                    yield return "allowCenter";
                    yield return "facing";
                    yield return "yawRange";
                    yield return "subCellJitter";
                    if (!grid)
                    {
                        yield return "snapToInsideCorner";
                        bool ceilCorner = prop.FindPropertyRelative("snapToInsideCorner").boolValue;
                        if (!ceilCorner) yield return "snapToCeilingWall";
                        if (ceilCorner || prop.FindPropertyRelative("snapToCeilingWall").boolValue) yield return "wallGap";
                    }
                    yield return "sharesTile";
                    break;

                case PropAnchor.WallMounted:
                    // Mounted on a wall face — no floor cell, no zones.
                    yield return "guaranteed";
                    if (prop.FindPropertyRelative("guaranteed").boolValue)
                    {
                        yield return "count";
                    }
                    else
                    {
                        yield return "chancePerCell";
                        yield return "maxPerRoom";
                    }
                    yield return "mountHeight";
                    yield return "mountHeightJitter";
                    yield return "wallGap";     // distance off the wall face
                    yield return "subCellJitter"; // lateral spread along the wall
                    yield return "yawRange";      // narrow — variation on the out-from-wall facing
                    break;

                default: // FloorScatter
                    yield return "guaranteed";
                    if (prop.FindPropertyRelative("guaranteed").boolValue)
                    {
                        yield return "count";
                    }
                    else
                    {
                        yield return "chancePerCell";
                        yield return "maxPerRoom";
                    }
                    yield return "preferredZones";
                    yield return "allowCenter";
                    yield return "facing";
                    yield return "yawRange";
                    yield return "subCellJitter";
                    yield return "snapToInsideCorner";
                    bool floorCorner = prop.FindPropertyRelative("snapToInsideCorner").boolValue;
                    if (!floorCorner) yield return "snapToWall";
                    if (floorCorner || snap) yield return "wallGap";
                    yield return "sharesTile";
                    break;
            }
        }

        static string Header(SerializedProperty prop)
        {
            string name = prop.FindPropertyRelative("label").stringValue;
            if (string.IsNullOrEmpty(name))
            {
                var prefabs = prop.FindPropertyRelative("prefabs");
                name = prefabs.arraySize > 0 && prefabs.GetArrayElementAtIndex(0).objectReferenceValue != null
                    ? prefabs.GetArrayElementAtIndex(0).objectReferenceValue.name
                    : "(empty)";
            }

            var anchor = (PropAnchor)prop.FindPropertyRelative("anchor").enumValueIndex;
            string detail;
            switch (anchor)
            {
                case PropAnchor.Feature:
                    var mode = (FeaturePositionMode)prop.FindPropertyRelative("featurePositionMode").enumValueIndex;
                    detail = mode == FeaturePositionMode.RoomCenter
                        ? "Feature · room center"
                        : $"Feature · {(FeatureWallSide)prop.FindPropertyRelative("featureWallSide").enumValueIndex} " +
                          $"{(FeatureSpot)prop.FindPropertyRelative("featureSpot").enumValueIndex}";
                    break;
                case PropAnchor.CeilingHung:
                    if ((CeilingLayout)prop.FindPropertyRelative("ceilingLayout").enumValueIndex == CeilingLayout.Grid)
                        detail = $"Ceiling · grid ÷{prop.FindPropertyRelative("gridStride").intValue}";
                    else if (prop.FindPropertyRelative("snapToInsideCorner").boolValue)
                        detail = "Ceiling · inside corner";
                    else
                        detail = prop.FindPropertyRelative("snapToCeilingWall").boolValue ? "Ceiling · wall" : "Ceiling";
                    break;
                case PropAnchor.WallMounted:
                    detail = $"Wall-mounted · {prop.FindPropertyRelative("mountHeight").floatValue:0.#}m";
                    break;
                default:
                    string zones = ZonesLabel(prop.FindPropertyRelative("preferredZones").intValue);
                    detail = prop.FindPropertyRelative("guaranteed").boolValue
                        ? $"Scatter ×{prop.FindPropertyRelative("count").intValue} · {zones}"
                        : $"Scatter · {zones}";
                    break;
            }
            return $"{name}  —  {detail}";
        }

        static string ZonesLabel(int mask)
        {
            if (mask == 0) return "no zone";
            if (mask == (int)RoomZoneMask.Perimeter) return "Perimeter";
            var parts = new System.Collections.Generic.List<string>();
            foreach (RoomZone z in System.Enum.GetValues(typeof(RoomZone)))
                if ((mask & (1 << (int)z)) != 0) parts.Add(z.ToString());
            return string.Join("+", parts);
        }

        public override void OnGUI(Rect position, SerializedProperty prop, GUIContent label)
        {
            var header = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            prop.isExpanded = EditorGUI.Foldout(header, prop.isExpanded, Header(prop), true);
            if (!prop.isExpanded) return;

            EditorGUI.indentLevel++;
            float y = position.y + EditorGUIUtility.singleLineHeight + Pad;
            foreach (var name in VisibleFields(prop))
            {
                var p = prop.FindPropertyRelative(name);
                float h = EditorGUI.GetPropertyHeight(p, true);
                EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), p, true);
                y += h + Pad;
            }
            EditorGUI.indentLevel--;
        }

        public override float GetPropertyHeight(SerializedProperty prop, GUIContent label)
        {
            float h = EditorGUIUtility.singleLineHeight;
            if (!prop.isExpanded) return h;
            foreach (var name in VisibleFields(prop))
                h += EditorGUI.GetPropertyHeight(prop.FindPropertyRelative(name), true) + Pad;
            return h;
        }
    }
}

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
                    // Ceiling ignores zones/facing/snap (for now).
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
                    yield return "yawRange";
                    yield return "subCellJitter";
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
                    yield return "preferredZone";
                    yield return "allowCenter";
                    yield return "facing";
                    yield return "yawRange";
                    yield return "subCellJitter";
                    yield return "snapToWall";
                    if (snap) yield return "wallGap";
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
                    detail = "Ceiling";
                    break;
                default:
                    var zone = (RoomZone)prop.FindPropertyRelative("preferredZone").enumValueIndex;
                    detail = prop.FindPropertyRelative("guaranteed").boolValue
                        ? $"Scatter ×{prop.FindPropertyRelative("count").intValue} · {zone}"
                        : $"Scatter · {zone}";
                    break;
            }
            return $"{name}  —  {detail}";
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

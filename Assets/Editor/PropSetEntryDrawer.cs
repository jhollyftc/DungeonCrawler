using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Inspector drawer for PropSet entries: a summary foldout header
    /// ("Throne — Feature · Back Center") instead of "Element N", bold section
    /// headers, and only the fields that apply to the chosen anchor (Feature
    /// entries don't show scatter density, scatter entries don't show wall
    /// sides, wallGap only appears when a snap uses it, etc). Pure editor UX;
    /// the serialized data is untouched.
    /// </summary>
    [CustomPropertyDrawer(typeof(PropSet.PropEntry))]
    public class PropSetEntryDrawer : PropertyDrawer
    {
        const float Pad = 2f;
        const float HeaderGap = 6f;

        // A yielded token starting with '§' is a bold section header, not a
        // serialized field. Keeps VisibleFields the single source of truth for
        // both layout and height.
        static bool IsHeader(string s) => s.Length > 0 && s[0] == '§';

        static IEnumerable<string> VisibleFields(SerializedProperty prop)
        {
            yield return "label";
            yield return "prefabs";
            yield return "anchor";
            yield return "tier";

            var anchor = (PropAnchor)prop.FindPropertyRelative("anchor").enumValueIndex;
            bool guaranteed = prop.FindPropertyRelative("guaranteed").boolValue;
            bool insideCorner = prop.FindPropertyRelative("snapToInsideCorner").boolValue;

            switch (anchor)
            {
                case PropAnchor.Feature:
                    yield return "§Position";
                    yield return "featurePositionMode";
                    if ((FeaturePositionMode)prop.FindPropertyRelative("featurePositionMode").enumValueIndex
                        == FeaturePositionMode.WallSide)
                    {
                        yield return "featureWallSide";
                        yield return "featureSpot";
                        yield return "snapToWall";
                        if (prop.FindPropertyRelative("snapToWall").boolValue) yield return "wallGap";
                    }
                    yield return "§Orientation";
                    yield return "featureFacing";
                    yield return "featureYaw";
                    yield return "§Spacing";
                    yield return "minSpacing";
                    break;

                case PropAnchor.NearPropAsset:
                    yield return "§Host";
                    yield return "hostLabel";
                    yield return "chancePerHost";
                    yield return "§Placement";
                    yield return "yawRange";
                    yield return "subCellJitter";
                    yield return "minSpacing";
                    yield return "sharesTile";
                    break;

                case PropAnchor.NearWallAsset:
                    yield return "§Host";
                    yield return "hostLabel";     // matches WallAsset.featureLabel
                    yield return "chancePerHost";
                    yield return "§Placement";
                    yield return "wallGap";       // distance off the wall
                    yield return "subCellJitter"; // lateral along the wall
                    yield return "yawRange";
                    yield return "minSpacing";
                    yield return "sharesTile";
                    break;

                case PropAnchor.CeilingHung:
                {
                    yield return "§Count";
                    yield return "guaranteed";
                    if (guaranteed) yield return "count";
                    else { yield return "chancePerCell"; yield return "maxPerRoom"; }

                    yield return "§Layout";
                    yield return "ceilingLayout";
                    bool grid = (CeilingLayout)prop.FindPropertyRelative("ceilingLayout").enumValueIndex == CeilingLayout.Grid;
                    if (grid) yield return "gridStride";

                    yield return "§Where";
                    yield return "preferredZones";
                    yield return "allowCenter";
                    yield return "facing";
                    yield return "yawRange";

                    bool ceilCorner = !grid && insideCorner;
                    yield return "§Snapping";
                    if (!grid)
                    {
                        yield return "snapToInsideCorner";
                        if (!ceilCorner) yield return "snapToCeilingWall";
                        if (ceilCorner || prop.FindPropertyRelative("snapToCeilingWall").boolValue) yield return "wallGap";
                    }
                    if (!ceilCorner) yield return "subCellJitter"; // corner ignores jitter
                    yield return "sharesTile";
                    break;
                }

                case PropAnchor.WallMounted:
                    yield return "§Count";
                    yield return "guaranteed";
                    if (guaranteed) yield return "count";
                    else { yield return "chancePerCell"; yield return "maxPerRoom"; }

                    yield return "§Mount";
                    yield return "mountHeight";
                    yield return "mountHeightJitter";
                    yield return "wallGap";       // distance off the wall face
                    yield return "subCellJitter"; // lateral spread along the wall
                    yield return "yawRange";      // narrow — variation on the out-from-wall facing
                    break;

                default: // FloorScatter
                    yield return "§Count";
                    yield return "guaranteed";
                    if (guaranteed) yield return "count";
                    else { yield return "chancePerCell"; yield return "maxPerRoom"; }

                    yield return "§Where";
                    yield return "preferredZones";
                    yield return "allowCenter";
                    yield return "facing";
                    yield return "yawRange";

                    yield return "§Snapping";
                    yield return "snapToInsideCorner";
                    if (!insideCorner) yield return "snapToWall";
                    if (insideCorner || prop.FindPropertyRelative("snapToWall").boolValue) yield return "wallGap";
                    if (!insideCorner) yield return "subCellJitter"; // corner ignores jitter
                    yield return "minSpacing";
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
                case PropAnchor.NearPropAsset:
                    string hl = prop.FindPropertyRelative("hostLabel").stringValue;
                    detail = $"Near prop · {(string.IsNullOrEmpty(hl) ? "(no host)" : hl)}";
                    break;
                case PropAnchor.NearWallAsset:
                    string wl = prop.FindPropertyRelative("hostLabel").stringValue;
                    detail = $"Near wall · {(string.IsNullOrEmpty(wl) ? "(no host)" : wl)}";
                    break;
                default:
                    string zones = ZonesLabel(prop.FindPropertyRelative("preferredZones").intValue);
                    string corner = prop.FindPropertyRelative("snapToInsideCorner").boolValue ? " · corner" : "";
                    detail = prop.FindPropertyRelative("guaranteed").boolValue
                        ? $"Scatter ×{prop.FindPropertyRelative("count").intValue} · {zones}{corner}"
                        : $"Scatter · {zones}{corner}";
                    break;
            }
            return $"{name}  —  {detail}";
        }

        static string ZonesLabel(int mask)
        {
            if (mask == 0) return "no zone";
            if (mask == (int)RoomZoneMask.Perimeter) return "Perimeter";
            var parts = new List<string>();
            foreach (RoomZone z in System.Enum.GetValues(typeof(RoomZone)))
                if ((mask & (1 << (int)z)) != 0) parts.Add(z.ToString());
            return string.Join("+", parts);
        }

        public override void OnGUI(Rect position, SerializedProperty prop, GUIContent label)
        {
            var headerRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            prop.isExpanded = EditorGUI.Foldout(headerRect, prop.isExpanded, Header(prop), true);
            if (!prop.isExpanded) return;

            EditorGUI.indentLevel++;
            float line = EditorGUIUtility.singleLineHeight;
            float y = position.y + line + Pad;
            foreach (var name in VisibleFields(prop))
            {
                if (IsHeader(name))
                {
                    y += HeaderGap;
                    EditorGUI.LabelField(new Rect(position.x, y, position.width, line), name.Substring(1), EditorStyles.boldLabel);
                    y += line + Pad;
                }
                else
                {
                    var p = prop.FindPropertyRelative(name);
                    float h = EditorGUI.GetPropertyHeight(p, true);
                    EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), p, true);
                    y += h + Pad;
                }
            }
            EditorGUI.indentLevel--;
        }

        public override float GetPropertyHeight(SerializedProperty prop, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float h = line;
            if (!prop.isExpanded) return h;
            foreach (var name in VisibleFields(prop))
                h += IsHeader(name) ? HeaderGap + line + Pad
                                    : EditorGUI.GetPropertyHeight(prop.FindPropertyRelative(name), true) + Pad;
            return h;
        }
    }
}

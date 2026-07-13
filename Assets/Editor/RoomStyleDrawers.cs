using UnityEditor;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Element labels for RoomStyle's nested lists: instead of "Element 0",
    /// each entry's foldout reads its room type (and a short summary), so a
    /// long roomWalls or roomOpenings list scans at a glance. Default field
    /// drawing inside; only the label changes.
    /// </summary>
    static class RoomStyleDrawerUtil
    {
        public static string TypeName(SerializedProperty prop)
        {
            var t = prop.FindPropertyRelative("type");
            return t.enumDisplayNames[t.enumValueIndex];
        }

        public static void Draw(Rect r, SerializedProperty prop, string header)
        {
            EditorGUI.PropertyField(r, prop, new GUIContent(header), true);
        }

        public static float Height(SerializedProperty prop) =>
            EditorGUI.GetPropertyHeight(prop, true);
    }

    [CustomPropertyDrawer(typeof(RoomStyle.Entry))]
    public class RoomStyleEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect r, SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Draw(r, p, $"{RoomStyleDrawerUtil.TypeName(p)} — torches");
        public override float GetPropertyHeight(SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Height(p);
    }

    [CustomPropertyDrawer(typeof(RoomStyle.WallSet))]
    public class RoomStyleWallSetDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect r, SerializedProperty p, GUIContent l)
        {
            int count = p.FindPropertyRelative("walls").arraySize;
            RoomStyleDrawerUtil.Draw(r, p, $"{RoomStyleDrawerUtil.TypeName(p)} — {count} wall asset(s)");
        }
        public override float GetPropertyHeight(SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Height(p);
    }

    [CustomPropertyDrawer(typeof(RoomStyle.OpeningSet))]
    public class RoomStyleOpeningSetDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect r, SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Draw(r, p, $"{RoomStyleDrawerUtil.TypeName(p)} — openings");
        public override float GetPropertyHeight(SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Height(p);
    }

    [CustomPropertyDrawer(typeof(RoomStyle.PropSetEntry))]
    public class RoomStylePropSetEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect r, SerializedProperty p, GUIContent l)
        {
            var set = p.FindPropertyRelative("props").objectReferenceValue;
            string setName = set != null ? set.name : "(no set)";
            RoomStyleDrawerUtil.Draw(r, p, $"{RoomStyleDrawerUtil.TypeName(p)} — {setName}");
        }
        public override float GetPropertyHeight(SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Height(p);
    }

    /// <summary>
    /// WallAsset elements summarize as "PrefabName [BMT] ×cap !props !torch"
    /// — band eligibility, per-room cap, and placement restrictions visible
    /// without expanding.
    /// </summary>
    [CustomPropertyDrawer(typeof(RoomStyle.WallAsset))]
    public class RoomStyleWallAssetDrawer : PropertyDrawer
    {
        static string Header(SerializedProperty p)
        {
            var prefab = p.FindPropertyRelative("prefab").objectReferenceValue;
            string name = prefab != null ? prefab.name : "(no prefab)";

            string bands =
                (p.FindPropertyRelative("bottom").boolValue ? "B" : "") +
                (p.FindPropertyRelative("middle").boolValue ? "M" : "") +
                (p.FindPropertyRelative("top").boolValue ? "T" : "");
            if (bands.Length == 0) bands = "-";

            string s = $"{name} [{bands}]";
            int cap = p.FindPropertyRelative("maxPerRoom").intValue;
            if (cap > 0) s += $" ×{cap}";
            if (!p.FindPropertyRelative("allowPropsInFront").boolValue) s += " !props";
            if (!p.FindPropertyRelative("allowTorch").boolValue) s += " !torch";
            return s;
        }

        public override void OnGUI(Rect r, SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Draw(r, p, Header(p));
        public override float GetPropertyHeight(SerializedProperty p, GUIContent l) =>
            RoomStyleDrawerUtil.Height(p);
    }
}

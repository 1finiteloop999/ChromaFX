#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Displays all particle Custom1/Custom2 vector components without relying on
/// Unity's built-in Enum material drawer, which supports fewer entries.
/// </summary>
public sealed class PandaCustomChannelDrawer : MaterialPropertyDrawer
{
    static readonly string[] Options =
    {
        "None",
        "Custom1.x",
        "Custom1.y",
        "Custom1.z",
        "Custom1.w",
        "Custom2.x",
        "Custom2.y",
        "Custom2.z",
        "Custom2.w"
    };

    public override void OnGUI(
        Rect position,
        MaterialProperty property,
        string label,
        MaterialEditor editor)
    {
        EditorGUI.showMixedValue = property.hasMixedValue;
        EditorGUI.BeginChangeCheck();

        int current = Mathf.Clamp(Mathf.RoundToInt(property.floatValue), 0, 8);
        int selected = EditorGUI.Popup(position, label, current, Options);

        if (EditorGUI.EndChangeCheck())
            property.floatValue = selected;

        EditorGUI.showMixedValue = false;
    }

    public override float GetPropertyHeight(
        MaterialProperty property,
        string label,
        MaterialEditor editor)
    {
        return EditorGUIUtility.singleLineHeight;
    }
}
#endif

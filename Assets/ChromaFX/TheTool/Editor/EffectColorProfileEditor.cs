using UnityEditor;
using UnityEngine;

namespace ChromaFX.EditorTools
{
    /// <summary>
    /// EffectColorProfile 的分组 Inspector。
    ///
    /// 只重排显示，不改变任何参数与计算——常用的 7 个常驻，其余 23 个
    /// 按用途收进折叠组。折叠状态存 EditorPrefs，跨会话保留。
    /// </summary>
    [CustomEditor(typeof(EffectColorProfile))]
    public class EffectColorProfileEditor : Editor
    {
        const string PrefKey = "ChromaFX.ProfileFoldout.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "The settings you use often are always shown. The rest are grouped below — " +
                "they still work, you just rarely need them.\n" +
                "Full guide: TheTool/ChromaFX_Parameter_Guide.md",
                MessageType.None);

            // ── 常用（常驻） ──────────────────────────
            EditorGUILayout.Space(4);
            GUILayout.Label("Common", EditorStyles.boldLabel);
            Prop("hotLightnessDelta", "Hot End Brightness", "How bright the hot end is. Raise it if flame cores look dull.");
            Prop("hotChromaScale", "Hot End Color Keep", "Lower = closer to white hot. Higher = hot end stays colorful.");
            Prop("birthTravelTowardHot", "Birth Heat", "How hot a particle starts out.");
            Prop("deathTravelTowardDark", "Death Darkness", "Raise it if embers do not burn out.");
            Prop("neutralChromaCap", "Smoke Color Limit", "Lower it if smoke looks dirty. 0 = pure gray.");
            Prop("deathAlphaCoupling", "Dark Fade Out", "Fades dark particles so they do not look like black paper.");
            Prop("startValueJitter", "Brightness Jitter", "Higher = grainier, lower = more even.");

            EditorGUILayout.Space(6);

            // ── 折叠组 ────────────────────────────────
            if (Group("curve", "Curve Details"))
            {
                Prop("hotHueDrift", "Hot End Warm Shift");
                Prop("lchWarmAnchor", "Warm Anchor Hue", "About 85 = yellow-orange. LCh degrees, not HSV. Best left alone.");
                EditorGUILayout.Space(2);
                Prop("darkLightnessDelta", "Dark End Brightness");
                Prop("darkChromaScale", "Dark End Color Keep");
                Prop("darkHueDrift", "Dark End Neutral Shift");
                EditorGUILayout.Space(2);
                Prop("contrastScaleMin", "Contrast Slider Min", "Changing this alters how the Value Contrast slider feels.");
                Prop("contrastScaleMax", "Contrast Slider Max");
            }

            if (Group("chroma", "Color Purity Rules"))
            {
                Prop("primaryChromaFloor", "Primary Color Floor", "Stops gradients from passing through gray.");
                Prop("accentChromaFloor", "Accent Color Floor");
                Prop("neutralChromaFloor", "Neutral Color Floor", "Keep near 0 or smoke turns colored.");
                Prop("neutralChromaScale", "Neutral Color x Primary");
                Prop("neutralLightnessDelta", "Neutral Brightness Offset");
            }

            if (Group("accent", "Accent"))
            {
                Prop("accentBetaBoost", "Accent Template Pull");
                Prop("accentBlendToPrimary", "Soften Toward Primary", "Changes color difference, not screen area.");
                Prop("accentBudget", "Accent Budget", "Panel warnings only — does not change the picture.");
            }

            if (Group("timing", "Lifetime Timing"))
            {
                Prop("birthEndTime", "Birth Phase Ends");
                Prop("deathStartTime", "Fade Phase Starts");
            }

            if (Group("defaults", "Inherited Defaults (used when a binding is set to Inherit)"))
            {
                Prop("defaultCoolingStrength", "Cooling Strength");
                Prop("defaultDimmingStrength", "Dimming Strength");
                Prop("defaultSmokeFadeStrength", "Smoke Fade Strength");
                EditorGUILayout.Space(2);
                Prop("defaultPrimaryEnergy", "Primary Energy");
                Prop("defaultAccentEnergy", "Accent Energy");
                Prop("defaultNeutralEnergy", "Neutral Energy");
            }

            serializedObject.ApplyModifiedProperties();
        }

        void Prop(string name, string label, string tooltip = null)
        {
            var p = serializedObject.FindProperty(name);
            if (p == null)
            {
                EditorGUILayout.LabelField(label, $"missing field: {name}");
                return;
            }
            EditorGUILayout.PropertyField(p, new GUIContent(label, tooltip ?? name));
        }

        bool Group(string key, string title)
        {
            string pref = PrefKey + key;
            bool open = EditorPrefs.GetBool(pref, false);
            bool now = EditorGUILayout.Foldout(open, title, true, EditorStyles.foldoutHeader);
            if (now != open) EditorPrefs.SetBool(pref, now);
            EditorGUILayout.Space(2);
            return now;
        }
    }
}

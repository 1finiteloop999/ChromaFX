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
                "常用参数在上方常驻。其余参数按用途收进折叠组——" +
                "它们仍然生效，只是日常不需要动。\n" +
                "详细说明见 TheTool/ChromaFX_参数说明.md",
                MessageType.None);

            // ── 常用（常驻） ──────────────────────────
            EditorGUILayout.Space(4);
            GUILayout.Label("常用", EditorStyles.boldLabel);
            Prop("hotLightnessDelta", "热端亮度", "曲线热端有多亮。核心不够白热时调大");
            Prop("hotChromaScale", "热端保留颜色", "调小趋向白热，调大热端仍鲜艳");
            Prop("birthTravelTowardHot", "出生偏热程度", "粒子刚出生时向热端走多远");
            Prop("deathTravelTowardDark", "熄灭变暗程度", "火星要\"烧透\"就调大");
            Prop("neutralChromaCap", "烟雾颜色上限", "烟雾发彩显脏时调小，0 = 纯灰");
            Prop("deathAlphaCoupling", "暗部自动淡出", "防止出现黑色纸片");
            Prop("startValueJitter", "粒子亮度随机", "调大更有颗粒感，调小更整齐");

            EditorGUILayout.Space(6);

            // ── 折叠组 ────────────────────────────────
            if (Group("curve", "色调曲线细节"))
            {
                Prop("hotHueDrift", "热端偏暖角度");
                Prop("lchWarmAnchor", "暖色锚点 (LCh色相角)", "≈85° 为黄橙。与 HSV 色相不同，不建议改");
                EditorGUILayout.Space(2);
                Prop("darkLightnessDelta", "暗端亮度增量");
                Prop("darkChromaScale", "暗端保留颜色");
                Prop("darkHueDrift", "暗端向中性收敛角度");
                EditorGUILayout.Space(2);
                Prop("contrastScaleMin", "对比滑条下限倍率", "改动会让 Value Contrast 的手感变化");
                Prop("contrastScaleMax", "对比滑条上限倍率");
            }

            if (Group("chroma", "分族彩度规则"))
            {
                Prop("primaryChromaFloor", "主色系彩度下限", "防止渐变中途穿过灰色变脏");
                Prop("accentChromaFloor", "跳色系彩度下限");
                Prop("neutralChromaFloor", "中性系彩度下限", "应保持接近 0");
                Prop("neutralChromaScale", "中性彩度 = 主色 ×");
                Prop("neutralLightnessDelta", "中性基准亮度偏移");
            }

            if (Group("accent", "跳色"))
            {
                Prop("accentBetaBoost", "跳色 β 加成");
                Prop("accentBlendToPrimary", "跳色向主色柔化", "色差参数，不是面积参数");
                Prop("accentBudget", "跳色占比预算", "仅用于面板告警，不改变画面");
            }

            if (Group("timing", "生命周期时序"))
            {
                Prop("birthEndTime", "出生段结束时刻");
                Prop("deathStartTime", "熄灭段开始时刻");
            }

            if (Group("defaults", "默认继承值 (绑定设为 Inherit 时使用)"))
            {
                Prop("defaultCoolingStrength", "Cooling 默认强度");
                Prop("defaultDimmingStrength", "Dimming 默认强度");
                Prop("defaultSmokeFadeStrength", "SmokeFade 默认强度");
                EditorGUILayout.Space(2);
                Prop("defaultPrimaryEnergy", "主色系默认能量");
                Prop("defaultAccentEnergy", "跳色系默认能量");
                Prop("defaultNeutralEnergy", "中性系默认能量");
            }

            serializedObject.ApplyModifiedProperties();
        }

        void Prop(string name, string label, string tooltip = null)
        {
            var p = serializedObject.FindProperty(name);
            if (p == null)
            {
                EditorGUILayout.LabelField(label, $"字段缺失：{name}");
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

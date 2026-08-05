using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ChromaFX.EditorTools
{
    /// <summary>
    /// ChromaFX主配色面板（EditorWindow，免Play Mode）。
    /// 方案参数 → 三族曲线 → 逐粒子绑定（可编辑）→ Apply写入。
    /// 所有写入走Undo，Ctrl+Z可整体撤销。
    /// </summary>
    public class ChromaFXWindow : EditorWindow
    {
        [MenuItem("Tools/ChromaFX/Color Panel")]
        static void Open()
        {
            var w = GetWindow<ChromaFXWindow>("ChromaFX");
            w.minSize = new Vector2(380f, 560f);
        }

        ChromaFXTarget target;
        EffectColorProfile profile;

        Color seed = new Color(0.9f, 0.25f, 0.1f);
        HarmonyMode mode = HarmonyMode.Complementary;
        float harmonyStrength = 0.8f;
        float valueContrast = 0.5f;
        RecolorMode recolorMode = RecolorMode.PreservePeakIntensity;
        bool autoApply;
        bool showFitness;
        bool showAdvanced;

        string lastApplied = "";
        FitnessReport lastReport;
        bool hasReport;
        Vector2 scroll;
        readonly HashSet<int> expanded = new HashSet<int>();

        // 每次OnGUI只校验一次（Validate会分配，避免逐区块重复调用）
        bool configValid;
        string configReport = "";

        static readonly string[] RoleShort = { "HL", "MA", "SU", "AC", "SH", "AM", "LI" };
        static readonly Color StripBackdrop = new Color(0.16f, 0.16f, 0.16f);

        void OnEnable() => AutoDetect();

        void AutoDetect()
        {
            if (target == null) target = FindObjectOfType<ChromaFXTarget>();
            if (profile == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:EffectColorProfile");
                if (guids.Length > 0)
                    profile = AssetDatabase.LoadAssetAtPath<EffectColorProfile>(
                        AssetDatabase.GUIDToAssetPath(guids[0]));
            }
        }

        EffectColorProfile ActiveProfile => profile != null ? profile : EffectColorProfile.Default;

        void OnGUI()
        {
            configValid = target != null && target.HasBindings && target.Validate(out configReport);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawTargetSection();
            DrawBaselineSection();

            EditorGUILayout.Space(8);
            var custom = DrawSchemeSection();

            EditorGUILayout.Space(8);
            DrawCurveSection(custom);

            EditorGUILayout.Space(8);
            DrawBindingsSection(custom);

            EditorGUILayout.Space(8);
            DrawWarningsSection();

            EditorGUILayout.Space(8);
            DrawFitnessSection(custom);

            EditorGUILayout.Space(8);
            DrawApplySection(custom);

            EditorGUILayout.EndScrollView();
        }

        // ════════════════════════════════════════════
        // Target / Baseline
        // ════════════════════════════════════════════

        void DrawTargetSection()
        {
            GUILayout.Label("Target", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                target = (ChromaFXTarget)EditorGUILayout.ObjectField(
                    "ChromaFX Target", target, typeof(ChromaFXTarget), true);
                if (GUILayout.Button("Detect", GUILayout.Width(58))) AutoDetect();
            }
            profile = (EffectColorProfile)EditorGUILayout.ObjectField(
                "Color Profile", profile, typeof(EffectColorProfile), false);

            if (target == null)
            {
                EditorGUILayout.HelpBox(
                    "No ChromaFXTarget in scene. Open a scene containing the effect " +
                    "(e.g. eff_Campfire) or drag the component here.", MessageType.Warning);
                return;
            }

            if (!target.HasBindings)
            {
                EditorGUILayout.HelpBox(
                    "No particle bindings yet. Generate them before applying.", MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Build Default Bindings (Demo Fire)"))
                        target.BuildDefaultBindings();
                    if (GUILayout.Button("Migrate From Legacy Slots"))
                        target.MigrateFromLegacySlots();
                }
            }

            if (profile == null)
                EditorGUILayout.HelpBox(
                    "No Color Profile assigned — built-in defaults will be used.", MessageType.Info);
        }

        void DrawBaselineSection()
        {
            if (target == null) return;

            EditorGUILayout.Space(4);
            GUILayout.Label("Baseline", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Status", target.HasBaseline
                ? $"Captured  (v{target.BaselineVersion}, {target.BaselineCount} systems)"
                : "Not captured — first Apply will capture automatically");

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(target.HasBaseline))
                {
                    if (GUILayout.Button("Capture Baseline"))
                    {
                        target.CaptureBaseline(out string r);
                        Debug.Log("[ChromaFX] Capture Baseline:\n" + r);
                    }
                }
                using (new EditorGUI.DisabledScope(!target.HasBaseline))
                {
                    if (GUILayout.Button("Recapture") && EditorUtility.DisplayDialog(
                            "Recapture Baseline",
                            "Overwrite the stored baseline with the CURRENT state?\n\n" +
                            "Only do this when the effect is in its original/ideal state. " +
                            "If ChromaFX colors are currently applied, they would be baked " +
                            "into the baseline permanently.",
                            "Recapture", "Cancel"))
                    {
                        target.CaptureBaseline(out string r);
                        Debug.Log("[ChromaFX] Recapture Baseline:\n" + r);
                    }
                    if (GUILayout.Button("Restore Original"))
                    {
                        target.RestoreBaseline(out string r);
                        Debug.Log("[ChromaFX] Restore Original:\n" + r);
                        hasReport = false;
                    }
                }
            }
        }

        // ════════════════════════════════════════════
        // Scheme
        // ════════════════════════════════════════════

        ColorScheme DrawSchemeSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Scheme", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                showAdvanced = GUILayout.Toggle(showAdvanced,
                    new GUIContent("Advanced", "显示不影响画面的实验与评价控件"),
                    EditorStyles.miniButton, GUILayout.Width(74));
            }

            seed = EditorGUILayout.ColorField(new GUIContent("Seed Color"), seed, true, false, false);
            mode = (HarmonyMode)EditorGUILayout.EnumPopup("Harmony Mode", mode);
            harmonyStrength = EditorGUILayout.Slider(
                new GUIContent("Harmony Strength", "β: 0 = keep original hues, 1 = snap to template axis"),
                harmonyStrength, 0f, 1f);
            valueContrast = EditorGUILayout.Slider(
                new GUIContent("Value Contrast", "Scales the L* span between hot and dark ends"),
                valueContrast, 0f, 1f);
            if (showAdvanced)
                recolorMode = (RecolorMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Material-Only Mode",
                        "Ablation control. Only affects bindings whose write policy is MaterialOnly."),
                    recolorMode);

            var scheme = ColorTheoryEngine.GenerateScheme(new SchemeParams
            {
                seedColor = seed,
                mode = mode,
                harmonyStrength = harmonyStrength,
                valueContrast = valueContrast,
            }, "Custom", profile);

            DrawPaletteSwatches(scheme);
            return scheme;
        }

        void DrawPaletteSwatches(ColorScheme s)
        {
            EditorGUILayout.LabelField("Palette view (7 sampled points)", EditorStyles.miniLabel);
            Rect row = GUILayoutUtility.GetRect(10f, 34f, GUILayout.ExpandWidth(true));
            float w = row.width / 7f;
            for (int i = 0; i < 7; i++)
            {
                var rect = new Rect(row.x + i * w + 1f, row.y, w - 2f, 20f);
                EditorGUI.DrawRect(rect, s.roleColors[i]);
                GUI.Label(new Rect(rect.x, rect.y + 19f, rect.width, 13f),
                    RoleShort[i], EditorStyles.centeredGreyMiniLabel);
            }
        }

        // ════════════════════════════════════════════
        // 三族曲线
        // ════════════════════════════════════════════

        void DrawCurveSection(ColorScheme s)
        {
            GUILayout.Label("Family Curves  (t: hot → dark)", EditorStyles.boldLabel);
            DrawFamilyCurve(s, ColorFamily.Primary, "Primary");
            DrawFamilyCurve(s, ColorFamily.Accent, "Accent");
            DrawFamilyCurve(s, ColorFamily.Neutral, "Neutral");
        }

        void DrawFamilyCurve(ColorScheme s, ColorFamily family, string label)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(58));
                Rect r = GUILayoutUtility.GetRect(10f, 20f, GUILayout.ExpandWidth(true));
                DrawStrip(r, t => s.palette.Sample(family, t));

                // 该族所有绑定的采样点标记
                if (target != null && target.HasBindings)
                {
                    foreach (var b in target.bindings)
                    {
                        if (b?.system == null || b.colorFamily != family) continue;
                        if (b.writePolicy == ColorWritePolicy.Excluded) continue;
                        float x = r.x + Mathf.Clamp01(b.tonePosition) * r.width;
                        EditorGUI.DrawRect(new Rect(x - 1f, r.y, 2f, r.height), Color.white);
                        EditorGUI.DrawRect(new Rect(x - 1f, r.y + r.height - 4f, 2f, 4f), Color.black);
                    }
                }
            }
        }

        static void DrawStrip(Rect r, System.Func<float, Color> sample, int steps = 96)
        {
            EditorGUI.DrawRect(r, StripBackdrop);
            float w = r.width / steps;
            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)(steps - 1);
                EditorGUI.DrawRect(new Rect(r.x + i * w, r.y, w + 1f, r.height), sample(t));
            }
        }

        // ════════════════════════════════════════════
        // 绑定表（可编辑）
        // ════════════════════════════════════════════

        void DrawBindingsSection(ColorScheme scheme)
        {
            if (target == null || !target.HasBindings) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"Bindings ({target.bindings.Count})", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                autoApply = GUILayout.Toggle(autoApply, new GUIContent("Auto Apply",
                    "Re-apply immediately whenever a binding is edited"), EditorStyles.miniButton,
                    GUILayout.Width(80));
            }

            foreach (var b in target.bindings)
            {
                if (b?.system == null) continue;
                DrawBinding(b, scheme);
            }
        }

        void DrawBinding(ParticleBinding b, ColorScheme scheme)
        {
            int id = b.system.GetInstanceID();
            bool open = expanded.Contains(id);
            bool changed = false;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // ── 折叠头：名称 + 生命周期色条 ──
                using (new EditorGUILayout.HorizontalScope())
                {
                    // GUILayout.Toggle + foldout样式：等价于Foldout但可限定宽度
                    bool newOpen = GUILayout.Toggle(open, b.system.name,
                        EditorStyles.foldout, GUILayout.Width(150));
                    if (newOpen != open)
                    {
                        if (newOpen) expanded.Add(id); else expanded.Remove(id);
                    }

                    Rect r = GUILayoutUtility.GetRect(60f, 18f, GUILayout.ExpandWidth(true));
                    if (b.writePolicy == ColorWritePolicy.Excluded)
                    {
                        EditorGUI.DrawRect(r, StripBackdrop);
                        GUI.Label(r, "excluded", EditorStyles.centeredGreyMiniLabel);
                    }
                    else
                    {
                        var g = ParticleColorApplier.BuildLifetimeGradient(
                            b, scheme, target.GetBaseline(b.system), profile);
                        DrawStrip(r, t => g.Evaluate(t), 64);
                    }
                }

                if (!open) return;

                // ── 语义三轴 ──
                var family = (ColorFamily)EditorGUILayout.EnumPopup("Color Family", b.colorFamily);
                if (family != b.colorFamily) { Record("Edit Family"); b.colorFamily = family; changed = true; }

                float tone = EditorGUILayout.Slider(new GUIContent("Tone Position",
                    "0 = hot/bright end, 1 = dark end of the family curve"), b.tonePosition, 0f, 1f);
                if (!Mathf.Approximately(tone, b.tonePosition)) { Record("Edit Tone"); b.tonePosition = tone; changed = true; }

                using (new EditorGUI.DisabledScope(!b.UsesTemporal))
                {
                    var temporal = (TemporalProfile)EditorGUILayout.EnumPopup("Temporal Profile", b.temporalProfile);
                    if (temporal != b.temporalProfile) { Record("Edit Temporal"); b.temporalProfile = temporal; changed = true; }

                    changed |= InheritField("Temporal Strength", ref b.temporalStrength,
                        ActiveProfile.GetDefaultTemporalStrength(b.temporalProfile), 0f, 1f);
                }

                // ── 执行参数 ──
                // 上限8：素材基线HDR本身可能跨一个数量级（本项目0.47~5.99，12.7×），
                // 4倍不足以把偏暗的主体层拉到应有的层级
                changed |= InheritField("Energy Scale", ref b.energyScale,
                    ActiveProfile.GetDefaultEnergy(b.colorFamily), 0f, 8f);

                var policy = (ColorWritePolicy)EditorGUILayout.EnumPopup("Write Policy", b.writePolicy);
                if (policy != b.writePolicy) { Record("Edit Policy"); b.writePolicy = policy; changed = true; }

                if (showAdvanced)
                    changed |= InheritField("Visual Weight", ref b.visualWeight,
                        b.ResolveVisualWeight(), 0f, 100f, "Fitness only — does not change appearance");

                // ── 旅程读数 ──
                if (b.UsesTemporal)
                {
                    var (birth, body, death) = ParticleColorApplier.ToneJourney(b, ActiveProfile);
                    EditorGUILayout.LabelField("Tone Journey",
                        $"birth {birth:F2}  →  body {body:F2}  →  death {death:F2}",
                        EditorStyles.miniLabel);
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(target);
                if (autoApply) Apply(scheme);
            }
        }

        /// <summary>-1=Inherit 的字段：勾选框控制继承，取消后才可编辑具体值</summary>
        bool InheritField(string label, ref float value, float inheritedValue,
            float min, float max, string tooltip = null)
        {
            bool inherit = value < 0f;
            bool changed = false;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

                bool newInherit = GUILayout.Toggle(inherit, "Inherit", EditorStyles.miniButton,
                    GUILayout.Width(56));
                if (newInherit != inherit)
                {
                    Record("Edit " + label);
                    value = newInherit ? ParticleBinding.Inherit : inheritedValue;
                    changed = true;
                    inherit = newInherit;
                }

                if (inherit)
                {
                    GUILayout.Label($"{inheritedValue:F2}  (from profile)", EditorStyles.miniLabel);
                }
                else
                {
                    float v = EditorGUILayout.Slider(value, min, max);
                    if (!Mathf.Approximately(v, value))
                    {
                        Record("Edit " + label);
                        value = Mathf.Max(0f, v);
                        changed = true;
                    }
                }
            }
            return changed;
        }

        void Record(string action)
        {
            if (target != null) Undo.RecordObject(target, "ChromaFX " + action);
        }

        // ════════════════════════════════════════════
        // 告警
        // ════════════════════════════════════════════

        void DrawWarningsSection()
        {
            if (target == null || !target.HasBindings) return;

            if (!string.IsNullOrEmpty(configReport) && configReport != "绑定配置有效，无警告")
                EditorGUILayout.HelpBox(configReport, configValid ? MessageType.Warning : MessageType.Error);

            var hints = new List<string>();
            foreach (var b in target.bindings)
            {
                if (b?.system == null || b.writePolicy == ColorWritePolicy.Excluded) continue;
                string n = b.system.name;

                if (b.colorFamily == ColorFamily.Neutral && b.temporalProfile == TemporalProfile.Cooling)
                    hints.Add($"{n}: Neutral has no hot end to travel to — SmokeFade or Dimming fits better than Cooling.");

                if (b.colorFamily == ColorFamily.Neutral)
                {
                    var bl = target.GetBaseline(b.system);
                    if (bl != null)
                    {
                        ColorSpaces.SeparateIntensity(bl.materialColor, out float i0);
                        if (i0 * b.ResolveEnergy(ActiveProfile) > 2f)
                            hints.Add($"{n}: Neutral family driven at HDR {i0 * b.ResolveEnergy(ActiveProfile):F1} — glowing smoke is likely.");
                    }
                }

                if (b.tonePosition > 0.95f && b.temporalProfile != TemporalProfile.Constant)
                    hints.Add($"{n}: tone {b.tonePosition:F2} leaves almost no room to darken — lower it to give the journey range.");
            }

            if (hints.Count > 0)
                EditorGUILayout.HelpBox("Config hints:\n• " + string.Join("\n• ", hints), MessageType.Info);
        }

        // ════════════════════════════════════════════
        // Fitness（两层：调色板本身 / 实际使用）
        // ════════════════════════════════════════════

        void DrawFitnessSection(ColorScheme scheme)
        {
            showFitness = EditorGUILayout.Foldout(showFitness, "Fitness (metrics only, not a beauty score)", true);
            if (!showFitness) return;

            var pal = ChromaFXFitness.EvaluatePalette(scheme);
            EditorGUILayout.LabelField("Palette", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                $"Template fit: {pal.templateFit:F1}   L* span P/A/N: " +
                $"{pal.primaryLSpan:F0}/{pal.accentLSpan:F0}/{pal.neutralLSpan:F0}\n" +
                $"Family separation ΔE00  Primary-Accent: {pal.dE_PrimaryAccent:F1}   " +
                $"Primary-Neutral: {pal.dE_PrimaryNeutral:F1}\n" +
                $"Primary curve range ΔE00: {pal.primaryRange:F1}   " +
                $"Monotonic: {(pal.AllMonotonic ? "OK" : "VIOLATED")}   Gamut clips: {pal.gamutClips}",
                (pal.AllMonotonic && pal.gamutClips == 0) ? MessageType.None : MessageType.Warning);

            if (target == null || !target.HasBindings) return;

            var eff = ChromaFXFitness.EvaluateEffect(target, scheme, profile);
            EditorGUILayout.LabelField("Effect usage", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                $"Bindings: {eff.coloredCount} colored, {eff.excludedCount} excluded\n" +
                $"Accent share: {eff.accentShare:P0} (budget {eff.accentBudget:P0})" +
                $"{(eff.accentOverBudget ? "  ← OVER" : "")}\n" +
                $"Tone range [{eff.toneMin:F2}, {eff.toneMax:F2}]   " +
                $"bands hot/mid/dark: {eff.hotBand}/{eff.midBand}/{eff.darkBand}\n" +
                $"Energy-order inversions: {eff.energyOrderInversions}   " +
                $"Black-paper risks: {eff.blackPaperRisks}   Glowing neutral: {eff.glowingNeutralCount}   " +
                $"Gamut clips: {eff.gamutClips}" +
                (string.IsNullOrEmpty(eff.worstInversion) ? "" : $"\nWorst inversion: {eff.worstInversion}"),
                (eff.accentOverBudget || eff.blackPaperRisks > 0 || eff.energyOrderInversions > 0
                 || eff.glowingNeutralCount > 0 || eff.gamutClips > 0)
                    ? MessageType.Warning : MessageType.None);

            EditorGUILayout.LabelField(
                "Accent share is approximated from emission rate × lifetime × size² — " +
                "not a rendered pixel measurement.", EditorStyles.miniLabel);

            if (GUILayout.Button("Log CSV row (for experiments)"))
            {
                Debug.Log("[ChromaFX] " + ChromaFXFitness.CsvHeader);
                Debug.Log("[ChromaFX] " + ChromaFXFitness.ToCsvLine(scheme, pal, eff));
            }
        }

        // ════════════════════════════════════════════
        // Apply
        // ════════════════════════════════════════════

        void DrawApplySection(ColorScheme custom)
        {
            bool ok = configValid;

            using (new EditorGUI.DisabledScope(!ok))
            {
                if (GUILayout.Button("Apply Custom Scheme", GUILayout.Height(30)))
                    Apply(custom);
            }

            EditorGUILayout.Space(6);
            GUILayout.Label("Recommended Schemes", EditorStyles.boldLabel);
            foreach (var s in ColorTheoryEngine.GenerateRecommendedSchemes(seed, profile))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(s.name, EditorStyles.miniBoldLabel);
                    DrawPaletteSwatches(s);
                    using (new EditorGUI.DisabledScope(!ok))
                    {
                        if (GUILayout.Button("Apply")) Apply(s);
                    }
                }
            }

            if (hasReport)
            {
                EditorGUILayout.Space(6);
                GUILayout.Label("Last Applied", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    $"{lastApplied}\n" +
                    $"TemplateFit: {lastReport.templateFit:F1}   " +
                    $"L* order violations: {lastReport.valueOrderViolations}\n" +
                    $"ΔE00  Main-Sub: {lastReport.dE_MainSub:F1}   " +
                    $"Main-Accent: {lastReport.dE_MainAccent:F1}   " +
                    $"Main-Shadow: {lastReport.dE_MainShadow:F1}\n" +
                    "Undo with Ctrl+Z.", MessageType.Info);
            }
        }

        void Apply(ColorScheme scheme)
        {
            int written = ParticleColorApplier.Apply(target, scheme, recolorMode, profile);
            lastReport = SchemeFitness.Evaluate(scheme);
            lastApplied = $"\"{scheme.name}\" → {written} targets";
            hasReport = written > 0;
        }
    }
}

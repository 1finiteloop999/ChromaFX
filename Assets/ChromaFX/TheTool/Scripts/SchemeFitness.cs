using System.Text;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// 方案适配度指标（第一期：只计算与记录，不做寻优）。
    ///
    /// 总体思想（文献笔记§四.2）：
    ///   Score = TemplateFit + PairwiseHarmony + ValueOrder + RoleContrast − GamutPenalty
    /// 各子项独立记录，供实验D（客观指标×主观评分相关性）使用。
    /// 命名为"方案适配度(Scheme Fitness)"，不称美感分——
    /// Cohen-Or证明的是自动谐调方法，不证明模板=更受偏好。
    /// </summary>
    public struct FitnessReport
    {
        public string schemeName;

        /// <summary>模板距离（饱和度×角色权重加权，越小越贴合模板）</summary>
        public float templateFit;

        /// <summary>关键角色对ΔE00（Ou&Luo：不做全对等权比较，只测关键对）</summary>
        public float dE_MainSub;        // 期望：小-中（同色系连续性）
        public float dE_MainAccent;     // 期望：大（可辨识跳色）
        public float dE_MainShadow;     // 期望：大（明暗层级）
        public float dE_HighlightMain;  // 期望：中（明度有序色相连续）
        public float dE_MainAmbient;    // 期望：中（统一感）
        public float dE_MainLight;      // 期望：小-中（光色闭环）

        /// <summary>L*明度排序违反次数（期望序：HIGHLIGHT>MAIN>SUB>AMBIENT>SHADOW）</summary>
        public int valueOrderViolations;

        /// <summary>色域裁切计数（第一期HSV生成必在域内，恒0；字段为第二期Lab操作预留）</summary>
        public int gamutClipCount;

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"[{schemeName}] TemplateFit={templateFit:F1} ");
            sb.Append($"ΔE00: M-S={dE_MainSub:F1} M-A={dE_MainAccent:F1} M-Sh={dE_MainShadow:F1} ");
            sb.Append($"H-M={dE_HighlightMain:F1} M-Am={dE_MainAmbient:F1} M-L={dE_MainLight:F1} ");
            sb.Append($"L*违反={valueOrderViolations} 裁切={gamutClipCount}");
            return sb.ToString();
        }
    }

    public static class SchemeFitness
    {
        // 角色权重（Cohen-Or的像素权重→角色权重改写：
        // roleWeight ≈ 屏幕占比×Alpha能量×语义重要性 的先验近似）
        static readonly float[] RoleWeight =
        {
            1.0f,  // Highlight
            1.0f,  // Main
            0.8f,  // Sub
            0.6f,  // Accent
            0.5f,  // Shadow
            0.5f,  // Ambient
            0.4f,  // Light
        };

        /// <summary>计算一套方案的全部指标</summary>
        public static FitnessReport Evaluate(ColorScheme s)
        {
            var r = new FitnessReport { schemeName = s.name };

            // ── TemplateFit ──────────────────────────────
            float[] hues = new float[7];
            float[] sats = new float[7];
            for (int i = 0; i < 7; i++)
            {
                Vector3 hsv = ColorSpaces.RgbToHsv(s.roleColors[i]);
                hues[i] = hsv.x;
                sats[i] = hsv.y;
            }
            r.templateFit = HarmonyTemplates.TemplateDistance(
                hues, sats, RoleWeight, s.rotation, s.sectors);

            // ── 关键角色对ΔE00 ───────────────────────────
            Vector3[] lab = new Vector3[7];
            for (int i = 0; i < 7; i++)
                lab[i] = ColorSpaces.RgbToLab(s.roleColors[i]);

            Vector3 main = lab[(int)ColorRole.Main];
            r.dE_MainSub       = ColorSpaces.DeltaE00(main, lab[(int)ColorRole.Sub]);
            r.dE_MainAccent    = ColorSpaces.DeltaE00(main, lab[(int)ColorRole.Accent]);
            r.dE_MainShadow    = ColorSpaces.DeltaE00(main, lab[(int)ColorRole.Shadow]);
            r.dE_HighlightMain = ColorSpaces.DeltaE00(main, lab[(int)ColorRole.Highlight]);
            r.dE_MainAmbient   = ColorSpaces.DeltaE00(main, lab[(int)ColorRole.Ambient]);
            r.dE_MainLight     = ColorSpaces.DeltaE00(main, lab[(int)ColorRole.Light]);

            // ── L*排序校验 ───────────────────────────────
            // 期望明度序（笔记§三：高光—主体—次主体—环境—暗部）
            ColorRole[] order =
            {
                ColorRole.Highlight, ColorRole.Main, ColorRole.Sub,
                ColorRole.Ambient, ColorRole.Shadow,
            };
            for (int i = 0; i < order.Length - 1; i++)
            {
                if (lab[(int)order[i]].x <= lab[(int)order[i + 1]].x)
                    r.valueOrderViolations++;
            }

            // ── 色域裁切（第二期Lab操作预留） ─────────────
            r.gamutClipCount = 0;

            return r;
        }

        // ════════════════════════════════════════════
        // 自检
        // ════════════════════════════════════════════

        public static bool RunSelfTest()
        {
            bool allPass = true;
            void Check(string name, bool cond)
            {
                allPass &= cond;
                Debug.Log($"[ChromaFX] Fitness {name}: {(cond ? "PASS" : "FAIL")}");
            }

            Color seed = new Color(0.9f, 0.25f, 0.1f);
            var schemes = ColorTheoryEngine.GenerateRecommendedSchemes(seed);
            var reports = new FitnessReport[schemes.Length];
            for (int i = 0; i < schemes.Length; i++)
            {
                reports[i] = Evaluate(schemes[i]);
                Debug.Log("[ChromaFX] " + reports[i].Summary());
            }

            // 1. 类比方案全员在主轴扇区内 → TemplateFit应低于互补方案
            Check("类比TemplateFit更低", reports[0].templateFit <= reports[1].templateFit);
            // 2. 跳色对比 > 同色系连续（互补方案）
            Check("M-A > M-S (互补)", reports[1].dE_MainAccent > reports[1].dE_MainSub);
            // 3. 所有关键对ΔE00为正且有限
            bool positive = true;
            foreach (var r in reports)
                positive &= r.dE_MainSub > 0f && r.dE_MainAccent > 0f && r.dE_MainShadow > 0f
                         && !float.IsNaN(r.templateFit) && !float.IsInfinity(r.templateFit);
            Check("指标为正且有限", positive);
            // 4. 暗部与主体保持结构差（ΔE00 > 10：明显可区分）
            Check("M-Sh结构差", reports[1].dE_MainShadow > 10f);

            Debug.Log(allPass
                ? "[ChromaFX] SchemeFitness自检全部通过 ✓"
                : "[ChromaFX] SchemeFitness自检存在失败项 ✗");
            return allPass;
        }
    }
}

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// 调色板适配度：只评价三条族曲线本身（与具体特效资产无关）。
    /// 命名为"适配度"而非美感分——Cohen-Or证明的是自动谐调方法，
    /// 不证明模板符合度等同于更受偏好。
    /// </summary>
    public struct PaletteFitnessReport
    {
        public float templateFit;        // 曲线采样点的加权模板距离，越小越贴合
        public bool primaryMonotonic, accentMonotonic, neutralMonotonic;
        public float primaryLSpan, accentLSpan, neutralLSpan;   // 热端↔暗端L*跨度
        public float dE_PrimaryAccent;   // 同tone族间可分性
        public float dE_PrimaryNeutral;
        public float primaryRange;       // ΔE00(hot, dark)：曲线自身覆盖范围
        public int gamutClips;           // 三条曲线上发生RGB裁切的采样点数

        public bool AllMonotonic => primaryMonotonic && accentMonotonic && neutralMonotonic;

        public string Summary()
        {
            return $"TemplateFit={templateFit:F1}  L*Span P/A/N={primaryLSpan:F0}/{accentLSpan:F0}/{neutralLSpan:F0}  " +
                   $"family gap dE00 P-A={dE_PrimaryAccent:F1} P-N={dE_PrimaryNeutral:F1}  " +
                   $"Primary range dE00={primaryRange:F1}  monotonic={(AllMonotonic ? "OK" : "BROKEN")}  gamut clips={gamutClips}";
        }
    }

    /// <summary>
    /// 特效适配度：评价"这套调色板在这个特效上实际被怎么用"。
    ///
    /// 调色板再好，若某个特效根本没用到跳色、或全部粒子挤在同一色调段，
    /// 最终观感仍然不成立——这正是7点调色板指标无法回答的问题。
    ///
    /// 第一期不做渲染抓帧：占比由visualWeight近似（发射率×生命期×尺寸²），
    /// **不是渲染像素占比测量**。Bloom能量测量列为二期工作。
    /// </summary>
    public struct EffectFitnessReport
    {
        public int coloredCount, excludedCount;

        public float accentShare;        // visualWeight加权的跳色占比
        public float accentBudget;
        public bool accentOverBudget;

        public float toneMin, toneMax;
        public int hotBand, midBand, darkBand;   // tone三段分布（<0.33 / <0.67 / 其余）

        /// <summary>
        /// 能量序倒挂：色调位置说它明显更暗，实际发光能量却明显更亮。
        ///
        /// 注意不要退化成"tone更大是否L*更低"——那由曲线单调性保证，恒为0，
        /// 测的是构造性质而非特效。真正无保证的是 色调位置 与 HDR能量 两套
        /// 层级是否一致：最终屏幕亮度 ≈ (L*/100) × 基线I0 × energyScale。
        /// 二者矛盾正是"配色单看合理、整体发灰"的常见成因。
        /// </summary>
        public int energyOrderInversions;
        public string worstInversion;    // 最严重的一对，供UI直接提示

        public int blackPaperRisks;      // 熄灭色极暗但Alpha仍高
        public int glowingNeutralCount;  // 中性族被高HDR驱动
        public int gamutClips;

        public string Summary()
        {
            return $"Bindings={coloredCount} (excluded {excludedCount})  Accent share={accentShare:P0}" +
                   $"{(accentOverBudget ? $" OVER budget({accentBudget:P0})" : "")}  " +
                   $"tone[{toneMin:F2},{toneMax:F2}] bands={hotBand}/{midBand}/{darkBand}  " +
                   $"energy inversions={energyOrderInversions}  black-paper risks={blackPaperRisks}  " +
                   $"glowing smoke={glowingNeutralCount}  gamut clips={gamutClips}";
        }
    }

    public static class ChromaFXFitness
    {
        const float BlackPaperLuminance = 12f;   // L*低于此视为接近黑
        const float BlackPaperAlpha = 0.25f;     // 且末端Alpha高于此则有黑纸片风险
        const float GlowingNeutralHdr = 2f;
        const float InversionToneGap = 0.15f;    // tone需明显更暗才算一对
        const float InversionEnergyRatio = 1.5f; // 且能量明显更高

        // ════════════════════════════════════════════
        // 调色板层
        // ════════════════════════════════════════════

        public static PaletteFitnessReport EvaluatePalette(ColorScheme scheme)
        {
            var r = new PaletteFitnessReport();
            var pal = scheme.palette;

            r.primaryMonotonic = pal.primary.IsMonotonic;
            r.accentMonotonic = pal.accent.IsMonotonic;
            r.neutralMonotonic = pal.neutral.IsMonotonic;

            r.primaryLSpan = pal.primary.hot.x - pal.primary.dark.x;
            r.accentLSpan = pal.accent.hot.x - pal.accent.dark.x;
            r.neutralLSpan = pal.neutral.hot.x - pal.neutral.dark.x;

            // 曲线采样点的模板距离（饱和度加权：低彩度色的色相偏移影响小）
            const int steps = 9;
            var hues = new List<float>();
            var sats = new List<float>();
            foreach (ColorFamily f in new[] { ColorFamily.Primary, ColorFamily.Accent, ColorFamily.Neutral })
            {
                for (int i = 0; i < steps; i++)
                {
                    Vector3 hsv = ColorSpaces.RgbToHsv(pal.Sample(f, i / (float)(steps - 1)));
                    hues.Add(hsv.x);
                    sats.Add(hsv.y);
                }
            }
            r.templateFit = HarmonyTemplates.TemplateDistance(
                hues.ToArray(), sats.ToArray(), null, pal.rotation, pal.sectors);

            // 族间可分性（同tone比较）
            float tb = FamilyCurve.TBase;
            r.dE_PrimaryAccent = ColorSpaces.DeltaE00(
                pal.Sample(ColorFamily.Primary, tb), pal.Sample(ColorFamily.Accent, tb));
            r.dE_PrimaryNeutral = ColorSpaces.DeltaE00(
                pal.Sample(ColorFamily.Primary, tb), pal.Sample(ColorFamily.Neutral, tb));

            // Primary曲线自身覆盖范围
            r.primaryRange = ColorSpaces.DeltaE00(
                pal.Sample(ColorFamily.Primary, 0f), pal.Sample(ColorFamily.Primary, 1f));

            r.gamutClips = pal.CountGamutClamps(ColorFamily.Primary)
                         + pal.CountGamutClamps(ColorFamily.Accent)
                         + pal.CountGamutClamps(ColorFamily.Neutral);

            return r;
        }

        // ════════════════════════════════════════════
        // 特效层
        // ════════════════════════════════════════════

        public static EffectFitnessReport EvaluateEffect(ChromaFXTarget target,
            ColorScheme scheme, EffectColorProfile profile)
        {
            var r = new EffectFitnessReport();
            if (target == null || scheme?.palette == null) return r;
            if (profile == null) profile = EffectColorProfile.Default;

            r.accentBudget = profile.accentBudget;
            r.toneMin = 1f;
            r.toneMax = 0f;

            float totalWeight = 0f, accentWeight = 0f;
            // (tone, 有效发光能量, 名称)：用于检测色调层级与能量层级是否矛盾
            var energyList = new List<(float tone, float energy, string name)>();

            foreach (var b in target.bindings)
            {
                if (b?.system == null) continue;

                if (b.writePolicy == ColorWritePolicy.Excluded)
                {
                    r.excludedCount++;
                    continue;
                }
                r.coloredCount++;

                float w = Mathf.Max(0f, b.ResolveVisualWeight());
                totalWeight += w;
                if (b.colorFamily == ColorFamily.Accent) accentWeight += w;

                r.toneMin = Mathf.Min(r.toneMin, b.tonePosition);
                r.toneMax = Mathf.Max(r.toneMax, b.tonePosition);
                if (b.tonePosition < 0.33f) r.hotBand++;
                else if (b.tonePosition < 0.67f) r.midBand++;
                else r.darkBand++;

                Color body = scheme.palette.Sample(b.colorFamily, b.tonePosition, out bool oog);
                if (oog) r.gamutClips++;

                var baseline = target.GetBaseline(b.system);
                if (baseline != null)
                {
                    ColorSpaces.SeparateIntensity(baseline.materialColor, out float i0);
                    float hdr = i0 * b.ResolveEnergy(profile);

                    // 屏幕亮度近似 = 色调明度 × HDR能量
                    energyList.Add((b.tonePosition,
                        ColorSpaces.RgbToLab(body).x / 100f * hdr, b.system.name));

                    // 中性族被高HDR驱动 → 发光烟雾
                    if (b.colorFamily == ColorFamily.Neutral && hdr > GlowingNeutralHdr)
                        r.glowingNeutralCount++;
                }

                // 黑纸片：熄灭色极暗但末端Alpha仍高
                if (b.UsesTemporal && b.temporalProfile != TemporalProfile.Constant)
                {
                    var g = ParticleColorApplier.BuildLifetimeGradient(b, scheme, baseline, profile);
                    Color end = g.Evaluate(1f);
                    if (ColorSpaces.RgbToLab(end).x < BlackPaperLuminance && end.a > BlackPaperAlpha)
                        r.blackPaperRisks++;
                }
            }

            r.accentShare = totalWeight > 1e-5f ? accentWeight / totalWeight : 0f;
            r.accentOverBudget = r.accentShare > r.accentBudget + 1e-4f;

            // 能量序倒挂：明显更暗的色调位置却明显更亮
            float worstRatio = 0f;
            for (int i = 0; i < energyList.Count; i++)
            {
                for (int j = 0; j < energyList.Count; j++)
                {
                    if (i == j) continue;
                    var a = energyList[i];
                    var b2 = energyList[j];
                    if (b2.tone < a.tone + InversionToneGap) continue;
                    if (a.energy <= 1e-5f) continue;

                    float ratio = b2.energy / a.energy;
                    if (ratio <= InversionEnergyRatio) continue;

                    r.energyOrderInversions++;
                    if (ratio > worstRatio)
                    {
                        worstRatio = ratio;
                        r.worstInversion =
                            $"{b2.name} (tone {b2.tone:F2}) is {ratio:F1}x brighter than {a.name} (tone {a.tone:F2})";
                    }
                }
            }

            if (r.coloredCount == 0) { r.toneMin = 0f; r.toneMax = 0f; }
            return r;
        }

        /// <summary>
        /// 实验记录用的一行CSV（实验D：客观指标×主观评分相关分析）。
        /// 覆盖两层报告的全部数值字段——worstInversion为诊断文本，不入CSV。
        /// </summary>
        public static string ToCsvLine(ColorScheme s, PaletteFitnessReport p, EffectFitnessReport e)
        {
            var sb = new StringBuilder();
            // 方案参数
            sb.Append($"{ColorUtility.ToHtmlStringRGB(s.p.seedColor)},");
            sb.Append($"{s.p.mode},{s.p.harmonyStrength:F2},{s.p.valueContrast:F2},");
            // 调色板层
            sb.Append($"{p.templateFit:F2},");
            sb.Append($"{p.primaryLSpan:F1},{p.accentLSpan:F1},{p.neutralLSpan:F1},");
            sb.Append($"{(p.primaryMonotonic ? 1 : 0)},{(p.accentMonotonic ? 1 : 0)},{(p.neutralMonotonic ? 1 : 0)},");
            sb.Append($"{p.dE_PrimaryAccent:F2},{p.dE_PrimaryNeutral:F2},{p.primaryRange:F2},{p.gamutClips},");
            // 特效层
            sb.Append($"{e.coloredCount},{e.excludedCount},");
            sb.Append($"{e.accentShare:F3},{e.accentBudget:F3},{(e.accentOverBudget ? 1 : 0)},");
            sb.Append($"{e.toneMin:F2},{e.toneMax:F2},{e.hotBand},{e.midBand},{e.darkBand},");
            sb.Append($"{e.energyOrderInversions},{e.blackPaperRisks},{e.glowingNeutralCount},{e.gamutClips}");
            return sb.ToString();
        }

        public const string CsvHeader =
            "seed,mode,beta,contrast," +
            "templateFit,primaryLSpan,accentLSpan,neutralLSpan," +
            "monoPrimary,monoAccent,monoNeutral," +
            "dE_P_A,dE_P_N,primaryRange,palGamutClips," +
            "coloredCount,excludedCount,accentShare,accentBudget,overBudget," +
            "toneMin,toneMax,hotBand,midBand,darkBand," +
            "energyOrderInversions,blackPaperRisks,glowingNeutral,effGamutClips";

        // ════════════════════════════════════════════
        // 自检
        // ════════════════════════════════════════════

        public static bool RunSelfTest()
        {
            bool allPass = true;
            void Check(string name, bool cond)
            {
                allPass &= cond;
                Debug.Log($"[ChromaFX] Fitness2 {name}: {(cond ? "PASS" : "FAIL")}");
            }

            Color seed = new Color(0.9f, 0.25f, 0.1f);
            var s = ColorTheoryEngine.GenerateScheme(new SchemeParams
            {
                seedColor = seed, mode = HarmonyMode.Complementary,
                harmonyStrength = 0.8f, valueContrast = 0.5f,
            }, "FitnessTest");

            var pal = EvaluatePalette(s);
            Debug.Log("[ChromaFX] Palette: " + pal.Summary());

            Check("all three curves monotonic", pal.AllMonotonic);
            Check("Primary vs Accent separated (dE00>15)", pal.dE_PrimaryAccent > 15f);
            Check("Primary vs Neutral separated (dE00>10)", pal.dE_PrimaryNeutral > 10f);
            Check("Primary curve range wide enough (dE00>40)", pal.primaryRange > 40f);
            Check("L* spans positive", pal.primaryLSpan > 0f && pal.neutralLSpan > 0f);

            // 高对比方案的L*跨度应更大
            var hi = ColorTheoryEngine.GenerateScheme(new SchemeParams
            {
                seedColor = seed, mode = HarmonyMode.Complementary,
                harmonyStrength = 0.8f, valueContrast = 1f,
            }, "HiContrast");
            Check("higher contrast gives wider L* span", EvaluatePalette(hi).primaryLSpan > pal.primaryLSpan);

            var target = Object.FindObjectOfType<ChromaFXTarget>();
            if (target != null && target.HasBindings)
            {
                var eff = EvaluateEffect(target, s, null);
                Debug.Log("[ChromaFX] Effect: " + eff.Summary());
                Check("has bindings", eff.coloredCount > 0);
                Check("tone range valid", eff.toneMax >= eff.toneMin);
                Check("accent share within 0-1", eff.accentShare >= 0f && eff.accentShare <= 1f);
                Debug.Log("[ChromaFX] CSV: " + CsvHeader + "\n[ChromaFX] CSV: " + ToCsvLine(s, pal, eff));
            }
            else
            {
                Debug.Log("[ChromaFX] No ChromaFXTarget with bindings in the scene — effect fitness test skipped.");
            }

            Debug.Log(allPass
                ? "[ChromaFX] ChromaFXFitness self-test passed."
                : "[ChromaFX] ChromaFXFitness self-test FAILED.");
            return allPass;
        }
    }
}

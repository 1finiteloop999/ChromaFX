using System;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>7个配色角色槽位（论文§三：Itten对比 × 明度调子体系）</summary>
    public enum ColorRole
    {
        Highlight = 0, // 核心高光：最高V/HDR，靠近主色，可选暖偏移
        Main      = 1, // 主体：保留用户种子色语义
        Sub       = 2, // 次主体：与主体小-中色差，连续过渡（明暗交界带）
        Accent    = 3, // 跳色：投向对比轴，目标是张力与可辨识度
        Shadow    = 4, // 暗部：最低V，与主体保持结构差
        Ambient   = 5, // 环境辉光：低饱和大面积
        Light     = 6, // 光源：跟随主色色相，高V低S（写Light.color）
    }

    /// <summary>方案生成参数（对应UI四个控件）</summary>
    [Serializable]
    public struct SchemeParams
    {
        public Color seedColor;        // 用户主色
        public HarmonyMode mode;       // 配色模式
        [Range(0f, 1f)] public float harmonyStrength; // β：0保留原色相，1吸附模板轴心
        [Range(0f, 1f)] public float valueContrast;   // 明度对比度：0压缩柔和，1拉开张力
    }

    /// <summary>
    /// 一套生成完成的配色方案 = 三族色调曲线（palette）+ 7点调色板视图（roleColors）。
    ///
    /// 粒子取色的正式入口是 palette.Sample(family, tone)；roleColors是
    /// 供Window色卡、历史Fitness与实验数据使用的固定采样视图。
    /// HDR强度与Alpha由Applier按材质基线恢复，方案内恒为LDR、Alpha=1。
    /// </summary>
    public class ColorScheme
    {
        public string name;
        public SchemeParams p;
        public EffectPalette palette;
        public Color[] roleColors = new Color[7];

        internal float rotation;            // 模板旋转（=主色色相）
        internal HueSector[] sectors;

        public Color Get(ColorRole role) => roleColors[(int)role];

        // 同槽多粒子变体（列表顺序决定颜色）已随三轴绑定模型移除：
        // 层间差异现由 ParticleBinding.tonePosition 显式表达，与顺序无关。
    }

    /// <summary>
    /// ChromaFX颜色引擎（主编排）。
    ///
    /// 管线：种子色 → 和谐模板旋转对齐 → 三族基准锚点（算法确定）
    ///       → 热端/暗端LCh派生（Profile可调） → 可采样的色调曲线
    ///       → 7点调色板视图。
    ///
    /// 空间分工：HSV负责用户输入与色轮几何（种子、模板角度）；
    /// 色调曲线构建在LCh；评价（ΔE00、L*单调）同样在CIELAB系。
    /// 评价由SchemeFitness负责，本类只生成。
    /// </summary>
    public static class ColorTheoryEngine
    {
        /// <summary>
        /// 生成一套配色方案（三族曲线 + 7点调色板视图）。
        /// profile为空时使用EffectColorProfile.Default。
        /// </summary>
        public static ColorScheme GenerateScheme(SchemeParams p, string name = "Scheme",
            EffectColorProfile profile = null)
        {
            if (profile == null) profile = EffectColorProfile.Default;

            var palette = EffectPalette.Build(
                p.seedColor, p.mode,
                Mathf.Clamp01(p.harmonyStrength),
                Mathf.Clamp01(p.valueContrast),
                profile);

            var scheme = new ColorScheme
            {
                name = name,
                p = p,
                palette = palette,
                rotation = palette.rotation,
                sectors = palette.sectors,
                roleColors = palette.BuildPaletteColors(),
            };

            // MAIN严格保留种子色。曲线基准在LCh中即为种子色，此处只是消除
            // RGB→Lab→RGB往返的<1/255误差，使"保留用户输入"成为精确等式。
            Color seedLdr = p.seedColor; seedLdr.a = 1f;
            scheme.roleColors[(int)ColorRole.Main] = seedLdr;

            return scheme;
        }

        /// <summary>
        /// 3套推荐方案（第一期：固定策略，不做Fitness寻优）。
        /// 差异维度 = 模式 × 明度对比度（Schloss&Palmer：色相模式与明度对比是独立维度）
        /// </summary>
        public static ColorScheme[] GenerateRecommendedSchemes(Color seed,
            EffectColorProfile profile = null)
        {
            return new[]
            {
                GenerateScheme(new SchemeParams
                {
                    seedColor = seed, mode = HarmonyMode.Analogous,
                    harmonyStrength = 0.8f, valueContrast = 0.35f,
                }, "A · Soft Unified (Analogous)", profile),

                GenerateScheme(new SchemeParams
                {
                    seedColor = seed, mode = HarmonyMode.Complementary,
                    harmonyStrength = 0.75f, valueContrast = 0.55f,
                }, "B · Classic Contrast (Complementary)", profile),

                GenerateScheme(new SchemeParams
                {
                    seedColor = seed, mode = HarmonyMode.SplitComplementary,
                    harmonyStrength = 0.85f, valueContrast = 0.75f,
                }, "C · High Tension (Split-Complementary)", profile),
            };
        }

        // ════════════════════════════════════════════
        // 自检
        // ════════════════════════════════════════════

        /// <summary>结构断言 + 打印3套方案供肉眼核对</summary>
        public static bool RunSelfTest()
        {
            bool allPass = true;
            void Check(string name, bool cond)
            {
                allPass &= cond;
                Debug.Log($"[ChromaFX] Engine {name}: {(cond ? "PASS" : "FAIL")}");
            }

            Color seed = new Color(0.9f, 0.25f, 0.1f); // 橙红
            var p = new SchemeParams
            {
                seedColor = seed, mode = HarmonyMode.Complementary,
                harmonyStrength = 0.8f, valueContrast = 0.5f,
            };
            var s = GenerateScheme(p);
            var pal = s.palette;
            Vector3 mainHsv = ColorSpaces.RgbToHsv(s.Get(ColorRole.Main));
            Vector3 acHsv   = ColorSpaces.RgbToHsv(s.Get(ColorRole.Accent));
            Vector3 amHsv   = ColorSpaces.RgbToHsv(s.Get(ColorRole.Ambient));

            float L(ColorRole r) => ColorSpaces.RgbToLab(s.Get(r)).x;

            // 1. MAIN严格等于种子
            Check("MAIN=种子色", (Color)s.Get(ColorRole.Main) == new Color(seed.r, seed.g, seed.b, 1f));
            // 2. 明度秩序（在L*空间校验，与生成空间一致）
            Check("L*秩序 HL>MA>SH", L(ColorRole.Highlight) > L(ColorRole.Main)
                                    && L(ColorRole.Main) > L(ColorRole.Shadow));
            // 3. 三族曲线L*单调
            Check("三族曲线L*单调", pal.Validate(out _));
            // 4. Primary曲线基准点 ≈ 种子色（曲线基准即用户输入）
            Check("Primary(TBase)≈种子", ColorSpaces.DeltaE00(
                pal.Sample(ColorFamily.Primary, FamilyCurve.TBase), seed) < 1f);
            // 5. Accent基准来自模板投影（结构性质，参数面不可违反）
            float expectedAccentHue = HarmonyTemplates.ProjectHue(
                pal.rotation, pal.rotation, pal.sectors,
                Mathf.Min(1f, 0.8f * EffectColorProfile.Default.accentBetaBoost), targetSector: 1);
            Check("Accent基准=模板投影",
                ColorSpaces.HueDistance(pal.accentBaseHueHsv, expectedAccentHue) < 0.01f);
            // 6. 互补模式下ACCENT与MAIN色相距离显著
            Check("ACCENT对比", ColorSpaces.HueDistance(acHsv.x, mainHsv.x) > 90f);
            // 7. Neutral彩度受Cap约束（防彩色烟）
            float neutralC = ColorSpaces.LabToLch(ColorSpaces.RgbToLab(
                pal.Sample(ColorFamily.Neutral, FamilyCurve.TBase))).y;
            Check("Neutral彩度≤Cap", neutralC <= EffectColorProfile.Default.neutralChromaCap + 0.5f);
            // 8. AMBIENT饱和度低于MAIN
            Check("AMBIENT低饱和", amHsv.y < mainHsv.y);
            // 9. 类比模式Accent基准留在主轴扇区内
            //（校验基准锚点而非采样色：LCh等色相采样在RGB→HSV后会有数度偏移）
            var pa = p; pa.mode = HarmonyMode.Analogous;
            var sa = GenerateScheme(pa);
            float accDist = ColorSpaces.HueDistance(sa.palette.accentBaseHueHsv, mainHsv.x);
            Check("类比Accent基准在扇区内", accDist <= 46.81f);
            // 10. 曲线采样：tone越大L*越低（层间差异由tone显式表达，与列表顺序无关）
            float La = ColorSpaces.RgbToLab(pal.Sample(ColorFamily.Primary, 0.25f)).x;
            float Lb = ColorSpaces.RgbToLab(pal.Sample(ColorFamily.Primary, 0.60f)).x;
            float Lc = ColorSpaces.RgbToLab(pal.Sample(ColorFamily.Primary, 0.95f)).x;
            Check("tone递增→L*递减", La > Lb && Lb > Lc);
            // 11. 对比度缩放L*跨度
            var pHi = p; pHi.valueContrast = 1f;
            var pLo = p; pLo.valueContrast = 0f;
            float SpanOf(SchemeParams sp)
            {
                var c = GenerateScheme(sp).palette.primary;
                return c.hot.x - c.dark.x;
            }
            Check("对比度缩放L*跨度", SpanOf(pHi) > SpanOf(pLo));

            // 打印3套推荐方案：7点调色板 + Primary曲线采样（供肉眼核对）
            foreach (var scheme in GenerateRecommendedSchemes(seed))
            {
                string line = $"[ChromaFX] {scheme.name}: ";
                for (int i = 0; i < 7; i++)
                    line += $"{(ColorRole)i}=#{ColorUtility.ToHtmlStringRGB(scheme.roleColors[i])} ";
                Debug.Log(line);

                string curve = "[ChromaFX]   Primary曲线 t=0→1: ";
                for (int i = 0; i <= 5; i++)
                    curve += $"#{ColorUtility.ToHtmlStringRGB(scheme.palette.Sample(ColorFamily.Primary, i / 5f))} ";
                curve += " | Neutral曲线: ";
                for (int i = 0; i <= 5; i++)
                    curve += $"#{ColorUtility.ToHtmlStringRGB(scheme.palette.Sample(ColorFamily.Neutral, i / 5f))} ";
                Debug.Log(curve);
            }

            Debug.Log(allPass
                ? "[ChromaFX] ColorTheoryEngine自检全部通过 ✓"
                : "[ChromaFX] ColorTheoryEngine自检存在失败项 ✗");
            return allPass;
        }
    }
}

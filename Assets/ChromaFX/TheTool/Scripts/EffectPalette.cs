using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// 色彩族：粒子从哪条色调曲线取色（三轴绑定模型的轴1）。
    /// Neutral并非严格意义的"色相族"，故整体命名为ColorFamily。
    /// </summary>
    public enum ColorFamily
    {
        Primary = 0, // 主色族：种子色为基准
        Accent  = 1, // 跳色族：和谐模板投影为基准
        Neutral = 2, // 中性族：主色色相 + 极低彩度（烟雾/环境）
    }

    /// <summary>
    /// 一条色调曲线：hot(t=0) → base(t=TBase) → dark(t=1)，在LCh中分段插值。
    ///
    /// 命名说明（论文表述）：
    ///   色调位置轴 Tone Position 控制粒子在"热亮端→暗端"连续路径上的位置。
    ///   沿曲线移动会同时改变L*、C*与少量h，因此不称"明度位置"；
    ///   但要求 L* 随 t 单调下降（由锚点顺序保证，Validate负责校验）。
    /// </summary>
    public struct FamilyCurve
    {
        /// <summary>基准色所在的t（Primary曲线在此处等于种子色）</summary>
        public const float TBase = 0.45f;

        public Vector3 hot;      // LCh
        public Vector3 baseLch;  // LCh
        public Vector3 dark;     // LCh
        public float chromaFloor;
        public bool lockHue;     // Neutral：近灰轴锁定色相，避免无意义的色相旋转
        public float lockedHue;

        public Color Sample(float t) => Sample(t, out _);

        public Color Sample(float t, out bool outOfGamut)
        {
            t = Mathf.Clamp01(t);

            Vector3 a, b;
            float u;
            if (t <= TBase)
            {
                a = hot; b = baseLch;
                u = TBase > 1e-5f ? t / TBase : 0f;
            }
            else
            {
                a = baseLch; b = dark;
                u = (t - TBase) / (1f - TBase);
            }

            float L = Mathf.Lerp(a.x, b.x, u);
            float C = Mathf.Lerp(a.y, b.y, u);
            float h = lockHue
                ? lockedHue
                : ColorSpaces.WrapHue(a.z + ColorSpaces.SignedHueDelta(a.z, b.z) * u);

            // 彩度下限防插值穿灰；但下限不得超过基准彩度——
            // 否则低饱和种子色会被强行加彩（灰色种子必须保持灰）
            float floor = Mathf.Min(chromaFloor, baseLch.y);
            if (C > 0.01f) C = Mathf.Max(C, floor);

            return ColorSpaces.LabToRgb(ColorSpaces.LchToLab(new Vector3(L, C, h)), out outOfGamut);
        }

        /// <summary>L*是否随t单调下降</summary>
        public bool IsMonotonic => hot.x > baseLch.x && baseLch.x > dark.x;
    }

    /// <summary>
    /// 特效调色板：三条色调曲线 + 模板信息。
    ///
    /// 关键结构性质：三条曲线的**基准锚点由算法确定**，Profile中不存在
    /// 能直接改写基准色的字段——
    ///   Primary Base = 用户种子色
    ///   Accent  Base = HarmonyTemplates投影结果（只能来自和谐模板）
    ///   Neutral Base = Primary色相 + 极低彩度
    /// 因此"跳色来自和谐模板"这一理论主张在参数面上不可被违反，
    /// 而非仅靠使用约定。Profile只能调热端/暗端相对基准的LCh偏移。
    ///
    /// 曲线构建受 Smart et al. (2020) 将颜色序列表征为感知色彩空间中
    /// 连续路径的思想启发；本实现使用面向实时编辑的分段LCh插值，
    /// 并未复现其设计挖掘与B样条建模方法。
    /// </summary>
    public class EffectPalette
    {
        public FamilyCurve primary, accent, neutral;

        /// <summary>模板旋转角（=种子色相，HSV度）</summary>
        public float rotation;
        public HueSector[] sectors;

        /// <summary>Accent基准的HSV色相（结构校验用）</summary>
        public float accentBaseHueHsv;

        public FamilyCurve GetCurve(ColorFamily f)
        {
            switch (f)
            {
                case ColorFamily.Accent:  return accent;
                case ColorFamily.Neutral: return neutral;
                default:                  return primary;
            }
        }

        public Color Sample(ColorFamily f, float t) => GetCurve(f).Sample(t);
        public Color Sample(ColorFamily f, float t, out bool oog) => GetCurve(f).Sample(t, out oog);

        // ════════════════════════════════════════════
        // 7点调色板兼容视图
        // ════════════════════════════════════════════

        /// <summary>
        /// 旧7槽角色 → 曲线采样点的固定映射。
        /// 槽位体系降级为"调色板结构视图"：供Window色卡、历史Fitness与实验数据使用；
        /// 实际粒子取色一律走ParticleBinding，不经过此表。
        /// </summary>
        public static readonly (ColorRole role, ColorFamily family, float tone)[] PaletteMap =
        {
            (ColorRole.Highlight, ColorFamily.Primary, 0.05f),
            (ColorRole.Main,      ColorFamily.Primary, FamilyCurve.TBase),
            (ColorRole.Sub,       ColorFamily.Primary, 0.65f),
            (ColorRole.Accent,    ColorFamily.Accent,  0.50f),
            (ColorRole.Shadow,    ColorFamily.Neutral, 0.90f),
            (ColorRole.Ambient,   ColorFamily.Neutral, 0.40f),
            (ColorRole.Light,     ColorFamily.Primary, 0.15f),
        };

        public Color[] BuildPaletteColors()
        {
            var colors = new Color[7];
            foreach (var (role, family, tone) in PaletteMap)
                colors[(int)role] = Sample(family, tone);
            return colors;
        }

        // ════════════════════════════════════════════
        // 校验
        // ════════════════════════════════════════════

        /// <summary>三条曲线的L*单调性与彩度规则校验</summary>
        public bool Validate(out string report)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;

            void CheckCurve(string name, FamilyCurve c)
            {
                if (!c.IsMonotonic)
                {
                    sb.AppendLine($"警告：{name}曲线L*非单调（hot={c.hot.x:F1} base={c.baseLch.x:F1} dark={c.dark.x:F1}）" +
                                  "——种子色过亮或过暗，色调范围被端点截断");
                    ok = false;
                }
            }

            CheckCurve("Primary", primary);
            CheckCurve("Accent", accent);
            CheckCurve("Neutral", neutral);

            report = sb.Length == 0 ? "三族曲线校验通过" : sb.ToString().TrimEnd();
            return ok;
        }

        /// <summary>统计整条曲线上的色域裁切采样点数（GamutPenalty指标）</summary>
        public int CountGamutClamps(ColorFamily f, int steps = 21)
        {
            int n = 0;
            for (int i = 0; i < steps; i++)
            {
                Sample(f, i / (float)(steps - 1), out bool oog);
                if (oog) n++;
            }
            return n;
        }

        // ════════════════════════════════════════════
        // 构建
        // ════════════════════════════════════════════

        public static EffectPalette Build(Color seed, HarmonyMode mode,
            float harmonyStrength, float valueContrast, EffectColorProfile p)
        {
            if (p == null) p = EffectColorProfile.Default;

            Vector3 seedHsv = ColorSpaces.RgbToHsv(seed);
            float rotation = seedHsv.x;
            var sectors = HarmonyTemplates.GetSectors(mode);
            bool hasContrastAxis = sectors.Length > 1;

            // ── Primary基准 = 种子色（硬约束：保留用户输入语义） ──
            Vector3 pBase = ColorSpaces.LabToLch(ColorSpaces.RgbToLab(seed));

            // ── Accent基准 = 和谐模板投影（唯一来源） ──
            float accentHue = hasContrastAxis
                ? HarmonyTemplates.ProjectHue(rotation, rotation, sectors,
                      Mathf.Min(1f, harmonyStrength * p.accentBetaBoost), targetSector: 1)
                : HarmonyTemplates.SampleInSector(rotation, sectors[0], 1f); // 类比模式取扇区边缘

            Color accentRgb = ColorSpaces.HsvToRgb(accentHue, seedHsv.y, seedHsv.z);
            Vector3 aBase = ColorSpaces.LabToLch(ColorSpaces.RgbToLab(accentRgb));

            // 可选柔化：跳色向主色混合。这是色差强度参数，不是面积参数。
            if (p.accentBlendToPrimary > 0.001f)
                aBase = LerpLch(aBase, pBase, p.accentBlendToPrimary);

            // ── Neutral基准 = Primary色相 + 极低彩度 + 略暗 ──
            Vector3 nBase = new Vector3(
                Mathf.Clamp(pBase.x + p.neutralLightnessDelta, 2f, 98f),
                Mathf.Min(pBase.y * p.neutralChromaScale, p.neutralChromaCap),
                pBase.z);

            // valueContrast缩放的是三条曲线热端↔暗端的L*跨度（不再缩放HSV的V偏移）
            float span = Mathf.Lerp(p.contrastScaleMin, p.contrastScaleMax, Mathf.Clamp01(valueContrast));

            return new EffectPalette
            {
                rotation = rotation,
                sectors = sectors,
                accentBaseHueHsv = accentHue,
                primary = MakeCurve(pBase, nBase.z, span, p, p.primaryChromaFloor, false),
                accent  = MakeCurve(aBase, nBase.z, span, p, p.accentChromaFloor,  false),
                neutral = MakeCurve(nBase, nBase.z, span, p, p.neutralChromaFloor, true),
            };
        }

        /// <summary>由基准锚点派生热端与暗端（Profile只能调这两端相对基准的偏移）</summary>
        static FamilyCurve MakeCurve(Vector3 baseLch, float neutralHue, float span,
            EffectColorProfile p, float chromaFloor, bool lockHue)
        {
            // 热端：更亮、去彩（趋向白热）、向暖锚点漂移（漂移量受限）
            float hotL = Mathf.Min(baseLch.x + p.hotLightnessDelta * span, 99f);
            float hotC = baseLch.y * p.hotChromaScale;
            float hotH = baseLch.z;
            if (!lockHue)
            {
                float d = ColorSpaces.SignedHueDelta(baseLch.z, p.lchWarmAnchor);
                hotH = ColorSpaces.WrapHue(baseLch.z + Mathf.Sign(d) * Mathf.Min(Mathf.Abs(d), p.hotHueDrift));
            }

            // 暗端：更暗、去彩、向中性族色相收敛（所有族的暗端趋同于中性轴）
            float darkL = Mathf.Max(baseLch.x + p.darkLightnessDelta * span, 1f);
            float darkC = baseLch.y * p.darkChromaScale;
            float darkH = baseLch.z;
            if (!lockHue)
            {
                float d = ColorSpaces.SignedHueDelta(baseLch.z, neutralHue);
                darkH = ColorSpaces.WrapHue(baseLch.z + Mathf.Sign(d) * Mathf.Min(Mathf.Abs(d), p.darkHueDrift));
            }

            return new FamilyCurve
            {
                hot = new Vector3(hotL, hotC, hotH),
                baseLch = baseLch,
                dark = new Vector3(darkL, darkC, darkH),
                chromaFloor = chromaFloor,
                lockHue = lockHue,
                lockedHue = baseLch.z,
            };
        }

        /// <summary>LCh向量插值（色相走短弧）</summary>
        static Vector3 LerpLch(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(
                Mathf.Lerp(a.x, b.x, t),
                Mathf.Lerp(a.y, b.y, t),
                ColorSpaces.WrapHue(a.z + ColorSpaces.SignedHueDelta(a.z, b.z) * t));
        }
    }
}

using System;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>四种配色模式（对应色轮扇区模板）</summary>
    public enum HarmonyMode
    {
        Analogous,           // 类比：统一柔和，预期和谐度高（Schloss&Palmer 2011）
        Complementary,       // 互补：前景分离与视觉张力
        Triadic,             // 三角：多角色区分度
        SplitComplementary,  // 分裂互补：张力+比互补更缓和
    }

    /// <summary>
    /// 色轮扇区：center为相对模板旋转的中心角，width为扇区全宽（度）。
    /// 颜色色相落在 [center-width/2, center+width/2] 内即视为符合模板。
    /// </summary>
    [Serializable]
    public struct HueSector
    {
        public float center;
        public float width;

        public HueSector(float center, float width)
        {
            this.center = center;
            this.width = width;
        }
    }

    /// <summary>
    /// 和谐模板（Cohen-Or et al. 2006 / Matsuda模板体系）。
    ///
    /// 模板 = 一组可整体旋转的色相扇区。本项目约定：
    ///   - sectors[0] 为主轴扇区，模板旋转 = 用户主色色相（主色永远落在主轴上）
    ///   - sectors[1..] 为对比轴扇区（ACCENT/SHADOW的色相来源）
    ///   - 扇区宽度采用Matsuda原始定义（93.6°/18°），Triadic与SplitComplementary
    ///     为本项目在同一几何框架下的扩展（论文中如实说明）
    ///
    /// 谐调强度β（Tan et al. 2018）：β=0保留原色相，β=1完全吸附扇区轴心。
    /// 部分谐调（中等β）可能优于完全吸附——不要默认β=1。
    /// </summary>
    public static class HarmonyTemplates
    {
        // Matsuda模板扇区宽度常数
        const float WideSector = 93.6f;   // Matsuda "V"型大扇区
        const float NarrowSector = 18f;   // Matsuda "i"型小扇区

        /// <summary>获取指定模式的扇区组（center相对模板旋转）</summary>
        public static HueSector[] GetSectors(HarmonyMode mode)
        {
            switch (mode)
            {
                case HarmonyMode.Analogous:
                    // Matsuda V型：单个93.6°大扇区
                    return new[] { new HueSector(0f, WideSector) };

                case HarmonyMode.Complementary:
                    // Matsuda Y型：主轴93.6°大扇区 + 对面18°小扇区
                    return new[] { new HueSector(0f, WideSector), new HueSector(180f, NarrowSector) };

                case HarmonyMode.Triadic:
                    // 扩展：三个扇区120°等距，主轴较宽、对比轴较窄
                    return new[]
                    {
                        new HueSector(0f, WideSector * 0.5f),
                        new HueSector(120f, NarrowSector),
                        new HueSector(240f, NarrowSector),
                    };

                case HarmonyMode.SplitComplementary:
                    // 扩展：主轴93.6° + 互补两侧±30°各一个18°扇区
                    return new[]
                    {
                        new HueSector(0f, WideSector),
                        new HueSector(150f, NarrowSector),
                        new HueSector(210f, NarrowSector),
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        // ════════════════════════════════════════════
        // 距离与投影
        // ════════════════════════════════════════════

        /// <summary>
        /// 色相到单个扇区的圆弧距离（度）。落在扇区内返回0。
        /// rotation = 模板旋转角（即主色色相）。
        /// </summary>
        public static float SectorDistance(float hue, float rotation, HueSector sector)
        {
            float axis = ColorSpaces.WrapHue(rotation + sector.center);
            float d = ColorSpaces.HueDistance(hue, axis);
            return Mathf.Max(0f, d - sector.width * 0.5f);
        }

        /// <summary>
        /// 找最近扇区，out其索引与轴心色相（绝对角度）。返回到该扇区的距离。
        /// </summary>
        public static float NearestSector(
            float hue, float rotation, HueSector[] sectors,
            out int sectorIndex, out float axisHue)
        {
            sectorIndex = 0;
            float best = float.MaxValue;
            for (int i = 0; i < sectors.Length; i++)
            {
                float d = SectorDistance(hue, rotation, sectors[i]);
                if (d < best)
                {
                    best = d;
                    sectorIndex = i;
                }
            }
            axisHue = ColorSpaces.WrapHue(rotation + sectors[sectorIndex].center);
            return best;
        }

        /// <summary>
        /// 模板距离（Cohen-Or 2006简化式）：
        ///   TemplateDistance = Σ weight_i × saturation_i × arcDist(hue_i, 最近扇区)
        /// 低饱和颜色的色相偏移对视觉影响小，故按饱和度加权。
        /// weights为角色权重（ChromaFX中由槽位重要性给出，可传null等权）。
        /// </summary>
        public static float TemplateDistance(
            float[] hues, float[] saturations, float[] weights,
            float rotation, HueSector[] sectors)
        {
            float sum = 0f;
            for (int i = 0; i < hues.Length; i++)
            {
                float d = NearestSector(hues[i], rotation, sectors, out _, out _);
                float w = weights != null ? weights[i] : 1f;
                sum += w * saturations[i] * d;
            }
            return sum;
        }

        /// <summary>
        /// 谐调强度投影（Tan 2018的β思想）：
        /// 把色相向最近扇区的轴心移动β比例。β=0原样，β=1吸附轴心。
        /// 指定targetSector≥0时强制投向该扇区（角色规则用，如ACCENT投向对比轴）。
        /// </summary>
        public static float ProjectHue(
            float hue, float rotation, HueSector[] sectors, float beta,
            int targetSector = -1)
        {
            float axisHue;
            if (targetSector >= 0 && targetSector < sectors.Length)
                axisHue = ColorSpaces.WrapHue(rotation + sectors[targetSector].center);
            else
                NearestSector(hue, rotation, sectors, out _, out axisHue);

            float delta = ColorSpaces.SignedHueDelta(hue, axisHue);
            return ColorSpaces.WrapHue(hue + delta * Mathf.Clamp01(beta));
        }

        /// <summary>
        /// 在指定扇区内采样色相（同槽多粒子变体用）。
        /// t01: 0=扇区左边界, 0.5=轴心, 1=右边界。
        /// 变体分布在扇区内而不是同一点 → 同色系有差异不雷同（Cohen-Or笔记结论）。
        /// </summary>
        public static float SampleInSector(float rotation, HueSector sector, float t01)
        {
            float axis = rotation + sector.center;
            float offset = (Mathf.Clamp01(t01) - 0.5f) * sector.width;
            return ColorSpaces.WrapHue(axis + offset);
        }

        // ════════════════════════════════════════════
        // 自检
        // ════════════════════════════════════════════

        /// <summary>几何逻辑自检，结果打印Console，全过返回true</summary>
        public static bool RunSelfTest()
        {
            bool allPass = true;
            void Check(string name, bool cond)
            {
                allPass &= cond;
                Debug.Log($"[ChromaFX] Templates {name}: {(cond ? "PASS" : "FAIL")}");
            }

            var comp = GetSectors(HarmonyMode.Complementary);
            float rot = 30f; // 主色H=30°

            // 1. 主轴扇区内距离为0
            Check("主轴内距离=0", SectorDistance(30f, rot, comp[0]) < 1e-4f);
            // 2. 对比轴心(30+180=210)距离为0
            Check("对比轴心距离=0", SectorDistance(210f, rot, comp[1]) < 1e-4f);
            // 3. 扇区外距离 = 圆弧距离-半宽：H=120 距主轴(30±46.8) → 90-46.8=43.2
            Check("扇区外距离", Mathf.Abs(SectorDistance(120f, rot, comp[0]) - 43.2f) < 0.01f);
            // 4. β=1投影吸附轴心：H=120最近对比轴(210)? 距主轴43.2 vs 距对比轴 90-9=81 → 吸附主轴30
            float proj1 = ProjectHue(120f, rot, comp, 1f);
            Check("β=1吸附最近轴", Mathf.Abs(ColorSpaces.HueDistance(proj1, 30f)) < 1e-3f);
            // 5. β=0不动
            Check("β=0保持原色相", Mathf.Abs(ProjectHue(120f, rot, comp, 0f) - 120f) < 1e-4f);
            // 6. β=0.5移动一半
            float proj05 = ProjectHue(120f, rot, comp, 0.5f);
            Check("β=0.5走一半", Mathf.Abs(ColorSpaces.HueDistance(proj05, 75f)) < 0.01f);
            // 7. 强制投向对比扇区
            float projT = ProjectHue(120f, rot, comp, 1f, targetSector: 1);
            Check("强制投对比轴", Mathf.Abs(ColorSpaces.HueDistance(projT, 210f)) < 1e-3f);
            // 8. 投影后模板距离下降
            float[] hues = { 120f };
            float[] sats = { 1f };
            float before = TemplateDistance(hues, sats, null, rot, comp);
            float after = TemplateDistance(new[] { proj1 }, sats, null, rot, comp);
            Check("投影降低模板距离", after < before);
            // 9. 扇区采样边界：t=0/0.5/1
            var ana = GetSectors(HarmonyMode.Analogous);
            Check("采样t=0.5为轴心", Mathf.Abs(SampleInSector(rot, ana[0], 0.5f) - 30f) < 1e-3f);
            Check("采样t=1为右边界", Mathf.Abs(SampleInSector(rot, ana[0], 1f) - ColorSpaces.WrapHue(30f + 46.8f)) < 1e-3f);
            // 10. 色相环绕：rotation=350，扇区采样跨0°
            Check("环绕采样", SampleInSector(350f, ana[0], 1f) < 60f);

            Debug.Log(allPass
                ? "[ChromaFX] HarmonyTemplates自检全部通过 ✓"
                : "[ChromaFX] HarmonyTemplates自检存在失败项 ✗");
            return allPass;
        }
    }
}

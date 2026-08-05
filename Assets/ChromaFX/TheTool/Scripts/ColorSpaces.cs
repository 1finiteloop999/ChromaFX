using System;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// 色彩空间转换与色差计算（ChromaFX算法层地基）。
    ///
    /// 双空间架构（见文献笔记§四.1）：
    ///   HSV        —— 规则生成与用户交互空间（直观、可解释）
    ///   CIELAB/LCh —— 离线评价空间（ΔE00槽位间距、实验指标）
    ///
    /// 约定：
    ///   - 本类输入输出的 Color 均视为 sRGB 编码（Unity颜色选择器语义）
    ///   - 色相H单位为角度 [0,360)；Lab参考白点 D65
    ///   - HDR颜色须先经 SeparateIntensity 分离强度，再做Lab转换
    /// </summary>
    public static class ColorSpaces
    {
        // ════════════════════════════════════════════
        // HSV（H: 0-360°）
        // ════════════════════════════════════════════

        /// <summary>RGB→HSV，返回(H°, S, V)</summary>
        public static Vector3 RgbToHsv(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            return new Vector3(h * 360f, s, v);
        }

        /// <summary>HSV→RGB，h单位角度，自动环绕</summary>
        public static Color HsvToRgb(float h, float s, float v, float a = 1f)
        {
            Color c = Color.HSVToRGB(WrapHue(h) / 360f, Mathf.Clamp01(s), Mathf.Clamp01(v));
            c.a = a;
            return c;
        }

        /// <summary>色相环绕到 [0,360)</summary>
        public static float WrapHue(float h)
        {
            h %= 360f;
            return h < 0f ? h + 360f : h;
        }

        /// <summary>色相环绕距离 dH(a,b)=min(|a-b|, 360-|a-b|)，范围[0,180]</summary>
        public static float HueDistance(float h1, float h2)
        {
            float d = Mathf.Abs(WrapHue(h1) - WrapHue(h2));
            return d > 180f ? 360f - d : d;
        }

        /// <summary>from→to的最短带符号色相差，范围(-180,180]</summary>
        public static float SignedHueDelta(float from, float to)
        {
            float d = WrapHue(to) - WrapHue(from);
            if (d > 180f) d -= 360f;
            else if (d <= -180f) d += 360f;
            return d;
        }

        // ════════════════════════════════════════════
        // HDR强度分离（峰值通道强度保持启发式）
        // 材质_MainColor可能为HDR(如2.99/5.99)，Bloom层级由强度决定；
        // 染色只操作unitColor，强度原样保留。
        // 注意：max(r,g,b)不是光度学亮度，论文中勿称物理亮度。
        // ════════════════════════════════════════════

        /// <summary>分离HDR强度：返回unitColor（max通道=1），out强度倍率</summary>
        public static Color SeparateIntensity(Color hdr, out float intensity)
        {
            intensity = Mathf.Max(hdr.r, Mathf.Max(hdr.g, hdr.b));
            if (intensity <= 1e-6f)
            {
                intensity = 1f;
                return new Color(0f, 0f, 0f, hdr.a); // 纯黑无色相信息，强度按1处理
            }
            return new Color(hdr.r / intensity, hdr.g / intensity, hdr.b / intensity, hdr.a);
        }

        /// <summary>恢复HDR强度</summary>
        public static Color ApplyIntensity(Color unit, float intensity)
        {
            return new Color(unit.r * intensity, unit.g * intensity, unit.b * intensity, unit.a);
        }

        // ════════════════════════════════════════════
        // sRGB ↔ CIELAB / LCh（D65）
        // ════════════════════════════════════════════

        const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;   // D65白点
        const double Eps = 216.0 / 24389.0;                   // CIE ε
        const double Kap = 24389.0 / 27.0;                    // CIE κ

        static double SrgbToLinear(double c) =>
            c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        static double LinearToSrgb(double c) =>
            c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;

        static double Fwd(double t) =>
            t > Eps ? Math.Pow(t, 1.0 / 3.0) : (Kap * t + 16.0) / 116.0;

        static double Inv(double f)
        {
            double f3 = f * f * f;
            return f3 > Eps ? f3 : (116.0 * f - 16.0) / Kap;
        }

        /// <summary>sRGB(LDR)→Lab，返回(L*, a*, b*)。HDR请先SeparateIntensity。</summary>
        public static Vector3 RgbToLab(Color srgb)
        {
            double r = SrgbToLinear(Mathf.Clamp01(srgb.r));
            double g = SrgbToLinear(Mathf.Clamp01(srgb.g));
            double b = SrgbToLinear(Mathf.Clamp01(srgb.b));

            // sRGB(D65)→XYZ
            double x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
            double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
            double z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

            double fx = Fwd(x / Xn), fy = Fwd(y / Yn), fz = Fwd(z / Zn);
            return new Vector3(
                (float)(116.0 * fy - 16.0),
                (float)(500.0 * (fx - fy)),
                (float)(200.0 * (fy - fz)));
        }

        /// <summary>Lab→sRGB。outOfGamut=true表示发生了RGB裁切（色域惩罚指标用）</summary>
        public static Color LabToRgb(Vector3 lab, out bool outOfGamut)
        {
            double fy = (lab.x + 16.0) / 116.0;
            double fx = fy + lab.y / 500.0;
            double fz = fy - lab.z / 200.0;

            double x = Inv(fx) * Xn;
            double y = Inv(fy) * Yn;
            double z = Inv(fz) * Zn;

            // XYZ→线性sRGB
            double r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
            double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
            double b = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;

            outOfGamut = r < -1e-4 || r > 1.0001 || g < -1e-4 || g > 1.0001 || b < -1e-4 || b > 1.0001;

            return new Color(
                (float)LinearToSrgb(Math.Min(Math.Max(r, 0.0), 1.0)),
                (float)LinearToSrgb(Math.Min(Math.Max(g, 0.0), 1.0)),
                (float)LinearToSrgb(Math.Min(Math.Max(b, 0.0), 1.0)), 1f);
        }

        /// <summary>Lab→LCh，返回(L*, C*, h°)</summary>
        public static Vector3 LabToLch(Vector3 lab)
        {
            float c = Mathf.Sqrt(lab.y * lab.y + lab.z * lab.z);
            float h = Mathf.Atan2(lab.z, lab.y) * Mathf.Rad2Deg;
            return new Vector3(lab.x, c, WrapHue(h));
        }

        /// <summary>LCh→Lab</summary>
        public static Vector3 LchToLab(Vector3 lch)
        {
            float hRad = lch.z * Mathf.Deg2Rad;
            return new Vector3(lch.x, lch.y * Mathf.Cos(hRad), lch.y * Mathf.Sin(hRad));
        }

        /// <summary>
        /// LCh空间短路径插值（生命周期渐变键的生成工具）。
        /// RGB直接插值在互补色间会穿过灰色（"脏"），LCh色相走短弧+彩度下限避免此问题。
        /// minChroma仅当两端都有彩度(>1)时施加，避免向真灰色插值时被强行加彩。
        /// </summary>
        public static Color LerpLch(Color a, Color b, float t, float minChroma = 0f)
        {
            Vector3 la = LabToLch(RgbToLab(a));
            Vector3 lb = LabToLch(RgbToLab(b));

            float L = Mathf.Lerp(la.x, lb.x, t);
            float C = Mathf.Lerp(la.y, lb.y, t);
            float h = WrapHue(la.z + SignedHueDelta(la.z, lb.z) * t);
            if (la.y > 1f && lb.y > 1f)
                C = Mathf.Max(C, minChroma);

            Color c = LabToRgb(LchToLab(new Vector3(L, C, h)), out _);
            c.a = Mathf.Lerp(a.a, b.a, t);
            return c;
        }

        /// <summary>LCh降明度（同色相变暗，彩度轻微收缩但保留下限）</summary>
        public static Color DarkenLch(Color c, float amount, float minChroma = 0f)
        {
            Vector3 lch = LabToLch(RgbToLab(c));
            lch.x *= 1f - Mathf.Clamp01(amount);
            float shrunk = lch.y * (1f - 0.35f * Mathf.Clamp01(amount));
            lch.y = lch.y > 1f ? Mathf.Max(shrunk, minChroma) : shrunk;
            Color result = LabToRgb(LchToLab(lch), out _);
            result.a = c.a;
            return result;
        }

        // ════════════════════════════════════════════
        // CIEDE2000（Sharma, Wu & Dalal 2005 实现）
        // kL=kC=kH=1（参考条件）
        // ════════════════════════════════════════════

        /// <summary>两个Lab颜色的ΔE00色差</summary>
        public static float DeltaE00(Vector3 lab1, Vector3 lab2)
        {
            double L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            double L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            const double Pow25_7 = 6103515625.0; // 25^7

            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cbar = (C1 + C2) / 2.0;
            double Cbar7 = Math.Pow(Cbar, 7);
            double G = 0.5 * (1.0 - Math.Sqrt(Cbar7 / (Cbar7 + Pow25_7)));

            double a1p = (1.0 + G) * a1;
            double a2p = (1.0 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);

            double h1p = (Math.Abs(a1p) < 1e-12 && Math.Abs(b1) < 1e-12) ? 0.0 : Math.Atan2(b1, a1p) * 180.0 / Math.PI;
            if (h1p < 0) h1p += 360.0;
            double h2p = (Math.Abs(a2p) < 1e-12 && Math.Abs(b2) < 1e-12) ? 0.0 : Math.Atan2(b2, a2p) * 180.0 / Math.PI;
            if (h2p < 0) h2p += 360.0;

            double dLp = L2 - L1;
            double dCp = C2p - C1p;

            double dhp;
            if (C1p * C2p < 1e-12) dhp = 0.0;
            else
            {
                dhp = h2p - h1p;
                if (dhp > 180.0) dhp -= 360.0;
                else if (dhp < -180.0) dhp += 360.0;
            }
            double dHp = 2.0 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp * Math.PI / 360.0);

            double Lbarp = (L1 + L2) / 2.0;
            double Cbarp = (C1p + C2p) / 2.0;

            double hbarp;
            if (C1p * C2p < 1e-12) hbarp = h1p + h2p;
            else
            {
                double dh = Math.Abs(h1p - h2p);
                if (dh <= 180.0) hbarp = (h1p + h2p) / 2.0;
                else if (h1p + h2p < 360.0) hbarp = (h1p + h2p + 360.0) / 2.0;
                else hbarp = (h1p + h2p - 360.0) / 2.0;
            }

            double T = 1.0
                - 0.17 * Math.Cos((hbarp - 30.0) * Math.PI / 180.0)
                + 0.24 * Math.Cos((2.0 * hbarp) * Math.PI / 180.0)
                + 0.32 * Math.Cos((3.0 * hbarp + 6.0) * Math.PI / 180.0)
                - 0.20 * Math.Cos((4.0 * hbarp - 63.0) * Math.PI / 180.0);

            double dTheta = 30.0 * Math.Exp(-Math.Pow((hbarp - 275.0) / 25.0, 2.0));
            double Cbarp7 = Math.Pow(Cbarp, 7);
            double RC = 2.0 * Math.Sqrt(Cbarp7 / (Cbarp7 + Pow25_7));
            double SL = 1.0 + 0.015 * Math.Pow(Lbarp - 50.0, 2.0) / Math.Sqrt(20.0 + Math.Pow(Lbarp - 50.0, 2.0));
            double SC = 1.0 + 0.045 * Cbarp;
            double SH = 1.0 + 0.015 * Cbarp * T;
            double RT = -Math.Sin(2.0 * dTheta * Math.PI / 180.0) * RC;

            double tL = dLp / SL, tC = dCp / SC, tH = dHp / SH;
            return (float)Math.Sqrt(tL * tL + tC * tC + tH * tH + RT * tC * tH);
        }

        /// <summary>两个sRGB颜色(LDR)的ΔE00</summary>
        public static float DeltaE00(Color c1, Color c2) =>
            DeltaE00(RgbToLab(c1), RgbToLab(c2));

        // ════════════════════════════════════════════
        // 自检（Sharma 2005官方测试向量节选）
        // 验收标准：全部通过 → Step 1b完成
        // ════════════════════════════════════════════

        /// <summary>运行CIEDE2000官方测试向量，结果打印到Console，全过返回true</summary>
        public static bool RunSelfTest()
        {
            // {L1,a1,b1, L2,a2,b2, 期望ΔE00}（Sharma et al. 2005, Table 1）
            double[][] cases =
            {
                new[]{ 50.0000,  2.6772, -79.7751,  50.0000,  0.0000, -82.7485,  2.0425 },
                new[]{ 50.0000,  3.1571, -77.2803,  50.0000,  0.0000, -82.7485,  2.8615 },
                new[]{ 50.0000,  2.8361, -74.0200,  50.0000,  0.0000, -82.7485,  3.4412 },
                new[]{ 50.0000, -1.3802, -84.2814,  50.0000,  0.0000, -82.7485,  1.0000 },
                new[]{ 50.0000,  2.5000,   0.0000,  73.0000, 25.0000, -18.0000, 27.1492 },
                new[]{ 50.0000,  2.5000,   0.0000,  61.0000, -5.0000,  29.0000, 22.8977 },
                new[]{ 50.0000,  2.5000,   0.0000,  56.0000, -27.0000, -3.0000, 31.9030 },
                new[]{ 50.0000,  2.5000,   0.0000,  58.0000, 24.0000,  15.0000, 19.4535 },
            };

            bool allPass = true;
            for (int i = 0; i < cases.Length; i++)
            {
                var t = cases[i];
                float got = DeltaE00(
                    new Vector3((float)t[0], (float)t[1], (float)t[2]),
                    new Vector3((float)t[3], (float)t[4], (float)t[5]));
                bool pass = Mathf.Abs(got - (float)t[6]) < 0.001f;
                allPass &= pass;
                Debug.Log($"[ChromaFX] ΔE00 case {i + 1}: got={got:F4} expect={t[6]:F4} {(pass ? "PASS" : "FAIL")}");
            }

            // 往返转换检查：sRGB→Lab→sRGB 误差应小于1/255
            Color[] roundTrip = { Color.red, Color.green, Color.blue, Color.white, new Color(0.8f, 0.3f, 0.1f) };
            foreach (var c in roundTrip)
            {
                Color back = LabToRgb(RgbToLab(c), out _);
                bool pass = Mathf.Abs(back.r - c.r) < 0.004f && Mathf.Abs(back.g - c.g) < 0.004f && Mathf.Abs(back.b - c.b) < 0.004f;
                allPass &= pass;
                if (!pass) Debug.LogWarning($"[ChromaFX] Lab round-trip failed: {c} -> {back}");
            }

            Debug.Log(allPass
                ? "[ChromaFX] ColorSpaces self-test passed."
                : "[ChromaFX] ColorSpaces self-test FAILED.");
            return allPass;
        }
    }
}

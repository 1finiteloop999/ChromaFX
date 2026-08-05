using System.Collections.Generic;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// 材质染色模式（仅 MaterialOnly 策略使用；全通道下材质只承载能量）。
    /// PreserveLuminance为实验对照模式，带增益上限防蓝紫色炸Bloom。
    /// </summary>
    public enum RecolorMode
    {
        PreservePeakIntensity,
        PreserveLuminance,
        DirectReplace,
    }

    /// <summary>
    /// 颜色写入器（三轴绑定管线）。
    ///
    /// 取色：颜色一律来自 palette.Sample(binding.colorFamily, tone)——
    /// 列表顺序不再影响任何结果。
    ///
    /// 通道职责（最终色 = StartColor × COL × 材质_MainColor）：
    ///   StartColor  个体维度：基线逐键灰度化（保留模式/随机分布/Alpha）+ 抖动
    ///   COL         时间维度：沿自身族曲线的色调旅程（出生tone→本体tone→熄灭tone）
    ///   材质        层级维度：白 × 基线HDR强度 × energyScale
    ///
    /// 时间剖面 = 出生/熄灭两个方向上"走完剩余距离的多少比例"：
    ///   Constant  (0, 0)     恒色
    ///   Cooling   (1, 1)     完整旅程：出生偏热端、熄灭奔暗端
    ///   Dimming   (0, 1)     无出生高亮，只向暗端走
    ///   SmokeFade (0.35, 1)  微亮起后变暗
    ///
    /// 一切写入以基线为唯一原始依据（幂等）；每种策略都会把自己不拥有的
    /// 通道复位到基线，使Apply成为 基线×方案 的纯函数。
    /// </summary>
    public static class ParticleColorApplier
    {
        public const string ColorProperty = "_MainColor";

        const float LuminanceGainClamp = 4f;
        const int MaxGradientKeys = 8;

        /// <summary>应用整套方案。返回成功处理的目标数（光源计1）。</summary>
        public static int Apply(ChromaFXTarget target, ColorScheme scheme,
            RecolorMode materialOnlyMode = RecolorMode.PreservePeakIntensity,
            EffectColorProfile profile = null)
        {
            if (target == null || scheme == null || scheme.palette == null)
            {
                Debug.LogError("[ChromaFX] Apply failed: target or scheme is missing.");
                return 0;
            }
            if (profile == null) profile = EffectColorProfile.Default;

            if (!target.HasBindings)
            {
                Debug.LogError("[ChromaFX] Apply stopped: no particle bindings. " +
                               "Use Build Bindings on the ChromaFXTarget first.");
                return 0;
            }

            if (!target.Validate(out string report))
            {
                Debug.LogError($"[ChromaFX] Apply stopped, binding setup has errors:\n{report}");
                return 0;
            }

            if (!target.HasBaseline)
            {
                target.CaptureBaseline(out string capReport);
                Debug.Log($"[ChromaFX] Baseline captured automatically:\n{capReport}");
            }

            int written = 0;
            var writtenMats = new HashSet<Material>();

            foreach (var binding in target.bindings)
            {
                if (binding?.system == null) continue;

                var baseline = target.GetBaseline(binding.system);
                if (baseline == null)
                {
                    Debug.LogWarning($"[ChromaFX] {binding.system.name} has no baseline (newly added?). Skipped — " +
                                     "use Restore Original, then Recapture Baseline.", binding.system);
                    continue;
                }

                if (ApplyBinding(binding, baseline, scheme, materialOnlyMode, profile, writtenMats))
                    written++;
            }

            if (target.lightSource != null)
            {
#if UNITY_EDITOR
                UnityEditor.Undo.RecordObject(target.lightSource, "ChromaFX Apply");
#endif
                Color lightColor = scheme.Get(ColorRole.Light);
                lightColor.a = 1f;
                target.lightSource.color = lightColor;
                written++;
            }

            Debug.Log($"[ChromaFX] Applied \"{scheme.name}\" to {written} targets.");
            return written;
        }

        // ════════════════════════════════════════════
        // 单绑定处理
        // ════════════════════════════════════════════

        static bool ApplyBinding(ParticleBinding binding, SystemBaseline baseline,
            ColorScheme scheme, RecolorMode materialOnlyMode,
            EffectColorProfile profile, HashSet<Material> writtenMats)
        {
            var ps = binding.system;
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(ps, "ChromaFX Apply");
#endif
            // 绑定取色：唯一入口
            Color bodyColor = scheme.palette.Sample(binding.colorFamily, binding.tonePosition);
            float energy = binding.ResolveEnergy(profile);

            switch (binding.writePolicy)
            {
                case ColorWritePolicy.Excluded:
                    RestoreStartAndCol(ps, baseline);
                    WriteMaterial(baseline, writtenMats, () => baseline.materialColor);
                    return true;

                case ColorWritePolicy.MaterialOnly:
                    // 材质承载颜色；StartColor与COL保持基线，时间剖面被忽略
                    RestoreStartAndCol(ps, baseline);
                    WriteMaterial(baseline, writtenMats, () =>
                    {
                        Color c = RecolorMaterial(baseline.materialColor, bodyColor, materialOnlyMode);
                        return new Color(c.r * energy, c.g * energy, c.b * energy, baseline.materialColor.a);
                    });
                    return true;

                case ColorWritePolicy.FullPipeline:
                default:
                {
                    // 材质 = 白 × 基线I0 × energyScale（只承载能量）
                    ColorSpaces.SeparateIntensity(baseline.materialColor, out float i0);
                    float e = i0 * energy;
                    WriteMaterial(baseline, writtenMats,
                        () => new Color(e, e, e, baseline.materialColor.a));

                    // StartColor = 基线逐键灰度化 + 抖动
                    var mainModule = ps.main;
                    mainModule.startColor = GrayscaleStartColor(baseline.startColor, profile.startValueJitter);

                    // COL = 沿族曲线的色调旅程
                    WriteLifetimeGradient(ps, binding, baseline, scheme, profile);
                    return true;
                }
            }
        }

        /// <summary>共享材质去重写入（取色冲突已由Validate拦截）</summary>
        static void WriteMaterial(SystemBaseline baseline, HashSet<Material> writtenMats,
            System.Func<Color> compute)
        {
            Material mat = baseline.material;
            if (mat == null || !mat.HasProperty(ColorProperty)) return;
            if (!writtenMats.Add(mat)) return;

#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(mat, "ChromaFX Apply");
#endif
            mat.SetColor(ColorProperty, compute());
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(mat);
#endif
        }

        static void RestoreStartAndCol(ParticleSystem ps, SystemBaseline baseline)
        {
            var mainModule = ps.main;
            mainModule.startColor = baseline.startColor.ToMinMax();
            var col = ps.colorOverLifetime;
            col.enabled = baseline.colEnabled;
            col.color = baseline.colColor.ToMinMax();
        }

        // ════════════════════════════════════════════
        // 时间剖面 → 色调旅程
        // ════════════════════════════════════════════

        /// <summary>各剖面在"向热端/向暗端"两个方向上走完剩余距离的比例系数</summary>
        static (float birth, float death) TravelMultipliers(TemporalProfile p)
        {
            switch (p)
            {
                case TemporalProfile.Cooling:   return (1.00f, 1.00f);
                case TemporalProfile.Dimming:   return (0.00f, 1.00f);
                case TemporalProfile.SmokeFade: return (0.35f, 1.00f);
                default:                        return (0.00f, 0.00f); // Constant
            }
        }

        /// <summary>
        /// 色调旅程：出生 / 本体 / 熄灭三个tone。
        /// 旅程 = 向该端剩余距离的一个比例——已在热端的粒子无法更热，
        /// 但仍有很长的距离可以变暗（正是余烬的行为）。
        /// </summary>
        public static (float birth, float body, float death) ToneJourney(
            ParticleBinding binding, EffectColorProfile profile)
        {
            if (profile == null) profile = EffectColorProfile.Default;
            float tone = binding.tonePosition;
            float strength = binding.ResolveTemporalStrength(profile);
            var (birthMul, deathMul) = TravelMultipliers(binding.temporalProfile);

            return (
                Mathf.Clamp01(tone - profile.birthTravelTowardHot * strength * birthMul * tone),
                tone,
                Mathf.Clamp01(tone + profile.deathTravelTowardDark * strength * deathMul * (1f - tone)));
        }

        static void WriteLifetimeGradient(ParticleSystem ps, ParticleBinding binding,
            SystemBaseline baseline, ColorScheme scheme, EffectColorProfile profile)
        {
            var gradient = BuildLifetimeGradient(binding, scheme, baseline, profile);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(gradient);
        }

        /// <summary>
        /// 构建生命周期渐变。写入与UI预览共用此方法，保证面板所见即将写入的结果。
        /// baseline可为null（预览未捕获基线的绑定时Alpha按恒定1处理）。
        /// </summary>
        public static Gradient BuildLifetimeGradient(ParticleBinding binding,
            ColorScheme scheme, SystemBaseline baseline, EffectColorProfile profile)
        {
            if (profile == null) profile = EffectColorProfile.Default;

            var (birthTone, tone, deathTone) = ToneJourney(binding, profile);
            Color bodyColor = scheme.palette.Sample(binding.colorFamily, tone);
            var curve = scheme.palette.GetCurve(binding.colorFamily);
            GradientColorKey[] colorKeys;

            bool isConstant = binding.temporalProfile == TemporalProfile.Constant
                              || (Mathf.Approximately(birthTone, tone) && Mathf.Approximately(deathTone, tone));

            if (isConstant)
            {
                colorKeys = new[]
                {
                    new GradientColorKey(bodyColor, 0f),
                    new GradientColorKey(bodyColor, 1f),
                };
            }
            else
            {
                float t1 = profile.birthEndTime;
                float t2 = profile.deathStartTime;
                float tMid = (t2 + 1f) * 0.5f;

                // 熄灭段插中间键：Unity在RGB中线性插值，多采一点让轨迹留在LCh曲线上
                colorKeys = new[]
                {
                    new GradientColorKey(curve.Sample(birthTone), 0f),
                    new GradientColorKey(bodyColor, t1),
                    new GradientColorKey(bodyColor, t2),
                    new GradientColorKey(curve.Sample(Mathf.Lerp(tone, deathTone, 0.5f)), tMid),
                    new GradientColorKey(curve.Sample(deathTone), 1f),
                };
            }

            GradientAlphaKey[] alphaKeys = BaselineAlphaKeys(baseline);

            // 颜色越趋近黑灰，Alpha越同步下降（防黑色纸片）
            if (!isConstant && profile.deathAlphaCoupling > 0.001f)
            {
                float bodyL = Mathf.Max(ColorSpaces.RgbToLab(bodyColor).x, 1f);
                float deathL = ColorSpaces.RgbToLab(curve.Sample(deathTone)).x;
                float darkness = Mathf.Clamp01(deathL / bodyL);
                alphaKeys = CoupleDeathAlpha(alphaKeys, darkness,
                    profile.deathStartTime, profile.deathAlphaCoupling);
            }

            var gradient = new Gradient { mode = GradientMode.Blend };
            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        // ════════════════════════════════════════════
        // Alpha处理
        // ════════════════════════════════════════════

        /// <summary>
        /// 基线Alpha键。基线COL原本关闭时按约定用恒定1——
        /// 不激活模块中残留但从未生效的旧Alpha键。
        /// </summary>
        static GradientAlphaKey[] BaselineAlphaKeys(SystemBaseline b)
        {
            if (b == null || !b.colEnabled)
                return new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };

            var d = b.colColor;
            Gradient g = d.mode == ParticleSystemGradientMode.TwoGradients ? d.gradientMax : d.gradient;
            if (g != null && g.alphaKeys != null && g.alphaKeys.Length > 0)
                return g.alphaKeys;

            float a = d.mode == ParticleSystemGradientMode.TwoColors ? d.colorMax.a : d.color.a;
            return new[] { new GradientAlphaKey(a, 0f), new GradientAlphaKey(a, 1f) };
        }

        static float EvaluateAlpha(GradientAlphaKey[] keys, float time)
        {
            if (keys == null || keys.Length == 0) return 1f;
            if (time <= keys[0].time) return keys[0].alpha;
            for (int i = 1; i < keys.Length; i++)
            {
                if (time > keys[i].time) continue;
                float span = keys[i].time - keys[i - 1].time;
                float u = span > 1e-5f ? (time - keys[i - 1].time) / span : 0f;
                return Mathf.Lerp(keys[i - 1].alpha, keys[i].alpha, u);
            }
            return keys[keys.Length - 1].alpha;
        }

        /// <summary>
        /// 熄灭段Alpha耦合：deathStart之后按暗度比例衰减。
        /// 必须先在deathStart插入保持键——否则衰减会从它之前的那个键就开始，
        /// 提前把粒子淡掉。
        /// </summary>
        static GradientAlphaKey[] CoupleDeathAlpha(GradientAlphaKey[] keys,
            float darkness, float deathStart, float coupling)
        {
            float endFactor = Mathf.Lerp(1f, darkness, coupling);
            var list = new List<GradientAlphaKey>(keys);
            list.Sort((x, y) => x.time.CompareTo(y.time));

            bool HasKeyAt(float t)
            {
                foreach (var k in list) if (Mathf.Abs(k.time - t) < 1e-3f) return true;
                return false;
            }

            if (!HasKeyAt(deathStart) && list.Count < MaxGradientKeys)
                list.Add(new GradientAlphaKey(EvaluateAlpha(keys, deathStart), deathStart));

            if (!HasKeyAt(1f) && list.Count < MaxGradientKeys)
                list.Add(new GradientAlphaKey(EvaluateAlpha(keys, 1f), 1f));

            list.Sort((x, y) => x.time.CompareTo(y.time));

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].time <= deathStart + 1e-4f) continue;
                float k = (list[i].time - deathStart) / Mathf.Max(1f - deathStart, 1e-4f);
                var key = list[i];
                key.alpha *= Mathf.Lerp(1f, endFactor, Mathf.Clamp01(k));
                list[i] = key;
            }
            return list.ToArray();
        }

        // ════════════════════════════════════════════
        // StartColor灰度化（逐键/逐端点，保留模式与Alpha）
        // ════════════════════════════════════════════

        static Color ToGray(Color c)
        {
            float v = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            return new Color(v, v, v, c.a);
        }

        static Gradient GrayscaleGradient(Gradient src)
        {
            if (src == null) return null;
            var keys = src.colorKeys;
            for (int i = 0; i < keys.Length; i++)
                keys[i].color = ToGray(keys[i].color);
            var g = new Gradient { mode = src.mode };
            g.SetKeys(keys, src.alphaKeys);
            return g;
        }

        static ParticleSystem.MinMaxGradient GrayscaleStartColor(MinMaxGradientData d, float jitter)
        {
            switch (d.mode)
            {
                case ParticleSystemGradientMode.Color:
                {
                    Color g = ToGray(d.color);
                    if (jitter > 0.001f)
                    {
                        Color lo = new Color(g.r * (1f - jitter), g.g * (1f - jitter), g.b * (1f - jitter), g.a);
                        return new ParticleSystem.MinMaxGradient(lo, g);
                    }
                    return new ParticleSystem.MinMaxGradient(g);
                }
                case ParticleSystemGradientMode.TwoColors:
                    // 已有随机区间：只灰度化两端，不叠加jitter（避免重复计算）
                    return new ParticleSystem.MinMaxGradient(ToGray(d.colorMin), ToGray(d.colorMax));
                case ParticleSystemGradientMode.Gradient:
                    return new ParticleSystem.MinMaxGradient(GrayscaleGradient(d.gradient));
                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(
                        GrayscaleGradient(d.gradientMin), GrayscaleGradient(d.gradientMax));
                case ParticleSystemGradientMode.RandomColor:
                {
                    var mm = new ParticleSystem.MinMaxGradient(GrayscaleGradient(d.gradient));
                    mm.mode = ParticleSystemGradientMode.RandomColor;
                    return mm;
                }
                default:
                    return new ParticleSystem.MinMaxGradient(Color.white);
            }
        }

        // ════════════════════════════════════════════
        // MaterialOnly路径的染色运算（消融对照）
        // ════════════════════════════════════════════

        public static Color RecolorMaterial(Color original, Color schemeColor, RecolorMode mode)
        {
            switch (mode)
            {
                case RecolorMode.PreservePeakIntensity:
                {
                    ColorSpaces.SeparateIntensity(original, out float i0);
                    return new Color(schemeColor.r * i0, schemeColor.g * i0, schemeColor.b * i0, original.a);
                }
                case RecolorMode.PreserveLuminance:
                {
                    float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                    float k = Lum(schemeColor) > 1e-5f ? Lum(original) / Lum(schemeColor) : 1f;
                    k = Mathf.Min(k, LuminanceGainClamp);
                    return new Color(schemeColor.r * k, schemeColor.g * k, schemeColor.b * k, original.a);
                }
                case RecolorMode.DirectReplace:
                default:
                    return new Color(schemeColor.r, schemeColor.g, schemeColor.b, original.a);
            }
        }
    }
}

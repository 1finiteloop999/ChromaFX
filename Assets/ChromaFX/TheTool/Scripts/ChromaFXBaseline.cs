using System;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// MinMaxGradient的完整可序列化快照（深拷贝，不持有可变引用）。
    /// 覆盖全部模式：Color / TwoColors / Gradient / TwoGradients / RandomColor。
    /// </summary>
    [Serializable]
    public class MinMaxGradientData
    {
        public ParticleSystemGradientMode mode;
        public Color color = Color.white;
        public Color colorMin = Color.white;
        public Color colorMax = Color.white;
        public Gradient gradient;
        public Gradient gradientMin;
        public Gradient gradientMax;

        public static MinMaxGradientData Capture(ParticleSystem.MinMaxGradient g)
        {
            return new MinMaxGradientData
            {
                mode = g.mode,
                color = g.color,
                colorMin = g.colorMin,
                colorMax = g.colorMax,
                gradient = DeepCopy(g.gradient),
                gradientMin = DeepCopy(g.gradientMin),
                gradientMax = DeepCopy(g.gradientMax),
            };
        }

        public ParticleSystem.MinMaxGradient ToMinMax()
        {
            switch (mode)
            {
                case ParticleSystemGradientMode.Color:
                    return new ParticleSystem.MinMaxGradient(color);
                case ParticleSystemGradientMode.TwoColors:
                    return new ParticleSystem.MinMaxGradient(colorMin, colorMax);
                case ParticleSystemGradientMode.Gradient:
                    return new ParticleSystem.MinMaxGradient(DeepCopy(gradient));
                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(DeepCopy(gradientMin), DeepCopy(gradientMax));
                case ParticleSystemGradientMode.RandomColor:
                {
                    var mm = new ParticleSystem.MinMaxGradient(DeepCopy(gradient));
                    mm.mode = ParticleSystemGradientMode.RandomColor;
                    return mm;
                }
                default:
                    return new ParticleSystem.MinMaxGradient(color);
            }
        }

        public static Gradient DeepCopy(Gradient src)
        {
            if (src == null) return null;
            var g = new Gradient { mode = src.mode };
            g.SetKeys(src.colorKeys, src.alphaKeys); // 结构体数组，值拷贝
            return g;
        }
    }

    /// <summary>单个粒子系统的基线快照（按system引用匹配，不依赖列表顺序）</summary>
    [Serializable]
    public class SystemBaseline
    {
        public ParticleSystem system;
        public Material material;              // 渲染器sharedMaterial引用
        public Color materialColor;            // 完整_MainColor（含HDR与Alpha）
        public MinMaxGradientData startColor;  // main.startColor完整快照
        public bool colEnabled;                // Color over Lifetime启用状态
        public MinMaxGradientData colColor;    // COL完整快照
    }

    [Serializable]
    public class LightBaseline
    {
        public bool captured;
        public Color color;
        public float intensity;
        public float range;
    }
}

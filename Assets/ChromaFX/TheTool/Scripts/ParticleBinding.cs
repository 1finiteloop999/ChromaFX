using System;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// 时间剖面（轴3）：粒子颜色在生命周期内如何运动。
    /// 枚举已冻结；将来需要"保留原COL结构并重映射"时新增语义明确的
    /// BaselineLifetime，而不是含义模糊的PreserveOriginal。
    /// </summary>
    public enum TemporalProfile
    {
        /// <summary>恒色，只保留Alpha动画（辉光）</summary>
        Constant = 0,
        /// <summary>沿自身族曲线由热端滑向暗端（火焰、余烬——黑体冷却隐喻）</summary>
        Cooling = 1,
        /// <summary>同色调位置降明度，不经过热端（暗色火苗）</summary>
        Dimming = 2,
        /// <summary>微亮起后转暗，无出生高亮（烟雾）</summary>
        SmokeFade = 3,
    }

    /// <summary>
    /// 写入策略：只回答"写哪些通道"，不含任何时间语义。
    /// 时间行为完全由TemporalProfile表达，避免两个字段重复描述同一件事。
    /// </summary>
    public enum ColorWritePolicy
    {
        /// <summary>全通道：材质=白×能量，StartColor灰度化，COL由时间剖面生成</summary>
        FullPipeline = 0,
        /// <summary>仅材质承载颜色，StartColor与COL保持基线（旧管线/消融对照）</summary>
        MaterialOnly = 1,
        /// <summary>不参与配色，所有通道保持基线</summary>
        Excluded = 2,
    }

    /// <summary>
    /// 粒子绑定：三轴语义 + 执行参数。保存后是配色映射的唯一真相
    /// （名字映射表只用于生成初始绑定）。
    ///
    ///   Family   回答"从哪条曲线取色"
    ///   Tone     回答"取多亮多暗"
    ///   Temporal 回答"一生如何变化"
    ///   Policy   回答"写哪些通道"
    ///   Strength/Energy 回答"变化与发光有多强"
    ///   VisualWeight    只服务评价，不改变外观
    /// </summary>
    [Serializable]
    public class ParticleBinding
    {
        public ParticleSystem system;

        [Header("语义三轴")]
        public ColorFamily colorFamily = ColorFamily.Primary;
        [Range(0f, 1f)] public float tonePosition = FamilyCurve.TBase;
        public TemporalProfile temporalProfile = TemporalProfile.Cooling;

        [Header("执行参数（-1 = 继承默认）")]
        [Tooltip("-1 继承时间剖面默认强度")]
        public float temporalStrength = Inherit;
        [Tooltip("-1 继承色族默认能量倍率")]
        public float energyScale = Inherit;
        public ColorWritePolicy writePolicy = ColorWritePolicy.FullPipeline;

        [Header("仅供EffectFitness（不影响外观）")]
        [Tooltip("-1 由发射参数自动估计；非渲染像素测量")]
        public float visualWeight = Inherit;

        /// <summary>继承哨兵。新增字段默认为0会导致energyScale=0（粒子变黑）的静默失败，
        /// 因此迁移时必须显式写入-1。</summary>
        public const float Inherit = -1f;

        public bool InheritsTemporalStrength => temporalStrength < 0f;
        public bool InheritsEnergy => energyScale < 0f;
        public bool InheritsVisualWeight => visualWeight < 0f;

        public float ResolveTemporalStrength(EffectColorProfile p) =>
            InheritsTemporalStrength ? p.GetDefaultTemporalStrength(temporalProfile) : temporalStrength;

        public float ResolveEnergy(EffectColorProfile p) =>
            InheritsEnergy ? p.GetDefaultEnergy(colorFamily) : energyScale;

        /// <summary>
        /// 视觉权重：显式值优先，否则由发射参数估计——
        ///   并发粒子数 ≈ 发射率 × 生命期；视觉量 ≈ 并发数 × 尺寸²
        /// 这是基于发射参数的粗略近似，不是渲染像素占比测量。
        /// </summary>
        public float ResolveVisualWeight()
        {
            if (!InheritsVisualWeight) return visualWeight;
            if (system == null) return 0f;

            var main = system.main;
            var emission = system.emission;

            float rate = emission.enabled ? emission.rateOverTime.constantMax : 0f;
            float lifetime = Mathf.Max(main.startLifetime.constantMax, 0.01f);
            float size = Mathf.Max(main.startSize.constantMax, 0.001f);

            // 突发发射按一个周期内的平均速率折算
            for (int i = 0; i < emission.burstCount; i++)
            {
                var burst = emission.GetBurst(i);
                float interval = Mathf.Max(burst.repeatInterval, main.duration, 0.01f);
                rate += burst.count.constantMax / interval;
            }

            return rate * lifetime * size * size;
        }

        /// <summary>参与配色（非Excluded）</summary>
        public bool IsColored => writePolicy != ColorWritePolicy.Excluded;

        /// <summary>时间剖面是否生效（MaterialOnly/Excluded下忽略）</summary>
        public bool UsesTemporal => writePolicy == ColorWritePolicy.FullPipeline;
    }
}

using UnityEngine;
using UnityEngine.Serialization;

namespace ChromaFX
{
    /// <summary>
    /// 配色规则参数集（ScriptableObject资产）。
    ///
    /// 参数面的边界（重要）：
    /// 本Profile**不包含**任何能直接改写三族基准色的字段。基准锚点由算法确定
    /// （Primary=种子色、Accent=和谐模板投影、Neutral=主色相+极低彩度），
    /// Profile只能调节热端/暗端相对基准的LCh偏移与彩度规则。
    /// 这保证"跳色来自和谐模板"在结构上不可被参数违反。
    ///
    /// 同样不入Profile的还有：Matsuda扇区宽度(93.6°/18°)、Fitness角色权重
    /// ——它们是文献常数，改动等于改变理论依据。
    /// </summary>
    [CreateAssetMenu(menuName = "ChromaFX/Effect Color Profile", fileName = "NewColorProfile")]
    public class EffectColorProfile : ScriptableObject
    {
        [Header("── 色调曲线·热端（相对基准的LCh偏移） ──")]
        [Tooltip("热端L*增量（受Value Contrast缩放）")]
        [Range(0f, 60f)] public float hotLightnessDelta = 38f;
        [Tooltip("热端彩度倍率，<1趋向白热")]
        [Range(0f, 1.5f)] public float hotChromaScale = 0.55f;
        [Tooltip("热端向暖锚点漂移的最大角度（LCh色相度）")]
        [Range(0f, 45f)] public float hotHueDrift = 18f;
        [Tooltip("暖锚点（LCh色相角，≈85°为黄橙）。注意LCh色相角与HSV色相不同")]
        [Range(0f, 360f)] public float lchWarmAnchor = 85f;

        [Header("── 色调曲线·暗端 ──")]
        [Tooltip("暗端L*增量（负值，受Value Contrast缩放）")]
        [Range(-70f, 0f)] public float darkLightnessDelta = -42f;
        [Range(0f, 1.5f)] public float darkChromaScale = 0.55f;
        [Tooltip("暗端向中性族色相收敛的最大角度")]
        [Range(0f, 45f)] public float darkHueDrift = 12f;

        [Header("── 明度对比：缩放热端↔暗端的L*跨度 ──")]
        [Range(0.1f, 1f)] public float contrastScaleMin = 0.6f;
        [Range(1f, 2.5f)] public float contrastScaleMax = 1.4f;

        [Header("── 分族彩度规则（防插值穿灰／防彩色烟） ──")]
        [Range(0f, 30f)] public float primaryChromaFloor = 10f;
        [Range(0f, 30f)] public float accentChromaFloor  = 12f;
        [Tooltip("中性族彩度下限应接近0，否则烟雾会变成脏彩色")]
        [Range(0f, 10f)] public float neutralChromaFloor = 0f;
        [Tooltip("中性族基准彩度 = 主色彩度 × 此倍率")]
        [Range(0f, 1f)] public float neutralChromaScale = 0.25f;
        [Tooltip("中性族基准彩度上限（绝对值）")]
        [Range(0f, 20f)] public float neutralChromaCap = 6f;
        [Tooltip("中性族基准相对主色基准的L*偏移")]
        [Range(-30f, 10f)] public float neutralLightnessDelta = -8f;

        [Header("── 跳色 ──")]
        [Tooltip("Accent的β加成倍率（Tan：跳色可用较高β贴近对比轴）")]
        [Range(1f, 2f)] public float accentBetaBoost = 1.2f;
        [Tooltip("跳色向主色混合以柔化色差。这是色差强度参数，不是面积参数。" +
                 "自然篝火默认无Accent绑定，此值不生效")]
        [Range(0f, 1f)] public float accentBlendToPrimary = 0f;
        [Tooltip("跳色允许占据的视觉比例上限，仅供EffectFitness告警使用（Itten面积/延伸对比）")]
        [Range(0f, 1f)] public float accentBudget = 0.25f;

        [Header("── 生命周期色变方向 ──")]
        [Tooltip("出生色沿自身族曲线向热端移动的比例")]
        [FormerlySerializedAs("birthTowardHighlight")]
        [Range(0f, 1f)] public float birthTravelTowardHot = 0.6f;
        [Tooltip("熄灭色沿自身族曲线向暗端移动的比例")]
        [FormerlySerializedAs("deathTowardShadow")]
        [Range(0f, 1f)] public float deathTravelTowardDark = 0.5f;

        [Header("── 每粒子明度抖动 ──")]
        [Tooltip("StartColor为单色时扩展为[1-jitter,1]的随机灰度区间")]
        [Range(0f, 0.6f)] public float startValueJitter = 0.15f;

        [Header("── 生命周期时序 ──")]
        [Range(0f, 0.5f)] public float birthEndTime = 0.15f;
        [Range(0.5f, 1f)] public float deathStartTime = 0.70f;
        [Tooltip("熄灭段颜色变暗时Alpha同步衰减的耦合度（防黑色纸片）")]
        [Range(0f, 1f)] public float deathAlphaCoupling = 0.5f;

        [Header("── 时间剖面默认强度（Binding以-1继承） ──")]
        [Range(0f, 1f)] public float defaultCoolingStrength = 0.8f;
        [Range(0f, 1f)] public float defaultDimmingStrength = 0.5f;
        [Range(0f, 1f)] public float defaultSmokeFadeStrength = 0.35f;

        [Header("── 色族默认能量倍率（Binding以-1继承，粗略默认） ──")]
        [Range(0f, 4f)] public float defaultPrimaryEnergy = 1f;
        [Range(0f, 4f)] public float defaultAccentEnergy  = 1f;
        [Range(0f, 4f)] public float defaultNeutralEnergy = 1f;

        // ════════════════════════════════════════════
        // 访问器
        // ════════════════════════════════════════════

        /// <summary>时间剖面的默认强度（Binding.temporalStrength = -1 时继承）</summary>
        public float GetDefaultTemporalStrength(TemporalProfile profile)
        {
            switch (profile)
            {
                case TemporalProfile.Cooling:   return defaultCoolingStrength;
                case TemporalProfile.Dimming:   return defaultDimmingStrength;
                case TemporalProfile.SmokeFade: return defaultSmokeFadeStrength;
                default:                        return 0f; // Constant
            }
        }

        /// <summary>色族的默认能量倍率（Binding.energyScale = -1 时继承）</summary>
        public float GetDefaultEnergy(ColorFamily family)
        {
            switch (family)
            {
                case ColorFamily.Accent:  return defaultAccentEnergy;
                case ColorFamily.Neutral: return defaultNeutralEnergy;
                default:                  return defaultPrimaryEnergy;
            }
        }

        static EffectColorProfile _default;
        public static EffectColorProfile Default
        {
            get
            {
                if (_default == null)
                {
                    _default = CreateInstance<EffectColorProfile>();
                    _default.name = "Default (Runtime)";
                    _default.hideFlags = HideFlags.HideAndDontSave;
                }
                return _default;
            }
        }
    }
}

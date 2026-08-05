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
    ///
    /// Inspector 由 EffectColorProfileEditor 分组显示；此处的 Header/Tooltip
    /// 仅在关闭自定义 Inspector 时可见。
    /// </summary>
    [CreateAssetMenu(menuName = "ChromaFX/Effect Color Profile", fileName = "NewColorProfile")]
    public class EffectColorProfile : ScriptableObject
    {
        [Header("Tone Curve - Hot End")]
        [Tooltip("How much brighter the hot end is. Raise it if flame cores look dull.")]
        [Range(0f, 60f)] public float hotLightnessDelta = 38f;
        [Tooltip("How much color the hot end keeps. Lower = closer to white hot.")]
        [Range(0f, 1.5f)] public float hotChromaScale = 0.55f;
        [Tooltip("How far the hot end shifts toward the warm anchor.")]
        [Range(0f, 45f)] public float hotHueDrift = 18f;
        [Tooltip("Warm anchor hue in LCh degrees (about 85 = yellow-orange). Not the same as HSV hue.")]
        [Range(0f, 360f)] public float lchWarmAnchor = 85f;

        [Header("Tone Curve - Dark End")]
        [Tooltip("How much darker the dark end is (negative).")]
        [Range(-70f, 0f)] public float darkLightnessDelta = -42f;
        [Tooltip("How much color the dark end keeps.")]
        [Range(0f, 1.5f)] public float darkChromaScale = 0.55f;
        [Tooltip("How far the dark end bends toward the neutral hue.")]
        [Range(0f, 45f)] public float darkHueDrift = 12f;

        [Header("Value Contrast Mapping")]
        [Tooltip("Multiplier when the Value Contrast slider is at 0.")]
        [Range(0.1f, 1f)] public float contrastScaleMin = 0.6f;
        [Tooltip("Multiplier when the Value Contrast slider is at 1.")]
        [Range(1f, 2.5f)] public float contrastScaleMax = 1.4f;

        [Header("Color Purity Rules")]
        [Tooltip("Lowest color strength for the primary family. Stops gradients from passing through gray.")]
        [Range(0f, 30f)] public float primaryChromaFloor = 10f;
        [Tooltip("Lowest color strength for the accent family.")]
        [Range(0f, 30f)] public float accentChromaFloor = 12f;
        [Tooltip("Lowest color strength for the neutral family. Keep near 0 or smoke turns colored.")]
        [Range(0f, 10f)] public float neutralChromaFloor = 0f;
        [Tooltip("Neutral base color strength = primary color strength x this.")]
        [Range(0f, 1f)] public float neutralChromaScale = 0.25f;
        [Tooltip("Hard cap on neutral color strength. Lower this if smoke looks dirty.")]
        [Range(0f, 20f)] public float neutralChromaCap = 6f;
        [Tooltip("How much darker the neutral base is than the primary base.")]
        [Range(-30f, 10f)] public float neutralLightnessDelta = -8f;

        [Header("Accent")]
        [Tooltip("Extra pull toward the harmony template for accent colors.")]
        [Range(1f, 2f)] public float accentBetaBoost = 1.2f;
        [Tooltip("Blend the accent toward the primary to soften it. " +
                 "This changes color difference, not screen area.")]
        [Range(0f, 1f)] public float accentBlendToPrimary = 0f;
        [Tooltip("How much of the effect accent colors may take up. " +
                 "Used only for panel warnings — it does not change the picture.")]
        [Range(0f, 1f)] public float accentBudget = 0.25f;

        [Header("Lifetime Travel")]
        [Tooltip("How far a new particle starts toward the hot end of its curve.")]
        [FormerlySerializedAs("birthTowardHighlight")]
        [Range(0f, 1f)] public float birthTravelTowardHot = 0.6f;
        [Tooltip("How far a dying particle travels toward the dark end. " +
                 "Raise it if embers do not burn out.")]
        [FormerlySerializedAs("deathTowardShadow")]
        [Range(0f, 1f)] public float deathTravelTowardDark = 0.5f;

        [Header("Per-Particle Jitter")]
        [Tooltip("Random brightness spread between particles. Raise it for a grainier look.")]
        [Range(0f, 0.6f)] public float startValueJitter = 0.15f;

        [Header("Lifetime Timing")]
        [Tooltip("When the birth phase ends (share of the particle lifetime).")]
        [Range(0f, 0.5f)] public float birthEndTime = 0.15f;
        [Tooltip("When the fade-out phase starts.")]
        [Range(0.5f, 1f)] public float deathStartTime = 0.70f;
        [Tooltip("How much opacity drops as the color gets darker. Stops dark particles " +
                 "looking like black paper.")]
        [Range(0f, 1f)] public float deathAlphaCoupling = 0.5f;

        [Header("Inherited Defaults - Temporal Strength")]
        [Range(0f, 1f)] public float defaultCoolingStrength = 0.8f;
        [Range(0f, 1f)] public float defaultDimmingStrength = 0.5f;
        [Range(0f, 1f)] public float defaultSmokeFadeStrength = 0.35f;

        [Header("Inherited Defaults - Family Energy")]
        [Range(0f, 4f)] public float defaultPrimaryEnergy = 1f;
        [Range(0f, 4f)] public float defaultAccentEnergy = 1f;
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

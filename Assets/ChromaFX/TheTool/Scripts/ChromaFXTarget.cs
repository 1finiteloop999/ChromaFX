using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ChromaFX
{
    /// <summary>
    /// ChromaFX配色目标（挂在特效Prefab根节点）。
    ///
    /// 数据模型：三轴绑定列表是配色映射的**唯一真相**——
    /// 名字映射表只用于生成初始绑定，生成后不再参与任何决策。
    /// 基线快照保证Apply幂等与可还原。
    ///
    /// 旧槽位字段在迁移完成并确认前保留（Applier将于5.6c切换到绑定），
    /// 不在迁移成功前删除任何已序列化数据。
    /// </summary>
    [AddComponentMenu("ChromaFX/ChromaFX Target")]
    [DisallowMultipleComponent]
    public class ChromaFXTarget : MonoBehaviour
    {
        public const int CurrentBaselineVersion = 1;
        public const int CurrentBindingVersion = 1;

        [Header("粒子绑定（唯一真相）")]
        public List<ParticleBinding> bindings = new List<ParticleBinding>();

        [Header("光源槽位")]
        public Light lightSource;

        [SerializeField] int bindingVersion;

        [Header("基线快照（Apply幂等与Restore的依据，勿手改）")]
        [SerializeField] int baselineVersion;
        [SerializeField] List<SystemBaseline> baselines = new List<SystemBaseline>();
        [SerializeField] LightBaseline lightBaseline = new LightBaseline();

        // ── 旧槽位数据：仅作为迁移来源保留，执行路径已全部改用bindings。
        //    不主动删除已序列化数据，以便随时重新迁移或回溯。 ──
        [HideInInspector] public List<ParticleSystem> highlight = new List<ParticleSystem>();
        [HideInInspector] public List<ParticleSystem> main = new List<ParticleSystem>();
        [HideInInspector] public List<ParticleSystem> sub = new List<ParticleSystem>();
        [HideInInspector] public List<ParticleSystem> accent = new List<ParticleSystem>();
        [HideInInspector] public List<ParticleSystem> shadow = new List<ParticleSystem>();
        [HideInInspector] public List<ParticleSystem> ambient = new List<ParticleSystem>();
        [HideInInspector] public List<ParticleSystem> excluded = new List<ParticleSystem>();

        public bool HasBaseline => baselineVersion > 0 && baselines.Count > 0;
        public int BaselineVersion => baselineVersion;
        public int BaselineCount => baselines.Count;
        public bool HasBindings => bindings != null && bindings.Count > 0;
        public int BindingVersion => bindingVersion;

        public ParticleBinding GetBinding(ParticleSystem ps)
        {
            foreach (var b in bindings)
                if (b != null && b.system == ps) return b;
            return null;
        }

        public SystemBaseline GetBaseline(ParticleSystem ps)
        {
            foreach (var b in baselines)
                if (b.system == ps) return b;
            return null;
        }

        // ════════════════════════════════════════════
        // 绑定生成与迁移
        // ════════════════════════════════════════════

        /// <summary>篝火默认绑定表（名字映射仅用于生成初始绑定）</summary>
        static readonly (string name, ColorFamily family, float tone,
            TemporalProfile temporal, float strength, ColorWritePolicy policy)[] DemoFireTemplate =
        {
            // 内焰：白热核心，恒色
            ("Fire_Core",       ColorFamily.Primary, 0.05f, TemporalProfile.Constant,  ParticleBinding.Inherit, ColorWritePolicy.FullPipeline),
            // 主体火焰：弱冷却
            ("Fire_Base",       ColorFamily.Primary, 0.45f, TemporalProfile.Cooling,   0.30f, ColorWritePolicy.FullPipeline),
            ("Fire_Smallfire",  ColorFamily.Primary, 0.60f, TemporalProfile.Cooling,   0.30f, ColorWritePolicy.FullPipeline),
            // 余烬：基线HDR 2.996 = 发亮余烬（非冷灰烬），热端出生+强冷却
            ("Fire_Ash",        ColorFamily.Primary, 0.15f, TemporalProfile.Cooling,   ParticleBinding.Inherit, ColorWritePolicy.FullPipeline),
            // 碎片：自然篝火用Primary而非Accent，避免"单看好看整体怪"
            ("Fire_Fragments",  ColorFamily.Primary, 0.28f, TemporalProfile.Cooling,   0.55f, ColorWritePolicy.FullPipeline),
            // 暗色火苗：同色调降明，不经过热端
            ("Fire_Small_dark", ColorFamily.Primary, 0.85f, TemporalProfile.Dimming,   ParticleBinding.Inherit, ColorWritePolicy.FullPipeline),
            // 体积烟雾：中性族，无出生高亮
            ("Fire_Smoke",      ColorFamily.Neutral, 0.90f, TemporalProfile.SmokeFade, ParticleBinding.Inherit, ColorWritePolicy.FullPipeline),
            // 辉光：Primary低彩度（用Neutral会重新变灰），恒色只保留Alpha动画
            ("Glow_Core",       ColorFamily.Primary, 0.10f, TemporalProfile.Constant,  ParticleBinding.Inherit, ColorWritePolicy.FullPipeline),
            ("Glow_floor",      ColorFamily.Primary, 0.25f, TemporalProfile.Constant,  ParticleBinding.Inherit, ColorWritePolicy.FullPipeline),
            ("Glow_floor2",     ColorFamily.Primary, 0.35f, TemporalProfile.Constant,  ParticleBinding.Inherit, ColorWritePolicy.FullPipeline),
            // 热扭曲：无颜色
            ("Fire_Distortion", ColorFamily.Neutral, 0.50f, TemporalProfile.Constant,  ParticleBinding.Inherit, ColorWritePolicy.Excluded),
        };

        /// <summary>按名字生成篝火默认绑定（覆盖现有绑定）</summary>
        [ContextMenu("Build Default Bindings (Demo Fire)")]
        public void BuildDefaultBindings()
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "ChromaFX Build Bindings");
#endif
            bindings.Clear();

            foreach (var t in DemoFireTemplate)
            {
                var tr = FindDeepChild(transform, t.name);
                if (tr == null || !tr.TryGetComponent(out ParticleSystem ps))
                {
                    Debug.LogWarning($"[ChromaFX] 生成绑定：未找到子物体 '{t.name}'", this);
                    continue;
                }
                bindings.Add(new ParticleBinding
                {
                    system = ps,
                    colorFamily = t.family,
                    tonePosition = t.tone,
                    temporalProfile = t.temporal,
                    temporalStrength = t.strength,
                    energyScale = ParticleBinding.Inherit,
                    visualWeight = ParticleBinding.Inherit,
                    writePolicy = t.policy,
                });
            }

            if (lightSource == null)
                lightSource = GetComponentInChildren<Light>(true);

            bindingVersion = CurrentBindingVersion;
            Validate(out string report);
            Debug.Log($"[ChromaFX] 已生成 {bindings.Count} 条绑定。校验：\n{report}", this);
            MarkDirty();
        }

        /// <summary>旧角色槽 → 三轴绑定的通用迁移（保留用户原有配置）</summary>
        [ContextMenu("Migrate From Legacy Slots")]
        public void MigrateFromLegacySlots()
        {
            // 旧角色 → (族, 基准tone, 时间剖面)
            var map = new (List<ParticleSystem> slot, ColorFamily family, float tone, TemporalProfile temporal)[]
            {
                (highlight, ColorFamily.Primary, 0.08f, TemporalProfile.Constant),
                (main,      ColorFamily.Primary, 0.45f, TemporalProfile.Cooling),
                (sub,       ColorFamily.Primary, 0.60f, TemporalProfile.Cooling),
                (accent,    ColorFamily.Accent,  0.50f, TemporalProfile.Cooling),
                (shadow,    ColorFamily.Neutral, 0.85f, TemporalProfile.Dimming),
                (ambient,   ColorFamily.Primary, 0.30f, TemporalProfile.Constant),
            };

#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "ChromaFX Migrate Bindings");
#endif
            bindings.Clear();
            var seen = new HashSet<ParticleSystem>();

            foreach (var (slot, family, tone, temporal) in map)
            {
                if (slot == null) continue;
                for (int i = 0; i < slot.Count; i++)
                {
                    var ps = slot[i];
                    if (ps == null || !seen.Add(ps)) continue;
                    bindings.Add(NewBinding(ps, family,
                        // 同槽多粒子原先靠列表顺序取变体，迁移为显式tone阶梯
                        Mathf.Clamp01(tone + i * 0.08f), temporal, ColorWritePolicy.FullPipeline));
                }
            }

            foreach (var ps in excluded)
            {
                if (ps == null || !seen.Add(ps)) continue;
                bindings.Add(NewBinding(ps, ColorFamily.Neutral, 0.5f,
                    TemporalProfile.Constant, ColorWritePolicy.Excluded));
            }

            bindingVersion = CurrentBindingVersion;
            Validate(out string report);
            Debug.Log($"[ChromaFX] 已从旧槽位迁移 {bindings.Count} 条绑定（旧数据保留未删除）。校验：\n{report}", this);
            MarkDirty();
        }

        /// <summary>所有继承字段显式写入-1：新增字段默认为0会导致energyScale=0（粒子变黑）的静默失败</summary>
        static ParticleBinding NewBinding(ParticleSystem ps, ColorFamily family, float tone,
            TemporalProfile temporal, ColorWritePolicy policy)
        {
            return new ParticleBinding
            {
                system = ps,
                colorFamily = family,
                tonePosition = tone,
                temporalProfile = temporal,
                temporalStrength = ParticleBinding.Inherit,
                energyScale = ParticleBinding.Inherit,
                visualWeight = ParticleBinding.Inherit,
                writePolicy = policy,
            };
        }

        // ════════════════════════════════════════════
        // 校验
        // ════════════════════════════════════════════

        public bool Validate(out string report)
        {
            var sb = new StringBuilder();
            bool ok = true;

            if (!HasBindings)
            {
                report = "错误：尚未生成粒子绑定——请执行 Build Default Bindings 或 Migrate From Legacy Slots";
                return false;
            }

            var seenSystems = new HashSet<ParticleSystem>();
            var matOwner = new Dictionary<Material, ParticleBinding>();

            foreach (var b in bindings)
            {
                if (b == null || b.system == null)
                {
                    sb.AppendLine("错误：存在空绑定或空粒子系统引用");
                    ok = false;
                    continue;
                }

                if (!seenSystems.Add(b.system))
                {
                    sb.AppendLine($"错误：{b.system.name} 存在多条绑定");
                    ok = false;
                }

                if (b.tonePosition < 0f || b.tonePosition > 1f)
                {
                    sb.AppendLine($"错误：{b.system.name} 的tonePosition超出[0,1]");
                    ok = false;
                }

                // 哨兵校验：只接受-1或非负
                if (b.temporalStrength < 0f && !Mathf.Approximately(b.temporalStrength, ParticleBinding.Inherit))
                {
                    sb.AppendLine($"错误：{b.system.name} 的temporalStrength非法（只接受-1或≥0）");
                    ok = false;
                }
                if (b.energyScale < 0f && !Mathf.Approximately(b.energyScale, ParticleBinding.Inherit))
                {
                    sb.AppendLine($"错误：{b.system.name} 的energyScale非法（只接受-1或≥0）");
                    ok = false;
                }
                if (b.visualWeight < 0f && !Mathf.Approximately(b.visualWeight, ParticleBinding.Inherit))
                {
                    sb.AppendLine($"错误：{b.system.name} 的visualWeight非法（只接受-1或≥0）");
                    ok = false;
                }

                // 共享材质冲突
                var renderer = b.system.GetComponent<ParticleSystemRenderer>();
                Material mat = renderer != null ? renderer.sharedMaterial : null;
                if (mat == null)
                {
                    sb.AppendLine($"警告：{b.system.name} 无渲染器或材质");
                }
                else if (matOwner.TryGetValue(mat, out var other))
                {
                    bool sameColor = other.colorFamily == b.colorFamily
                                     && Mathf.Approximately(other.tonePosition, b.tonePosition);
                    if (!sameColor)
                    {
                        sb.AppendLine($"错误：{other.system.name} 与 {b.system.name} 共享材质 {mat.name}，" +
                                      "但取色不同——后写入者会覆盖前者，请复制独立材质");
                        ok = false;
                    }
                    else if (!Mathf.Approximately(other.energyScale, b.energyScale))
                    {
                        sb.AppendLine($"警告：{other.system.name} 与 {b.system.name} 共享材质 {mat.name}，" +
                                      "但energyScale不一致，实际只会生效其中之一");
                    }
                }
                else
                {
                    matOwner[mat] = b;
                }

                if (!b.UsesTemporal && b.temporalProfile != TemporalProfile.Constant)
                {
                    sb.AppendLine($"提示：{b.system.name} 为 {b.writePolicy}，时间剖面被忽略");
                }
            }

            if (lightSource == null)
                sb.AppendLine("警告：光源槽为空（Light将跳过）");

            report = sb.Length == 0 ? "绑定配置有效，无警告" : sb.ToString().TrimEnd();
            return ok;
        }

        // ════════════════════════════════════════════
        // 基线捕获
        // ════════════════════════════════════════════

        /// <summary>
        /// 捕获当前状态为基线（覆盖旧基线）。捕获前请确认处于原始/理想状态。
        /// 遍历来源：优先绑定列表，未生成绑定时回落到旧槽位。
        /// </summary>
        public bool CaptureBaseline(out string report)
        {
            var sb = new StringBuilder();
            bool ok = true;

#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "ChromaFX Capture Baseline");
#endif
            baselines.Clear();

            var systems = CollectSystemsForBaseline();
            foreach (var ps in systems)
                baselines.Add(CaptureSystem(ps, sb));

            if (lightSource != null)
            {
                lightBaseline.captured = true;
                lightBaseline.color = lightSource.color;
                lightBaseline.intensity = lightSource.intensity;
                lightBaseline.range = lightSource.range;
            }
            else
            {
                lightBaseline.captured = false;
            }

            baselineVersion = CurrentBaselineVersion;
            MarkDirty();

            sb.AppendLine($"已捕获 {baselines.Count} 个粒子系统" +
                          (lightBaseline.captured ? " + 光源" : "（无光源）") +
                          $"，基线版本 v{baselineVersion}");
            report = sb.ToString().TrimEnd();
            return ok;
        }

        /// <summary>基线覆盖范围：全部绑定，含Excluded（Restore需还原完整状态）</summary>
        List<ParticleSystem> CollectSystemsForBaseline()
        {
            var list = new List<ParticleSystem>();
            var seen = new HashSet<ParticleSystem>();
            foreach (var b in bindings)
                if (b?.system != null && seen.Add(b.system)) list.Add(b.system);
            return list;
        }

        SystemBaseline CaptureSystem(ParticleSystem ps, StringBuilder sb)
        {
            var b = new SystemBaseline { system = ps };

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                b.material = renderer.sharedMaterial;
                b.materialColor = b.material.HasProperty(ParticleColorApplier.ColorProperty)
                    ? b.material.GetColor(ParticleColorApplier.ColorProperty)
                    : Color.white;
                if (!b.material.HasProperty(ParticleColorApplier.ColorProperty))
                    sb.AppendLine($"警告：{ps.name} 的材质无 {ParticleColorApplier.ColorProperty} 属性");
            }
            else
            {
                sb.AppendLine($"警告：{ps.name} 无渲染器或材质");
            }

            b.startColor = MinMaxGradientData.Capture(ps.main.startColor);

            var col = ps.colorOverLifetime;
            b.colEnabled = col.enabled;
            b.colColor = MinMaxGradientData.Capture(col.color);

            return b;
        }

        // ════════════════════════════════════════════
        // 基线还原
        // ════════════════════════════════════════════

        public bool RestoreBaseline(out string report)
        {
            if (!HasBaseline)
            {
                report = "无基线可还原";
                return false;
            }

            var sb = new StringBuilder();
            var restoredMats = new HashSet<Material>();
            int count = 0;

            foreach (var b in baselines)
            {
                if (b.system == null)
                {
                    sb.AppendLine("警告：基线中存在失效的系统引用（对象被删除？）");
                    continue;
                }

#if UNITY_EDITOR
                UnityEditor.Undo.RecordObject(b.system, "ChromaFX Restore");
#endif
                if (b.material != null && restoredMats.Add(b.material)
                    && b.material.HasProperty(ParticleColorApplier.ColorProperty))
                {
#if UNITY_EDITOR
                    UnityEditor.Undo.RecordObject(b.material, "ChromaFX Restore");
#endif
                    b.material.SetColor(ParticleColorApplier.ColorProperty, b.materialColor);
#if UNITY_EDITOR
                    UnityEditor.EditorUtility.SetDirty(b.material);
#endif
                }

                var mainModule = b.system.main;
                mainModule.startColor = b.startColor.ToMinMax();

                var col = b.system.colorOverLifetime;
                col.enabled = b.colEnabled;
                col.color = b.colColor.ToMinMax();

                count++;
            }

            if (lightBaseline.captured && lightSource != null)
            {
#if UNITY_EDITOR
                UnityEditor.Undo.RecordObject(lightSource, "ChromaFX Restore");
#endif
                lightSource.color = lightBaseline.color;
                lightSource.intensity = lightBaseline.intensity;
                lightSource.range = lightBaseline.range;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(lightSource);
#endif
            }

            sb.AppendLine($"已还原 {count} 个粒子系统" + (lightBaseline.captured ? " + 光源" : ""));
            report = sb.ToString().TrimEnd();
            return true;
        }

        // ════════════════════════════════════════════

        void MarkDirty()
        {
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        static Transform FindDeepChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var found = FindDeepChild(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}

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
    /// </summary>
    [AddComponentMenu("ChromaFX/ChromaFX Target")]
    [DisallowMultipleComponent]
    public class ChromaFXTarget : MonoBehaviour
    {
        public const int CurrentBaselineVersion = 1;
        public const int CurrentBindingVersion = 1;

        [Header("Particle Bindings")]
        public List<ParticleBinding> bindings = new List<ParticleBinding>();

        [Header("Light")]
        public Light lightSource;

        [SerializeField] int bindingVersion;

        [Header("Baseline Snapshot (do not edit by hand)")]
        [SerializeField] int baselineVersion;
        [SerializeField] List<SystemBaseline> baselines = new List<SystemBaseline>();
        [SerializeField] LightBaseline lightBaseline = new LightBaseline();

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
                    Debug.LogWarning($"[ChromaFX] Build bindings: child '{t.name}' not found.", this);
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
            Debug.Log($"[ChromaFX] Created {bindings.Count} bindings. Check:\n{report}", this);
            MarkDirty();
        }

        /// <summary>
        /// 为该物体下所有粒子系统创建绑定（通用，不依赖名字）。
        /// 默认全部为 Primary / 0.45 / Cooling，之后由用户逐条调整。
        /// </summary>
        [ContextMenu("Build Bindings From Children (Any Effect)")]
        public void BuildBindingsFromChildren()
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "ChromaFX Build Bindings");
#endif
            bindings.Clear();
            var seen = new HashSet<ParticleSystem>();

            foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null || !seen.Add(ps)) continue;
                bindings.Add(NewBinding(ps, ColorFamily.Primary, FamilyCurve.TBase,
                    TemporalProfile.Cooling, ColorWritePolicy.FullPipeline));
            }

            if (lightSource == null)
                lightSource = GetComponentInChildren<Light>(true);

            bindingVersion = CurrentBindingVersion;
            Validate(out string report);
            Debug.Log($"[ChromaFX] Created {bindings.Count} bindings from children. Check:\n{report}", this);
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
                report = "Error: no particle bindings yet — run Build Default Bindings " +
                         "or Build Bindings From Children.";
                return false;
            }

            var seenSystems = new HashSet<ParticleSystem>();
            var matOwner = new Dictionary<Material, ParticleBinding>();

            foreach (var b in bindings)
            {
                if (b == null || b.system == null)
                {
                    sb.AppendLine("Error: a binding is empty or has no particle system.");
                    ok = false;
                    continue;
                }

                if (!seenSystems.Add(b.system))
                {
                    sb.AppendLine($"Error: {b.system.name} has more than one binding.");
                    ok = false;
                }

                if (b.tonePosition < 0f || b.tonePosition > 1f)
                {
                    sb.AppendLine($"Error: {b.system.name} tone position is outside 0-1.");
                    ok = false;
                }

                // 哨兵校验：只接受-1或非负
                if (b.temporalStrength < 0f && !Mathf.Approximately(b.temporalStrength, ParticleBinding.Inherit))
                {
                    sb.AppendLine($"Error: {b.system.name} temporal strength must be -1 (inherit) or 0 and up.");
                    ok = false;
                }
                if (b.energyScale < 0f && !Mathf.Approximately(b.energyScale, ParticleBinding.Inherit))
                {
                    sb.AppendLine($"Error: {b.system.name} energy scale must be -1 (inherit) or 0 and up.");
                    ok = false;
                }
                if (b.visualWeight < 0f && !Mathf.Approximately(b.visualWeight, ParticleBinding.Inherit))
                {
                    sb.AppendLine($"Error: {b.system.name} visual weight must be -1 (inherit) or 0 and up.");
                    ok = false;
                }

                // 共享材质冲突
                var renderer = b.system.GetComponent<ParticleSystemRenderer>();
                Material mat = renderer != null ? renderer.sharedMaterial : null;
                if (mat == null)
                {
                    sb.AppendLine($"Warning: {b.system.name} has no renderer or material.");
                }
                else if (matOwner.TryGetValue(mat, out var other))
                {
                    bool sameColor = other.colorFamily == b.colorFamily
                                     && Mathf.Approximately(other.tonePosition, b.tonePosition);
                    if (!sameColor)
                    {
                        sb.AppendLine($"Error: {other.system.name} and {b.system.name} share material " +
                                      $"{mat.name} but ask for different colors. One will overwrite the " +
                                      "other — give one of them its own material copy.");
                        ok = false;
                    }
                    else if (!Mathf.Approximately(other.energyScale, b.energyScale))
                    {
                        sb.AppendLine($"Warning: {other.system.name} and {b.system.name} share material " +
                                      $"{mat.name} but have different energy scales. Only one will apply.");
                    }
                }
                else
                {
                    matOwner[mat] = b;
                }

                if (!b.UsesTemporal && b.temporalProfile != TemporalProfile.Constant)
                {
                    sb.AppendLine($"Note: {b.system.name} is {b.writePolicy}, so its temporal profile is ignored.");
                }
            }

            if (lightSource == null)
                sb.AppendLine("Warning: no light assigned — the Light color will be skipped.");

            report = sb.Length == 0 ? "Bindings OK" : sb.ToString().TrimEnd();
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

            sb.AppendLine($"Captured {baselines.Count} particle systems" +
                          (lightBaseline.captured ? " + light" : " (no light)") +
                          $", baseline v{baselineVersion}.");
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
                    sb.AppendLine($"Warning: material on {ps.name} has no {ParticleColorApplier.ColorProperty} property.");
            }
            else
            {
                sb.AppendLine($"Warning: {ps.name} has no renderer or material.");
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
                report = "No baseline to restore.";
                return false;
            }

            var sb = new StringBuilder();
            var restoredMats = new HashSet<Material>();
            int count = 0;

            foreach (var b in baselines)
            {
                if (b.system == null)
                {
                    sb.AppendLine("Warning: a baseline entry points to a missing particle system (deleted?).");
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

            sb.AppendLine($"Restored {count} particle systems" + (lightBaseline.captured ? " + light" : "") + ".");
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

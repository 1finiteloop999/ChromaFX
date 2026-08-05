using UnityEditor;
using UnityEngine;

namespace ChromaFX.EditorTools
{
    /// <summary>
    /// 【一次性迁移脚本】把火焰材质的_MainColor恢复到基线审计时的原始值
    /// （数据来源：2026-07-24 资产审计逐材质记录）。
    ///
    /// 用途：此前的Applier存在"反复Apply强度递减"缺陷，材质当前值已不可信。
    /// 在Capture Baseline之前先运行本脚本恢复真值，视觉确认后再捕获基线。
    /// 基线捕获完成后【删除本文件】。
    ///
    /// 唯一的有意偏离：Fire_Small_dark 原值为(1, 0.98427665, 0.98427665)，
    /// 带肉眼不可见的微红（违反灰度前提），本次恢复为纯白并在此记录。
    /// </summary>
    public static class RestoreV0MaterialColors
    {
        const string Dir = "Assets/ChromaFX/Demo/Fire/Materials/";
        const string Prop = "_MainColor";

        // (文件名, V0颜色)——文件名为重命名后的现名，注释标注审计时旧名
        static readonly (string file, Color color)[] V0 =
        {
            ("Fire_Ash.mat",        new Color(2.9960787f, 2.9960787f, 2.9960787f, 1f)),
            ("Fire_Base.mat",       new Color(0.4716981f, 0.4716981f, 0.4716981f, 1f)),
            ("Fire_Core.mat",       new Color(2.9960787f, 2.9960787f, 2.9960787f, 1f)), // 旧名Fire_Base2.mat
            ("Fire_Smallfire.mat",  new Color(2.9960785f, 2.9960785f, 2.9960785f, 1f)), // 旧名Fire_Small.mat
            ("Fire_Fragments.mat",  new Color(1.8109595f, 1.8109595f, 1.8109595f, 1f)), // 旧名Fire_Small 1.mat
            ("Fire_Small_dark.mat", new Color(1f, 1f, 1f, 1f)),                          // 微红修正为纯白，见类注释
            ("Fire_Smoke.mat",      new Color(1f, 1f, 1f, 1f)),
            ("Glow_Core.mat",       new Color(1f, 1f, 1f, 1f)),                          // 旧名Fire_Glow.mat
            ("Glow_floor.mat",      new Color(1f, 1f, 1f, 1f)),
            ("Glow_floor2.mat",     new Color(5.992157f, 5.992157f, 5.992157f, 1f)),
            ("Fire_Distortion.mat", new Color(1f, 1f, 1f, 0f)),                          // 系统未写过，校验用
        };

        // Point light原始值（来源：eff_Campfire.prefab资产内的Light组件，未被污染）
        static readonly Color LightColorV0 = new Color(1f, 0.8885548f, 0.5943395f, 1f);
        const float LightIntensityV0 = 0.6f;

        [MenuItem("Tools/ChromaFX/Migration/Restore V0 Material Colors")]
        static void Run()
        {
            int restored = 0, missing = 0;

            foreach (var (file, color) in V0)
            {
                string path = Dir + file;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    Debug.LogError($"[ChromaFX V0] 未找到材质：{path}（文件名是否又变了？）");
                    missing++;
                    continue;
                }
                if (!mat.HasProperty(Prop))
                {
                    Debug.LogError($"[ChromaFX V0] {file} 无属性 {Prop}，跳过");
                    missing++;
                    continue;
                }

                Color old = mat.GetColor(Prop);
                Undo.RecordObject(mat, "Restore V0 Colors");
                mat.SetColor(Prop, color);
                EditorUtility.SetDirty(mat);
                restored++;
                Debug.Log($"[ChromaFX V0] {file}: ({old.r:F3},{old.g:F3},{old.b:F3},{old.a:F2}) → " +
                          $"({color.r:F3},{color.g:F3},{color.b:F3},{color.a:F2})");
            }

            // 恢复场景实例的Light（Apply曾写过其color）
            var target = Object.FindObjectOfType<ChromaFXTarget>();
            if (target != null && target.lightSource != null)
            {
                Undo.RecordObject(target.lightSource, "Restore V0 Colors");
                target.lightSource.color = LightColorV0;
                target.lightSource.intensity = LightIntensityV0;
                EditorUtility.SetDirty(target.lightSource);
                Debug.Log("[ChromaFX V0] Point light 已恢复暖橙原色(1, 0.889, 0.594)，强度0.6");
            }
            else
            {
                Debug.LogWarning("[ChromaFX V0] 场景中未找到ChromaFXTarget/光源——" +
                                 "若场景实例的Light有override，请手动在Prefab实例上Revert该颜色");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[ChromaFX V0] 完成：恢复{restored}个材质，失败{missing}个。" +
                      "请视觉核对火焰是否回到手调完成时的状态（对照最初的白色火焰截图），" +
                      "确认后我们再执行Capture Baseline。");
        }
    }
}

using UnityEditor;

namespace ChromaFX.EditorTools
{
    /// <summary>Step 1b验收入口：菜单一键运行ColorSpaces自检</summary>
    public static class ChromaFXSelfTest
    {
        [MenuItem("Tools/ChromaFX/Run ColorSpaces SelfTest")]
        static void RunColorSpaces()
        {
            ColorSpaces.RunSelfTest();
        }

        [MenuItem("Tools/ChromaFX/Run HarmonyTemplates SelfTest")]
        static void RunTemplates()
        {
            HarmonyTemplates.RunSelfTest();
        }

        [MenuItem("Tools/ChromaFX/Run ColorTheoryEngine SelfTest")]
        static void RunEngine()
        {
            ColorTheoryEngine.RunSelfTest();
        }

        [MenuItem("Tools/ChromaFX/Run SchemeFitness SelfTest (legacy 7-point)")]
        static void RunFitness()
        {
            SchemeFitness.RunSelfTest();
        }

        [MenuItem("Tools/ChromaFX/Run Palette+Effect Fitness SelfTest")]
        static void RunFitness2()
        {
            ChromaFXFitness.RunSelfTest();
        }

        [MenuItem("Tools/ChromaFX/Run ALL SelfTests")]
        static void RunAll()
        {
            bool ok = ColorSpaces.RunSelfTest();
            ok &= HarmonyTemplates.RunSelfTest();
            ok &= ColorTheoryEngine.RunSelfTest();
            ok &= SchemeFitness.RunSelfTest();
            ok &= ChromaFXFitness.RunSelfTest();
            UnityEngine.Debug.Log(ok
                ? "[ChromaFX] ══ 全部自检通过 ══"
                : "[ChromaFX] ══ 存在失败项，见上方日志 ══");
        }

        // （Step 4的临时Test Apply菜单已由ChromaFXWindow替代并删除）
    }
}

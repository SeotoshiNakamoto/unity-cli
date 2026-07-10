using UnityEditor;

namespace UnityCliConnector
{
    internal static class EditorProcessGuard
    {
        public static bool IsPrimaryEditorProcess =>
            !AssetDatabase.IsAssetImportWorkerProcess();
    }
}

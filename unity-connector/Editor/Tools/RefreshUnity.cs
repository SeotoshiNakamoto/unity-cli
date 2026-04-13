using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Description = "Refresh Unity assets and optionally request script compilation.")]
    public static class RefreshUnity
    {
        public class Parameters
        {
            [ToolParameter("Refresh mode: if_dirty (default) or force")]
            public string Mode { get; set; }

            [ToolParameter("Scope: all (default) or specific path")]
            public string Scope { get; set; }

            [ToolParameter("Compile mode: none (default) or request")]
            public string Compile { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            // Play 모드 / 컴파일 중 / Play 모드 전환 직전 refresh 는 도메인 리로드를 유발하여
            // 실행 중 상태(씬/코루틴/네트워크/Library DLL 로딩)를 손상시킨다.
            // AssetDatabase.Refresh 호출 전에 하드 블록.
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new ErrorResponse(
                    "Cannot refresh while Play mode is active or transitioning — domain reload would corrupt scene/network/asmdef state. Stop Play and retry.");
            }

            if (EditorApplication.isCompiling)
            {
                return new ErrorResponse(
                    "Cannot refresh while scripts are compiling — would trigger a second reload. Wait for compilation to finish and retry.");
            }

            var p = new ToolParams(@params ?? new JObject());
            string mode = p.Get("mode", "if_dirty");
            string scope = p.Get("scope", "all");
            string compile = p.Get("compile", "none");

            bool compileRequested = false;

            AssetDatabase.Refresh(string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase)
                ? ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                : ImportAssetOptions.ForceSynchronousImport);

            if (string.Equals(compile, "request", StringComparison.OrdinalIgnoreCase))
            {
                CompilationPipeline.RequestScriptCompilation();
                compileRequested = true;
            }

            return new SuccessResponse("Refresh requested.", new
            {
                refresh_triggered = true,
                compile_requested = compileRequested,
            });
        }
    }
}

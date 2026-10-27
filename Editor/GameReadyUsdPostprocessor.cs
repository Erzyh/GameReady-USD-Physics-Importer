using System.IO;
using UnityEditor;
using UnityEditor.Importer.USD;
using UnityEngine;
using UnityEngine.Importer;

namespace USDGameReady.Editor
{
    /// <summary>
    /// GameReady usda가 프로젝트에 들어올 때 임포터 그래프를 자동으로 지정한다.
    /// 판정: 파일 앞부분에 physics:mass 또는 gameReady 문자열이 있으면 GameReady 에셋으로 본다.
    /// SimReady처럼 payloads/Physics/physics.usda 를 둔 루트도 포함하고, payloads/ 아래 레이어는 제외한다.
    /// 처음 임포트되거나 그래프 참조가 깨진 파일만 그래프를 우리 것으로 바꾸고 isUsdRoot를 켠다. 그래프가 살아 있는 파일과 나머지 usd 파일은 손대지 않는다.
    /// 메뉴 GameReady/Auto-assign Importer Graph 로 끄고 켤 수 있다.
    /// </summary>
    public class GameReadyUsdPostprocessor : AssetPostprocessor
    {
        public const string GraphPath = "Packages/com.eglab.gameready.usd/Editor/ImportGraph/usdGameReadyImporter.asset";
        const string k_PrefKey = "GameReady.AutoAssignGraph";
        const string k_Menu = "GameReady/Auto-assign Importer Graph";

        static bool AutoAssign
        {
            get => EditorPrefs.GetBool(k_PrefKey, true);
            set => EditorPrefs.SetBool(k_PrefKey, value);
        }

        [MenuItem(k_Menu)]
        static void Toggle() => AutoAssign = !AutoAssign;

        [MenuItem(k_Menu, true)]
        static bool ToggleValidate() { Menu.SetChecked(k_Menu, AutoAssign); return true; }

        void OnPreprocessAsset()
        {
            if (!AutoAssign) return;
            if (!(assetImporter is UsdModularImporter imp)) return;
            // 처음 들어오는 파일(.meta 없음)과 그래프 참조가 깨진 파일만 손댄다.
            // 이미 임포트되어 그래프가 살아 있는 파일의 선택은 사용자 몫이다.
            bool graphBroken = !imp.importSettingsMissing && imp.Graph.asset == null;
            if (!imp.importSettingsMissing && !graphBroken) return;
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (ext != ".usda" && ext != ".usd" && ext != ".usdc") return;
            if (!LooksGameReady(assetPath)) return;

            var graph = AssetDatabase.LoadAssetAtPath<ImporterGraph>(GraphPath);
            if (graph == null)
            {
                Debug.LogWarning("[GameReady] 임포터 그래프가 없습니다. 메뉴 GameReady/Rebuild USD Importer Graph 를 실행하세요: " + GraphPath);
                return;
            }
            bool changed = false;
            if (imp.Graph.asset != graph) { imp.Graph = graph; changed = true; }
            if (!imp.isUsdRoot) { imp.isUsdRoot = true; changed = true; }
            if (changed)
                Debug.Log("[GameReady] 임포터 그래프 자동 지정: " + assetPath);
        }

        static bool LooksGameReady(string path)
        {
            // SimReady 같은 다중 레이어 에셋: payloads/ 아래 레이어는 루트가 아니다
            var norm = path.Replace('\\', '/');
            if (norm.Contains("/payloads/")) return false;
            // 루트가 바이너리(usdc)라 앞부분에 글자가 없을 때: 옆에 물리 페이로드가 있으면 GameReady로 연다
            var dir = Path.GetDirectoryName(path) ?? "";
            if (File.Exists(Path.Combine(dir, "payloads", "Physics", "physics.usda"))) return true;
            try
            {
                using var sr = new StreamReader(path);
                var buf = new char[64 * 1024];
                int n = sr.Read(buf, 0, buf.Length);
                var head = new string(buf, 0, n);
                return head.Contains("physics:mass") || head.Contains("gameReady");
            }
            catch { return false; }
        }
    }
}

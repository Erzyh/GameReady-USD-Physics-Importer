// GameReady 배치모드 실측 — 에디터 개입 없는 usda 임포트 + 덤프
// ------------------------------------------------------------------
// 사용법 (Unity 닫힌 상태에서):
//   Unity.exe -batchmode -projectPath <프로젝트> -executeMethod GameReadyBatch.Run
//             -grUsda <입력.usda> -grOut <출력.json> -logFile <로그> -quit
// 패키지 com.eglab.gameready.usd 에 포함.
// ------------------------------------------------------------------
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Importer.USD;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Importer;
using USDGameReady.Editor;

public static class GameReadyBatch
{
    public static void Run()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            string usda = Arg(args, "-grUsda");
            string outJson = Arg(args, "-grOut");
            if (usda == null || outJson == null)
                throw new Exception("-grUsda / -grOut 인자 필요");

            // 1) usda(+동반 텍스처)를 프로젝트로 복사 -> 임포터 그래프가 처리
            const string dstDir = "Assets/GameReadyBatch";
            if (Directory.Exists(dstDir))
                Directory.Delete(dstDir, true);
            Directory.CreateDirectory(dstDir);
            string dst = dstDir + "/" + Path.GetFileName(usda);
            File.Copy(usda, dst, true);
            foreach (var png in Directory.GetFiles(
                         Path.GetDirectoryName(usda), "*.png"))
                File.Copy(png, dstDir + "/" + Path.GetFileName(png), true);

            // usd는 기본 미임포트(DefaultUSD) — .meta를 선작성해서
            // 첫 임포트부터 isUsdRoot + GameReady 그래프가 적용되게 한다
            string graphGuid = AssetDatabase.AssetPathToGUID(
                GameReadyUsdPostprocessor.GraphPath);
            string meta =
                "fileFormatVersion: 2\n" +
                "guid: " + Guid.NewGuid().ToString("N") + "\n" +
                "ScriptedImporter:\n" +
                "  internalIDToNameTable: []\n" +
                "  externalObjects: {}\n" +
                "  serializedVersion: 2\n" +
                "  userData: \n" +
                "  assetBundleName: \n" +
                "  assetBundleVariant: \n" +
                "  script: {fileID: 11500000, guid: 7953a3c9778e49e4fbcdfd6a826af2cf, type: 3}\n" +
                "  graph: {fileID: 11400000, guid: " + graphGuid + ", type: 2}\n" +
                "  importSettingOverrides: []\n" +
                "  isUsdRoot: 1\n" +
                "  references:\n" +
                "    version: 2\n" +
                "    RefIds: []\n";
            File.WriteAllText(dst + ".meta", meta);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(dst,
                ImportAssetOptions.ForceSynchronousImport
                | ImportAssetOptions.ForceUpdate);

            // 2) 빈 씬에 임포트 결과 배치
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                        NewSceneMode.Single);
            var all = AssetDatabase.LoadAllAssetsAtPath(dst);
            GameObject main = AssetDatabase.LoadMainAssetAtPath(dst) as GameObject;
            foreach (var o in all)
                Debug.Log($"[GameReadyBatch] sub-asset: {o.GetType().Name} '{o.name}'");
            if (main == null)
            {
                foreach (var o in all)
                    if (o is GameObject go &&
                        (go.transform.parent == null || go.transform.root == go.transform))
                    { main = go; break; }
            }
            if (main == null)
            {
                // 임포터가 프리팹을 별도 산출물로 만드는 경우: 같은 폴더에서 탐색
                foreach (var guid in AssetDatabase.FindAssets("t:GameObject", new[] { dstDir }))
                {
                    var p2 = AssetDatabase.GUIDToAssetPath(guid);
                    Debug.Log("[GameReadyBatch] candidate prefab: " + p2);
                    main = AssetDatabase.LoadMainAssetAtPath(p2) as GameObject;
                    if (main != null) break;
                }
            }
            if (main == null)
                throw new Exception("임포트 결과에서 GameObject를 찾지 못함: " + dst);
            PrefabUtility.InstantiatePrefab(main);

            // 3) 덤프
            RoundTripDump.ExportTo(outJson);
            Debug.Log("[GameReadyBatch] OK " + outJson);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[GameReadyBatch] FAIL " + e);
            EditorApplication.Exit(1);
        }
    }

    static string Arg(string[] a, string k)
    {
        int i = Array.IndexOf(a, k);
        return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : null;
    }
}

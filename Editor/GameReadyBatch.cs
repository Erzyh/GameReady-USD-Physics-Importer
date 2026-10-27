// GameReady 배치모드 실측 — 에디터 개입 없는 usda 임포트 + 덤프
// ------------------------------------------------------------------
// 사용법 (Unity 닫힌 상태에서):
//   Unity.exe -batchmode -projectPath <프로젝트> -executeMethod GameReadyBatch.Run
//             -grUsda <입력.usda> -grOut <출력.json> -logFile <로그> -quit
//   SimReady 같은 다중 레이어 에셋은 -grUsda 에 루트 usd를 주면 옆의 payloads/ 를 같이 복사한다.
//   내보내기 왕복: -executeMethod GameReadyBatch.RunExport -grUsda <입력.usda> -grOutUsda <출력.usda> [-grOut <덤프.json>]
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

            ImportAndPlace(usda);

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


    // 내보내기 왕복: usda 임포트 -> 씬 배치 -> GameReady usda로 다시 내보내기 (+ 선택: 덤프)
    public static void RunExport()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            string usda = Arg(args, "-grUsda");
            string outUsda = Arg(args, "-grOutUsda");
            string outJson = Arg(args, "-grOut");
            if (usda == null || outUsda == null)
                throw new Exception("-grUsda / -grOutUsda 인자 필요");
            var inst = ImportAndPlace(usda);
            if (outJson != null) RoundTripDump.ExportTo(outJson);
            GameReadyUsdExporter.Export(new[] { inst }, outUsda, rootIsWorld: true);
            Debug.Log("[GameReadyBatch] EXPORT OK " + outUsda);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[GameReadyBatch] FAIL " + e);
            EditorApplication.Exit(1);
        }
    }


    // 내보내기 자체 시험: USD에서 오지 않은 일반 Unity 씬을 코드로 만들어 내보낸다
    //   -executeMethod GameReadyBatch.RunExportSelfTest -grOutUsda <출력.usda> -grOut <씬 덤프.json>
    public static void RunExportSelfTest()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            string outUsda = Arg(args, "-grOutUsda");
            string outJson = Arg(args, "-grOut");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("World");

            var ground = new GameObject("Ground");
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(10, 1, 10);
            var gb = ground.AddComponent<BoxCollider>(); gb.center = new Vector3(0, -0.5f, 0);

            var crate = new GameObject("Crate");
            crate.transform.SetParent(root.transform, false);
            crate.transform.localPosition = new Vector3(1, 0.5f, 2);
            crate.transform.localRotation = Quaternion.Euler(0, 30, 0);
            var crb = crate.AddComponent<Rigidbody>(); crb.mass = 12;
            var cb = crate.AddComponent<BoxCollider>(); cb.center = new Vector3(0, 0.25f, 0); cb.size = new Vector3(0.5f, 0.5f, 0.5f);
            var vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(vis.GetComponent<BoxCollider>());
            vis.name = "Visual";
            vis.transform.SetParent(crate.transform, false);
            vis.transform.localPosition = new Vector3(0, 0.25f, 0);
            vis.transform.localScale = new Vector3(0.5f, 0.25f, 0.5f);
            var lab = crate.AddComponent<USDGameReady.GameReadyLabel>();
            lab.ontologyClass = "Asset/Prop/Container/Crate";
            lab.tags = new[] { "materialClass:wood", "function:container" };
            lab.materialProperties = new[] { "flammable" };
            lab.source = "Tier3";

            var ball = new GameObject("Ball");
            ball.transform.SetParent(root.transform, false);
            ball.transform.localPosition = new Vector3(-1, 1, -0.5f);
            ball.transform.localRotation = Quaternion.Euler(10, 20, 30);
            var brb = ball.AddComponent<Rigidbody>(); brb.mass = 2;
            var sc = ball.AddComponent<SphereCollider>(); sc.radius = 0.3f;
            sc.sharedMaterial = new PhysicsMaterial("bouncy") { staticFriction = 0.2f, dynamicFriction = 0.15f, bounciness = 0.8f };

            var pole = new GameObject("Pole");
            pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = new Vector3(0, 2, 0);
            pole.layer = 3;
            var prb = pole.AddComponent<Rigidbody>(); prb.mass = 5;
            var cc = pole.AddComponent<CapsuleCollider>(); cc.direction = 0; cc.radius = 0.1f; cc.height = 2;

            var rock = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            rock.name = "Rock";
            rock.transform.SetParent(root.transform, false);
            rock.transform.localPosition = new Vector3(3, 0.5f, -2);
            UnityEngine.Object.DestroyImmediate(rock.GetComponent<CapsuleCollider>());
            var rmc = rock.AddComponent<MeshCollider>(); rmc.sharedMesh = rock.GetComponent<MeshFilter>().sharedMesh; rmc.convex = false;

            var frame = new GameObject("Frame");
            frame.transform.SetParent(root.transform, false);
            frame.transform.localPosition = new Vector3(-3, 1, 0);
            var frb = frame.AddComponent<Rigidbody>(); frb.isKinematic = true;
            frame.AddComponent<BoxCollider>().size = new Vector3(0.1f, 2, 0.1f);
            var door = new GameObject("Door");
            door.transform.SetParent(root.transform, false);
            door.transform.localPosition = new Vector3(-2.5f, 1, 0);
            var drb = door.AddComponent<Rigidbody>(); drb.mass = 20;
            door.AddComponent<BoxCollider>().size = new Vector3(1, 2, 0.05f);
            var hj = door.AddComponent<ConfigurableJoint>();
            hj.connectedBody = frb; hj.autoConfigureConnectedAnchor = false;
            hj.anchor = new Vector3(-0.5f, 0, 0); hj.connectedAnchor = new Vector3(0, 0, 0);
            hj.xMotion = hj.yMotion = hj.zMotion = ConfigurableJointMotion.Locked;
            hj.angularYMotion = hj.angularZMotion = ConfigurableJointMotion.Locked;
            hj.angularXMotion = ConfigurableJointMotion.Limited;
            var lo = hj.lowAngularXLimit; lo.limit = -90; hj.lowAngularXLimit = lo;
            var hi = hj.highAngularXLimit; hi.limit = 10; hj.highAngularXLimit = hi;
            hj.angularXDrive = new JointDrive { positionSpring = 50, positionDamper = 5, maximumForce = float.MaxValue };
            hj.targetRotation = Quaternion.AngleAxis(20, Vector3.right);

            var slider = new GameObject("Slider");
            slider.transform.SetParent(root.transform, false);
            slider.transform.localPosition = new Vector3(1, 1.2f, 2);
            var srb = slider.AddComponent<Rigidbody>(); srb.mass = 1;
            slider.AddComponent<SphereCollider>().radius = 0.1f;
            var sj = slider.AddComponent<ConfigurableJoint>();
            sj.connectedBody = crb; sj.autoConfigureConnectedAnchor = false;
            sj.anchor = Vector3.zero; sj.connectedAnchor = new Vector3(0, 0.7f, 0);
            sj.xMotion = ConfigurableJointMotion.Limited;
            sj.yMotion = sj.zMotion = ConfigurableJointMotion.Locked;
            sj.angularXMotion = sj.angularYMotion = sj.angularZMotion = ConfigurableJointMotion.Locked;
            var ll = sj.linearLimit; ll.limit = 0.3f; sj.linearLimit = ll;
            sj.xDrive = new JointDrive { positionSpring = 100, positionDamper = 2, maximumForce = float.MaxValue };
            sj.targetPosition = new Vector3(0.1f, 0, 0);

            if (outJson != null) RoundTripDump.ExportTo(outJson);
            var r = GameReadyUsdExporter.Export(new[] { root }, outUsda);
            Debug.Log("[GameReadyBatch] SELFTEST OK " + r.Summary().Replace("\n", " | "));
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[GameReadyBatch] FAIL " + e);
            EditorApplication.Exit(1);
        }
    }

    static GameObject ImportAndPlace(string usda)
    {
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
            // 다중 레이어 에셋(NVIDIA SimReady): 루트 옆 payloads/ 트리를 통째로 복사
            var payloads = Path.Combine(Path.GetDirectoryName(usda), "payloads");
            if (Directory.Exists(payloads)) CopyTree(payloads, dstDir + "/payloads");
            // 내보내기가 만든 <이름>_textures/ 폴더도 같이 복사 (상대 경로 유지)
            foreach (var td in Directory.GetDirectories(Path.GetDirectoryName(usda), "*_textures"))
            {
                string tdst = dstDir + "/" + Path.GetFileName(td);
                Directory.CreateDirectory(tdst);
                foreach (var tf in Directory.GetFiles(td))
                    File.Copy(tf, tdst + "/" + Path.GetFileName(tf), true);
            }

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
            return (GameObject)PrefabUtility.InstantiatePrefab(main);

    }

    static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    static string Arg(string[] a, string k)
    {
        int i = Array.IndexOf(a, k);
        return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : null;
    }
}

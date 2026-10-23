using System.Collections.Generic;
using System.Linq;
using USDGameReady;
using Unity.Importer.USD;
using UnityEditor;
using UnityEngine;
using UnityEngine.Importer;

namespace USDGameReady.Editor
{
    public static class USDGameReadyGraphBuilder
    {
        private const string k_SourceGraphPath =
            "Packages/com.unity.importer.usd/Unity.Importer.USD.Editor/ImportGraph/usdImporter.asset";

        private const string k_OutputDir =
            "Packages/com.eglab.gameready.usd/Editor/ImportGraph";

        private const string k_OutputGraphPath =
            "Packages/com.eglab.gameready.usd/Editor/ImportGraph/usdGameReadyImporter.asset";

        [MenuItem("GameReady/Rebuild USD Importer Graph")]
        public static void BuildGraph()
        {
            var sourceGraph = AssetDatabase.LoadAssetAtPath<ImporterGraph>(k_SourceGraphPath);
            if (sourceGraph == null)
            {
                Debug.LogError($"[USDGameReady] 기본 그래프를 찾을 수 없습니다: {k_SourceGraphPath}");
                return;
            }

            var graph = Object.Instantiate(sourceGraph);

            var filterPhysicsNode     = new FilterUsdPhysicsPrimsNode();
            var createRigidbodyNode   = new CreateRigidbodyNode();

            var buildHierarchyNode = graph.Nodes.OfType<BuildHierarchyNode>().FirstOrDefault();
            if (buildHierarchyNode == null)
            {
                Debug.LogError("[USDGameReady] BuildHierarchyNode를 그래프에서 찾을 수 없습니다.");
                return;
            }

            var hierarchyEdge = graph.Edges.FirstOrDefault(e =>
                e.Destination.Node == (INode<InputPorts, OutputPorts>)buildHierarchyNode &&
                e.Origin.Node is IDictionaryAggregatorNode<Dictionary<string, GameObject>>);

            if (hierarchyEdge.Origin.Node == null)
            {
                Debug.LogError("[USDGameReady] aggregator → BuildHierarchyNode 엣지를 찾을 수 없습니다.");
                return;
            }

            var finalAggregator = hierarchyEdge.Origin.Node;

            var stageOpenNode = graph.Nodes.OfType<UsdStageOpenNode>().FirstOrDefault();
            if (stageOpenNode == null)
            {
                Debug.LogError("[USDGameReady] UsdStageOpenNode를 그래프에서 찾을 수 없습니다.");
                return;
            }

            graph.RemoveEdge(hierarchyEdge);

            graph.AddNode(filterPhysicsNode);
            graph.AddNode(createRigidbodyNode);

            // aggregator -> UsdPhysics(Rigidbody) -> BuildHierarchy
            graph.AddEdge(new Edge(
                finalAggregator,     nameof(IDictionaryAggregatorNode<Dictionary<string, GameObject>>.Output.output),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.gameObjects)));

            graph.AddEdge(new Edge(
                stageOpenNode,     nameof(UsdStageOpenNode.Output.stage),
                filterPhysicsNode, nameof(FilterUsdPhysicsPrimsNode.Input.stage)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.masses),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.masses)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.centersOfMass),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.centersOfMass)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.inertiaDiagonals),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.inertiaDiagonals)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.principalAxes),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.principalAxes)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.collisionPaths),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.collisionPaths)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.colliderShapes),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.colliderShapes)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.colliderBoxSizes),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.colliderBoxSizes)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.colliderRadii),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.colliderRadii)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.colliderCenters),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.colliderCenters)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.colliderRotations),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.colliderRotations)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.materials),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.materials)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.layers),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.layers)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointTypes),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointTypes)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointBody0),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointBody0)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointBody1),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointBody1)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointLocalPos0),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointLocalPos0)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointLocalPos1),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointLocalPos1)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointAxes),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointAxes)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointLimits),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointLimits)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.colliderCapsuleHeights),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.colliderCapsuleHeights)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.colliderCapsuleAxes),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.colliderCapsuleAxes)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointD6Specs),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointD6Specs)));

            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.jointDrives),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.jointDrives)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.labelClasses),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.labelClasses)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.labelTags),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.labelTags)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.labelMatProps),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.labelMatProps)));
            graph.AddEdge(new Edge(
                filterPhysicsNode,   nameof(FilterUsdPhysicsPrimsNode.Output.labelSources),
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.labelSources)));

            graph.AddEdge(new Edge(
                createRigidbodyNode, nameof(CreateRigidbodyNode.Output.gameObjects),
                buildHierarchyNode,  nameof(BuildHierarchyNode.Input.gameObjects)));

            // ImportSettings (Inspector에 표시됨)
            graph.AddImportSetting(new ImportSetting<bool>(USDGameReadyImportSettings.EnableUsdPhysics, true));

            graph.AddSettingEdge(new SettingEdge(USDGameReadyImportSettings.EnableUsdPhysics,
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.enabled)));
            graph.AddImportSetting(new ImportSetting<bool>(USDGameReadyImportSettings.ExactJointLimits, true));
            graph.AddSettingEdge(new SettingEdge(USDGameReadyImportSettings.ExactJointLimits,
                createRigidbodyNode, nameof(CreateRigidbodyNode.Input.exactLimits)));

            System.IO.Directory.CreateDirectory(k_OutputDir);
            AssetDatabase.CreateAsset(graph, k_OutputGraphPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[USDGameReady] 커스텀 임포터 그래프 저장 완료: {k_OutputGraphPath}");
        }
    }
}

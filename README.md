# GameReady USD Physics Importer

A Unity package that teaches the official USD importer (`com.unity.importer.usd`) to read **UsdPhysics**. Mass, center of mass, inertia, collision prims, physics materials and joints authored in a `.usda` file become `Rigidbody`, `Collider`, `PhysicsMaterial` and `ConfigurableJoint` components at import time. No per-asset setup in the editor.

The built-in importer currently ignores UsdPhysics, so the same file imports as mesh and textures only.

## Install

Requires Unity 6000.0 or later. `com.unity.importer.usd` 1.0.0-pre.2 is pulled in as a dependency.

Add to `Packages/manifest.json`:

```json
"com.eglab.gameready.usd": "https://github.com/Erzyh/GameReady-USD-Physics-Importer.git"
```

Or clone and use a local path:

```json
"com.eglab.gameready.usd": "file:../GameReady-USD-Physics-Importer"
```

After the editor compiles, the importer graph `Editor/ImportGraph/usdGameReadyImporter.asset` is ready. If it is missing, run **GameReady > Rebuild USD Importer Graph** once.

## Use

Drop a `.usda` under `Assets`. On first import, a file whose header contains `physics:mass` or `gameReady` is assigned this package's graph and marked as a USD root automatically. Files whose graph reference is broken (for example after the graph asset moved) are repaired the same way. Files that already have a valid graph and files without physics are left untouched. Toggle this with **GameReady > Auto-assign Importer Graph**, or pick the graph by hand in the Inspector of any `.usda`.

## How it works

The Unity USD importer processes a file as a node graph: open the stage, convert prims to meshes and materials, collect a `GameObject` dictionary, then build the hierarchy. This package clones the default graph and inserts two nodes just before hierarchy assembly.

1. `FilterUsdPhysicsPrimsNode` walks every prim and extracts, per path: `physics:mass`, `physics:centerOfMass`, `physics:diagonalInertia`, `physics:principalAxes`, `physics:collisionEnabled` with the collider shape (Cube, Sphere, Capsule or Mesh) and size, `physics:staticFriction`, `physics:dynamicFriction`, `physics:restitution`, revolute, prismatic and D6 joints, and the optional `gameReady:layer` attribute. Values stay in USD coordinates.
2. `CreateRigidbodyNode` takes the `GameObject` dictionary and those tables and attaches components. The right-handed to left-handed conversion (z flip) is applied once here, including the conjugation of quaternions and the per-axis sign rules for joint limits. Friction and restitution are stored on a serialized `UsdPhysMatInfo` component and applied as a `PhysicsMaterial` on `Awake`, because materials created during import are not kept as prefab sub-assets. If a sibling prim named `render` exists, collider mesh renderers are hidden.
3. The result goes to the original `BuildHierarchyNode`. Everything else is the stock importer.

## Files

| Path | Role |
|---|---|
| `Runtime/UsdPhysMatInfo.cs` | Carries friction and restitution into the prefab |
| `Editor/FilterUsdPhysicsPrimsNode.cs` | UsdPhysics extraction node |
| `Editor/CreateRigidbodyNode.cs` | Component creation and coordinate conversion |
| `Editor/USDGameReadyGraphBuilder.cs` | Builds the importer graph from the default one |
| `Editor/GameReadyUsdPostprocessor.cs` | Assigns the graph on first import |
| `Editor/GameReadyBatch.cs`, `Editor/RoundTripDump.cs` | Batch import and dump tools used for round-trip measurement |
| `Editor/ImportGraph/usdGameReadyImporter.asset` | The generated graph |

## Limits

- `com.unity.importer.usd` is a pre-release. The node API may change; the dependency version is pinned.
- Unity convex mesh colliders are limited to 256 faces. Author collision meshes with about 90 vertices or fewer.
- Unity supports only symmetric limits on the Y and Z angular axes and a single linear limit, so asymmetric USD limits are approximated by the larger absolute value.
- Unity only. The Godot and Unreal adapters live in the main GameReady repository.

## Validation

Checked against nine generated props (2 to 120 kg, up to 16 convex colliders each) and a round-trip suite of 200 synthetic assets. With the default graph the same files import with zero rigidbodies; with this package every declared body and collider is created.

## License

MIT. See `LICENSE`.

---

## 한국어 요약

Unity 공식 USD 임포터의 노드 그래프에 물리 노드 두 개를 끼워 넣는 패키지입니다. usda 안의 UsdPhysics 값을 읽어 Rigidbody, 콜라이더, 물리 재질, 조인트를 임포트 시점에 자동으로 붙입니다. 내장 임포터는 UsdPhysics를 해석하지 않으므로 이 패키지가 없으면 메시와 텍스처만 들어옵니다.

설치는 `Packages/manifest.json`에 위 git 주소 한 줄을 추가하면 됩니다. 이후 usda 파일을 Assets 아래에 넣으면 첫 임포트 때 그래프가 자동 지정됩니다.

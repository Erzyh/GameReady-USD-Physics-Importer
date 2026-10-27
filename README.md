# GameReady USD Physics Importer and Exporter

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

Drop a `.usda` under `Assets`. On first import, a file whose header contains `physics:mass` or `gameReady` is assigned this package's graph and marked as a USD root automatically. Files whose graph reference is broken (for example after the graph asset moved) are repaired the same way. A root file next to `payloads/Physics/physics.usda` (the NVIDIA SimReady layout) is assigned the graph too, and layers inside `payloads/` are skipped. For SimReady, copy the whole `simready_usd` folder under `Assets`. Files that already have a valid graph and files without physics are left untouched. Toggle this with **GameReady > Auto-assign Importer Graph**, or pick the graph by hand in the Inspector of any `.usda`.

## How it works

The Unity USD importer processes a file as a node graph: open the stage, convert prims to meshes and materials, collect a `GameObject` dictionary, then build the hierarchy. This package clones the default graph and inserts two nodes just before hierarchy assembly.

1. `FilterUsdPhysicsPrimsNode` walks every prim and extracts, per path: `physics:mass`, `physics:centerOfMass`, `physics:diagonalInertia`, `physics:principalAxes`, `physics:collisionEnabled` with the collider shape (Cube, Sphere, Capsule or Mesh) and size, `physics:staticFriction`, `physics:dynamicFriction`, `physics:restitution`, revolute, prismatic and D6 joints, and the optional `gameReady:layer` attribute. Values stay in USD coordinates.
2. `CreateRigidbodyNode` takes the `GameObject` dictionary and those tables and attaches components. The right-handed to left-handed conversion (z flip) is applied once here, including the conjugation of quaternions and the per-axis sign rules for joint limits. Friction and restitution are stored on a serialized `UsdPhysMatInfo` component and applied as a `PhysicsMaterial` on `Awake`, because materials created during import are not kept as prefab sub-assets. If a sibling prim named `render` exists, collider mesh renderers are hidden.
3. The result goes to the original `BuildHierarchyNode`. Everything else is the stock importer.

### v0.2 additions

- **Exact asymmetric limits.** Unity stores low and high limits separately only on the primary (twist) axis, and one radial linear limit. When a joint has a single moving rotation axis on Y or Z with asymmetric limits, that axis becomes the joint's primary axis. When a joint has a single limited linear axis with asymmetric limits, `connectedAnchor` is shifted by the midpoint so the limit becomes symmetric. Both are exact; no approximation. Toggle with the import setting `GR-Exact Joint Limits` (on by default). The change is recorded on a `GameReadyJointInfo` component so the round-trip dump can report the original USD meaning.
- **Twist sign.** Measured in PhysX: a USD angle maps to the same Unity twist angle on X and Y, and to the negated angle on Z. v0.1 negated X, which mirrored asymmetric X limits in simulation.
- **Joint drives.** `PhysicsDriveAPI` (stiffness, damping, target position) becomes `ConfigurableJoint` drives. Angular stiffness and damping are converted from per-degree (UsdPhysics) to per-radian.
- **Semantic labels.** `gameReady:label:class`, `gameReady:label:tags`, `gameReady:source` and `gameReady:mat:*` become a `GameReadyLabel` component with queries such as `IsA("Asset/Prop/Weapon")`, `HasFunction("openable")` and `IsFlammable`.

### v0.3 additions

- **Exporter.** **GameReady > Export Selection to USD...** and **GameReady > Export Scene to USD...** write a GameReady usda. It applies the importer's conversion in reverse, so a file imported and exported again keeps its values.
  - Hierarchy and transforms. The z flip and quaternion conjugation are undone.
  - `Rigidbody` mass, center of mass, inertia and principal axes become `PhysicsMassAPI`. A kinematic body is written as a static body.
  - Box, Sphere and Capsule colliders become `Cube`, `Sphere` and `Capsule` prims with `purpose = "guide"`. Collider center, size, direction and transform scale are folded into the prim.
  - `MeshCollider` becomes a Mesh prim with `physics:approximation`: `convexHull` when convex, `none` when not.
  - Friction and restitution come from the `PhysicsMaterial`, or from `UsdPhysMatInfo`. The layer goes to `gameReady:layer`.
  - `ConfigurableJoint` becomes a `PhysicsJoint` with per-axis `PhysicsLimitAPI` and `PhysicsDriveAPI`. Moved primary axes and anchor shifts are reversed, and angular stiffness and damping go from per-radian back to per-degree.
  - Render meshes keep points, normals, UVs and per-submesh `GeomSubset` bindings, with the triangle winding reversed.
  - Materials become `UsdPreviewSurface`. The importer's `_DiffuseColor` and `_DiffuseColorMap` are read, as are URP and Built-in names. Textures are copied, or read back from the GPU, into a `<name>_textures/` folder next to the file.
  - `GameReadyLabel` becomes `gameReady:label:*`, `gameReady:source` and `gameReady:mat:*`.
- **Lossless joints.** The importer now stores the original USD axis spec and a fingerprint of the created joint on `GameReadyJointInfo`. If the joint was not edited in Unity, the exporter writes the original spec back. This includes asymmetric Y and Z limits that Unity cannot hold when two or more rotation axes move.
- **Non-convex mesh colliders on import.** A Mesh prim with `physics:approximation = "none"` imports as a non-convex `MeshCollider`. Under a dynamic body it stays convex, because Unity requires that.
- **Batch.** `GameReadyBatch.RunExport -grUsda <in> -grOutUsda <out>` imports a file, places it and exports it again. `GameReadyBatch.RunExportSelfTest -grOutUsda <out>` builds a plain Unity scene in code and exports it.

Exporter limits: only `ConfigurableJoint` is exported, so Hinge, Fixed, Spring and Character joints are skipped with a warning. Terrain and Wheel colliders are skipped. A trigger is written as a normal collider. Custom joint axes are written as `localRot`, which the importer reads from v0.4. Skinned meshes and animation are not exported.

### v0.4 additions: UsdPhysics files from other tools (NVIDIA SimReady)

GameReady files put mass on the body prim and are Y-up. Other UsdPhysics files often do not. v0.4 reads them as the UsdPhysics spec defines.

- **Z-up stages.** Physics values are first rotated into the Y-up convention, then the usual z flip is applied. The two steps together equal the stock importer's Y and Z swap for meshes and transforms, and the measured joint sign rules still hold because the first step is a proper rotation.
- **Bodies are `PhysicsRigidBodyAPI` prims.** Mass, center of mass and inertia written on collider prims under a body are combined into that body: total mass, mass-weighted center of mass, and the inertia tensors moved to the common center (parallel axis theorem) and diagonalized again. A body with mass on the body prim uses that value, as the spec says. A body with no mass anywhere gets its mass from the bound physics material's `physics:density` (1000 kg/m³ if none) and the collider volume.
- **Colliders are `PhysicsCollisionAPI` prims.** `physics:collisionEnabled` no longer has to be authored.
- **Physics materials through `material:binding:physics`.** Friction and restitution are read from the bound material when the collider prim has none of its own.
- **Joint targets below the body.** When `physics:body0` or `physics:body1` points at a prim under the body (for example the collision mesh), the joint goes to the nearest body and `localPos` and `localRot` are moved into that body's frame.
- **Joint frames.** `localRot1` sets the joint axes on body 1, and `localRot0` turns the anchor shift into body 0's frame.
- **Fixed joints** become a `ConfigurableJoint` with every axis locked. Missing or infinite revolute and prismatic limits mean a free axis.
- **Kinematic bodies.** `physics:kinematicEnabled` sets `Rigidbody.isKinematic`.
- **Multi-layer assets.** Layers under a `payloads/` folder are not treated as roots. A root that sits next to `payloads/Physics/physics.usda` gets the GameReady graph even when it is a binary crate file. `GameReadyBatch` copies the `payloads/` tree with the root.

### SimReady import validation

38 NVIDIA SimReady assets, one or more from each of 31 categories (furniture, carts, cabinets, ladders, tools, kitchenware, boxes, bins, lamps and more), were imported and compared with values computed independently by `usd-core` (`unity_tools/checks/compare_simready_import.py`).

| | Result |
|---|---|
| Bodies, colliders, joints | 197/197, 195/195, 159/159 |
| Mass, center of mass, inertia tensor | relative error up to 6e-8, 4e-8 m, 6e-7 |
| Joint axes | all match (abs dot product 1.0) |
| Joint anchors | within 1 µm, except 7 joints whose two anchors are already apart in the source file (largest 161 mm, a fixed joint in a hospital bed). The importer keeps the source values. |
| SimReady to Unity to GameReady usda to Unity (4 assets) | bodies, joints and limits unchanged, positions within 1.6e-6 m |

Before v0.4 the same files imported with bodies in the wrong place, because mass sat on the collision meshes.

### Export validation

Checked with `unity_tools/checks/compare_usd_export.py`, which compares world-space values.

| Input | Original vs export | Export vs re-import and export |
|---|---|---|
| Round-trip suite (7 bodies, capsules, 3 joints) | equal (rounding up to 1e-8) | equal |
| Joint check (7 joints, drives, asymmetric limits) | equal (drive rounding up to 3e-7) | equal |
| Convex decomposition asset (7 convex colliders) | equal | equal |
| Textured asset (20,000 faces, texture, label) | equal; 22 unused vertices dropped | equal |
| Plain Unity scene (6 bodies, non-convex mesh, 2 driven joints) | Unity, USD, Unity: equal (rotation 2e-7) | equal |

The exported files pass the OpenUSD validator rules except where the test file itself has no GameReady label.

## Files

| Path | Role |
|---|---|
| `Runtime/UsdPhysMatInfo.cs` | Carries friction and restitution into the prefab |
| `Runtime/GameReadyLabel.cs` | Semantic labels (class, tags, material properties) for game code |
| `Runtime/GameReadyJointInfo.cs` | Record of exact limit conversions (primary axis, anchor shift) |
| `Editor/FilterUsdPhysicsPrimsNode.cs` | UsdPhysics extraction node |
| `Editor/CreateRigidbodyNode.cs` | Component creation and coordinate conversion |
| `Editor/USDGameReadyGraphBuilder.cs` | Builds the importer graph from the default one |
| `Editor/GameReadyUsdPostprocessor.cs` | Assigns the graph on first import |
| `Editor/GameReadyUsdExporter.cs` | Unity to GameReady usda exporter |
| `Editor/GameReadyBatch.cs`, `Editor/RoundTripDump.cs` | Batch import and dump tools used for round-trip measurement |
| `Editor/ImportGraph/usdGameReadyImporter.asset` | The generated graph |

## Limits

- `com.unity.importer.usd` is a pre-release. The node API may change; the dependency version is pinned.
- Unity convex mesh colliders are limited to 256 faces. Author collision meshes with about 90 vertices or fewer.
- Asymmetric limits are exact only when one rotation axis or one linear axis moves. With two or more moving rotation axes, asymmetric Y and Z limits are approximated by the larger absolute value, and several limited linear axes share one radial limit.
- Unity shares one drive between the Y and Z swing axes. Different Y and Z drives use the stiffer one.
- Unity builds the connected body's joint frame from the pose at creation, so `localRot0` is used only for the anchor shift. Files whose two joint frames disagree in the rest pose import with body 1's frame.
- `sdf` and `convexDecomposition` mesh colliders under a moving body become one convex hull, because Unity has neither. SimReady uses `sdf` everywhere, so concave parts (a bin's opening, a cart's frame) become solid in Unity.
- `metersPerUnit` other than 1 is not applied to physics lengths. A warning is logged.
- Unity only. The Godot and Unreal adapters live in the main GameReady repository.

## Validation

Checked against nine generated props (2 to 120 kg, up to 16 convex colliders each) and a round-trip suite of 200 synthetic assets. With the default graph the same files import with zero rigidbodies; with this package every declared body and collider is created.

## License

MIT. See `LICENSE`.

---

## 한국어 요약

**v0.4**: NVIDIA SimReady처럼 다른 도구가 만든 UsdPhysics 파일도 읽습니다. 처리하는 항목은 다음과 같습니다.

- Z-up 좌표계
- 충돌 메시에 나뉘어 적힌 질량 (강체별로 합산)
- 질량이 없는 강체 (물리 재질 밀도로 계산)
- `material:binding:physics`로 연결된 마찰과 반발
- 강체 아래 메시를 가리키는 조인트
- 조인트 틀 (`localRot`)
- Fixed 조인트와 무한대 한계

SimReady 31개 범주 38개로 검증했습니다.
- 강체 197개, 조인트 159개가 모두 들어옵니다.
- 질량, 무게중심, 관성 오차는 최대 6e-7입니다.
- 조인트 축은 전부 일치합니다.

SimReady의 `sdf` 메시 콜라이더는 Unity에 대응하는 콜라이더가 없어 볼록 껍질이 됩니다.

Unity 공식 USD 임포터의 노드 그래프에 물리 노드 두 개를 끼워 넣는 패키지입니다. v0.3부터 반대 방향인 내보내기도 들어 있습니다. 메뉴 GameReady > Export Selection to USD... 또는 Export Scene to USD...로 씬을 GameReady usda로 저장합니다. 임포터 변환을 거꾸로 적용하므로 임포트했다가 다시 내보내면 물리값, 라벨, 메시가 보존됩니다. usda 안의 UsdPhysics 값을 읽어 Rigidbody, 콜라이더, 물리 재질, 조인트를 임포트 시점에 자동으로 붙입니다. 내장 임포터는 UsdPhysics를 해석하지 않으므로 이 패키지가 없으면 메시와 텍스처만 들어옵니다.

설치는 `Packages/manifest.json`에 위 git 주소 한 줄을 추가하면 됩니다. 이후 usda 파일을 Assets 아래에 넣으면 첫 임포트 때 그래프가 자동 지정됩니다.

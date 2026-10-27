// GameReady USD Exporter — Unity 씬을 GameReady usda(UsdPhysics + gameReady:*)로 내보낸다.
// ------------------------------------------------------------------
// 임포터(CreateRigidbodyNode)의 변환을 그대로 거꾸로 적용한다.
//   좌표: Unity(LH, Y-up) -> USD(RH, Y-up), B = diag(1,1,-1). 벡터 z 반전, 쿼터니언 (x,y,z,w) -> (-x,-y,z,w)
//   메시: 정점과 노멀 z 반전, 삼각형 감기 순서 반전
//   조인트: 주축 재지정(GameReadyJointInfo.primaryDof), 앵커 이동(anchorShift), twist 부호, 드라이브 단위(/rad -> /deg)
// 메뉴: GameReady > Export Selection to USD... / Export Scene to USD...
// 배치: GameReadyBatch.RunExport (-grUsda 입력 -grOutUsda 출력)
// ------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace USDGameReady.Editor
{
    public static class GameReadyUsdExporter
    {
        public const string OntologyVersion = "0.3.1";

        // ---------------- 메뉴 ----------------

        [MenuItem("GameReady/Export Selection to USD...")]
        static void ExportSelectionMenu()
        {
            var roots = Selection.transforms.Select(t => t.gameObject).ToArray();
            string path = EditorUtility.SaveFilePanel("Export GameReady USD", "", roots[0].name + ".usda", "usda");
            if (string.IsNullOrEmpty(path)) return;
            var r = Export(roots, path, roots.Length == 1 && IsUsdPrefabRoot(roots[0]));
            EditorUtility.DisplayDialog("GameReady USD", r.Summary(), "OK");
        }

        // USD 파일을 임포트해 만든 프리팹의 루트 = 원래 /World (자식이 World 아래 prim들)
        static bool IsUsdPrefabRoot(GameObject go)
        {
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) return false;
            var p = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) ?? "";
            var e = System.IO.Path.GetExtension(p).ToLowerInvariant();
            return e == ".usda" || e == ".usd" || e == ".usdc";
        }

        [MenuItem("GameReady/Export Selection to USD...", true)]
        static bool ExportSelectionValidate() => Selection.transforms.Length > 0;

        [MenuItem("GameReady/Export Scene to USD...")]
        static void ExportSceneMenu()
        {
            var scene = SceneManager.GetActiveScene();
            string path = EditorUtility.SaveFilePanel("Export GameReady USD", "",
                (string.IsNullOrEmpty(scene.name) ? "scene" : scene.name) + ".usda", "usda");
            if (string.IsNullOrEmpty(path)) return;
            var r = Export(scene.GetRootGameObjects(), path);
            EditorUtility.DisplayDialog("GameReady USD", r.Summary(), "OK");
        }

        // ---------------- 결과 ----------------

        public class Report
        {
            public string path;
            public int prims, bodies, colliders, joints, jointsRestored, meshes, materials, textures, labels;
            public List<string> warnings = new List<string>();
            public string Summary() =>
                $"{Path.GetFileName(path)}\nprims {prims}, bodies {bodies}, colliders {colliders}, joints {joints} (원본 사양 복원 {jointsRestored})\n" +
                $"meshes {meshes}, materials {materials}, textures {textures}, labels {labels}" +
                (warnings.Count > 0 ? $"\nwarnings {warnings.Count} (Console 참고)" : "");
        }

        // ---------------- prim 트리 ----------------

        class Prim
        {
            public string name, type;
            public List<string> apis = new List<string>();
            public List<string> meta = new List<string>();      // ( ... ) 안의 추가 메타데이터
            public List<string> attrs = new List<string>();
            public List<Prim> children = new List<Prim>();
            public Prim parent;
            public string Path => parent == null ? "/" + name : parent.Path + "/" + name;

            public Prim Add(string childName, string childType)
            {
                var c = new Prim { name = UniqueName(childName), type = childType, parent = this };
                children.Add(c);
                return c;
            }

            string UniqueName(string n)
            {
                n = Sanitize(n);
                string u = n;
                for (int i = 1; children.Any(c => c.name == u); i++) u = n + "_" + i;
                return u;
            }
        }

        static string Sanitize(string n)
        {
            if (string.IsNullOrEmpty(n)) return "prim";
            var sb = new StringBuilder();
            foreach (char ch in n)
                sb.Append((ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_')) ? ch : '_');
            var s = sb.ToString();
            if (char.IsDigit(s[0])) s = "_" + s;
            return s;
        }

        // ---------------- 값 포맷 ----------------

        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static string F(float v) => v.ToString("G9", IC);
        static string F(double v) => v.ToString("G9", IC);
        static Vector3 XfVec(Vector3 v) => new Vector3(v.x, v.y, -v.z);
        static Quaternion XfQuat(Quaternion q) => new Quaternion(-q.x, -q.y, q.z, q.w);
        static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";
        static string Q(Quaternion q) => $"({F(q.w)}, {F(q.x)}, {F(q.y)}, {F(q.z)})";   // usda quatf: (w, x, y, z)
        static string Str(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ---------------- 내보내기 ----------------

        class Ctx
        {
            public Report rep;
            public string outDir, texDirName;
            public Prim world, looks;
            public Dictionary<GameObject, Prim> goPrim = new Dictionary<GameObject, Prim>();
            public Dictionary<Material, Prim> matPrim = new Dictionary<Material, Prim>();
            public Dictionary<Texture, string> texFile = new Dictionary<Texture, string>();
            public List<ConfigurableJoint> joints = new List<ConfigurableJoint>();
        }

        public static Report Export(IList<GameObject> roots, string path, bool rootIsWorld = false)
        {
            var ctx = new Ctx { rep = new Report { path = path } };
            ctx.outDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            ctx.texDirName = System.IO.Path.GetFileNameWithoutExtension(path) + "_textures";
            ctx.world = new Prim { name = "World", type = "Xform" };

            var g = Physics.gravity;
            var ps = ctx.world.Add("physicsScene", "PhysicsScene");
            // 프로젝트 중력 그대로 (0이면 방향은 기본 -Y, 크기 0)
            ps.attrs.Add($"vector3f physics:gravityDirection = {V(g.sqrMagnitude > 0 ? XfVec(g.normalized) : Vector3.down)}");
            ps.attrs.Add($"float physics:gravityMagnitude = {F(g.magnitude)}");

            // 루트가 하나이고 이름이 World면 (GameReady usda를 임포트한 결과) 그 GO를 /World로 쓴다
            if (roots.Count == 1 && (rootIsWorld || roots[0].name == "World"))
            {
                WriteXform(ctx.world, roots[0].transform);
                ctx.goPrim[roots[0]] = ctx.world;
                foreach (Transform c in roots[0].transform) Visit(ctx, ctx.world, c.gameObject);
                CollectComponents(ctx, roots[0], ctx.world);
            }
            else
                foreach (var r in roots) Visit(ctx, ctx.world, r);

            foreach (var j in ctx.joints) WriteJoint(ctx, j);

            var sb = new StringBuilder();
            sb.Append("#usda 1.0\n(\n");
            sb.Append("    doc = \"\"\"GameReady usda exported from Unity (com.eglab.gameready.usd).\n");
            sb.Append("    Frame: RH Y-up, meters, kg. Limit angles in degrees (UsdPhysics).\"\"\"\n");
            sb.Append("    defaultPrim = \"World\"\n    upAxis = \"Y\"\n    metersPerUnit = 1\n    kilogramsPerUnit = 1\n)\n\n");
            Serialize(sb, ctx.world, 0, ctx.rep);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

            foreach (var w in ctx.rep.warnings) Debug.LogWarning("[GameReady Export] " + w);
            Debug.Log("[GameReady Export] " + ctx.rep.Summary().Replace("\n", " | "));
            return ctx.rep;
        }

        static void Serialize(StringBuilder sb, Prim p, int depth, Report rep)
        {
            rep.prims++;
            string ind = new string(' ', depth * 4);
            sb.Append($"{ind}def {p.type} {Str(p.name)}");
            if (p.apis.Count > 0 || p.meta.Count > 0)
            {
                sb.Append(" (\n");
                if (p.apis.Count > 0)
                    sb.Append($"{ind}    prepend apiSchemas = [{string.Join(", ", p.apis.Select(Str))}]\n");
                foreach (var m in p.meta) sb.Append($"{ind}    {m}\n");
                sb.Append($"{ind})");
            }
            sb.Append($"\n{ind}{{\n");
            foreach (var a in p.attrs) sb.Append($"{ind}    {a}\n");
            foreach (var c in p.children) Serialize(sb, c, depth + 1, rep);
            sb.Append($"{ind}}}\n");
        }

        static void WriteXform(Prim p, Transform t, bool withScale = true)
        {
            var ops = new List<string>();
            if (t.localPosition != Vector3.zero)
            { p.attrs.Add($"float3 xformOp:translate = {V(XfVec(t.localPosition))}"); ops.Add("\"xformOp:translate\""); }
            if (t.localRotation != Quaternion.identity)
            { p.attrs.Add($"quatf xformOp:orient = {Q(XfQuat(t.localRotation))}"); ops.Add("\"xformOp:orient\""); }
            if (withScale && t.localScale != Vector3.one)
            { p.attrs.Add($"float3 xformOp:scale = {V(t.localScale)}"); ops.Add("\"xformOp:scale\""); }
            if (ops.Count > 0) p.attrs.Add($"uniform token[] xformOpOrder = [{string.Join(", ", ops)}]");
        }

        static void WriteTRS(Prim p, Vector3 pos, Quaternion rot, Vector3? scale)
        {
            var ops = new List<string>();
            p.attrs.Add($"float3 xformOp:translate = {V(XfVec(pos))}"); ops.Add("\"xformOp:translate\"");
            p.attrs.Add($"quatf xformOp:orient = {Q(XfQuat(rot))}"); ops.Add("\"xformOp:orient\"");
            if (scale.HasValue) { p.attrs.Add($"float3 xformOp:scale = {V(scale.Value)}"); ops.Add("\"xformOp:scale\""); }
            p.attrs.Add($"uniform token[] xformOpOrder = [{string.Join(", ", ops)}]");
        }

        // GO 하나 -> prim. 자식이 없고 메시나 콜라이더 하나만 가진 GO는 그 자체를 Mesh/Cube/... prim으로 접는다
        // (임포터가 만든 콜라이더 GO와 렌더 GO가 원래 prim 경로로 돌아가게)
        static void Visit(Ctx ctx, Prim parent, GameObject go)
        {
            var cols = go.GetComponents<Collider>().Where(Supported).ToArray();
            foreach (var bad in go.GetComponents<Collider>().Where(c => !Supported(c)))
                ctx.rep.warnings.Add($"{go.name}: {bad.GetType().Name}는 지원하지 않아 생략합니다");
            var mf = go.GetComponent<MeshFilter>();
            var rb = go.GetComponent<Rigidbody>();
            bool leaf = go.transform.childCount == 0 && rb == null && go.GetComponent<GameReadyLabel>() == null
                        && go.GetComponents<Joint>().Length == 0;

            // 임포터가 만든 빈 Looks/physicsScene GO는 건너뜀 (다시 만들어 씀)
            if (go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1 &&
                (go.name == "physicsScene" || go.name == "Looks"))
                return;

            Prim p;
            if (leaf && cols.Length == 1 && mf == null && !(cols[0] is MeshCollider))
            {
                p = parent.Add(go.name, PrimType(cols[0]));
                WritePrimitiveCollider(ctx, p, cols[0], go.transform, collapsed: true);
            }
            else if (leaf && mf != null && mf.sharedMesh != null &&
                     (cols.Length == 0 || (cols.Length == 1 && cols[0] is MeshCollider mc0 &&
                                           (mc0.sharedMesh == null || mc0.sharedMesh == mf.sharedMesh))))
            {
                p = parent.Add(go.name, "Mesh");
                WriteXform(p, go.transform);
                WriteMesh(ctx, p, mf.sharedMesh, go.GetComponent<MeshRenderer>());
                if (cols.Length == 1) WriteColliderCommon(ctx, p, cols[0], meshCollider: (MeshCollider)cols[0]);
                var mr0 = go.GetComponent<MeshRenderer>();
                if (mr0 == null || !mr0.enabled)
                    p.attrs.Add("uniform token purpose = \"guide\"");          // 숨긴 콜라이더 메시
            }
            else
            {
                p = parent.Add(go.name, "Xform");
                WriteXform(p, go.transform);
                if (mf != null && mf.sharedMesh != null)
                {
                    var mp = p.Add(go.name + "_mesh", "Mesh");
                    WriteMesh(ctx, mp, mf.sharedMesh, go.GetComponent<MeshRenderer>());
                }
                int k = 0;
                foreach (var c in cols)
                {
                    if (c is MeshCollider mc)
                    {
                        var cp = p.Add(go.name + "_col" + k++, "Mesh");
                        if (mc.sharedMesh == null) { ctx.rep.warnings.Add($"{go.name}: MeshCollider without mesh"); continue; }
                        WriteMeshGeometry(ctx, cp, mc.sharedMesh, null);
                        WriteColliderCommon(ctx, cp, c, meshCollider: mc);
                    }
                    else
                    {
                        var cp = p.Add(go.name + "_col" + k++, PrimType(c));
                        WritePrimitiveCollider(ctx, cp, c, go.transform, collapsed: false);
                    }
                }
                foreach (Transform ch in go.transform) Visit(ctx, p, ch.gameObject);
            }
            ctx.goPrim[go] = p;
            CollectComponents(ctx, go, p);
        }

        static void CollectComponents(Ctx ctx, GameObject go, Prim p)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) WriteBody(ctx, p, rb);
            var lab = go.GetComponent<GameReadyLabel>();
            if (lab != null) WriteLabel(ctx, p, lab);
            foreach (var j in go.GetComponents<ConfigurableJoint>()) ctx.joints.Add(j);
            foreach (var j in go.GetComponents<Joint>())
                if (!(j is ConfigurableJoint))
                    ctx.rep.warnings.Add($"{go.name}: {j.GetType().Name}는 지원하지 않습니다 (ConfigurableJoint만)");
        }

        static bool Supported(Collider c) =>
            c is BoxCollider || c is SphereCollider || c is CapsuleCollider || c is MeshCollider;

        static string PrimType(Collider c) =>
            c is BoxCollider ? "Cube" : c is SphereCollider ? "Sphere" : c is CapsuleCollider ? "Capsule" : "Mesh";

        // ---------------- 강체, 라벨 ----------------

        static void WriteBody(Ctx ctx, Prim p, Rigidbody rb)
        {
            if (rb.isKinematic)
            {
                // 정적 바디 (임포터는 조인트 body0이면 kinematic Rigidbody로 다시 만든다)
                ctx.rep.bodies++;
                return;
            }
            p.apis.Add("PhysicsRigidBodyAPI");
            p.apis.Add("PhysicsMassAPI");
            p.attrs.Add($"float physics:mass = {F(rb.mass)}");
            p.attrs.Add($"point3f physics:centerOfMass = {V(XfVec(rb.centerOfMass))}");
            p.attrs.Add($"float3 physics:diagonalInertia = {V(rb.inertiaTensor)}");
            p.attrs.Add($"quatf physics:principalAxes = {Q(XfQuat(rb.inertiaTensorRotation))}");
            ctx.rep.bodies++;
        }

        static void WriteLabel(Ctx ctx, Prim p, GameReadyLabel lab)
        {
            if (string.IsNullOrEmpty(lab.ontologyClass)) return;
            p.attrs.Add($"uniform token gameReady:guid = {Str("unity" + p.Path)}");
            p.attrs.Add($"token gameReady:label:class = {Str(lab.ontologyClass)}");
            p.attrs.Add($"uniform token gameReady:ontologyVersion = {Str(OntologyVersion)}");
            if (lab.tags != null && lab.tags.Length > 0)
                p.attrs.Add($"string[] gameReady:label:tags = [{string.Join(", ", lab.tags.Select(Str))}]");
            if (!string.IsNullOrEmpty(lab.source))
                p.attrs.Add($"token gameReady:source = {Str(lab.source)}");
            var mp = lab.materialProperties ?? Array.Empty<string>();
            foreach (var flag in new[] { "flammable", "conductive", "magnetic" })
                p.attrs.Add($"bool gameReady:mat:{flag} = {(mp.Contains(flag) ? 1 : 0)}");
            p.attrs.Add($"float gameReady:mat:buoyancy = {(mp.Contains("buoyant") ? 1 : 0)}");
            ctx.rep.labels++;
        }

        // ---------------- 콜라이더 ----------------

        static void WritePrimitiveCollider(Ctx ctx, Prim p, Collider c, Transform t, bool collapsed)
        {
            // collapsed: prim이 GO 자체 (GO의 로컬 TRS를 합성). 아니면 GO prim의 자식 (GO 로컬 공간)
            Vector3 ls = collapsed ? t.localScale : Vector3.one;
            Vector3 lp = collapsed ? t.localPosition : Vector3.zero;
            Quaternion lr = collapsed ? t.localRotation : Quaternion.identity;
            if (c is BoxCollider b)
            {
                p.attrs.Add("double size = 1");
                var size = Vector3.Scale(b.size, ls);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                WriteTRS(p, lp + lr * Vector3.Scale(b.center, ls), lr, size);
            }
            else if (c is SphereCollider s)
            {
                float m = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                p.attrs.Add($"double radius = {F(s.radius * m)}");
                WriteTRS(p, lp + lr * Vector3.Scale(s.center, ls), lr, null);
            }
            else if (c is CapsuleCollider cc)
            {
                int ax = cc.direction;
                float sa = Mathf.Abs(ls[ax]);
                float sr = Mathf.Max(Mathf.Abs(ls[(ax + 1) % 3]), Mathf.Abs(ls[(ax + 2) % 3]));
                float r = cc.radius * sr;
                float h = Mathf.Max(0f, cc.height * sa - 2f * r);        // USD height = 원통부
                p.attrs.Add($"double radius = {F(r)}");
                p.attrs.Add($"double height = {F(h)}");
                p.attrs.Add($"uniform token axis = \"{"XYZ"[ax]}\"");
                WriteTRS(p, lp + lr * Vector3.Scale(cc.center, ls), lr, null);
            }
            p.attrs.Add("uniform token purpose = \"guide\"");                  // 물리 프록시는 렌더하지 않음
            WriteColliderCommon(ctx, p, c, null);
        }

        static void WriteColliderCommon(Ctx ctx, Prim p, Collider c, MeshCollider meshCollider)
        {
            p.apis.Add("PhysicsCollisionAPI");
            if (meshCollider != null)
            {
                p.apis.Add("PhysicsMeshCollisionAPI");
                // 비볼록 MeshCollider는 삼각형 메시 그대로 (UsdPhysics approximation none)
                p.attrs.Add($"uniform token physics:approximation = \"{(meshCollider.convex ? "convexHull" : "none")}\"");
            }
            p.attrs.Add($"bool physics:collisionEnabled = {(c.enabled ? 1 : 0)}");
            float fs = 0.6f, fd = 0.6f, rest = 0f;                              // Unity 기본 PhysicsMaterial
            var info = c.GetComponent<UsdPhysMatInfo>();
            var pm = c.sharedMaterial;
            if (pm != null) { fs = pm.staticFriction; fd = pm.dynamicFriction; rest = pm.bounciness; }
            else if (info != null) { fs = info.staticFriction; fd = info.dynamicFriction; rest = info.bounciness; }
            p.attrs.Add($"float physics:staticFriction = {F(fs)}");
            p.attrs.Add($"float physics:dynamicFriction = {F(fd)}");
            p.attrs.Add($"float physics:restitution = {F(rest)}");
            p.attrs.Add($"custom float gameReady:layer = {c.gameObject.layer}");
            if (c.isTrigger) ctx.rep.warnings.Add($"{c.name}: 트리거 콜라이더는 일반 콜라이더로 내보냅니다");
            ctx.rep.colliders++;
        }

        // ---------------- 메시, 머티리얼 ----------------

        static void WriteMesh(Ctx ctx, Prim p, Mesh mesh, MeshRenderer mr)
        {
            var mats = mr != null ? mr.sharedMaterials : null;
            WriteMeshGeometry(ctx, p, mesh, mats);
            ctx.rep.meshes++;
        }

        static void WriteMeshGeometry(Ctx ctx, Prim p, Mesh mesh, Material[] mats)
        {
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var uv = mesh.uv;
            var counts = new StringBuilder();
            var idx = new StringBuilder();
            var subsets = new List<(int start, int count)>();
            int face = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                { ctx.rep.warnings.Add($"{mesh.name}: 삼각형이 아닌 서브메시 {s} 생략"); subsets.Add((face, 0)); continue; }
                var tri = mesh.GetTriangles(s);
                int start = face;
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    if (face > 0) { counts.Append(", "); idx.Append(", "); }
                    counts.Append('3');
                    idx.Append(tri[i]).Append(", ").Append(tri[i + 2]).Append(", ").Append(tri[i + 1]);   // 감기 반전
                    face++;
                }
                subsets.Add((start, face - start));
            }
            if (verts.Length == 0) return;
            var mn = Vector3.positiveInfinity; var mx = Vector3.negativeInfinity;
            var pts = new StringBuilder();
            for (int i = 0; i < verts.Length; i++)
            {
                var v = XfVec(verts[i]);
                mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v);
                if (i > 0) pts.Append(", ");
                pts.Append(V(v));
            }
            p.attrs.Add("uniform token subdivisionScheme = \"none\"");
            p.attrs.Add($"float3[] extent = [{V(mn)}, {V(mx)}]");
            p.attrs.Add($"point3f[] points = [{pts}]");
            p.attrs.Add($"int[] faceVertexCounts = [{counts}]");
            p.attrs.Add($"int[] faceVertexIndices = [{idx}]");
            if (normals != null && normals.Length == verts.Length)
                p.attrs.Add($"normal3f[] normals = [{string.Join(", ", normals.Select(n => V(XfVec(n))))}] (\n" +
                            "        interpolation = \"vertex\"\n    )");
            if (uv != null && uv.Length == verts.Length)
                p.attrs.Add($"texCoord2f[] primvars:st = [{string.Join(", ", uv.Select(u => $"({F(u.x)}, {F(u.y)})"))}] (\n" +
                            "        interpolation = \"vertex\"\n    )");

            if (mats == null || mats.Length == 0) return;
            var used = new List<(int s, Material m)>();
            for (int s = 0; s < subsets.Count; s++)
                if (s < mats.Length && mats[s] != null && subsets[s].count > 0) used.Add((s, mats[s]));
            if (used.Count == 0) return;
            if (used.Count == 1 || used.All(u => u.m == used[0].m))
            {
                p.apis.Add("MaterialBindingAPI");
                p.attrs.Add($"rel material:binding = <{MaterialPrim(ctx, used[0].m).Path}>");
                return;
            }
            foreach (var (s, m) in used)
            {
                var gs = p.Add("subset_" + s, "GeomSubset");
                gs.apis.Add("MaterialBindingAPI");
                gs.attrs.Add("uniform token elementType = \"face\"");
                gs.attrs.Add("uniform token familyName = \"materialBind\"");
                gs.attrs.Add($"int[] indices = [{string.Join(", ", Enumerable.Range(subsets[s].start, subsets[s].count))}]");
                gs.attrs.Add($"rel material:binding = <{MaterialPrim(ctx, m).Path}>");
            }
        }

        static Prim MaterialPrim(Ctx ctx, Material m)
        {
            if (ctx.matPrim.TryGetValue(m, out var mp)) return mp;
            if (ctx.looks == null) ctx.looks = ctx.world.Add("Looks", "Scope");
            mp = ctx.looks.Add(m.name, "Material");
            ctx.matPrim[m] = mp;
            ctx.rep.materials++;

            // Unity USD 임포터 머티리얼(_DiffuseColor, _DiffuseColorMap ...)과 URP/내장 셰이더 이름을 모두 확인
            Color col = Color.white;
            foreach (var prop in new[] { "_DiffuseColor", "_BaseColor", "_Color" })
                if (m.HasProperty(prop)) { col = m.GetColor(prop); break; }
            Texture tex = FirstTexture(m, "_DiffuseColorMap", "_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap");
            Texture nrm = FirstTexture(m, "_NormalMap", "_BumpMap");
            float metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
            float rough = m.HasProperty("_Roughness") ? m.GetFloat("_Roughness")
                        : 1f - (m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness")
                              : m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 0.5f);
            float opacity = m.HasProperty("_Opacity") ? m.GetFloat("_Opacity") : col.a;

            string mpath = mp.Path;
            mp.attrs.Add($"token outputs:surface.connect = <{mpath}/pbr.outputs:surface>");
            var pbr = mp.Add("pbr", "Shader");
            pbr.attrs.Add("uniform token info:id = \"UsdPreviewSurface\"");
            pbr.attrs.Add($"float inputs:metallic = {F(metallic)}");
            pbr.attrs.Add($"float inputs:roughness = {F(rough)}");
            pbr.attrs.Add($"float inputs:opacity = {F(opacity)}");
            string file = tex != null ? TextureFile(ctx, tex) : null;
            string nfile = nrm != null ? TextureFile(ctx, nrm) : null;
            if (file != null || nfile != null)
            {
                var st = mp.Add("st", "Shader");
                st.attrs.Add("uniform token info:id = \"UsdPrimvarReader_float2\"");
                st.attrs.Add("string inputs:varname = \"st\"");
                st.attrs.Add("float2 outputs:result");
            }
            if (nfile != null)
            {
                pbr.attrs.Add($"normal3f inputs:normal.connect = <{mpath}/nrm.outputs:rgb>");
                var nt = mp.Add("nrm", "Shader");
                nt.attrs.Add("uniform token info:id = \"UsdUVTexture\"");
                nt.attrs.Add($"asset inputs:file = @{nfile}@");
                nt.attrs.Add("token inputs:sourceColorSpace = \"raw\"");
                nt.attrs.Add("float4 inputs:scale = (2, 2, 2, 1)");
                nt.attrs.Add("float4 inputs:bias = (-1, -1, -1, 0)");
                nt.attrs.Add($"float2 inputs:st.connect = <{mpath}/st.outputs:result>");
                nt.attrs.Add("float3 outputs:rgb");
            }
            if (file != null)
            {
                pbr.attrs.Add($"color3f inputs:diffuseColor.connect = <{mpath}/tex.outputs:rgb>");
                var tx = mp.Add("tex", "Shader");
                tx.attrs.Add("uniform token info:id = \"UsdUVTexture\"");
                tx.attrs.Add($"asset inputs:file = @{file}@");
                tx.attrs.Add("token inputs:sourceColorSpace = \"sRGB\"");
                tx.attrs.Add($"float4 inputs:scale = ({F(col.r)}, {F(col.g)}, {F(col.b)}, {F(col.a)})");
                tx.attrs.Add($"float2 inputs:st.connect = <{mpath}/st.outputs:result>");
                tx.attrs.Add("float3 outputs:rgb");
            }
            else
                pbr.attrs.Add($"color3f inputs:diffuseColor = ({F(col.r)}, {F(col.g)}, {F(col.b)})");
            pbr.attrs.Add("token outputs:surface");
            return mp;
        }

        static Texture FirstTexture(Material m, params string[] props)
        {
            foreach (var p in props)
                if (m.HasProperty(p) && m.GetTexture(p) != null) return m.GetTexture(p);
            return null;
        }

        // 텍스처를 usda 옆 <이름>_textures/ 폴더에 PNG로 저장하고 상대 경로를 돌려준다.
        // 원본 이미지 파일이 있으면 복사, 없으면(임베디드, 읽기 불가) GPU에서 읽어 인코딩
        static string TextureFile(Ctx ctx, Texture tex)
        {
            if (ctx.texFile.TryGetValue(tex, out var f)) return f;
            string dir = System.IO.Path.Combine(ctx.outDir, ctx.texDirName);
            Directory.CreateDirectory(dir);
            string src = AssetDatabase.GetAssetPath(tex);
            string ext = System.IO.Path.GetExtension(src).ToLowerInvariant();
            string name = Sanitize(string.IsNullOrEmpty(tex.name) ? "tex" : tex.name);
            string rel;
            try
            {
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                {
                    rel = $"./{ctx.texDirName}/{name}{ext}";
                    File.Copy(System.IO.Path.GetFullPath(src), System.IO.Path.Combine(dir, name + ext), true);
                }
                else
                {
                    rel = $"./{ctx.texDirName}/{name}.png";
                    var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32,
                                                        RenderTextureReadWrite.sRGB);
                    Graphics.Blit(tex, rt);
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var t2 = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false);
                    t2.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                    t2.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), t2.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(t2);
                }
            }
            catch (Exception e)
            {
                ctx.rep.warnings.Add($"텍스처 {tex.name} 저장 실패: {e.Message}");
                rel = null;
            }
            ctx.texFile[tex] = rel;
            if (rel != null) ctx.rep.textures++;
            return rel;
        }

        // ---------------- 조인트 ----------------

        static readonly string[] k_Dofs = { "transX", "transY", "transZ", "rotX", "rotY", "rotZ" };

        class Dof { public char motion = 'L'; public float lo, hi; public bool drive; public float k, c, t; }

        // 주축 a, 보조축 s 로 정한 오른손 틀 (x=a, y=s의 직교 성분)
        static Quaternion AxesFrame(Vector3 a, Vector3 s)
        {
            var z = Vector3.Cross(a.normalized, s.normalized).normalized;
            var y = Vector3.Cross(z, a.normalized).normalized;
            return Quaternion.LookRotation(z, y);
        }

        static void WriteJoint(Ctx ctx, ConfigurableJoint j)
        {
            var go1 = j.gameObject;
            if (!ctx.goPrim.TryGetValue(go1, out var p1)) return;
            Prim p0 = null;
            if (j.connectedBody != null && !ctx.goPrim.TryGetValue(j.connectedBody.gameObject, out p0))
            {
                ctx.rep.warnings.Add($"{go1.name}: 연결 바디 {j.connectedBody.name}가 내보내기 범위 밖이라 조인트 생략");
                return;
            }

            GameReadyJointInfo info = go1.GetComponents<GameReadyJointInfo>().FirstOrDefault(i => i.joint == j);
            string primary = info != null ? info.primaryDof ?? "" : "";
            Vector3 shiftUsd = info != null ? XfVec(info.anchorShift) : Vector3.zero;

            // 조인트 프레임 (임포터의 역): j.axis = F * 주축, j.secondaryAxis = F * 보조축.
            // 주축 재지정분을 걷어낸 F가 USD localRot1 (Unity 좌표). 기본 축이면 항등
            Vector3 b1 = Vector3.right, b2 = Vector3.up;
            if (primary == "rotY") { b1 = Vector3.up; b2 = Vector3.right; }
            else if (primary == "rotZ") { b1 = Vector3.forward; b2 = Vector3.up; }
            Quaternion frameU = AxesFrame(j.axis, j.secondaryAxis) * Quaternion.Inverse(AxesFrame(b1, b2));
            if (Quaternion.Angle(frameU, Quaternion.identity) < 1e-3f) frameU = Quaternion.identity;

            var d = new Dictionary<string, Dof>();
            foreach (var k in k_Dofs) d[k] = new Dof();

            // 선형: 공용 반경 한계 -> 대칭 구간, 앵커 이동이 있으면 그 축의 중앙값을 되돌림
            float L = j.linearLimit.limit;
            SetLinear(d["transX"], j.xMotion, L, shiftUsd.x);
            SetLinear(d["transY"], j.yMotion, L, shiftUsd.y);
            SetLinear(d["transZ"], j.zMotion, L, shiftUsd.z);

            // 회전: 주축(twist) 한계는 low/high 그대로. rotZ를 주축으로 옮긴 경우만 부호 반전
            float lx = j.lowAngularXLimit.limit, hx = j.highAngularXLimit.limit;
            if (primary == "rotY" || primary == "rotZ")
            {
                var pd = d[primary];
                SetMotion(pd, j.angularXMotion);
                if (pd.motion == 'M')
                {
                    if (primary == "rotZ") { pd.lo = -hx; pd.hi = -lx; } else { pd.lo = lx; pd.hi = hx; }
                }
                if (j.angularYMotion != ConfigurableJointMotion.Locked || j.angularZMotion != ConfigurableJointMotion.Locked)
                    ctx.rep.warnings.Add($"{go1.name}: 주축 재지정 조인트의 나머지 회전축은 잠금으로 내보냅니다");
            }
            else
            {
                SetMotion(d["rotX"], j.angularXMotion);
                if (d["rotX"].motion == 'M') { d["rotX"].lo = lx; d["rotX"].hi = hx; }
                SetMotion(d["rotY"], j.angularYMotion);
                if (d["rotY"].motion == 'M') { float a = Mathf.Abs(j.angularYLimit.limit); d["rotY"].lo = -a; d["rotY"].hi = a; }
                SetMotion(d["rotZ"], j.angularZMotion);
                if (d["rotZ"].motion == 'M') { float a = Mathf.Abs(j.angularZLimit.limit); d["rotZ"].lo = -a; d["rotZ"].hi = a; }
            }

            // 임포트 뒤 Unity에서 조인트를 고치지 않았으면 원본 USD 사양을 그대로 쓴다
            // (Unity가 담지 못하는 비대칭 Y/Z 한계, 드라이브 원래 값까지 손실 없이)
            bool restored = info != null && !string.IsNullOrEmpty(info.usdSpec) &&
                            info.importSignature == GameReadyJointInfo.Signature(j);
            if (restored) { ParseSpec(info.usdSpec, d); ctx.rep.jointsRestored++; goto WRITE; }

            // 드라이브 (임포터 ApplyDrives의 역): 각 강성과 감쇠 /rad -> /deg
            const float radPerDeg = Mathf.PI / 180f;
            var tp = j.targetPosition;
            ReadLinearDrive(d["transX"], j.xDrive, tp.x + shiftUsd.x);
            ReadLinearDrive(d["transY"], j.yDrive, tp.y + shiftUsd.y);
            ReadLinearDrive(d["transZ"], j.zDrive, -tp.z + shiftUsd.z);
            var tr = j.targetRotation;
            if (HasDrive(j.angularXDrive))
            {
                string dof = primary.Length > 0 ? primary : "rotX";
                float twist = SignedAngle(tr.x, tr.w);
                var dd = d[dof];
                dd.drive = true; dd.k = j.angularXDrive.positionSpring * radPerDeg; dd.c = j.angularXDrive.positionDamper * radPerDeg;
                dd.t = dof == "rotZ" ? -twist : twist;
            }
            if (HasDrive(j.angularYZDrive))
            {
                foreach (var dof in new[] { "rotY", "rotZ" })
                {
                    if (dof == primary || d[dof].motion == 'L') continue;
                    var dd = d[dof];
                    dd.drive = true; dd.k = j.angularYZDrive.positionSpring * radPerDeg; dd.c = j.angularYZDrive.positionDamper * radPerDeg;
                    dd.t = dof == "rotY" ? -SignedAngle(tr.y, tr.w) : SignedAngle(tr.z, tr.w);
                }
            }

        WRITE:
            // 조인트 prim은 body1 prim의 부모 아래 (GameReady usda 관례)
            var parent = p1.parent ?? ctx.world;
            var jp = parent.Add("joint_" + go1.name, "PhysicsJoint");
            foreach (var k in k_Dofs) if (d[k].motion != 'F') jp.apis.Add("PhysicsLimitAPI:" + k);
            foreach (var k in k_Dofs) if (d[k].drive) jp.apis.Add("PhysicsDriveAPI:" + k);
            if (p0 != null) jp.attrs.Add($"rel physics:body0 = <{p0.Path}>");
            jp.attrs.Add($"rel physics:body1 = <{p1.Path}>");

            var r1 = go1.transform.rotation;
            var r0 = j.connectedBody != null ? j.connectedBody.transform.rotation : Quaternion.identity;
            var rot0U = Quaternion.Inverse(r0) * r1 * frameU;
            // 앵커 이동량은 조인트 좌표 -> body0 틀로 돌려서 뺀다 (임포터와 같은 규칙)
            Vector3 lp0 = XfVec(j.connectedAnchor) - XfQuat(rot0U) * shiftUsd;
            Vector3 lp1 = XfVec(j.anchor);
            jp.attrs.Add($"point3f physics:localPos0 = {V(lp0)}");
            jp.attrs.Add($"quatf physics:localRot0 = {Q(XfQuat(rot0U))}");
            jp.attrs.Add($"point3f physics:localPos1 = {V(lp1)}");
            jp.attrs.Add($"quatf physics:localRot1 = {Q(XfQuat(frameU))}");
            foreach (var k in k_Dofs)
            {
                var dd = d[k];
                if (dd.motion == 'M')
                {
                    jp.attrs.Add($"float limit:{k}:physics:low = {F(dd.lo)}");
                    jp.attrs.Add($"float limit:{k}:physics:high = {F(dd.hi)}");
                }
                else if (dd.motion == 'L')
                {
                    jp.attrs.Add($"float limit:{k}:physics:low = 1");
                    jp.attrs.Add($"float limit:{k}:physics:high = -1");
                }
                if (dd.drive)
                {
                    jp.attrs.Add($"uniform token drive:{k}:physics:type = \"force\"");
                    jp.attrs.Add($"float drive:{k}:physics:targetPosition = {F(dd.t)}");
                    jp.attrs.Add($"float drive:{k}:physics:stiffness = {F(dd.k)}");
                    jp.attrs.Add($"float drive:{k}:physics:damping = {F(dd.c)}");
                }
            }
            if (j.breakForce < float.PositiveInfinity) jp.attrs.Add($"float physics:breakForce = {F(j.breakForce)}");
            if (j.breakTorque < float.PositiveInfinity) jp.attrs.Add($"float physics:breakTorque = {F(j.breakTorque)}");
            ctx.rep.joints++;
        }

        static void ParseSpec(string spec, Dictionary<string, Dof> d)
        {
            foreach (var e in spec.Split(';'))
            {
                if (string.IsNullOrEmpty(e)) continue;
                var f = e.Split(':');
                if (!d.TryGetValue(f[0], out var x)) continue;
                x.motion = f[1][0]; x.drive = false;
                int i = 2;
                if (x.motion == 'M') { x.lo = float.Parse(f[2], IC); x.hi = float.Parse(f[3], IC); i = 4; }
                if (f.Length > i && f[i] == "D")
                { x.drive = true; x.k = float.Parse(f[i + 1], IC); x.c = float.Parse(f[i + 2], IC); x.t = float.Parse(f[i + 3], IC); }
            }
        }

        static void SetMotion(Dof d, ConfigurableJointMotion m) =>
            d.motion = m == ConfigurableJointMotion.Locked ? 'L' : m == ConfigurableJointMotion.Free ? 'F' : 'M';

        static void SetLinear(Dof d, ConfigurableJointMotion m, float L, float mid)
        {
            SetMotion(d, m);
            if (d.motion == 'M') { d.lo = -L + mid; d.hi = L + mid; }
        }

        static bool HasDrive(JointDrive jd) => jd.positionSpring > 0f || jd.positionDamper > 0f;

        static void ReadLinearDrive(Dof d, JointDrive jd, float target)
        {
            if (!HasDrive(jd)) return;
            d.drive = true; d.k = jd.positionSpring; d.c = jd.positionDamper; d.t = target;
        }

        // 단일 축 회전 쿼터니언 성분에서 각(도). 축 하나만 돌았을 때 정확
        static float SignedAngle(float axisComponent, float w) =>
            2f * Mathf.Atan2(axisComponent, w) * Mathf.Rad2Deg;
    }
}

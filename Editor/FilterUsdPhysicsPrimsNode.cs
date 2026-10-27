using System.Collections.Generic;
using pxr;
using UnityEngine;
using UnityEngine.Importer;

namespace USDGameReady
{
    /// <summary>
    /// UsdPhysics(표준) 필터 — GameReady 왕복(Ours) 어댑터 v0.1.
    /// 강체(physics:mass), 충돌 프림(physics:collisionEnabled: Cube/Sphere analytic),
    /// 마찰/반발 material, gameReady:layer, 조인트 드라이브(스프링), GameReady 라벨까지 읽는다.
    /// 값은 USD 원 좌표계(RH) 그대로 — 좌표 변환은 Create 노드에서 일괄 적용.
    /// </summary>
    [System.Serializable]
    [NodeMetadata("FilterUsdPhysicsPrimsNode", 0)]
    public class FilterUsdPhysicsPrimsNode : Node<FilterUsdPhysicsPrimsNode.InputPort, FilterUsdPhysicsPrimsNode.OutputPort>
    {
        public class InputPort : InputPorts
        {
            public UsdStage stage;
        }

        public class OutputPort : OutputPorts
        {
            public Dictionary<string, float> masses = new();
            public Dictionary<string, Vector3> centersOfMass = new();
            public Dictionary<string, Vector3> inertiaDiagonals = new();
            public Dictionary<string, Quaternion> principalAxes = new();
            public HashSet<string> collisionPaths = new();
            public Dictionary<string, string> colliderShapes = new();   // "Cube"|"Sphere"|"Mesh"
            public Dictionary<string, Vector3> colliderBoxSizes = new();
            public Dictionary<string, float> colliderRadii = new();
            public Dictionary<string, Vector3> materials = new();       // (staticF, dynamicF, restitution)
            public Dictionary<string, float> layers = new();
            public Dictionary<string, string> jointTypes = new();       // "Revolute"|"Prismatic"
            public Dictionary<string, string> jointBody0 = new();
            public Dictionary<string, string> jointBody1 = new();
            public Dictionary<string, Vector3> jointLocalPos0 = new();
            public Dictionary<string, Vector3> jointLocalPos1 = new();
            public Dictionary<string, string> jointAxes = new();        // "X"|"Y"|"Z"
            public Dictionary<string, Vector2> jointLimits = new();     // (lower, upper)
            public Dictionary<string, float> colliderCapsuleHeights = new();   // USD height(원통부)
            public Dictionary<string, string> colliderCapsuleAxes = new();     // "X"|"Y"|"Z"
            public Dictionary<string, Vector3> colliderCenters = new();        // 콜라이더 로컬 오프셋
            public Dictionary<string, Quaternion> colliderRotations = new();   // 콜라이더 로컬 회전
            public Dictionary<string, string> jointD6Specs = new();
            // "rotX:-30:10;rotY:...;transX:L;..." (L=locked)
            public Dictionary<string, string> jointDrives = new();
            // "rotY:k:c:t;transZ:k:c:t;" USD 원단위 그대로 (각: 강성과 감쇠는 /deg, 평형점은 deg)
            public Dictionary<string, string> labelClasses = new();   // gameReady:label:class
            public Dictionary<string, string> labelTags = new();      // gameReady:label:tags ('|' 구분)
            public Dictionary<string, string> labelMatProps = new();  // gameReady:mat:* 중 true인 것 ('|' 구분)
            public Dictionary<string, string> labelSources = new();   // gameReady:source
            // v0.4 (외부 UsdPhysics, 예: NVIDIA SimReady)
            public Dictionary<string, float> densities = new();       // 질량이 없는 강체: 물리 재질 밀도 (없으면 1000)
            public HashSet<string> kinematicBodies = new();           // physics:kinematicEnabled
            public Dictionary<string, Quaternion> jointLocalRot0 = new();   // 조인트 틀 (body 기준)
            public Dictionary<string, Quaternion> jointLocalRot1 = new();
        }

        static readonly string[] k_Dofs = { "transX", "transY", "transZ", "rotX", "rotY", "rotZ" };

        // ---- 외부 UsdPhysics 정규화 ----
        // Z-up 스테이지는 Y-up USD 관례로 먼저 돌린다: R = X축 -90도, (x,y,z) -> (x,z,-y).
        // 그 뒤 Create 노드의 B=diag(1,1,-1)을 거치면 합성이 기본 임포터의 Y/Z 맞바꿈과 같다.
        // R은 진짜 회전이라 실측으로 확정한 조인트 부호 규칙이 그대로 유지된다.
        bool m_Zup;
        static readonly Quaternion k_R = Quaternion.AngleAxis(-90f, Vector3.right);
        Vector3 V(Vector3 v) => m_Zup ? new Vector3(v.x, v.z, -v.y) : v;
        Quaternion Conj(Quaternion q) => m_Zup ? k_R * q * Quaternion.Inverse(k_R) : q;   // 틀 안의 회전
        Quaternion Frame(Quaternion q) => m_Zup ? k_R * q : q;                             // 조인트 틀 (축 토큰 유지)
        Vector3 Size(Vector3 s) => m_Zup ? new Vector3(s.x, s.z, s.y) : s;
        string CapAxis(string a) => !m_Zup ? a : a == "Y" ? "Z" : a == "Z" ? "Y" : a;

        static readonly TfToken k_RigidBodyAPI = new TfToken("PhysicsRigidBodyAPI");
        static readonly TfToken k_CollisionAPI = new TfToken("PhysicsCollisionAPI");

        // prim 국소 행렬 (열 벡터 관례, USD 좌표 그대로).
        // 변환 op가 없는 prim은 GetFusedTransform이 빈 값(0 행렬)을 돌려주므로 항등으로 본다
        static Matrix4x4 Local(UsdPrim p)
        {
            var g = Vt.VtValueToGfMatrix4d(UsdCs.GetFusedTransform(p, UsdTimeCode.EarliestTime().GetValue()));
            var a = new double[16];
            g.CopyToArray(a);                       // USD는 행 벡터 관례: a[r*4+c]
            if (a[15] == 0.0) return Matrix4x4.identity;
            var m = new Matrix4x4();
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[c, r] = (float)a[r * 4 + c];
            return m;
        }

        // from prim 좌표 -> 조상 to 좌표
        static Matrix4x4 RelTo(UsdPrim from, string to)
        {
            var m = Matrix4x4.identity;
            var p = from;
            while (p.IsValid() && p.GetPath().ToString() != to && p.GetPath().ToString() != "/")
            {
                m = Local(p) * m;
                p = p.GetParent();
            }
            return m;
        }

        static Quaternion Rot(Matrix4x4 m)
        {
            // 축척을 걷어낸 회전 (열 정규화)
            var c1 = ((Vector3)m.GetColumn(1)).normalized;
            var c2 = ((Vector3)m.GetColumn(2)).normalized;
            return Quaternion.LookRotation(c2, c1);
        }

        static Vector3 ReadVec(UsdPrim p, string name, out bool ok)
        {
            var a = p.GetAttribute(new TfToken(name));
            ok = a.IsValid() && a.HasAuthoredValue();
            if (!ok) return Vector3.zero;
            var v = Vt.VtValueToGfVec3f(a.Get(UsdTimeCode.EarliestTime()));
            return new Vector3(v[0], v[1], v[2]);
        }

        static Quaternion ReadQuat(UsdPrim p, string name, out bool ok)
        {
            var a = p.GetAttribute(new TfToken(name));
            ok = a.IsValid() && a.HasAuthoredValue();
            if (!ok) return Quaternion.identity;
            var q = Vt.VtValueToGfQuatf(a.Get(UsdTimeCode.EarliestTime()));
            var im = q.GetImaginary();
            return new Quaternion(im[0], im[1], im[2], q.GetReal());
        }

        static bool ReadFloat(UsdPrim p, string name, out float v)
        {
            v = 0f;
            var a = p.GetAttribute(new TfToken(name));
            if (!(a.IsValid() && a.HasAuthoredValue())) return false;
            v = Vt.VtValueTofloat(a.Get(UsdTimeCode.EarliestTime()));
            return true;
        }

        // material:binding:physics 를 prim부터 조상으로 따라가 물리 재질 prim을 찾는다
        static UsdPrim BoundPhysMaterial(UsdStage stage, UsdPrim prim)
        {
            var tok = new TfToken("material:binding:physics");
            var p = prim;
            while (p.IsValid() && p.GetPath().ToString() != "/")
            {
                var r = p.GetRelationship(tok);
                if (r.IsValid())
                {
                    var t = r.GetTargets();
                    if (t != null && t.Count > 0)
                    {
                        var m = stage.GetPrimAtPath(t[0]);
                        if (m.IsValid()) return m;
                    }
                }
                p = p.GetParent();
            }
            return null;
        }

        // 가장 가까운 강체 조상 (자기 포함). 없으면 null
        static string BodyOf(UsdPrim prim, HashSet<string> bodies)
        {
            var p = prim;
            while (p.IsValid() && p.GetPath().ToString() != "/")
            {
                var s = p.GetPath().ToString();
                if (bodies.Contains(s)) return s;
                p = p.GetParent();
            }
            return null;
        }

        class MassPart
        {
            public float m;
            public Vector3 com; public bool hasCom;          // body 좌표
            public Vector3 diag; public Quaternion axes; public bool hasInertia;
        }

        // 조각 질량을 강체 하나로 합친다: 질량 합, 질량 가중 COM, 평행축 정리 + 대각화
        static void Combine(List<MassPart> parts, out float mass, out Vector3 com, out bool hasCom,
                            out Vector3 diag, out Quaternion axes, out bool hasInertia)
        {
            mass = 0f; com = Vector3.zero; hasCom = true; hasInertia = true;
            diag = Vector3.zero; axes = Quaternion.identity;
            foreach (var p in parts) { mass += p.m; hasCom &= p.hasCom; hasInertia &= p.hasInertia; }
            if (mass <= 0f) { hasCom = hasInertia = false; return; }
            if (parts.Count == 1)
            {
                com = parts[0].com; diag = parts[0].diag; axes = parts[0].axes;
                return;
            }
            if (!hasCom) { hasInertia = false; return; }
            foreach (var p in parts) com += p.m * p.com;
            com /= mass;
            if (!hasInertia) return;
            var I = new double[3, 3];
            foreach (var p in parts)
            {
                var R = Matrix4x4.Rotate(p.axes);
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        I[i, j] += R[i, 0] * p.diag.x * R[j, 0] + R[i, 1] * p.diag.y * R[j, 1] + R[i, 2] * p.diag.z * R[j, 2];
                var d = p.com - com;
                double dd = d.sqrMagnitude;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        I[i, j] += p.m * ((i == j ? dd : 0.0) - d[i] * d[j]);
            }
            Jacobi(I, out var ev, out var V);
            diag = new Vector3((float)ev[0], (float)ev[1], (float)ev[2]);
            var c0 = new Vector3((float)V[0, 0], (float)V[1, 0], (float)V[2, 0]);
            var c1 = new Vector3((float)V[0, 1], (float)V[1, 1], (float)V[2, 1]);
            var c2 = Vector3.Cross(c0, c1);                  // 오른손 틀 (det +1)
            axes = Quaternion.LookRotation(c2, c1);
        }

        // 3x3 대칭 행렬 Jacobi 고유분해. V의 열이 고유벡터
        static void Jacobi(double[,] A, out double[] ev, out double[,] V)
        {
            var a = (double[,])A.Clone();
            V = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-30) break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (System.Math.Abs(a[p, q]) < 1e-300) continue;
                        double th = 0.5 * System.Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
                        double c = System.Math.Cos(th), s = System.Math.Sin(th);
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = V[k, p], vkq = V[k, q];
                            V[k, p] = c * vkp - s * vkq; V[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            ev = new[] { a[0, 0], a[1, 1], a[2, 2] };
        }

        // PhysicsDriveAPI:<instance> 한 개를 "dof:k:c:t;" 로 읽는다 (없으면 빈 문자열)
        static string ReadDrive(UsdPrim prim, string instance, string dof)
        {
            var kA = prim.GetAttribute(new TfToken($"drive:{instance}:physics:stiffness"));
            var cA = prim.GetAttribute(new TfToken($"drive:{instance}:physics:damping"));
            if (!(kA.IsValid() && kA.HasAuthoredValue()) && !(cA.IsValid() && cA.HasAuthoredValue()))
                return "";
            float k = 0, c = 0, t = 0;
            if (kA.IsValid() && kA.HasAuthoredValue()) k = Vt.VtValueTofloat(kA.Get(UsdTimeCode.EarliestTime()));
            if (cA.IsValid() && cA.HasAuthoredValue()) c = Vt.VtValueTofloat(cA.Get(UsdTimeCode.EarliestTime()));
            var tA = prim.GetAttribute(new TfToken($"drive:{instance}:physics:targetPosition"));
            if (tA.IsValid() && tA.HasAuthoredValue()) t = Vt.VtValueTofloat(tA.Get(UsdTimeCode.EarliestTime()));
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return $"{dof}:{k.ToString(ic)}:{c.ToString(ic)}:{t.ToString(ic)};";
        }

        static void ReadLabels(UsdPrim prim, string path, OutputPort o)
        {
            var clsA = prim.GetAttribute(new TfToken("gameReady:label:class"));
            if (!(clsA.IsValid() && clsA.HasAuthoredValue())) return;
            o.labelClasses[path] = Vt.VtValueToTfToken(clsA.Get(UsdTimeCode.EarliestTime())).GetString();
            var tagA = prim.GetAttribute(new TfToken("gameReady:label:tags"));
            if (tagA.IsValid() && tagA.HasAuthoredValue())
            {
                var va = Vt.VtValueToVtStringArray(tagA.Get(UsdTimeCode.EarliestTime()));
                var arr = new List<string>();
                for (int i = 0; i < (int)va.size(); i++) arr.Add(va[i]);
                o.labelTags[path] = string.Join("|", arr);
            }
            var srcA = prim.GetAttribute(new TfToken("gameReady:source"));
            if (srcA.IsValid() && srcA.HasAuthoredValue())
                o.labelSources[path] = Vt.VtValueToTfToken(srcA.Get(UsdTimeCode.EarliestTime())).GetString();
            var props = new List<string>();
            foreach (var flag in new[] { "flammable", "conductive", "magnetic" })
            {
                var a = prim.GetAttribute(new TfToken($"gameReady:mat:{flag}"));
                if (a.IsValid() && a.HasAuthoredValue() && Vt.VtValueTobool(a.Get(UsdTimeCode.EarliestTime())))
                    props.Add(flag);
            }
            var bA = prim.GetAttribute(new TfToken("gameReady:mat:buoyancy"));   // 부력은 float
            if (bA.IsValid() && bA.HasAuthoredValue() && Vt.VtValueTofloat(bA.Get(UsdTimeCode.EarliestTime())) > 0f)
                props.Add("buoyant");
            if (props.Count > 0) o.labelMatProps[path] = string.Join("|", props);
        }

        public override void Run()
        {
            var massToken      = new TfToken("physics:mass");
            var collisionToken = new TfToken("physics:collisionEnabled");
            var sizeToken      = new TfToken("size");
            var radiusToken    = new TfToken("radius");
            var scaleToken     = new TfToken("xformOp:scale");
            var layerToken     = new TfToken("gameReady:layer");
            var axisToken      = new TfToken("physics:axis");
            var heightToken    = new TfToken("height");
            var capAxisToken   = new TfToken("axis");
            var stage = Input.stage;

            var up = new VtValue();
            m_Zup = stage.GetMetadata(UsdGeomTokens.upAxis, up) && !Vt.VtValueToTfToken(up).Equals(UsdGeomTokens.y);
            var mpuV = new VtValue();
            if (stage.GetMetadata(UsdGeomTokens.metersPerUnit, mpuV) &&
                System.Math.Abs(VtValue.CastToTypeOf(mpuV, new VtValue(0.0f)) - 1f) > 1e-6f)
                Debug.LogWarning("[USDGameReady] metersPerUnit != 1: 물리 길이 값은 축척하지 않습니다");

            // 1) 강체 목록 (PhysicsRigidBodyAPI). 질량이 강체가 아니라 그 아래 충돌 prim에 적힌 파일을 위해 먼저 모은다
            var bodies = new HashSet<string>();
            foreach (var prim in stage.Traverse(Usd.UsdTraverseInstanceProxies()))
                if (prim.HasAPI(k_RigidBodyAPI)) bodies.Add(prim.GetPath().ToString());
            var parts = new Dictionary<string, List<MassPart>>();

            foreach (var prim in stage.Traverse(Usd.UsdTraverseInstanceProxies()))
            {
                var path = prim.GetPath().ToString();
                ReadLabels(prim, path, Output);

                var massAttr = prim.GetAttribute(massToken);
                if (massAttr.IsValid() && massAttr.HasAuthoredValue())
                {
                    float m = Vt.VtValueTofloat(massAttr.Get(UsdTimeCode.EarliestTime()));
                    var com = ReadVec(prim, "physics:centerOfMass", out bool hasCom);
                    var diag = ReadVec(prim, "physics:diagonalInertia", out bool hasIn);
                    var ax = ReadQuat(prim, "physics:principalAxes", out bool hasAx);
                    string b = BodyOf(prim, bodies);
                    if (b == null || b == path)
                    {
                        // 강체 자신의 질량 (GameReady 관례, 강체 API 없는 구 파일 포함)
                        Output.masses[path] = m;
                        if (hasCom) Output.centersOfMass[path] = V(com);
                        if (hasIn)
                        {
                            Output.inertiaDiagonals[path] = Size(diag);
                            if (hasAx || m_Zup) Output.principalAxes[path] = Conj(ax);
                        }
                    }
                    else
                    {
                        // 충돌 prim의 질량 -> 강체 좌표로 옮겨 모은다
                        var M = RelTo(prim, b);
                        if (!parts.TryGetValue(b, out var list)) parts[b] = list = new List<MassPart>();
                        list.Add(new MassPart
                        {
                            m = m, com = M.MultiplyPoint3x4(com), hasCom = hasCom,
                            diag = diag, axes = Rot(M) * ax, hasInertia = hasIn
                        });
                    }
                }

                // 충돌: collisionEnabled=true 이거나, CollisionAPI가 붙고 끄지 않은 prim
                var colAttr = prim.GetAttribute(collisionToken);
                bool colAuthored = colAttr.IsValid() && colAttr.HasAuthoredValue();
                bool colOn = colAuthored ? Vt.VtValueTobool(colAttr.Get(UsdTimeCode.EarliestTime()))
                                         : prim.HasAPI(k_CollisionAPI);
                if (colOn)
                {
                    Output.collisionPaths.Add(path);
                    var typeName = prim.GetTypeName().ToString();
                    Output.colliderShapes[path] = typeName;
                    // 메시 콜라이더 근사: approximation = "none" 이면 삼각형 메시 그대로 (비볼록)
                    // sdf, convexHull, convexDecomposition 등은 Unity에서 볼록 메시 (움직이는 강체 제약)
                    if (typeName == "Mesh")
                    {
                        var apA = prim.GetAttribute(new TfToken("physics:approximation"));
                        if (apA.IsValid() && apA.HasAuthoredValue() &&
                            Vt.VtValueToTfToken(apA.Get(UsdTimeCode.EarliestTime())).GetString() == "none")
                            Output.colliderShapes[path] = "Mesh:none";
                    }

                    // 콜라이더 로컬 오프셋/회전 — 프리미티브 프림은 base 그래프가
                    // GO를 안 만들어 폴백 부착되므로, 여기서 읽어 Create가 복원
                    var c0 = ReadVec(prim, "xformOp:translate", out bool hasT);
                    if (hasT) Output.colliderCenters[path] = V(c0);
                    var r0 = ReadQuat(prim, "xformOp:orient", out bool hasR);
                    if (hasR) Output.colliderRotations[path] = Conj(r0);

                    if (typeName == "Cube")
                    {
                        float size = 1f;
                        var sAttr = prim.GetAttribute(sizeToken);
                        if (sAttr.IsValid() && sAttr.HasAuthoredValue())
                            size = (float)Vt.VtValueTodouble(sAttr.Get(UsdTimeCode.EarliestTime()));
                        var scale = Vector3.one;
                        var scAttr = prim.GetAttribute(scaleToken);
                        if (scAttr.IsValid() && scAttr.HasAuthoredValue())
                        {
                            var v = Vt.VtValueToGfVec3f(scAttr.Get(UsdTimeCode.EarliestTime()));
                            scale = new Vector3(v[0], v[1], v[2]);
                        }
                        Output.colliderBoxSizes[path] = Size(scale * size);
                    }
                    else if (typeName == "Sphere")
                    {
                        var rAttr = prim.GetAttribute(radiusToken);
                        if (rAttr.IsValid() && rAttr.HasAuthoredValue())
                            Output.colliderRadii[path] =
                                (float)Vt.VtValueTodouble(rAttr.Get(UsdTimeCode.EarliestTime()));
                    }
                    else if (typeName == "Capsule")
                    {
                        var rAttr = prim.GetAttribute(radiusToken);
                        if (rAttr.IsValid() && rAttr.HasAuthoredValue())
                            Output.colliderRadii[path] =
                                (float)Vt.VtValueTodouble(rAttr.Get(UsdTimeCode.EarliestTime()));
                        var hAttr = prim.GetAttribute(heightToken);
                        if (hAttr.IsValid() && hAttr.HasAuthoredValue())
                            Output.colliderCapsuleHeights[path] =
                                (float)Vt.VtValueTodouble(hAttr.Get(UsdTimeCode.EarliestTime()));
                        var aAttr = prim.GetAttribute(capAxisToken);
                        if (aAttr.IsValid() && aAttr.HasAuthoredValue())
                            Output.colliderCapsuleAxes[path] =
                                CapAxis(Vt.VtValueToTfToken(aAttr.Get(UsdTimeCode.EarliestTime())).GetString());
                    }

                    // 마찰/반발: prim에 직접 적힌 값, 없으면 material:binding:physics 재질
                    float fs = 0.5f, fd = 0.5f, rest = 0f;
                    bool own = ReadFloat(prim, "physics:staticFriction", out var v1) |
                               ReadFloat(prim, "physics:dynamicFriction", out var v2) |
                               ReadFloat(prim, "physics:restitution", out var v3);
                    var src = own ? prim : BoundPhysMaterial(stage, prim);
                    if (src != null)
                    {
                        if (ReadFloat(src, "physics:staticFriction", out v1)) fs = v1;
                        if (ReadFloat(src, "physics:dynamicFriction", out v2)) fd = v2;
                        if (ReadFloat(src, "physics:restitution", out v3)) rest = v3;
                    }
                    Output.materials[path] = new Vector3(fs, fd, rest);

                    var lAttr = prim.GetAttribute(layerToken);
                    if (lAttr.IsValid() && lAttr.HasAuthoredValue())
                        Output.layers[path] = Vt.VtValueTofloat(lAttr.Get(UsdTimeCode.EarliestTime()));
                }

                var tn = prim.GetTypeName().ToString();
                if (tn == "PhysicsJoint")
                {
                    // D6: PhysicsLimitAPI 인스턴스별 limit:{dof}:physics:low/high
                    Output.jointTypes[path] = "D6";
                    var spec = new System.Text.StringBuilder();
                    foreach (var dof in k_Dofs)
                    {
                        if (ReadFloat(prim, $"limit:{dof}:physics:low", out float lo) &&
                            ReadFloat(prim, $"limit:{dof}:physics:high", out float hi))
                        {
                            if (lo > hi) spec.Append($"{dof}:L;");                 // locked
                            else if (float.IsInfinity(lo) || float.IsInfinity(hi)) spec.Append($"{dof}:F;");
                            else spec.Append($"{dof}:{lo.ToString(System.Globalization.CultureInfo.InvariantCulture)}:" +
                                             $"{hi.ToString(System.Globalization.CultureInfo.InvariantCulture)};");
                        }
                        else spec.Append($"{dof}:F;");                             // free
                    }
                    Output.jointD6Specs[path] = spec.ToString();
                    var drv = new System.Text.StringBuilder();
                    foreach (var dof in k_Dofs)
                        drv.Append(ReadDrive(prim, dof, dof));
                    if (drv.Length > 0) Output.jointDrives[path] = drv.ToString();
                    ReadJointFrames(stage, prim, path, bodies);
                }
                else if (tn == "PhysicsRevoluteJoint" || tn == "PhysicsPrismaticJoint")
                {
                    Output.jointTypes[path] = tn == "PhysicsRevoluteJoint" ? "Revolute" : "Prismatic";
                    ReadJointFrames(stage, prim, path, bodies);
                    var ax = prim.GetAttribute(axisToken);
                    if (ax.IsValid() && ax.HasAuthoredValue())
                        Output.jointAxes[path] =
                            Vt.VtValueToTfToken(ax.Get(UsdTimeCode.EarliestTime())).GetString();

                    // 타입 조인트 드라이브: 인스턴스 이름이 angular/linear
                    string axTag = Output.jointAxes.TryGetValue(path, out var axv) ? axv : "X";
                    bool rev = tn == "PhysicsRevoluteJoint";
                    var tdrv = ReadDrive(prim, rev ? "angular" : "linear", (rev ? "rot" : "trans") + axTag);
                    if (tdrv.Length > 0) Output.jointDrives[path] = tdrv;

                    // 한계가 없거나 무한대면 자유 (Create가 NaN을 자유로 읽는다)
                    float lo = float.NaN, hi = float.NaN;
                    if (ReadFloat(prim, "physics:lowerLimit", out var l) && !float.IsInfinity(l)) lo = l;
                    if (ReadFloat(prim, "physics:upperLimit", out var h) && !float.IsInfinity(h)) hi = h;
                    Output.jointLimits[path] = new Vector2(lo, hi);
                }
                else if (tn == "PhysicsFixedJoint")
                {
                    Output.jointTypes[path] = "Fixed";
                    ReadJointFrames(stage, prim, path, bodies);
                }
            }

            // 2) 질량이 충돌 prim에 나뉘어 적힌 강체: 합친다. 아무 데도 없으면 재질 밀도로
            int nCombined = 0, nDensity = 0;
            foreach (var b in bodies)
            {
                var bp = stage.GetPrimAtPath(new SdfPath(b));
                var kin = bp.GetAttribute(new TfToken("physics:kinematicEnabled"));
                if (kin.IsValid() && kin.HasAuthoredValue() && Vt.VtValueTobool(kin.Get(UsdTimeCode.EarliestTime())))
                    Output.kinematicBodies.Add(b);
                if (Output.masses.ContainsKey(b)) continue;
                if (parts.TryGetValue(b, out var list))
                {
                    Combine(list, out float m, out var com, out bool hasCom, out var diag, out var ax, out bool hasIn);
                    if (m > 0f)
                    {
                        Output.masses[b] = m;
                        if (hasCom) Output.centersOfMass[b] = V(com);
                        if (hasIn) { Output.inertiaDiagonals[b] = Size(diag); Output.principalAxes[b] = Conj(ax); }
                        nCombined++;
                        continue;
                    }
                }
                float dens = 1000f;                                   // UsdPhysics 기본 밀도
                var mat = BoundPhysMaterial(stage, bp);
                if (mat != null && ReadFloat(mat, "physics:density", out var d) && d > 0f) dens = d;
                Output.densities[b] = dens;
                nDensity++;
            }

            Debug.Log($"[USDGameReady] UsdPhysics filter: bodies={Output.masses.Count + Output.densities.Count} " +
                      $"(partsCombined={nCombined}, density={nDensity}, kinematic={Output.kinematicBodies.Count}), " +
                      $"colliders={Output.collisionPaths.Count}, joints={Output.jointTypes.Count}, " +
                      $"drives={Output.jointDrives.Count}, labels={Output.labelClasses.Count}, zUp={m_Zup}");
        }

        // 조인트 body0/body1과 틀. rel 타깃이 강체가 아니라 그 아래 prim(예: 충돌 메시)이면
        // 가장 가까운 강체로 옮기고 localPos/localRot도 그 강체 좌표로 바꾼다 (UsdPhysics 규약)
        void ReadJointFrames(UsdStage stage, UsdPrim prim, string path, HashSet<string> bodies)
        {
            for (int k = 0; k < 2; k++)
            {
                var rel = prim.GetRelationship(new TfToken(k == 0 ? "physics:body0" : "physics:body1"));
                if (!rel.IsValid()) continue;
                var tt = rel.GetTargets();
                if (tt == null || tt.Count == 0) continue;
                string target = CreateRigidbodyNode.ResolvePath(path, tt[0].ToString());
                var lp = ReadVec(prim, $"physics:localPos{k}", out _);
                var lr = ReadQuat(prim, $"physics:localRot{k}", out bool hasRot);
                var tp = stage.GetPrimAtPath(new SdfPath(target));
                string body = tp.IsValid() ? BodyOf(tp, bodies) : null;
                bool moved = body != null && body != target;
                if (moved)
                {
                    var M = RelTo(tp, body);
                    lp = M.MultiplyPoint3x4(lp);
                    lr = Rot(M) * lr;
                    target = body;
                }
                (k == 0 ? Output.jointBody0 : Output.jointBody1)[path] = target;
                (k == 0 ? Output.jointLocalPos0 : Output.jointLocalPos1)[path] = V(lp);
                if (m_Zup || moved || (hasRot && Quaternion.Angle(lr, Quaternion.identity) > 1e-4f))
                    (k == 0 ? Output.jointLocalRot0 : Output.jointLocalRot1)[path] = Frame(lr);
            }
        }
    }
}

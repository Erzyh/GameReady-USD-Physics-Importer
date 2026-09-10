using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Importer;

namespace USDGameReady
{
    /// <summary>
    /// UsdPhysics → Unity 컴포넌트 부착 (Ours 어댑터 v0.1).
    /// v0.1 변경: (1) 좌표 변환 B=diag(1,1,-1)을 물리 벡터/쿼터니언에 일괄 적용
    /// (v0의 '부분 적용' 버그 수정), (2) Cube/Sphere analytic prim → Box/SphereCollider,
    /// (3) PhysicsMaterial(마찰/반발) 생성, (4) gameReady:layer → GameObject.layer.
    /// </summary>
    [System.Serializable]
    [NodeMetadata("CreateRigidbodyNode", 0)]
    public class CreateRigidbodyNode : Node<CreateRigidbodyNode.InputPort, CreateRigidbodyNode.OutputPort>
    {
        public class InputPort : InputPorts
        {
            public Dictionary<string, GameObject> gameObjects;
            public Dictionary<string, float> masses;
            public Dictionary<string, Vector3> centersOfMass;
            public Dictionary<string, Vector3> inertiaDiagonals;
            public Dictionary<string, Quaternion> principalAxes;
            public HashSet<string> collisionPaths;
            public Dictionary<string, Vector3> colliderCenters;
            public Dictionary<string, Quaternion> colliderRotations;
            public Dictionary<string, string> colliderShapes;
            public Dictionary<string, Vector3> colliderBoxSizes;
            public Dictionary<string, float> colliderRadii;
            public Dictionary<string, Vector3> materials;
            public Dictionary<string, float> layers;
            public Dictionary<string, string> jointTypes;
            public Dictionary<string, string> jointBody0;
            public Dictionary<string, string> jointBody1;
            public Dictionary<string, Vector3> jointLocalPos0;
            public Dictionary<string, Vector3> jointLocalPos1;
            public Dictionary<string, string> jointAxes;
            public Dictionary<string, Vector2> jointLimits;
            public Dictionary<string, float> colliderCapsuleHeights;
            public Dictionary<string, string> colliderCapsuleAxes;
            public Dictionary<string, string> jointD6Specs;
            public bool enabled = true;
        }

        public class OutputPort : OutputPorts
        {
            public Dictionary<string, GameObject> gameObjects;
        }

        // rel 타깃이 상대경로("../frame")로 올 수 있어 조인트 prim 기준으로 절대화
        static string ResolvePath(string jointPath, string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (target.StartsWith("/")) return target;
            var stack = new List<string>(jointPath.Split('/'));
            stack.RemoveAll(string.IsNullOrEmpty);
            foreach (var seg in target.Split('/'))
            {
                if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
                else if (seg != "." && seg.Length > 0) stack.Add(seg);
            }
            return "/" + string.Join("/", stack);
        }

        // USD(RH, Y-up) -> Unity(LH, Y-up): B = diag(1,1,-1)
        static Vector3 XfVec(Vector3 v) => new Vector3(v.x, v.y, -v.z);
        // 회전 켤레 B R B^T: (w,x,y,z) -> (w,-x,-y,z)
        static Quaternion XfQuat(Quaternion q) => new Quaternion(-q.x, -q.y, q.z, q.w);

        public override void Run()
        {
            Output.gameObjects = Input.gameObjects;
            if (!Input.enabled || Input.gameObjects == null)
                return;

            int nb = 0, nc = 0;

            if (Input.collisionPaths != null)
            {
                foreach (var path in Input.collisionPaths)
                {
                    // 충돌 prim에 GO가 없으면(프리미티브 프림은 base 그래프가
                    // GO 미생성) 자식 GO를 만들어 로컬 오프셋/회전을 복원한다
                    // (body GO 직접 부착은 오프셋 소실 — N=200 Unity 실측이 검출)
                    if (!Input.gameObjects.TryGetValue(path, out var go))
                    {
                        var cut = path.LastIndexOf('/');
                        if (cut <= 0 || !Input.gameObjects.TryGetValue(
                                path.Substring(0, cut), out var bodyGo))
                            continue;
                        go = new GameObject(path.Substring(cut + 1));
                        go.transform.SetParent(bodyGo.transform, false);
                        Vector3 c = Vector3.zero;
                        if (Input.colliderCenters != null)
                            Input.colliderCenters.TryGetValue(path, out c);
                        Quaternion q = Quaternion.identity;
                        if (Input.colliderRotations != null &&
                            Input.colliderRotations.TryGetValue(path, out var q0))
                            q = q0;
                        go.transform.localPosition = XfVec(c);
                        go.transform.localRotation = XfQuat(q);
                    }
                    if (go.GetComponent<Collider>() != null) continue;

                    string shape = null;
                    if (Input.colliderShapes != null) Input.colliderShapes.TryGetValue(path, out shape);

                    Collider col = null;
                    if (shape == "Cube")
                    {
                        var bc = go.AddComponent<BoxCollider>();
                        if (Input.colliderBoxSizes != null &&
                            Input.colliderBoxSizes.TryGetValue(path, out var size))
                            bc.size = size;                    // 크기 = 축별 절대값이라 B 불변
                        col = bc;
                    }
                    else if (shape == "Sphere")
                    {
                        var sc = go.AddComponent<SphereCollider>();
                        if (Input.colliderRadii != null &&
                            Input.colliderRadii.TryGetValue(path, out var r))
                            sc.radius = r;
                        col = sc;
                    }
                    else if (shape == "Capsule")
                    {
                        var cc = go.AddComponent<CapsuleCollider>();
                        float r2 = 0.25f, h2 = 0.5f;
                        if (Input.colliderRadii != null) Input.colliderRadii.TryGetValue(path, out r2);
                        if (Input.colliderCapsuleHeights != null) Input.colliderCapsuleHeights.TryGetValue(path, out h2);
                        cc.radius = r2;
                        cc.height = h2 + 2f * r2;      // USD height=원통부, Unity height=캡 포함 전체
                        string cax = "Y";
                        if (Input.colliderCapsuleAxes != null) Input.colliderCapsuleAxes.TryGetValue(path, out cax);
                        cc.direction = cax == "X" ? 0 : (cax == "Z" ? 2 : 1);   // 축선은 z-flip 불변
                        col = cc;
                    }
                    else
                    {
                        var mf = go.GetComponentInChildren<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            var mc = go.AddComponent<MeshCollider>();
                            mc.sharedMesh = mf.sharedMesh;
                            mc.convex = true;
                            col = mc;
                        }
                    }
                    if (col == null) continue;
                    nc++;

                    // 시각 레이어(형제 "render" prim)가 있으면 콜라이더 렌더러 숨김
                    // (usda의 purpose="guide" 의도 — 임포터가 purpose를 무시해서 수동 처리)
                    // 계층은 BuildHierarchyNode가 나중에 만들므로 Transform.Find 대신 경로 사전으로 판정
                    var cutR = path.LastIndexOf('/');
                    bool hasRender = cutR > 0 && Input.gameObjects.ContainsKey(path.Substring(0, cutR) + "/render");
                    if (hasRender)
                        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                            r.enabled = false;

                    if (Input.materials != null && Input.materials.TryGetValue(path, out var m))
                    {
                        // PhysicsMaterial 은 프리팹 서브에셋 미등록으로 소실됨 →
                        // 직렬화 컴포넌트로 보존, 런타임 Awake 에서 적용
                        var info = col.gameObject.GetComponent<UsdPhysMatInfo>();
                        if (info == null) info = col.gameObject.AddComponent<UsdPhysMatInfo>();
                        info.staticFriction = m.x;
                        info.dynamicFriction = m.y;
                        info.bounciness = m.z;
                    }
                    if (Input.layers != null && Input.layers.TryGetValue(path, out var layer))
                        go.layer = Mathf.Clamp(Mathf.RoundToInt(layer), 0, 31);
                }
            }

            if (Input.masses != null)
            {
                foreach (var kvp in Input.masses)
                {
                    if (!Input.gameObjects.TryGetValue(kvp.Key, out var go)) continue;
                    var rb = go.GetComponent<Rigidbody>();
                    if (rb == null) rb = go.AddComponent<Rigidbody>();
                    rb.mass = kvp.Value;

                    if (Input.centersOfMass != null &&
                        Input.centersOfMass.TryGetValue(kvp.Key, out var com))
                    {
                        rb.automaticCenterOfMass = false;
                        rb.centerOfMass = XfVec(com);          // ← 좌표 변환 적용
                    }
                    if (Input.inertiaDiagonals != null &&
                        Input.inertiaDiagonals.TryGetValue(kvp.Key, out var inertia))
                    {
                        rb.automaticInertiaTensor = false;
                        rb.inertiaTensor = inertia;            // 대각값은 축 반전 불변
                        if (Input.principalAxes != null &&
                            Input.principalAxes.TryGetValue(kvp.Key, out var rot))
                            rb.inertiaTensorRotation = XfQuat(rot);   // ← 켤레 변환 적용
                    }
                    nb++;
                }
            }

            // ---- joints: USD typed joint -> ConfigurableJoint ----
            int nj = 0;
            if (Input.jointTypes != null)
            {
                foreach (var kvp in Input.jointTypes)
                {
                    var path = kvp.Key;
                    if (Input.jointBody0 == null || Input.jointBody1 == null) break;
                    if (!Input.jointBody0.TryGetValue(path, out var b0Path)) continue;
                    if (!Input.jointBody1.TryGetValue(path, out var b1Path)) continue;
                    b0Path = ResolvePath(path, b0Path);
                    b1Path = ResolvePath(path, b1Path);
                    if (!Input.gameObjects.TryGetValue(b0Path, out var go0))
                    { Debug.LogWarning($"[USDGameReady] joint {path}: body0 GO not found: {b0Path}"); continue; }
                    if (!Input.gameObjects.TryGetValue(b1Path, out var go1))
                    { Debug.LogWarning($"[USDGameReady] joint {path}: body1 GO not found: {b1Path}"); continue; }

                    var rb0 = go0.GetComponent<Rigidbody>();
                    if (rb0 == null) { rb0 = go0.AddComponent<Rigidbody>(); rb0.isKinematic = true; }

                    var j = go1.AddComponent<ConfigurableJoint>();
                    j.connectedBody = rb0;
                    j.autoConfigureConnectedAnchor = false;
                    Vector3 lp0 = Vector3.zero, lp1 = Vector3.zero;
                    if (Input.jointLocalPos0 != null) Input.jointLocalPos0.TryGetValue(path, out lp0);
                    if (Input.jointLocalPos1 != null) Input.jointLocalPos1.TryGetValue(path, out lp1);
                    j.anchor = XfVec(lp1);
                    j.connectedAnchor = XfVec(lp0);

                    j.xMotion = ConfigurableJointMotion.Locked;
                    j.yMotion = ConfigurableJointMotion.Locked;
                    j.zMotion = ConfigurableJointMotion.Locked;
                    j.angularXMotion = ConfigurableJointMotion.Locked;
                    j.angularYMotion = ConfigurableJointMotion.Locked;
                    j.angularZMotion = ConfigurableJointMotion.Locked;

                    string axis = "X";
                    if (Input.jointAxes != null) Input.jointAxes.TryGetValue(path, out axis);
                    Vector2 lim = Vector2.zero;
                    if (Input.jointLimits != null) Input.jointLimits.TryGetValue(path, out lim);
                    float lo = lim.x, hi = lim.y;

                    // 좌표 변환 B=diag(1,1,-1), det=-1 의 per-axis 부호 규칙:
                    //  선형: z축 flip / 각: x,y축 flip (pseudovector), z축 불변
                    if (kvp.Value == "D6")
                    {
                        string d6 = null;
                        if (Input.jointD6Specs != null) Input.jointD6Specs.TryGetValue(path, out d6);
                        if (!string.IsNullOrEmpty(d6))
                        {
                            foreach (var entry in d6.Split(';'))
                            {
                                if (string.IsNullOrEmpty(entry)) continue;
                                var f = entry.Split(':');
                                var dof = f[0];
                                if (f[1] == "L") continue;                 // 이미 Locked 초기화
                                if (f[1] == "F")                            // Free
                                {
                                    SetMotion(j, dof, ConfigurableJointMotion.Free);
                                    continue;
                                }
                                float dlo = float.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture);
                                float dhi = float.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
                                ApplyLimit(j, dof, dlo, dhi);
                            }
                        }
                    }
                    else if (kvp.Value == "Revolute")
                    {
                        float l2 = lo, h2 = hi;
                        if (axis == "X" || axis == "Y") { l2 = -hi; h2 = -lo; }
                        if (axis == "X")
                        {
                            j.angularXMotion = ConfigurableJointMotion.Limited;
                            var sl = j.lowAngularXLimit;  sl.limit = l2; j.lowAngularXLimit = sl;
                            var sh = j.highAngularXLimit; sh.limit = h2; j.highAngularXLimit = sh;
                        }
                        else if (axis == "Y")
                        {
                            j.angularYMotion = ConfigurableJointMotion.Limited;
                            var sy = j.angularYLimit;
                            sy.limit = Mathf.Max(Mathf.Abs(l2), Mathf.Abs(h2));   // Unity Y: 대칭만
                            j.angularYLimit = sy;
                        }
                        else
                        {
                            j.angularZMotion = ConfigurableJointMotion.Limited;
                            var sz = j.angularZLimit;
                            sz.limit = Mathf.Max(Mathf.Abs(lo), Mathf.Abs(hi));   // z 각: 부호 불변, 대칭만
                            j.angularZLimit = sz;
                        }
                    }
                    else   // Prismatic
                    {
                        float l2 = lo, h2 = hi;
                        if (axis == "Z") { l2 = -hi; h2 = -lo; }
                        if (axis == "X") j.xMotion = ConfigurableJointMotion.Limited;
                        else if (axis == "Y") j.yMotion = ConfigurableJointMotion.Limited;
                        else j.zMotion = ConfigurableJointMotion.Limited;
                        var ll = j.linearLimit;
                        ll.limit = Mathf.Max(Mathf.Abs(l2), Mathf.Abs(h2));       // Unity: 단일 대칭 한계
                        j.linearLimit = ll;
                    }
                    nj++;
                }
            }

            Debug.Log($"[USDGameReady] CreateRigidbodyNode: rigidbodies={nb}, colliders={nc}, joints={nj}");
        }

        static void SetMotion(ConfigurableJoint j, string dof, ConfigurableJointMotion m)
        {
            switch (dof)
            {
                case "transX": j.xMotion = m; break;
                case "transY": j.yMotion = m; break;
                case "transZ": j.zMotion = m; break;
                case "rotX": j.angularXMotion = m; break;
                case "rotY": j.angularYMotion = m; break;
                case "rotZ": j.angularZMotion = m; break;
            }
        }

        // per-axis 부호 규칙 (B=diag(1,1,-1), det=-1): 선형 z flip / 각 x,y flip, z 불변
        static void ApplyLimit(ConfigurableJoint j, string dof, float lo, float hi)
        {
            switch (dof)
            {
                case "transX":
                case "transY":
                case "transZ":
                {
                    float l2 = lo, h2 = hi;
                    if (dof == "transZ") { l2 = -hi; h2 = -lo; }
                    SetMotion(j, dof, ConfigurableJointMotion.Limited);
                    var ll = j.linearLimit;
                    // 전 축 공용 한계: 축별 덮어쓰기(last-wins) 금지, max 누적
                    // (N=200 실측 gen0095/gen0119가 검출한 결함)
                    ll.limit = Mathf.Max(ll.limit,
                                         Mathf.Max(Mathf.Abs(l2), Mathf.Abs(h2)));
                    j.linearLimit = ll;
                    break;
                }
                case "rotX":
                {
                    float l2 = -hi, h2 = -lo;                       // x 각: flip
                    j.angularXMotion = ConfigurableJointMotion.Limited;
                    var sl = j.lowAngularXLimit;  sl.limit = l2; j.lowAngularXLimit = sl;
                    var sh = j.highAngularXLimit; sh.limit = h2; j.highAngularXLimit = sh;
                    break;
                }
                case "rotY":
                {
                    float l2 = -hi, h2 = -lo;                       // y 각: flip
                    j.angularYMotion = ConfigurableJointMotion.Limited;
                    var sy = j.angularYLimit;
                    sy.limit = Mathf.Max(Mathf.Abs(l2), Mathf.Abs(h2));   // 대칭만
                    j.angularYLimit = sy;
                    break;
                }
                case "rotZ":
                {
                    j.angularZMotion = ConfigurableJointMotion.Limited;   // z 각: 불변
                    var sz = j.angularZLimit;
                    sz.limit = Mathf.Max(Mathf.Abs(lo), Mathf.Abs(hi));   // 대칭만
                    j.angularZLimit = sz;
                    break;
                }
            }
        }
    }
}

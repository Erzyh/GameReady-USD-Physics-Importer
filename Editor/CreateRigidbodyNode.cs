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
    /// v0.2: (5) 비대칭 한계 정확 변환 — 단일 회전축 조인트는 그 축을 주축(twist)으로 옮기고,
    /// 단일 선형축은 connectedAnchor를 중앙값만큼 옮겨 대칭 한계로 만든다 (근사 없음).
    /// (6) UsdPhysics DriveAPI → JointDrive(스프링), (7) GameReady 라벨 → GameReadyLabel.
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
            public Dictionary<string, string> jointDrives;
            public Dictionary<string, string> labelClasses;
            public Dictionary<string, string> labelTags;
            public Dictionary<string, string> labelMatProps;
            public Dictionary<string, string> labelSources;
            public bool enabled = true;
            public bool exactLimits = true;
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
                            // approximation "none" = 비볼록 삼각형 메시. 단 움직이는 강체 아래면 Unity 제약상 볼록
                            bool dynamicAncestor = false;
                            if (Input.masses != null)
                                foreach (var bk in Input.masses.Keys)
                                    if (path == bk || path.StartsWith(bk + "/")) { dynamicAncestor = true; break; }
                            mc.convex = !(shape == "Mesh:none" && !dynamicAncestor);
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

            // ---- joints: USD joint -> ConfigurableJoint ----
            int nj = 0, nExact = 0, nDrive = 0;
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

                    // 1) USD 의미로 축별 사양 수집 (USD 좌표, 각은 도)
                    var dofs = CollectDofs(kvp.Value, path);
                    ParseDrives(path, dofs);

                    // 2) 정확 변환 계획
                    string primary = "";
                    Vector3 shiftUsd = Vector3.zero;
                    if (ExactOverride ?? Input.exactLimits)
                    {
                        primary = PickPrimary(dofs);
                        shiftUsd = PickLinearShift(dofs);
                    }

                    var j = go1.AddComponent<ConfigurableJoint>();
                    j.connectedBody = rb0;
                    j.autoConfigureConnectedAnchor = false;
                    Vector3 lp0 = Vector3.zero, lp1 = Vector3.zero;
                    if (Input.jointLocalPos0 != null) Input.jointLocalPos0.TryGetValue(path, out lp0);
                    if (Input.jointLocalPos1 != null) Input.jointLocalPos1.TryGetValue(path, out lp1);
                    j.anchor = XfVec(lp1);
                    j.connectedAnchor = XfVec(lp0 + shiftUsd);

                    j.xMotion = ConfigurableJointMotion.Locked;
                    j.yMotion = ConfigurableJointMotion.Locked;
                    j.zMotion = ConfigurableJointMotion.Locked;
                    j.angularXMotion = ConfigurableJointMotion.Locked;
                    j.angularYMotion = ConfigurableJointMotion.Locked;
                    j.angularZMotion = ConfigurableJointMotion.Locked;

                    if (primary == "rotY")
                    { j.axis = Vector3.up; j.secondaryAxis = Vector3.right; }
                    else if (primary == "rotZ")
                    { j.axis = Vector3.forward; j.secondaryAxis = Vector3.up; }

                    // 3) 한계
                    foreach (var dof in k_Dofs)
                    {
                        var d = dofs[dof];
                        if (d.motion == 'L') continue;
                        if (d.motion == 'F')
                        {
                            SetMotion(j, dof == primary ? "rotX" : dof, ConfigurableJointMotion.Free);
                            continue;
                        }
                        float lo = d.lo, hi = d.hi;
                        if (dof.StartsWith("trans") && shiftUsd != Vector3.zero)
                        {
                            float mid = Axis(shiftUsd, dof);
                            lo -= mid; hi -= mid;                       // 중앙화된 대칭 구간
                        }
                        if (dof == primary)
                            ApplyPrimaryLimit(j, dof, lo, hi);
                        else
                            ApplyLimit(j, dof, lo, hi);
                    }

                    // 4) 스프링
                    if (ApplyDrives(j, dofs, primary, shiftUsd)) nDrive++;

                    // 모든 조인트에 원본 USD 사양을 기록 (내보내기가 손실 없이 되돌리도록)
                    {
                        var info = go1.AddComponent<GameReadyJointInfo>();
                        info.joint = j;
                        info.usdPath = path;
                        info.primaryDof = primary;
                        info.anchorShift = XfVec(shiftUsd);
                        info.usdSpec = SpecString(dofs);
                        info.importSignature = GameReadyJointInfo.Signature(j);
                        if (primary.Length > 0 || shiftUsd != Vector3.zero) nExact++;
                    }
                    nj++;
                }
            }

            // ---- GameReady 라벨 → GameReadyLabel ----
            int nl = 0;
            if (Input.labelClasses != null)
            {
                foreach (var kv in Input.labelClasses)
                {
                    if (!Input.gameObjects.TryGetValue(kv.Key, out var go)) continue;
                    var lab = go.GetComponent<GameReadyLabel>();
                    if (lab == null) lab = go.AddComponent<GameReadyLabel>();
                    lab.ontologyClass = kv.Value;
                    if (Input.labelTags != null && Input.labelTags.TryGetValue(kv.Key, out var tg))
                        lab.tags = tg.Split('|');
                    if (Input.labelMatProps != null && Input.labelMatProps.TryGetValue(kv.Key, out var mp))
                        lab.materialProperties = mp.Split('|');
                    if (Input.labelSources != null && Input.labelSources.TryGetValue(kv.Key, out var src))
                        lab.source = src;
                    nl++;
                }
            }

            Debug.Log($"[USDGameReady] CreateRigidbodyNode: rigidbodies={nb}, colliders={nc}, joints={nj}, " +
                      $"exact={nExact}, drives={nDrive}, labels={nl}");
        }

        // ---------------- 조인트 사양 ----------------

        static readonly string[] k_Dofs = { "transX", "transY", "transZ", "rotX", "rotY", "rotZ" };
        const float k_Eps = 1e-5f;

        /// <summary>검증 도구용: null이 아니면 임포트 설정 대신 이 값을 쓴다.</summary>
        public static bool? ExactOverride = null;

        // Unity 드라이브 목표의 부호 관례 (Unity 실측으로 확정: unity_spring_check)
        const float LinearTargetSign = 1f;
        const float AngularTargetSign = 1f;

        class DofSpec
        {
            public char motion = 'L';          // L=locked, F=free, M=limited
            public float lo, hi;
            public bool hasDrive;
            public float k, c, t;              // USD 단위 (각: 강성과 감쇠 /deg, 평형점 deg)
        }

        Dictionary<string, DofSpec> CollectDofs(string type, string path)
        {
            var dofs = new Dictionary<string, DofSpec>();
            foreach (var d in k_Dofs) dofs[d] = new DofSpec();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (type == "D6")
            {
                string d6 = null;
                if (Input.jointD6Specs != null) Input.jointD6Specs.TryGetValue(path, out d6);
                if (string.IsNullOrEmpty(d6)) return dofs;
                foreach (var entry in d6.Split(';'))
                {
                    if (string.IsNullOrEmpty(entry)) continue;
                    var f = entry.Split(':');
                    var sp = dofs[f[0]];
                    if (f[1] == "L") sp.motion = 'L';
                    else if (f[1] == "F") sp.motion = 'F';
                    else { sp.motion = 'M'; sp.lo = float.Parse(f[1], ic); sp.hi = float.Parse(f[2], ic); }
                }
                return dofs;
            }
            string axis = "X";
            if (Input.jointAxes != null) Input.jointAxes.TryGetValue(path, out axis);
            Vector2 lim = Vector2.zero;
            if (Input.jointLimits != null) Input.jointLimits.TryGetValue(path, out lim);
            var s1 = dofs[(type == "Revolute" ? "rot" : "trans") + axis];
            s1.motion = 'M'; s1.lo = lim.x; s1.hi = lim.y;
            return dofs;
        }

        void ParseDrives(string path, Dictionary<string, DofSpec> dofs)
        {
            if (Input.jointDrives == null || !Input.jointDrives.TryGetValue(path, out var spec)) return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var entry in spec.Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var f = entry.Split(':');
                if (!dofs.TryGetValue(f[0], out var sp)) continue;
                sp.hasDrive = true;
                sp.k = float.Parse(f[1], ic); sp.c = float.Parse(f[2], ic); sp.t = float.Parse(f[3], ic);
            }
        }

        static string SpecString(Dictionary<string, DofSpec> dofs)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            foreach (var k in k_Dofs)
            {
                var d = dofs[k];
                sb.Append(k).Append(':').Append(d.motion);
                if (d.motion == 'M') sb.Append(':').Append(d.lo.ToString("R", ic)).Append(':').Append(d.hi.ToString("R", ic));
                if (d.hasDrive)
                    sb.Append(":D:").Append(d.k.ToString("R", ic)).Append(':').Append(d.c.ToString("R", ic))
                      .Append(':').Append(d.t.ToString("R", ic));
                sb.Append(';');
            }
            return sb.ToString();
        }

        // 움직이는 회전축이 Y나 Z 하나뿐이고 한계가 비대칭이면 그 축을 주축으로 옮긴다.
        // Unity는 주축(X, twist)만 low/high를 따로 저장하므로 이렇게 하면 근사가 없다.
        // 선형축이 모두 잠긴 경우만 (주축을 바꾸면 선형 축 방향도 바뀌기 때문)
        static string PickPrimary(Dictionary<string, DofSpec> dofs)
        {
            foreach (var t in new[] { "transX", "transY", "transZ" })
                if (dofs[t].motion != 'L') return "";
            string active = null;
            foreach (var r in new[] { "rotX", "rotY", "rotZ" })
            {
                if (dofs[r].motion == 'L') continue;
                if (active != null) return "";
                active = r;
            }
            if (active == null || active == "rotX") return "";
            var d = dofs[active];
            bool asym = d.motion == 'M' && Mathf.Abs(d.lo + d.hi) > k_Eps;
            return asym ? active : "";
        }

        // 제한된 선형축이 하나뿐이고 비대칭이면 그 축 방향으로 옮길 중앙값 (USD 좌표)
        static Vector3 PickLinearShift(Dictionary<string, DofSpec> dofs)
        {
            string lim = null;
            foreach (var t in new[] { "transX", "transY", "transZ" })
            {
                if (dofs[t].motion == 'L') continue;
                if (dofs[t].motion == 'F' || lim != null) return Vector3.zero;
                lim = t;
            }
            if (lim == null) return Vector3.zero;
            var d = dofs[lim];
            float mid = 0.5f * (d.lo + d.hi);
            if (Mathf.Abs(mid) < k_Eps) return Vector3.zero;
            return lim == "transX" ? new Vector3(mid, 0, 0)
                 : lim == "transY" ? new Vector3(0, mid, 0) : new Vector3(0, 0, mid);
        }

        static float Axis(Vector3 v, string dof) =>
            dof == "transX" ? v.x : dof == "transY" ? v.y : v.z;

        // 주축으로 옮긴 회전축의 한계. twist 부호(실측 확정): x, y는 그대로, z는 반전
        static void ApplyPrimaryLimit(ConfigurableJoint j, string dof, float lo, float hi)
        {
            float l2 = lo, h2 = hi;
            if (dof == "rotZ") { l2 = -hi; h2 = -lo; }
            j.angularXMotion = ConfigurableJointMotion.Limited;
            var sl = j.lowAngularXLimit;  sl.limit = l2; j.lowAngularXLimit = sl;
            var sh = j.highAngularXLimit; sh.limit = h2; j.highAngularXLimit = sh;
        }

        static JointDrive MakeDrive(float k, float c) => new JointDrive
        { positionSpring = k, positionDamper = c, maximumForce = float.MaxValue };

        // UsdPhysics 드라이브 → JointDrive. 각 강성과 감쇠는 /deg → /rad (PhysX 드라이브 단위)
        static bool ApplyDrives(ConfigurableJoint j, Dictionary<string, DofSpec> dofs,
                                string primary, Vector3 shiftUsd)
        {
            bool any = false;
            const float degPerRad = 180f / Mathf.PI;

            // 선형: 축별 드라이브. 앵커를 옮겼으면 평형점도 같이 옮긴다
            var tp = Vector3.zero;
            foreach (var t in new[] { "transX", "transY", "transZ" })
            {
                var d = dofs[t];
                if (!d.hasDrive) continue;
                float target = d.t - Axis(shiftUsd, t);
                var drv = MakeDrive(d.k, d.c);
                if (t == "transX") { j.xDrive = drv; tp.x = target; }
                else if (t == "transY") { j.yDrive = drv; tp.y = target; }
                else { j.zDrive = drv; tp.z = -target; }            // z flip
                any = true;
            }
            if (any) j.targetPosition = LinearTargetSign * tp;

            // 각: 주축(twist)은 angularXDrive, 나머지 Y/Z는 공용 angularYZDrive
            Quaternion tr = Quaternion.identity;
            DofSpec yz = null;
            foreach (var r in new[] { "rotX", "rotY", "rotZ" })
            {
                var d = dofs[r];
                if (!d.hasDrive) continue;
                var drv = MakeDrive(d.k * degPerRad, d.c * degPerRad);
                if (r == "rotX" || r == primary)
                {
                    // twist 목표 각: 한계와 같은 부호 (x, y 그대로 / z 반전)
                    float ang = r == "rotZ" ? -d.t : d.t;
                    j.angularXDrive = drv;
                    tr *= Quaternion.AngleAxis(AngularTargetSign * ang, Vector3.right);
                }
                else
                {
                    if (yz != null && (yz.k != d.k || yz.c != d.c))
                        Debug.LogWarning("[USDGameReady] Y/Z 스프링이 다르지만 Unity는 하나로 공유합니다. 강성이 큰 쪽을 씁니다");
                    if (yz == null || d.k > yz.k)
                    { j.angularYZDrive = drv; yz = d; }
                    // swing 목표 각은 수치 회전 규칙 (y 반전 / z 그대로). 실측 미확인, 기본 저작은 0
                    float ang = r == "rotZ" ? d.t : -d.t;
                    var ax = r == "rotY" ? Vector3.up : Vector3.forward;
                    tr *= Quaternion.AngleAxis(AngularTargetSign * ang, ax);
                }
                any = true;
            }
            if (tr != Quaternion.identity) j.targetRotation = tr;
            return any;
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

        // 축별 부호 규칙 (B=diag(1,1,-1)): 선형 z 반전 / twist(X)는 그대로 (실측) / swing Y, Z는 대칭이라 부호 무관
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
                    // twist 부호: Unity 실측(unity_joint_check)으로 확정 — USD 각이 그대로 twist 각
                    // (거울 변환의 부호 반전과 Unity twist 부호 관례가 상쇄). v0.1은 반전해서 넣어
                    // 비대칭 X 한계가 실제 거동에서 좌우 반전돼 있었다.
                    float l2 = lo, h2 = hi;
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

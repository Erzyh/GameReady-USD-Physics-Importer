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
        }

        static readonly string[] k_Dofs = { "transX", "transY", "transZ", "rotX", "rotY", "rotZ" };

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
            var comToken       = new TfToken("physics:centerOfMass");
            var inertiaToken   = new TfToken("physics:diagonalInertia");
            var axesToken      = new TfToken("physics:principalAxes");
            var collisionToken = new TfToken("physics:collisionEnabled");
            var sizeToken      = new TfToken("size");
            var radiusToken    = new TfToken("radius");
            var scaleToken     = new TfToken("xformOp:scale");
            var fsToken        = new TfToken("physics:staticFriction");
            var fdToken        = new TfToken("physics:dynamicFriction");
            var restToken      = new TfToken("physics:restitution");
            var layerToken     = new TfToken("gameReady:layer");
            var body0Token     = new TfToken("physics:body0");
            var body1Token     = new TfToken("physics:body1");
            var lp0Token       = new TfToken("physics:localPos0");
            var lp1Token       = new TfToken("physics:localPos1");
            var axisToken      = new TfToken("physics:axis");
            var loToken        = new TfToken("physics:lowerLimit");
            var hiToken        = new TfToken("physics:upperLimit");
            var heightToken    = new TfToken("height");
            var capAxisToken   = new TfToken("axis");
            var translateToken = new TfToken("xformOp:translate");
            var orientToken    = new TfToken("xformOp:orient");

            var prims = Input.stage.Traverse(Usd.UsdTraverseInstanceProxies());
            foreach (var prim in prims)
            {
                var path = prim.GetPath().ToString();
                ReadLabels(prim, path, Output);

                var massAttr = prim.GetAttribute(massToken);
                if (massAttr.IsValid() && massAttr.HasAuthoredValue())
                {
                    Output.masses[path] = Vt.VtValueTofloat(massAttr.Get(UsdTimeCode.EarliestTime()));

                    var comAttr = prim.GetAttribute(comToken);
                    if (comAttr.IsValid() && comAttr.HasAuthoredValue())
                    {
                        var v = Vt.VtValueToGfVec3f(comAttr.Get(UsdTimeCode.EarliestTime()));
                        Output.centersOfMass[path] = new Vector3(v[0], v[1], v[2]);
                    }
                    var inAttr = prim.GetAttribute(inertiaToken);
                    if (inAttr.IsValid() && inAttr.HasAuthoredValue())
                    {
                        var v = Vt.VtValueToGfVec3f(inAttr.Get(UsdTimeCode.EarliestTime()));
                        Output.inertiaDiagonals[path] = new Vector3(v[0], v[1], v[2]);
                    }
                    var axAttr = prim.GetAttribute(axesToken);
                    if (axAttr.IsValid() && axAttr.HasAuthoredValue())
                    {
                        var q = Vt.VtValueToGfQuatf(axAttr.Get(UsdTimeCode.EarliestTime()));
                        var im = q.GetImaginary();
                        Output.principalAxes[path] = new Quaternion(im[0], im[1], im[2], q.GetReal());
                    }
                }

                var colAttr = prim.GetAttribute(collisionToken);
                if (colAttr.IsValid() && colAttr.HasAuthoredValue() &&
                    Vt.VtValueTobool(colAttr.Get(UsdTimeCode.EarliestTime())))
                {
                    Output.collisionPaths.Add(path);
                    var typeName = prim.GetTypeName().ToString();
                    Output.colliderShapes[path] = typeName;
                    // 메시 콜라이더 근사: approximation = "none" 이면 삼각형 메시 그대로 (비볼록)
                    if (typeName == "Mesh")
                    {
                        var apA = prim.GetAttribute(new TfToken("physics:approximation"));
                        if (apA.IsValid() && apA.HasAuthoredValue() &&
                            Vt.VtValueToTfToken(apA.Get(UsdTimeCode.EarliestTime())).GetString() == "none")
                            Output.colliderShapes[path] = "Mesh:none";
                    }

                    // 콜라이더 로컬 오프셋/회전 — 프리미티브 프림은 base 그래프가
                    // GO를 안 만들어 폴백 부착되므로, 여기서 읽어 Create가 복원
                    var trAttr = prim.GetAttribute(translateToken);
                    if (trAttr.IsValid() && trAttr.HasAuthoredValue())
                    {
                        var v = Vt.VtValueToGfVec3f(trAttr.Get(UsdTimeCode.EarliestTime()));
                        Output.colliderCenters[path] = new Vector3(v[0], v[1], v[2]);
                    }
                    var orAttr = prim.GetAttribute(orientToken);
                    if (orAttr.IsValid() && orAttr.HasAuthoredValue())
                    {
                        var q = Vt.VtValueToGfQuatf(orAttr.Get(UsdTimeCode.EarliestTime()));
                        var im = q.GetImaginary();
                        Output.colliderRotations[path] = new Quaternion(im[0], im[1], im[2], q.GetReal());
                    }

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
                        Output.colliderBoxSizes[path] = scale * size;
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
                                Vt.VtValueToTfToken(aAttr.Get(UsdTimeCode.EarliestTime())).GetString();
                    }

                    float fs = 0.5f, fd = 0.5f, rest = 0f;
                    var a1 = prim.GetAttribute(fsToken);
                    if (a1.IsValid() && a1.HasAuthoredValue()) fs = Vt.VtValueTofloat(a1.Get(UsdTimeCode.EarliestTime()));
                    var a2 = prim.GetAttribute(fdToken);
                    if (a2.IsValid() && a2.HasAuthoredValue()) fd = Vt.VtValueTofloat(a2.Get(UsdTimeCode.EarliestTime()));
                    var a3 = prim.GetAttribute(restToken);
                    if (a3.IsValid() && a3.HasAuthoredValue()) rest = Vt.VtValueTofloat(a3.Get(UsdTimeCode.EarliestTime()));
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
                    foreach (var dof in new[] { "transX", "transY", "transZ", "rotX", "rotY", "rotZ" })
                    {
                        var loA = prim.GetAttribute(new TfToken($"limit:{dof}:physics:low"));
                        var hiA = prim.GetAttribute(new TfToken($"limit:{dof}:physics:high"));
                        if (loA.IsValid() && loA.HasAuthoredValue() && hiA.IsValid() && hiA.HasAuthoredValue())
                        {
                            float lo = Vt.VtValueTofloat(loA.Get(UsdTimeCode.EarliestTime()));
                            float hi = Vt.VtValueTofloat(hiA.Get(UsdTimeCode.EarliestTime()));
                            if (lo > hi) spec.Append($"{dof}:L;");                 // locked
                            else spec.Append($"{dof}:{lo}:{hi};");                 // limited
                        }
                        else spec.Append($"{dof}:F;");                             // free
                    }
                    Output.jointD6Specs[path] = spec.ToString();
                    var drv = new System.Text.StringBuilder();
                    foreach (var dof in k_Dofs)
                        drv.Append(ReadDrive(prim, dof, dof));
                    if (drv.Length > 0) Output.jointDrives[path] = drv.ToString();

                    var rr0 = prim.GetRelationship(body0Token);
                    if (rr0.IsValid())
                    {
                        var tt0 = rr0.GetTargets();
                        if (tt0 != null && tt0.Count > 0) Output.jointBody0[path] = tt0[0].ToString();
                    }
                    var rr1 = prim.GetRelationship(body1Token);
                    if (rr1.IsValid())
                    {
                        var tt1 = rr1.GetTargets();
                        if (tt1 != null && tt1.Count > 0) Output.jointBody1[path] = tt1[0].ToString();
                    }
                    var pp0 = prim.GetAttribute(lp0Token);
                    if (pp0.IsValid() && pp0.HasAuthoredValue())
                    {
                        var v = Vt.VtValueToGfVec3f(pp0.Get(UsdTimeCode.EarliestTime()));
                        Output.jointLocalPos0[path] = new Vector3(v[0], v[1], v[2]);
                    }
                    var pp1 = prim.GetAttribute(lp1Token);
                    if (pp1.IsValid() && pp1.HasAuthoredValue())
                    {
                        var v = Vt.VtValueToGfVec3f(pp1.Get(UsdTimeCode.EarliestTime()));
                        Output.jointLocalPos1[path] = new Vector3(v[0], v[1], v[2]);
                    }
                }
                else if (tn == "PhysicsRevoluteJoint" || tn == "PhysicsPrismaticJoint")
                {
                    Output.jointTypes[path] = tn == "PhysicsRevoluteJoint" ? "Revolute" : "Prismatic";

                    var r0 = prim.GetRelationship(body0Token);
                    if (r0.IsValid())
                    {
                        var t0 = r0.GetTargets();
                        if (t0 != null && t0.Count > 0)
                            Output.jointBody0[path] = t0[0].ToString();
                    }
                    var r1 = prim.GetRelationship(body1Token);
                    if (r1.IsValid())
                    {
                        var t1 = r1.GetTargets();
                        if (t1 != null && t1.Count > 0)
                            Output.jointBody1[path] = t1[0].ToString();
                    }

                    var p0 = prim.GetAttribute(lp0Token);
                    if (p0.IsValid() && p0.HasAuthoredValue())
                    {
                        var v = Vt.VtValueToGfVec3f(p0.Get(UsdTimeCode.EarliestTime()));
                        Output.jointLocalPos0[path] = new Vector3(v[0], v[1], v[2]);
                    }
                    var p1 = prim.GetAttribute(lp1Token);
                    if (p1.IsValid() && p1.HasAuthoredValue())
                    {
                        var v = Vt.VtValueToGfVec3f(p1.Get(UsdTimeCode.EarliestTime()));
                        Output.jointLocalPos1[path] = new Vector3(v[0], v[1], v[2]);
                    }
                    var ax = prim.GetAttribute(axisToken);
                    if (ax.IsValid() && ax.HasAuthoredValue())
                        Output.jointAxes[path] =
                            Vt.VtValueToTfToken(ax.Get(UsdTimeCode.EarliestTime())).GetString();

                    // 타입 조인트 드라이브: 인스턴스 이름이 angular/linear
                    string axTag = Output.jointAxes.TryGetValue(path, out var axv) ? axv : "X";
                    bool rev = tn == "PhysicsRevoluteJoint";
                    var tdrv = ReadDrive(prim, rev ? "angular" : "linear", (rev ? "rot" : "trans") + axTag);
                    if (tdrv.Length > 0) Output.jointDrives[path] = tdrv;

                    float lo = 0, hi = 0;
                    var la = prim.GetAttribute(loToken);
                    if (la.IsValid() && la.HasAuthoredValue()) lo = Vt.VtValueTofloat(la.Get(UsdTimeCode.EarliestTime()));
                    var ha = prim.GetAttribute(hiToken);
                    if (ha.IsValid() && ha.HasAuthoredValue()) hi = Vt.VtValueTofloat(ha.Get(UsdTimeCode.EarliestTime()));
                    Output.jointLimits[path] = new Vector2(lo, hi);
                }
            }

            Debug.Log($"[USDGameReady] UsdPhysics filter: bodies={Output.masses.Count}, " +
                      $"colliders={Output.collisionPaths.Count}, joints={Output.jointTypes.Count}, " +
                      $"drives={Output.jointDrives.Count}, labels={Output.labelClasses.Count}");
        }
    }
}

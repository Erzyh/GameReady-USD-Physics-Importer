// GameReady round-trip dump tool
// ------------------------------------------------------------------
// 사용법:
//   1) 패키지 com.eglab.gameready.usd 설치
//   2) roundtrip_suite.usda 를 임포트해 씬에 배치
//   3) 메뉴  GameReady > Export RoundTrip Dump  실행 → JSON 저장
//   4) 저장된 JSON을  python -m roundtrip.run_real <파일>  로 측정
//
// 값은 "유니티가 실제로 갖고 있는 그대로" 기록합니다 (변환 금지).
// 좌표는 에셋 루트(부모) 기준 local 값 = IR의 body 좌표와 대응.
// ------------------------------------------------------------------
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class RoundTripDump
{
    [MenuItem("GameReady/Export RoundTrip Dump")]
    public static void Export()
    {
        string path = EditorUtility.SaveFilePanel(
            "Save RoundTrip Dump", "", "unity_dump.json", "json");
        if (string.IsNullOrEmpty(path)) return;
        ExportTo(path);
    }

    // 배치모드용 비대화형 진입점 (GameReadyBatch가 호출)
    public static void ExportTo(string path)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"engine\": \"Unity\",\n  \"bodies\": [\n");

        var bodies = Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
        bool firstB = true;
        foreach (var rb in bodies)
        {
            if (!firstB) sb.Append(",\n");
            firstB = false;
            AppendBody(sb, rb);
        }
        sb.Append("\n  ],\n  \"joints\": [\n");

        var joints = Object.FindObjectsByType<ConfigurableJoint>(FindObjectsSortMode.None);
        bool firstJ = true;
        foreach (var j in joints)
        {
            if (!firstJ) sb.Append(",\n");
            firstJ = false;
            AppendJoint(sb, j);
        }
        sb.Append("\n  ],\n");

        // ---- scene inventory (self-diagnosis: 씬에 뭐가 있는지) ----
        var allT = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        var allMf = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        var allCol = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
        sb.Append("  \"inventory\": {\n");
        sb.Append($"    \"transforms\": {allT.Length},\n");
        sb.Append($"    \"meshFilters\": {allMf.Length},\n");
        sb.Append($"    \"colliders\": {allCol.Length},\n");
        sb.Append($"    \"rigidbodies\": {bodies.Length},\n");
        sb.Append($"    \"configurableJoints\": {joints.Length},\n");
        sb.Append("    \"rootObjects\": [");
        var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
            sb.Append((i > 0 ? ", " : "") + "\"" + roots[i].name + "\"");
        sb.Append("]\n  }\n}\n");

        System.IO.File.WriteAllText(path, sb.ToString());
        Debug.Log($"[GameReady] RoundTrip dump written: {path}  " +
                  $"(bodies={bodies.Length}, joints={joints.Length}, " +
                  $"transforms={allT.Length}, colliders={allCol.Length})");
    }

    // ---------- helpers ----------

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string V3(Vector3 v) => $"[{F(v.x)}, {F(v.y)}, {F(v.z)}]";
    static string QWXYZ(Quaternion q) => $"[{F(q.w)}, {F(q.x)}, {F(q.y)}, {F(q.z)}]";

    /// 에셋 루트 = Rigidbody의 최상위 조상 중 "World" 바로 아래 노드
    static string AssetName(Transform t)
    {
        Transform cur = t, prev = t;
        while (cur.parent != null) { prev = cur; cur = cur.parent; }
        // 루트가 World면 그 아래 노드가 에셋, 아니면 루트 자체가 에셋
        return (cur.name == "World" && prev != cur) ? prev.name
             : (t.parent != null ? t.parent.name : t.name);
    }

    static void AppendBody(StringBuilder sb, Rigidbody rb)
    {
        var t = rb.transform;
        sb.Append("    {\n");
        sb.Append($"      \"asset\": \"{AssetName(t)}\",\n");
        sb.Append($"      \"name\": \"{t.name}\",\n");
        sb.Append($"      \"position\": {V3(t.localPosition)},\n");
        sb.Append($"      \"worldPosition\": {V3(t.position)},\n");
        sb.Append($"      \"worldRotation_wxyz\": {QWXYZ(t.rotation)},\n");
        sb.Append($"      \"rotation_wxyz\": {QWXYZ(t.localRotation)},\n");
        sb.Append($"      \"isKinematic\": {(rb.isKinematic ? "true" : "false")},\n");
        sb.Append($"      \"mass\": {F(rb.mass)},\n");
        sb.Append($"      \"centerOfMass\": {V3(rb.centerOfMass)},\n");
        sb.Append($"      \"inertiaTensor\": {V3(rb.inertiaTensor)},\n");
        sb.Append($"      \"inertiaTensorRotation_wxyz\": {QWXYZ(rb.inertiaTensorRotation)}");

        // 다중 콜라이더 (CoACD 분해 에셋: 바디 하나에 볼록 조각 N개)
        var cols = rb.GetComponentsInChildren<Collider>();
        if (cols.Length > 0)
        {
            sb.Append(",\n      \"colliders\": [\n");
            for (int i = 0; i < cols.Length; i++)
            {
                if (i > 0) sb.Append(",\n");
                AppendCollider(sb, rb, cols[i]);
            }
            sb.Append("\n      ]");
        }
        sb.Append("\n    }");
    }

    /// 바디 기준 로컬 좌표 — 월드 합성 금지(원거리 배치 시 float32 노이즈가
    /// 원점 기준 상대오차를 폭발시킴, N=200 실측), 로컬 변환만 누적
    static Vector3 LocalToBody(Rigidbody rb, Transform t, Vector3 p)
    {
        while (t != null && t != rb.transform)
        {
            p = t.localPosition + t.localRotation * Vector3.Scale(p, t.localScale);
            t = t.parent;
        }
        return p;
    }

    static Quaternion LocalRotToBody(Rigidbody rb, Transform t)
    {
        Quaternion q = Quaternion.identity;
        while (t != null && t != rb.transform)
        {
            q = t.localRotation * q;
            t = t.parent;
        }
        return q;
    }

    static void AppendCollider(StringBuilder sb, Rigidbody rb, Collider col)
    {
        Vector3 centerLocal;
        Quaternion rotLocal = LocalRotToBody(rb, col.transform);
        sb.Append("        {\n");
        sb.Append($"          \"name\": \"{col.gameObject.name}\",\n");
        if (col is BoxCollider b)
        {
            centerLocal = LocalToBody(rb, col.transform, b.center);
            sb.Append("          \"type\": \"Box\",\n");
            sb.Append($"          \"size\": {V3(b.size)},\n");
        }
        else if (col is SphereCollider s)
        {
            centerLocal = LocalToBody(rb, col.transform, s.center);
            sb.Append("          \"type\": \"Sphere\",\n");
            sb.Append($"          \"radius\": {F(s.radius)},\n");
        }
        else if (col is CapsuleCollider c)
        {
            centerLocal = LocalToBody(rb, col.transform, c.center);
            sb.Append("          \"type\": \"Capsule\",\n");
            sb.Append($"          \"radius\": {F(c.radius)},\n");
            sb.Append($"          \"height\": {F(c.height)},\n");
            sb.Append($"          \"direction\": {c.direction},\n");
        }
        else
        {
            centerLocal = LocalToBody(rb, col.transform, Vector3.zero);
            sb.Append($"          \"type\": \"{col.GetType().Name}\",\n");
        }
        sb.Append($"          \"center\": {V3(centerLocal)},\n");
        sb.Append($"          \"rotation_wxyz\": {QWXYZ(rotLocal)}");
        var m = col.sharedMaterial;
        if (m != null)
        {
            sb.Append(",\n          \"material\": { ");
            sb.Append($"\"staticFriction\": {F(m.staticFriction)}, ");
            sb.Append($"\"dynamicFriction\": {F(m.dynamicFriction)}, ");
            sb.Append($"\"bounciness\": {F(m.bounciness)} }}");
        }
        else
        {
            var info = col.GetComponent<USDGameReady.UsdPhysMatInfo>();
            if (info != null)
            {
                sb.Append(",\n          \"material\": { ");
                sb.Append($"\"staticFriction\": {F(info.staticFriction)}, ");
                sb.Append($"\"dynamicFriction\": {F(info.dynamicFriction)}, ");
                sb.Append($"\"bounciness\": {F(info.bounciness)} }}");
            }
        }
        sb.Append($",\n          \"layer\": {col.gameObject.layer}\n        }}");
    }

    static string Motion(ConfigurableJointMotion m) => m.ToString(); // Locked/Limited/Free

    static string UsdPathOf(ConfigurableJoint j)
    {
        foreach (var info in j.GetComponents<USDGameReady.GameReadyJointInfo>())
            if (info.joint == j) return info.usdPath;
        return "";
    }

    static void AppendJoint(StringBuilder sb, ConfigurableJoint j)
    {
        var t = j.transform;
        string other = j.connectedBody ? j.connectedBody.name : "world";
        sb.Append("    {\n");
        sb.Append($"      \"asset\": \"{AssetName(t)}\",\n");
        sb.Append($"      \"name\": \"{t.name}\",\n");
        sb.Append($"      \"connectedBodies\": [\"{other}\", \"{t.name}\"],\n");
        sb.Append($"      \"anchor\": {V3(j.connectedAnchor)},\n");
        sb.Append($"      \"connectedAnchor\": {V3(j.anchor)},\n");
        sb.Append("      \"motions\": { ");
        sb.Append($"\"x\": \"{Motion(j.xMotion)}\", \"y\": \"{Motion(j.yMotion)}\", ");
        sb.Append($"\"z\": \"{Motion(j.zMotion)}\", ");
        sb.Append($"\"angularX\": \"{Motion(j.angularXMotion)}\", ");
        sb.Append($"\"angularY\": \"{Motion(j.angularYMotion)}\", ");
        sb.Append($"\"angularZ\": \"{Motion(j.angularZMotion)}\" }},\n");
        sb.Append($"      \"linearLimit\": {F(j.linearLimit.limit)},\n");
        sb.Append($"      \"lowAngularXLimit\": {F(j.lowAngularXLimit.limit)},\n");
        sb.Append($"      \"highAngularXLimit\": {F(j.highAngularXLimit.limit)},\n");
        sb.Append($"      \"angularYLimit\": {F(j.angularYLimit.limit)},\n");
        sb.Append($"      \"angularZLimit\": {F(j.angularZLimit.limit)},\n");
        // v0.2 임포터: twist 한계를 USD 각 부호 그대로 저장 (측정기가 구버전 덤프와 구분)
        // 월드 기준 (외부 UsdPhysics 검증용: 두 몸의 앵커가 한 점인지, 축 방향)
        var wa = t.TransformPoint(j.anchor);
        var wc = j.connectedBody ? j.connectedBody.transform.TransformPoint(j.connectedAnchor) : j.connectedAnchor;
        sb.Append($"      \"worldAnchor\": {V3(wa)},\n");
        sb.Append($"      \"worldConnectedAnchor\": {V3(wc)},\n");
        sb.Append($"      \"worldAxis\": {V3(t.TransformDirection(j.axis).normalized)},\n");
        sb.Append($"      \"worldSecondaryAxis\": {V3(t.TransformDirection(j.secondaryAxis).normalized)},\n");
        sb.Append($"      \"usdPath\": \"{UsdPathOf(j)}\",\n");
        sb.Append("      \"twistConvention\": \"usd\"");
        // 정확 변환 기록: 주축으로 옮긴 USD 축, connectedAnchor에 더한 이동량 (측정기가 USD 의미로 복원)
        foreach (var info in j.GetComponents<USDGameReady.GameReadyJointInfo>())
        {
            if (info.joint != j) continue;
            if (!string.IsNullOrEmpty(info.primaryDof))
                sb.Append($",\n      \"gameReadyPrimary\": \"{info.primaryDof}\"");
            if (info.anchorShift != Vector3.zero)
                sb.Append($",\n      \"gameReadyAnchorShift\": {V3(info.anchorShift)}");
        }
        sb.Append("\n");
        sb.Append("    }");
    }
}

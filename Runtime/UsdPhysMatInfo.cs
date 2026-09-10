using UnityEngine;

namespace USDGameReady
{
    /// <summary>
    /// UsdPhysics material 값 운반용 컴포넌트.
    /// 임포트 시 생성한 PhysicsMaterial 은 프리팹 서브에셋으로 등록되지 않아 소실되므로,
    /// 직렬화되는 컴포넌트로 값을 보존하고 런타임(Awake)에 실제 material 로 적용한다.
    /// </summary>
    public class UsdPhysMatInfo : MonoBehaviour
    {
        public float staticFriction = 0.5f;
        public float dynamicFriction = 0.5f;
        public float bounciness = 0f;

        void Awake()
        {
            var col = GetComponent<Collider>();
            if (col == null) return;
            var pm = new PhysicsMaterial("usdPhysMat");
            pm.staticFriction = staticFriction;
            pm.dynamicFriction = dynamicFriction;
            pm.bounciness = bounciness;
            col.sharedMaterial = pm;
        }
    }
}

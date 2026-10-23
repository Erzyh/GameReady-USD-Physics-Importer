using UnityEngine;

namespace USDGameReady
{
    /// <summary>
    /// 정확 변환으로 ConfigurableJoint 표현을 바꾼 경우의 기록.
    /// primaryDof: 비대칭 한계 축을 조인트 주축(twist)으로 옮겼으면 그 USD 축 이름("rotY"|"rotZ"), 아니면 빈 문자열.
    /// anchorShift: 비대칭 선형 한계를 대칭으로 만들려고 connectedAnchor에 더한 값(Unity 좌표).
    /// 거동은 USD 원본과 같고, 왕복 측정 덤프가 이 기록으로 USD 의미를 복원한다.
    /// </summary>
    public class GameReadyJointInfo : MonoBehaviour
    {
        public ConfigurableJoint joint;
        public string usdPath = "";
        public string primaryDof = "";
        public Vector3 anchorShift = Vector3.zero;

        // 원본 USD 축별 사양 ("rotX:M:-15:25;rotY:L;...", 드라이브는 ":D:k:c:t" 덧붙임).
        // Unity가 그대로 담지 못하는 값(2축 이상일 때 비대칭 Y/Z 한계 등)을 내보내기 때 복원하는 데 쓴다
        public string usdSpec = "";
        // 임포트 직후 조인트 값의 지문. 지금 값과 같으면 사용자가 Unity에서 고치지 않은 것
        public string importSignature = "";

        public static string Signature(ConfigurableJoint j)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string f(float v) => v.ToString("G6", ic);
            string d(JointDrive x) => $"{f(x.positionSpring)}/{f(x.positionDamper)}";
            return string.Join(",", new[] {
                j.xMotion.ToString(), j.yMotion.ToString(), j.zMotion.ToString(),
                j.angularXMotion.ToString(), j.angularYMotion.ToString(), j.angularZMotion.ToString(),
                f(j.linearLimit.limit), f(j.lowAngularXLimit.limit), f(j.highAngularXLimit.limit),
                f(j.angularYLimit.limit), f(j.angularZLimit.limit),
                d(j.xDrive), d(j.yDrive), d(j.zDrive), d(j.angularXDrive), d(j.angularYZDrive),
                j.axis.ToString("G6"), j.secondaryAxis.ToString("G6"),
                j.anchor.ToString("G6"), j.connectedAnchor.ToString("G6"),
                j.targetPosition.ToString("G6"), j.targetRotation.ToString("G6"),
                j.connectedBody ? j.connectedBody.name : "" });
        }
    }
}

using System;
using UnityEngine;

namespace USDGameReady
{
    /// <summary>
    /// GameReady 라벨(gameReady:label:*, gameReady:mat:*)을 게임 코드에서 쓰도록 보존하는 컴포넌트.
    /// 예: 불 시스템은 IsFlammable, 상호작용 시스템은 HasFunction("openable")로 대상을 고른다.
    /// 온톨로지 클래스는 경로 문자열이라 IsA("Asset/Prop/Weapon")처럼 상위 클래스로도 질의한다.
    /// </summary>
    public class GameReadyLabel : MonoBehaviour
    {
        public string ontologyClass = "";
        public string[] tags = Array.Empty<string>();
        public string[] materialProperties = Array.Empty<string>();
        public string source = "";

        public bool IsA(string classPrefix) =>
            !string.IsNullOrEmpty(ontologyClass) &&
            (ontologyClass == classPrefix || ontologyClass.StartsWith(classPrefix + "/"));

        public bool HasTag(string tag) => Array.IndexOf(tags, tag) >= 0;

        public bool HasFunction(string function) => HasTag("function:" + function);

        public string Facet(string facet)
        {
            foreach (var t in tags)
                if (t.StartsWith(facet + ":")) return t.Substring(facet.Length + 1);
            return null;
        }

        public string MaterialClass => Facet("materialClass");
        public bool IsFlammable => Array.IndexOf(materialProperties, "flammable") >= 0;
        public bool IsConductive => Array.IndexOf(materialProperties, "conductive") >= 0;
        public bool IsBuoyant => Array.IndexOf(materialProperties, "buoyant") >= 0;
    }
}

#if UNITY_EDITOR
using UltEvents;
using UnityEngine;

namespace UltSharp.EditorTestingUtilities
{
    [ExecuteAlways]
    public class IfBlockRunner : MonoBehaviour
    {
        public static bool Active;
        private void OnEnable()
        {
            if (Active)
                GetComponent<LifeCycleEvents>().EnableEvent.Invoke();
        }
    }
}
#endif
using UnityEngine;

namespace MuMech
{
    /// <summary>
    ///     An un-throttled Unity proxy that forces background simulation updates
    ///     exclusively on the graphic frame loop to protect performance during Time Warp.
    /// </summary>
    public class StageStatsUnityDriver : MonoBehaviour
    {
        private MechJebModuleStageStats _targetModule;

        public void Initialize(MechJebModuleStageStats module)
        {
            _targetModule = module;
        }

        private void Update()
        {
            if (_targetModule == null) return;

            // Raw engine frame pulse—resets the frame budget and handles all scene runs
            _targetModule.DriverUpdate();
        }
    }
}

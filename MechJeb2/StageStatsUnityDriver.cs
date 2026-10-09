using UnityEngine;

namespace MuMech
{
    /// <summary>
    ///     An un-throttled Unity proxy that forces background simulation updates
    ///     even when the parent MechJeb module is hidden or deactivated by the core.
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

            // Raw engine frame pulse—harvest data and check if we need to dispatch a job
            _targetModule.DriverUpdate();
        }
    }
}

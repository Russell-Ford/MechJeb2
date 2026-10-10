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

        // FPS Performance Profiling Primitives
        private int _totalFramesCount = 0;
        private float _totalElapsedTime = 0f;
        private float _nextLogIntervalTimer = 10f;
        private const float LOG_INTERVAL_DURATION_SECONDS = 10f;

        public void Initialize(MechJebModuleStageStats module)
        {
            _targetModule = module;

            if (!HighLogic.LoadedSceneIsEditor)
            {
                Debug.Log("[Performance Profile] Flight scene active. Frame-hook tracking engaged...");
            }
        }

        private void Update()
        {
            if (_targetModule == null) return;

            // Raw engine frame pulse—resets the frame budget and handles all scene runs
            _targetModule.DriverUpdate();

            // Track performance metrics smoothly on the frame loop
            if (!HighLogic.LoadedSceneIsEditor)
            {
                _totalFramesCount++;
                float delta = Time.unscaledDeltaTime;
                _totalElapsedTime += delta;
                _nextLogIntervalTimer -= delta;

                if (_nextLogIntervalTimer <= 0f && _totalElapsedTime > 0f)
                {
                    float runningAverageFps = _totalFramesCount / _totalElapsedTime;
                    Debug.Log(string.Format(
                        "[Performance Progress] Running Total Time: {0:F1}s | Total Frames: {1} | Current Flight Speed: {2:F2} FPS",
                        _totalElapsedTime,
                        _totalFramesCount,
                        runningAverageFps
                    ));
                    _nextLogIntervalTimer = LOG_INTERVAL_DURATION_SECONDS;
                }
            }
        }

        private void OnDestroy()
        {
            if (!HighLogic.LoadedSceneIsEditor && _totalElapsedTime > 0f)
            {
                float finalAverageFps = _totalFramesCount / _totalElapsedTime;
                Debug.Log(string.Format(
                    "[Performance Profile Done] Scene Exited. Final Total Time: {0:F2}s | Final Total Frames: {1} | Final Average Run Rate: {2:F2} FPS",
                    _totalElapsedTime,
                    _totalFramesCount,
                    finalAverageFps
                ));
            }
        }
    }
}

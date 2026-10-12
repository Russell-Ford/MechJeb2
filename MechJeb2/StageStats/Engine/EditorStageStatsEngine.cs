using UnityEngine;

namespace MuMech
{
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorStageStatsEngine : MonoBehaviour
    {
        public static CelestialBody EditorBody { get; set; }
        public static double AltSLT { get; set; }
        public static double Mach { get; set; }

        private bool _editorIsDirty = true;
        private bool _hasCompletedInitialPass = false;
        private bool _isProcessingVac = false;
        private bool _isProcessingAtmo = false;

        private void Start()
        {
            StageStatsProvider.EnsureGlobalSceneListener();

            GameEvents.onEditorShipModified.Add(OnEditorMod);
            GameEvents.onPartCrossfeedStateChange.Add(OnEditorPartCrossfeedChange);
            GameEvents.StageManager.OnGUIStageSequenceModified.Add(OnEditorStagingChange);
        }

        private void OnEditorMod(ShipConstruct s) => TriggerEditorRecalculation();
        private void OnEditorPartCrossfeedChange(Part p) => TriggerEditorRecalculation();
        private void OnEditorStagingChange() => TriggerEditorRecalculation();

        public void TriggerEditorRecalculation() => _editorIsDirty = true;

        private void Update()
        {
            // 1. CONCRETE ASSEMBLY GUARD:
            // If the user has completely cleared the assembly floor, or if they are holding a ghost part
            // but haven't placed a root core piece down yet, drop out and keep the threads resting.
            if (EditorLogic.fetch?.ship == null || EditorLogic.fetch.ship.Count == 0)
            {
                _hasCompletedInitialPass = false;
                return;
            }

            // Harvest asynchronously as the background lanes resolve
            StageStatsProvider.TryHarvestAtmo();
            StageStatsProvider.TryHarvestVac();

            // Track background lifecycle progress transitions
            if (_isProcessingVac && StageStatsProvider.ManagerVac.FuelFlowSimulation.IsReady) _isProcessingVac = false;
            if (_isProcessingAtmo && StageStatsProvider.ManagerAtmo.FuelFlowSimulation.IsReady) _isProcessingAtmo = false;

            // Wait out active thread ticks to completely prevent execution stacking or thread thrashing
            if (_isProcessingVac || _isProcessingAtmo) return;

            // 2. DATA STABILIZATION PEEK:
            // Check if the consumer arrays are completely empty. If the rocket has visible parts 
            // but the background simulation returned a zero-stage array, flag the engine as dirty.
            bool dataIsEmpty = StageStatsProvider.AtmoStats == null || StageStatsProvider.AtmoStats.Count == 0;

            if (dataIsEmpty)
            {
                _editorIsDirty = true;
            }

            // QUIET MODE GATE: If the design is stable and a valid layout pass is locked, rest completely.
            if (!_editorIsDirty && _hasCompletedInitialPass) return;

            var craft = EditorLogic.fetch.ship;

            // 3. LAUNCH VACUUM SWEEP PASS
            StageStatsProvider.ManagerVac.Build(craft);
            StageStatsProvider.ManagerVac.DVLinearThrust = true;
            StageStatsProvider.ManagerVac.SetConditions(0, 0, 0);
            StageStatsProvider.ManagerVac.TryStartFuelFlowSimulationJob();
            _isProcessingVac = true;

            // 4. LAUNCH ATMOSPHERIC SWEEP PASS
            CelestialBody body = EditorBody ?? FlightGlobals.currentMainBody;
            double pressure = body.atmosphere ? body.GetPressure(AltSLT) : 0;
            double density = body.GetDensity(pressure, body.GetTemperature(0)) / 1.225;

            StageStatsProvider.ManagerAtmo.Build(craft);
            StageStatsProvider.ManagerAtmo.DVLinearThrust = true;
            StageStatsProvider.ManagerAtmo.SetConditions(density, pressure * PhysicsGlobals.KpaToAtmospheres, Mach);
            StageStatsProvider.ManagerAtmo.TryStartFuelFlowSimulationJob();
            _isProcessingAtmo = true;

            // Drop dirty flags. If the thread pass returns valid elements, this locks. 
            // If it returns zero elements due to an early assembly state glitch, Step 2 will instantly catch it next frame.
            _editorIsDirty = false;
            _hasCompletedInitialPass = true;
        }

        private void OnDestroy()
        {
            GameEvents.onEditorShipModified.Remove(OnEditorMod);
            GameEvents.onPartCrossfeedStateChange.Remove(OnEditorPartCrossfeedChange);
            GameEvents.StageManager.OnGUIStageSequenceModified.Remove(OnEditorStagingChange);
        }
    }
}

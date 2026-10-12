using UnityEngine;
using MechJebLibBindings.FuelFlowSimulation;
using MechJebLib.FuelFlowSimulation;
using MechJebLibBindings;

namespace MuMech
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightStageStatsEngine : MonoBehaviour
    {
        private bool _needsRebuild = true;
        private bool _alternateTick = false;

        // --- Concrete Structural State Flags ---
        private bool _hasCompletedInitialBaseline = false;

        private void Start()
        {
            StageStatsProvider.EnsureGlobalSceneListener();

            GameEvents.StageManager.OnGUIStageSequenceModified.Add(OnStructuralChange);
            GameEvents.onStageActivate.Add(OnStageActivated);
            GameEvents.onPartDie.Add(OnPartDestroyed);
            GameEvents.onVesselWasModified.Add(OnVesselStructureChanged);
        }

        private void Update()
        {
            if (FlightGlobals.ActiveVessel == null) return;

            var v = FlightGlobals.ActiveVessel;

            // 1. CONCRETE INITIALIZATION CHECK:
            // If KSP is still physics-packing the vessel or parts list hydration is incomplete, 
            // skip the frame loop and refuse to run calculations on an unstable empty craft structure.
            if (v.packed || v.parts == null || v.parts.Count == 0)
            {
                _hasCompletedInitialBaseline = false; // Reset to force baseline evaluation on drop
                return;
            }

            var masterJeb = v.GetMasterMechJeb();
            if (masterJeb == null || !masterJeb.running) return;

            // 2. Harvest completed background tasks cleanly
            StageStatsProvider.TryHarvestAtmo();
            StageStatsProvider.TryHarvestVac();

            // 3. Concurrency Shield: Exit immediately if threads are busy out in the pool
            if (!StageStatsProvider.ManagerVac.FuelFlowSimulation.IsReady ||
                !StageStatsProvider.ManagerAtmo.FuelFlowSimulation.IsReady)
            {
                return;
            }

            var statsModule = masterJeb.GetComputerModule<MechJebModuleStageStats>();

            bool panelsAreVisible = statsModule != null && statsModule.Enabled;
            bool enginesActive = v.ctrlState.mainThrottle > 0 || v.ActionGroups[KSPActionGroup.RCS];

            // 4. THE DATA STABILIZATION GATEWAY:
            // If the provider lists are empty but the ship is ready, or if we haven't locked 
            // an initial baseline yet, we override the zero-throttle sleep check.
            bool dataIsEmpty = StageStatsProvider.AtmoStats == null || StageStatsProvider.AtmoStats.Count == 0;

            if (!_hasCompletedInitialBaseline || dataIsEmpty)
            {
                _needsRebuild = true; // Force a clean vessel manager build pass
            }
            else if (!enginesActive && !_needsRebuild && !panelsAreVisible)
            {
                return; // Sleep safely if data is stable and ship is resting
            }

            bool dvLinearThrust = statsModule == null || statsModule.DVLinearThrust;
            _alternateTick = !_alternateTick;

            if (_alternateTick)
            {
                if (_needsRebuild) { StageStatsProvider.ManagerVac.Build(v); }
                StageStatsProvider.ManagerVac.DVLinearThrust = dvLinearThrust;
                StageStatsProvider.ManagerVac.SetConditions(0, 0, 0);

                if (statsModule != null && statsModule.VesselState != null)
                {
                    StageStatsProvider.ManagerVac.SetInitial(
                        statsModule.VesselState.Time,
                        statsModule.VesselState.OrbitalPosition.WorldToV3Rotated(),
                        statsModule.VesselState.OrbitalVelocity.WorldToV3Rotated(),
                        statsModule.VesselState.Forward.WorldToV3Rotated()
                    );
                }

                StageStatsProvider.ManagerVac.TryStartFuelFlowSimulationJob();
            }
            else
            {
                if (_needsRebuild) { StageStatsProvider.ManagerAtmo.Build(v); _needsRebuild = false; }
                StageStatsProvider.ManagerAtmo.DVLinearThrust = dvLinearThrust;
                StageStatsProvider.ManagerAtmo.SetConditions(v.atmDensity / 1.225, v.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres, v.mach);

                if (statsModule != null && statsModule.VesselState != null)
                {
                    StageStatsProvider.ManagerAtmo.SetInitial(
                        statsModule.VesselState.Time,
                        statsModule.VesselState.OrbitalPosition.WorldToV3Rotated(),
                        statsModule.VesselState.OrbitalVelocity.WorldToV3Rotated(),
                        statsModule.VesselState.Forward.WorldToV3Rotated()
                    );
                }

                StageStatsProvider.ManagerAtmo.TryStartFuelFlowSimulationJob();

                // Confirm stabilization once a complete, verified data-rich pass has resolved
                if (StageStatsProvider.AtmoStats != null && StageStatsProvider.AtmoStats.Count > 0)
                {
                    _hasCompletedInitialBaseline = true;
                }
            }
        }

        private void OnDestroy()
        {
            GameEvents.StageManager.OnGUIStageSequenceModified.Remove(OnStructuralChange);
            GameEvents.onStageActivate.Remove(OnStageActivated);
            //GameEvents.onPartDie.Remove(OnPartDestroyed);
            GameEvents.onVesselWasModified.Remove(OnVesselStructureChanged);
        }

        private void OnStructuralChange() => _needsRebuild = true;
        private void OnStageActivated(int s) => OnStructuralChange();
        private void OnPartDestroyed(Part p) => OnStructuralChange();
        private void OnVesselStructureChanged(Vessel data)
        {
            if (data == FlightGlobals.ActiveVessel) OnStructuralChange();
        }
    }
}

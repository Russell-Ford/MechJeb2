extern alias JetBrainsAnnotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using MechJebLib.FuelFlowSimulation;
using MechJebLib.Primitives;
using MechJebLib.Utils;
using MechJebLibBindings;
using MechJebLibBindings.FuelFlowSimulation;
using Unity.Profiling;
using Debug = UnityEngine.Debug;

namespace MuMech
{
    public class MechJebModuleStageStats : ComputerModule
    {
        [ToggleInfoItem("#MechJeb_DVincludecosinelosses", InfoItem.Category.Thrust, showInEditor = true)] //ΔV include cosine losses
        public readonly bool DVLinearThrust = true;

        public CelestialBody EditorBody;
        public bool LiveSLT = true;
        public double AltSLT = 0;
        public double Mach = 0;

        private int _vabRebuildTimer = 1;

        public readonly List<FuelStats> AtmoStats = new List<FuelStats>();
        public readonly List<FuelStats> VacStats = new List<FuelStats>();
        public double AtmoT, VacT;
        public V3 AtmoR, VacR;
        public V3 AtmoV, VacV;
        public V3 AtmoU, VacU;

        public MechJebModuleStageStats(MechJebCore core) : base(core)
        {
            Enabled = true;
        }

        private bool _vesselModified = true;

        protected override void OnModuleEnabled() => _vesselModified = true;

        protected override void OnModuleDisabled()
        {
            _vesselManagerAtmo.Release();
            _vesselManagerVac.Release();
        }

        private readonly SimVesselManager _vesselManagerAtmo = new SimVesselManager("StageStatsAtmo");
        private readonly SimVesselManager _vesselManagerVac = new SimVesselManager("StageStatsVac");

        public override void OnFixedUpdate() => GetResults();

        public override void OnUpdate() => GetResults();

        private static ProfilerMarker _newRunSimulationProfile = new ProfilerMarker("RunSimulation");
        private static ProfilerMarker _newBuildProfile = new ProfilerMarker("Build");
        private static ProfilerMarker _newUpdateProfile = new ProfilerMarker("Update");
        private static ProfilerMarker _newVacProfile = new ProfilerMarker("Vac");
        private static ProfilerMarker _newAtmoProfile = new ProfilerMarker("Atmo");

        private StageStatsUnityDriver _unityDriver;

        private bool _runAlternateTick = false;

        /// <summary>
        ///     Invoked by the unsuppressed Unity driver every graphic frame.
        ///     Alternates stream dispatches to prevent physical cache contention between threads.
        /// </summary>
        public void DriverUpdate()
        {
            GetResults();

            // Guard clause to ensure previous passes are clear
            if (!SimulationReady())
                return;

            // Scene and validation guard checks
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorBody is null) return;
            }
            else
            {
                if (Vessel is null) return;
            }

            _runAlternateTick = !_runAlternateTick;

            if (_runAlternateTick)
            {
                // Odd Frame: Isolate and execute the Vacuum stream exclusively
                RunVacuumSimulationOnly();
            }
            else
            {
                // Even Frame: Isolate and execute the Atmospheric stream exclusively
                RunAtmosphericSimulationOnly();
            }
        }


        private void GetResults()
        {
            // --- Atmospheric Stream Processing ---
            var atmoState = _vesselManagerAtmo.FuelFlowSimulation.State;

            if (atmoState == PersistentAsyncJob.JobState.Completed)
            {
                AtmoStats.Clear();
                foreach (FuelStats item in _vesselManagerAtmo.FuelFlowSimulation.Segments)
                    AtmoStats.Add(item);

                AtmoT = _vesselManagerAtmo.T;
                AtmoR = _vesselManagerAtmo.R;
                AtmoV = _vesselManagerAtmo.V;
                AtmoU = _vesselManagerAtmo.U;

                if (!_vesselManagerAtmo.FuelFlowSimulation.TryMarkReady())
                    Debug.LogWarning("[MechJebModuleStageStats] Delayed resetting atmo stream; worker is busy.");
            }
            else if (atmoState == PersistentAsyncJob.JobState.Faulted)
            {
                Debug.Log("[MechJebModuleStageStats] atmo stats failed");
                if (_vesselManagerAtmo.FuelFlowSimulation.Exception != null)
                    Debug.Log(_vesselManagerAtmo.FuelFlowSimulation.Exception);

                if (!_vesselManagerAtmo.FuelFlowSimulation.TryMarkReady())
                    Debug.LogWarning("[MechJebModuleStageStats] Delayed resetting atmo stream after fault; worker is busy.");
            }

            // --- Vacuum Stream Processing ---
            var vacState = _vesselManagerVac.FuelFlowSimulation.State;

            if (vacState == PersistentAsyncJob.JobState.Completed)
            {
                VacStats.Clear();
                foreach (FuelStats item in _vesselManagerVac.FuelFlowSimulation.Segments)
                    VacStats.Add(item);

                VacT = _vesselManagerVac.T;
                VacR = _vesselManagerVac.R;
                VacV = _vesselManagerVac.V;
                VacU = _vesselManagerVac.U;

                if (!_vesselManagerVac.FuelFlowSimulation.TryMarkReady())
                    Debug.LogWarning("[MechJebModuleStageStats] Delayed resetting vac stream; worker is busy.");
            }
            else if (vacState == PersistentAsyncJob.JobState.Faulted)
            {
                Debug.Log("[MechJebModuleStageStats] vac stats failed");
                if (_vesselManagerVac.FuelFlowSimulation.Exception != null)
                    Debug.Log(_vesselManagerVac.FuelFlowSimulation.Exception);

                if (!_vesselManagerVac.FuelFlowSimulation.TryMarkReady())
                    Debug.LogWarning("[MechJebModuleStageStats] Delayed resetting vac stream after fault; worker is busy.");
            }
        }


        private void RunSimulation()
        {
            using ProfilerMarker.AutoScope auto = _newRunSimulationProfile.Auto();

            CelestialBody simBody = HighLogic.LoadedSceneIsEditor ? EditorBody : Vessel.mainBody;

            double staticPressureKpa = HighLogic.LoadedSceneIsEditor || !LiveSLT
                ? simBody.atmosphere ? simBody.GetPressure(AltSLT) : 0
                : Vessel.staticPressurekPa;
            double atmDensity = (HighLogic.LoadedSceneIsEditor || !LiveSLT
                ? simBody.GetDensity(simBody.GetPressure(AltSLT), simBody.GetTemperature(0))
                : Vessel.atmDensity) / 1.225;
            double mach = HighLogic.LoadedSceneIsEditor ? Mach : Vessel.mach;

            if (_vesselModified || HighLogic.LoadedSceneIsEditor)
            {
                using ProfilerMarker.AutoScope auto2 = _newBuildProfile.Auto();

                IShipconstruct v = HighLogic.LoadedSceneIsEditor ? (IShipconstruct)EditorLogic.fetch.ship : Vessel;
                _vesselManagerAtmo.Build(v);
                _vesselManagerVac.Build(v);
                _vesselModified = false;
            }
            else
            {
                using ProfilerMarker.AutoScope auto2 = _newUpdateProfile.Auto();

                _vesselManagerAtmo.Update();
                _vesselManagerVac.Update();
            }

            using (_newVacProfile.Auto())
            {
                _vesselManagerVac.DVLinearThrust = DVLinearThrust;
                _vesselManagerVac.SetConditions(0, 0, 0);
                _vesselManagerVac.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());

                // Gracefully log a warning and return if the vacuum thread is still busy, 
                // preventing an engine lockup or mod crash.
                if (!_vesselManagerVac.TryStartFuelFlowSimulationJob())
                {
                    Debug.LogWarning("[MechJebModuleStageStats] Overlapping simulation pass skipped: Vac thread is still working.");
                    return;
                }
            }

            using (_newAtmoProfile.Auto())
            {
                _vesselManagerAtmo.DVLinearThrust = DVLinearThrust;
                _vesselManagerAtmo.SetConditions(atmDensity, staticPressureKpa * PhysicsGlobals.KpaToAtmospheres, mach);
                _vesselManagerAtmo.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());

                // Mirror the non-disruptive return guard for the atmospheric thread pass
                if (!_vesselManagerAtmo.TryStartFuelFlowSimulationJob())
                {
                    Debug.LogWarning("[MechJebModuleStageStats] Overlapping simulation pass skipped: Atmo thread is still working.");
                    return;
                }
            }
        }

        private void RunVacuumSimulationOnly()
        {
            using ProfilerMarker.AutoScope auto = _newRunSimulationProfile.Auto();

            // Perform structural builds or updates before launching the job if needed
            if (_vesselModified || HighLogic.LoadedSceneIsEditor)
            {
                using ProfilerMarker.AutoScope auto2 = _newBuildProfile.Auto();
                IShipconstruct v = HighLogic.LoadedSceneIsEditor ? (IShipconstruct)EditorLogic.fetch.ship : Vessel;

                _vesselManagerVac.Build(v);
                _vesselModified = false;
            }
            else
            {
                using ProfilerMarker.AutoScope auto2 = _newUpdateProfile.Auto();
                _vesselManagerVac.Update();
            }

            using (_newVacProfile.Auto())
            {
                _vesselManagerVac.DVLinearThrust = DVLinearThrust;
                _vesselManagerVac.SetConditions(0, 0, 0);
                _vesselManagerVac.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());

                if (!_vesselManagerVac.TryStartFuelFlowSimulationJob())
                {
                    Debug.LogWarning("[MechJebModuleStageStats] Overlapping Vacuum simulation pass skipped: thread busy.");
                }
            }
        }

        private void RunAtmosphericSimulationOnly()
        {
            using ProfilerMarker.AutoScope auto = _newRunSimulationProfile.Auto();

            CelestialBody simBody = HighLogic.LoadedSceneIsEditor ? EditorBody : Vessel.mainBody;

            double staticPressureKpa = HighLogic.LoadedSceneIsEditor || !LiveSLT
                ? simBody.atmosphere ? simBody.GetPressure(AltSLT) : 0
                : Vessel.staticPressurekPa;
            double atmDensity = (HighLogic.LoadedSceneIsEditor || !LiveSLT
                ? simBody.GetDensity(simBody.GetPressure(AltSLT), simBody.GetTemperature(0))
                : Vessel.atmDensity) / 1.225;
            double mach = HighLogic.LoadedSceneIsEditor ? Mach : Vessel.mach;

            if (_vesselModified || HighLogic.LoadedSceneIsEditor)
            {
                using ProfilerMarker.AutoScope auto2 = _newBuildProfile.Auto();
                IShipconstruct v = HighLogic.LoadedSceneIsEditor ? (IShipconstruct)EditorLogic.fetch.ship : Vessel;

                _vesselManagerAtmo.Build(v);
                _vesselModified = false;
            }
            else
            {
                using ProfilerMarker.AutoScope auto2 = _newUpdateProfile.Auto();
                _vesselManagerAtmo.Update();
            }

            using (_newAtmoProfile.Auto())
            {
                _vesselManagerAtmo.DVLinearThrust = DVLinearThrust;
                _vesselManagerAtmo.SetConditions(atmDensity, staticPressureKpa * PhysicsGlobals.KpaToAtmospheres, mach);
                _vesselManagerAtmo.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());

                if (!_vesselManagerAtmo.TryStartFuelFlowSimulationJob())
                {
                    Debug.LogWarning("[MechJebModuleStageStats] Overlapping Atmospheric simulation pass skipped: thread busy.");
                }
            }
        }


        private void StartSimulation()
        {
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (_vabRebuildTimer > 0)
                {
                    PartSet.BuildPartSets(EditorLogic.fetch.ship.parts, null);
                    _vabRebuildTimer--;
                    _vesselModified = true;
                }
            }
            else
                Vessel.UpdateResourceSetsIfDirty();

            RunSimulation();
        }

        private bool SimulationReady() => _vesselManagerAtmo.FuelFlowSimulation.IsReady && _vesselManagerVac.FuelFlowSimulation.IsReady;

        private readonly Stopwatch _stopwatch = new Stopwatch();

        private void TryStartSimulation()
        {
            // Our ultimate safety check: if either thread is still running its loop pass,
            // we exit immediately without allocating anything or disrupting the active run.
            if (!SimulationReady())
                return;

            // Maintain scene validation checks to prevent NullReferenceExceptions during loading sequences
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorBody is null) return;
            }
            else
            {
                if (Vessel is null) return;
            }

            // Immediately invoke the simulation loop at the absolute maximum speed allowed 
            // by your background thread execution duration.
            StartSimulation();
        }

        public override void OnStart(PartModule.StartState state)
        {
            // Instantly spawn our independent engine hook on the active GameObject container
            if (_unityDriver == null)
            {
                _unityDriver = HighLogic.LoadedSceneIsEditor
                    ? EditorLogic.fetch.gameObject.AddComponent<StageStatsUnityDriver>()
                    : Vessel.gameObject.AddComponent<StageStatsUnityDriver>();

                _unityDriver.Initialize(this);
            }

            GameEvents.onVesselStandardModification.Add(onVesselStandardModification);
            GameEvents.StageManager.OnGUIStageSequenceModified.Add(OnGUIStageSequenceModified);
            if (HighLogic.LoadedSceneIsEditor)
            {
                GameEvents.onEditorShipModified.Add(OnEditorShipModified);
                GameEvents.onPartCrossfeedStateChange.Add(OnPartCrossfeedStateChange);
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            // Destroy the proxy component to avoid dangling memory leaks across scene switches
            if (_unityDriver != null)
            {
                UnityEngine.Object.Destroy(_unityDriver);
                _unityDriver = null!;
            }

            _vesselManagerAtmo.Dispose();
            _vesselManagerVac.Dispose();

            GameEvents.onVesselStandardModification.Remove(onVesselStandardModification);
            GameEvents.StageManager.OnGUIStageSequenceModified.Remove(OnGUIStageSequenceModified);
            GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
            GameEvents.onPartCrossfeedStateChange.Remove(OnPartCrossfeedStateChange);
        }

        private void OnPartCrossfeedStateChange(Part data)
        {
            _vesselModified = true;
            _vabRebuildTimer = 2;
        }

        private void OnEditorShipModified(ShipConstruct data)
        {
            _vesselModified = true;
            _vabRebuildTimer = 2;
        }

        private void OnGUIStageSequenceModified() => _vesselModified = true;

        private void onVesselStandardModification(Vessel data) => _vesselModified = true;

        public void RequestUpdate()
        {
            //GetResults();

            //TryStartSimulation();
        }
    }
}

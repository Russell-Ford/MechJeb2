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

        //public override void OnFixedUpdate() => GetResults();

        //public override void OnUpdate() => GetResults();

        private static ProfilerMarker _newRunSimulationProfile = new ProfilerMarker("RunSimulation");
        private static ProfilerMarker _newBuildProfile = new ProfilerMarker("Build");
        private static ProfilerMarker _newUpdateProfile = new ProfilerMarker("Update");
        private static ProfilerMarker _newVacProfile = new ProfilerMarker("Vac");
        private static ProfilerMarker _newAtmoProfile = new ProfilerMarker("Atmo");

        private StageStatsUnityDriver _unityDriver;

        private bool _runAlternateTick = false;

        private readonly List<FuelStats> _atmoBuffer = new List<FuelStats>();
        private readonly List<FuelStats> _vacBuffer = new List<FuelStats>();

        /// <summary>
        ///     Invoked by the driver proxy exclusively on every graphic render frame.
        ///     Maximizes FPS by decoupling calculations completely from physics time warp ticks.
        /// </summary>
        public void DriverUpdate()
        {
            // Always harvest completed thread data instantly on the graphics frame pass
            GetResults();

            // Scene validation guards
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorBody is null) return;
            }
            else
            {
                if (Vessel is null) return;
            }

            ExecuteStaggeredSimulation();
        }

       

        /// <summary>
        ///     Core orchestration logic that safely handles the alternating frame runs.
        /// </summary>
        private void ExecuteStaggeredSimulation()
        {
            _runAlternateTick = !_runAlternateTick;

            if (_runAlternateTick)
            {
                if (_vesselManagerVac.FuelFlowSimulation.IsReady)
                {
                    RunVacuumSimulationOnly();
                }
            }
            else
            {
                if (_vesselManagerAtmo.FuelFlowSimulation.IsReady)
                {
                    RunAtmosphericSimulationOnly();
                }
            }
        }


        private void GetResults()
        {
            // --- Atmospheric Stream Processing ---
            var atmoState = _vesselManagerAtmo.FuelFlowSimulation.State;

            if (atmoState == PersistentAsyncJob.JobState.Completed)
            {
                // Harvest data into the hidden scratch buffer first
                _atmoBuffer.Clear();
                foreach (FuelStats item in _vesselManagerAtmo.FuelFlowSimulation.Segments)
                    _atmoBuffer.Add(item);

                // Atomic Swap: Downstream modules reading AtmoStats will never catch an empty window
                lock (AtmoStats)
                {
                    AtmoStats.Clear();
                    AtmoStats.AddRange(_atmoBuffer);
                }

                AtmoT = _vesselManagerAtmo.T;
                AtmoR = _vesselManagerAtmo.R;
                AtmoV = _vesselManagerAtmo.V;
                AtmoU = _vesselManagerAtmo.U;

                _vesselManagerAtmo.FuelFlowSimulation.TryMarkReady();
            }
            else if (atmoState == PersistentAsyncJob.JobState.Faulted)
            {
                Debug.Log("[MechJebModuleStageStats] atmo stats failed");
                if (_vesselManagerAtmo.FuelFlowSimulation.Exception != null)
                    Debug.Log(_vesselManagerAtmo.FuelFlowSimulation.Exception);

                _vesselManagerAtmo.FuelFlowSimulation.TryMarkReady();
            }

            // --- Vacuum Stream Processing ---
            var vacState = _vesselManagerVac.FuelFlowSimulation.State;

            if (vacState == PersistentAsyncJob.JobState.Completed)
            {
                // Harvest data into the hidden scratch buffer first
                _vacBuffer.Clear();
                foreach (FuelStats item in _vesselManagerVac.FuelFlowSimulation.Segments)
                    _vacBuffer.Add(item);

                // Atomic Swap: Downstream modules reading VacStats will never catch an empty window
                lock (VacStats)
                {
                    VacStats.Clear();
                    VacStats.AddRange(_vacBuffer);
                }

                VacT = _vesselManagerVac.T;
                VacR = _vesselManagerVac.R;
                VacV = _vesselManagerVac.V;
                VacU = _vesselManagerVac.U;

                _vesselManagerVac.FuelFlowSimulation.TryMarkReady();
            }
            else if (vacState == PersistentAsyncJob.JobState.Faulted)
            {
                Debug.Log("[MechJebModuleStageStats] vac stats failed");
                if (_vesselManagerVac.FuelFlowSimulation.Exception != null)
                    Debug.Log(_vesselManagerVac.FuelFlowSimulation.Exception);

                _vesselManagerVac.FuelFlowSimulation.TryMarkReady();
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

                _vesselManagerVac.TryStartFuelFlowSimulationJob();
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

                _vesselManagerAtmo.TryStartFuelFlowSimulationJob();
            }
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
            //empty stub for backwards compat
            //GetResults();

            //TryStartSimulation();
        }
    }
}

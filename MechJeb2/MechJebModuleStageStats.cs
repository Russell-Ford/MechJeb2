extern alias JetBrainsAnnotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using MechJebLib.FuelFlowSimulation;
using MechJebLib.Primitives;
using MechJebLibBindings;
using MechJebLibBindings.FuelFlowSimulation;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MuMech
{
    public class MechJebModuleStageStats : ComputerModule
    {
        public MechJebModuleStageStats(MechJebCore core) : base(core)
        {
            Enabled = true;
        }
        //public bool LiveTracking = true; always live tracking now :)

        [ToggleInfoItem("#MechJeb_DVincludecosinelosses", InfoItem.Category.Thrust, showInEditor = true)] //ΔV include cosine losses
        public readonly bool DVLinearThrust = true;
        

        public CelestialBody EditorBody;
        public bool LiveSLT = true;
        public double AltSLT = 0;
        public double Mach = 0;
        public int RefreshInterval
        {
            get
            {
                return _refreshInterval;
            }
            set
            {
                if(value < minRefreshInterval) _refreshInterval = minRefreshInterval;
            }
        }
        

        public readonly List<FuelStats> AtmoStats = new List<FuelStats>();
        public readonly List<FuelStats> VacStats = new List<FuelStats>();
        public double AtmoT, VacT;
        public V3 AtmoR, VacR;
        public V3 AtmoV, VacV;
        public V3 AtmoU, VacU;

        private int minRefreshInterval = 10; //target double ksp's rate as minimum and match the UI for now
        private int _refreshInterval = 10; //milliseconds
        private bool _vesselModified = true;
        //private float _previousThrottleSetting = 0f; idk if this should be implemented

        private readonly SimVesselManager _vesselManagerAtmo = new SimVesselManager(); //holds our Atmo thread (lies, just makes a new one)
        private int atmoSimCountAfterRebuild = 0;
        private readonly SimVesselManager _vesselManagerVac = new SimVesselManager(); //holds our vac thread (more lies)
        private int vacSimCountAfterRebuild = 0; //idk if we need these counters

        /// <summary>
        /// *****************************************************************************************************************************************
        // OnUpdate is called on each frame, meaning we can expect the following rates in fps/ms/s
        // 360 FPS = 2.778~ ms = .0028s (Giga Gamer monitor)
        // 180 FPS = 5.556~ ms = .0055s (Ksp stock max)
        // 120 FPS = 8.333~ ms = .0083s (gamer monitor)
        // 60 FPS = 16.667~ ms = .0167s (avg monitor)
// ******* 50 FPS = 20.000~ ms = .02s (ksp 1x physics speed) *******
        // 30 FPS = 33.333~ ms = .0333s (playable)
        // 10 FPS = 100~ ms    = .1s (border-line unplayable)
        // 5 FPS  = 200~ ms    = .2s (you're just simulating at this point instead of playing)
        // There's no reason to try for granularity beyond what the game allows. We should be trying to predict the output on OUR next frame.
        // This refactor ties our polling to the fps instead of the physics engine so that we will poll slower.
        // If the sim MUST be perfect, then we must tie ourselves to the physics engine and force the user to take the FPS hit.
        // If good enough is good enough (which it seems like that's the case), then we should let the user decide the poll rate.
        // I'd leave this here for anyone to stumble upon and think about what it means to bind ourselves to onFixedUpdate
        // *****************************************************************************************************************************************
        // It is possible to create a toggle for the user to choose OnFixedUpdate, but that's beyond what I'm doing now.
        // If you insist on binding to OnFixedUpdate, you've been warned.
        //public override void OnFixedUpdate() => GetResults();
        /// </summary>
        public override void OnUpdate() {
            // if our user defined limit is being surpassed, don't run a sim. old data will suffice.
            if (_moduleStopwatch.ElapsedMilliseconds < RefreshInterval)
                return;

            // our limit isn't surpassed, let's check if our sims finished, quickly yoink the data, start another
            // then sleep for user defined length in ms
            tryStartVacSim();
            tryStartAtmoSim();
        }
        //unity forces us to expose these. need an abstraction layer to hide them
        public override void OnStart(PartModule.StartState state)
        {
            Debug.Log("[MechJebModuleStageStats] started. Registering shared listeners");
            OnDestroy(); //ensure we don't double register listeners until we know for sure that we aren't
            GameEvents.onVesselStandardModification.Add(onVesselStandardModification);
            GameEvents.StageManager.OnGUIStageSequenceModified.Add(OnGUIStageSequenceModified);
            GameEvents.onStageActivate.Add(onStageActivate);
            if (HighLogic.LoadedSceneIsEditor)
            {
                Debug.Log("[MechJebModuleStageStats] started in editor. Registering editor listener");
                GameEvents.onEditorShipModified.Add(OnEditorShipModified);
                //GameEvents.onPartCrossfeedStateChange.Add(OnPartCrossfeedStateChange);
            }
        }

        public override void OnDestroy()
        {
            cleanupListeners();
            _vesselManagerAtmo.Release();
            _vesselManagerVac.Release();
            //GameEvents.onPartCrossfeedStateChange.Remove(OnPartCrossfeedStateChange);
        }

        private void cleanupListeners()
        {
            GameEvents.onVesselStandardModification.Remove(onVesselStandardModification);
            GameEvents.StageManager.OnGUIStageSequenceModified.Remove(OnGUIStageSequenceModified);
            GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
            GameEvents.onStageActivate.Remove(onStageActivate);
        }

        protected override void OnModuleEnabled() {
            Debug.Log("[MechJebModuleStageStats] enabled. Before or after OnStart?");
        }

        protected override void OnModuleDisabled()
        {
            Debug.Log("[MechJebModuleStageStats] disabled. Can we re-enable?");
            //_vesselManagerAtmo.Release();
            //_vesselManagerVac.Release();
        }

        // TODO: verify this is still tracked and we update properly onpartcrossfeedstatechange
        // private void OnPartCrossfeedStateChange(Part data)
        // {
        //     _vesselModified = true;
        //     TryStartSimulation();
        //     //_vabRebuildTimer = 2;
        // }

        private void OnGUIStageSequenceModified() {
            Debug.Log("[MechJebModuleStageStats] GUIStageSequenceModified. Rebuilding...");
            RebuildVesselAndTryStartSims(); //whether editor or vab we have to rebuild
            //could just build a function in SimVesselUpdater that runs a sim starting from highest stage modified instead
        }

        private void onVesselStandardModification(Vessel data) {
            Debug.Log("[MechJebModuleStageStats] onVesselStandardModification fired. Rebuilding...");
            RebuildVesselAndTryStartSims();
        }
        private void onStageActivate(int stageNum) //idk if this is the stageNum we were on or the stageNum we're going to.
        {
            Debug.Log("[MechJebModuleStageStats] onStageActivate fired. Rebuilding...");
            RebuildVesselAndTryStartSims(); //idk if the updater can handle this rn.. we only have to do this if user stages early.
            //could just call stage on the SimVessel?
        }

        private void OnEditorShipModified(ShipConstruct editorVessel)
        {
            Debug.Log("[MechJebModuleStageStats] OnEditorShipModified fired. Rebuilding...");
            // This LOOKS the exact same as calling RebuildVesselAndTryStartSims().
            // Unity just gave us the pointer to the vessel on the stack. Let's not go searching for it.
            _vesselManagerAtmo.Build(editorVessel);
            _vesselManagerVac.Build(editorVessel);
            _vesselModified = true;
            vacSimCountAfterRebuild = 0;
            atmoSimCountAfterRebuild = 0;
            tryStartAtmoSim();
            tryStartVacSim();
        }

       
        private bool tryStartVacSim()
        {
            if(_vesselManagerVac.TryGetResults())
            {
                //vesselManager told us it's hot and ready, let's go get it.
                getVacSimResults();
                //check flight first because that's where we need live calcs (very minor optimization)
                if(HighLogic.LoadedSceneIsFlight)
                {
                    StartVacSimulation(); //gogogo integrate (in discrete ticks) as fast as we can without slowing the game
                    // we can check if the throttle changed here, but why? just gogogo
                } else if(HighLogic.LoadedSceneIsEditor && _vesselModified)
                {
                    if(vacSimCountAfterRebuild > 2)
                    {
                        _vesselModified = false;
                        vacSimCountAfterRebuild = 0;
                    } else
                    {
                        StartVacSimulation();
                        vacSimCountAfterRebuild++;
                    }
                    
                }

            }
            return false;
        }

        private void getVacSimResults()
        {
            VacStats.Clear();
            foreach (FuelStats item in _vesselManagerVac.Segments)
                VacStats.Add(item);

            VacT = _vesselManagerVac.T;
            VacR = _vesselManagerVac.R;
            VacV = _vesselManagerVac.V;
            VacU = _vesselManagerVac.U;
        }

        private bool tryStartAtmoSim()
        {
            if(_vesselManagerAtmo.TryGetResults())
            {
                getAtmoSimResults();
                //check flight first because that's where we need live calcs (very minor optimization)
                if(HighLogic.LoadedSceneIsFlight)
                {
                    // FIX ME: Real Fuels provides atmDensity in a different format than stock.
                    // we need to ask real fuels for the value and convert it here
                    // or we can check if real fuels is loaded and follow a different call stack into the sim
                    // maybe the sim grabs it already?
                    StartAtmoSimulation(Vessel.atmDensity, Vessel.staticPressurekPa, Vessel.mach);
                } else if(HighLogic.LoadedSceneIsEditor && _vesselModified)
                {
                    if(atmoSimCountAfterRebuild > 2)
                    {
                        Debug.Log($"[MechJebModuleStageStats] atmo hit max rebuilds of 3. time since last: { _stopwatchAtmo.ElapsedMilliseconds}ms. sleeping till next build");
                        _vesselModified = false;
                        atmoSimCountAfterRebuild = 0;
                    } else
                    {
                        CelestialBody simBody = EditorBody;
                        double simKpa = simBody.GetPressure(AltSLT);
                        //we use the stock simBody's atmDensity in the editor.
                        double simAtmDensity = simBody.GetDensity(simBody.GetPressure(AltSLT), simBody.GetTemperature(AltSLT));
                        double simMach = 0;
                        Debug.Log($"[MechJebModuleStageStats] editor atmo rebuild number: { atmoSimCountAfterRebuild }. time since last: { _stopwatchAtmo.ElapsedMilliseconds}ms. sleeping till next build");
                        Debug.Log($"[MechJebModuleStageStats] running atmo sim with: density={ atmoSimCountAfterRebuild }, kpa={simKpa}, mach={simMach}");
                        StartAtmoSimulation(simAtmDensity, simKpa, simMach);
                        atmoSimCountAfterRebuild++;
                    }
                    
                }

            }
            return false;
        }

        private void getAtmoSimResults()
        {
            AtmoStats.Clear();
            foreach (FuelStats item in _vesselManagerAtmo.Segments)
                AtmoStats.Add(item);

            AtmoT = _vesselManagerAtmo.T;
            AtmoR = _vesselManagerAtmo.R;
            AtmoV = _vesselManagerAtmo.V;
            AtmoU = _vesselManagerAtmo.U;
        }

        
        private readonly Stopwatch _moduleStopwatch = Stopwatch.StartNew();
        private readonly Stopwatch _stopwatchVac = Stopwatch.StartNew();
        private readonly Stopwatch _stopwatchAtmo = Stopwatch.StartNew();
        private readonly Stopwatch _stopwatchBuild = Stopwatch.StartNew();
        //private static ProfilerMarker _newRunSimulationProfile = new ProfilerMarker("RunSimulation");
        private static ProfilerMarker _newBuildProfile = new ProfilerMarker("Build");
        //private static ProfilerMarker _newUpdateProfile = new ProfilerMarker("Update");
        private static ProfilerMarker _newVacProfile = new ProfilerMarker("Vac");
        private static ProfilerMarker _newAtmoProfile = new ProfilerMarker("Atmo");

        private void StartVacSimulation()
        {
            using (_newVacProfile.Auto())
            {
                _vesselManagerVac.DVLinearThrust = DVLinearThrust;
                _vesselManagerVac.SetConditions(0, 0, 0);
                _vesselManagerVac.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());
                if (_vesselManagerVac.TryStartFuelFlowSimulationJob())
                {
                    Debug.Log("[MechJebModuleStageStats][Vacuum] Started job. Time since last: " + _stopwatchVac.ElapsedMilliseconds);
                    _stopwatchVac.Restart();
                }
                else
                    Debug.Log($"[MechJebModuleStageStats] Vacuum sim requested too early. Current job time: {_stopwatchVac.ElapsedMilliseconds}ms");
            }
        }

        private void StartAtmoSimulation(double atmDensity, double staticPressureKpa, double mach)
        {
            using (_newAtmoProfile.Auto())
            {
                _vesselManagerAtmo.DVLinearThrust = DVLinearThrust;
                _vesselManagerAtmo.SetConditions(atmDensity, staticPressureKpa * PhysicsGlobals.KpaToAtmospheres, mach);
                _vesselManagerAtmo.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());
                if (_vesselManagerAtmo.TryStartFuelFlowSimulationJob())
                {
                    Debug.Log("[MechJebModuleStageStats][Atmosphere] Started job. Time since last: " + _stopwatchAtmo.ElapsedMilliseconds);
                    _stopwatchAtmo.Restart();
                }
                else
                    Debug.Log($"[MechJebModuleStageStats] Atmo sim requested too early. Current job time: {_stopwatchAtmo.ElapsedMilliseconds}ms");            
            }
        }

        //eventually this shouldn't be needed and we can just call update, but this is a large refactor.
        //pls send pizza and coffee
        private void RebuildVesselAndTryStartSims()
        {
            using (_newBuildProfile.Auto()) //no idea how to use this.. not worth the effort rn.
            {
                Debug.Log($"[MechJebModuleStageStats][RebuildVesselAndTryStartSims] time since last rebuild: { _stopwatchBuild.ElapsedMilliseconds}ms");
                _stopwatchBuild.Restart();
                IShipconstruct v = HighLogic.LoadedSceneIsEditor ? (IShipconstruct)EditorLogic.fetch.ship : Vessel;
                _vesselManagerAtmo.Build(v);
                _vesselManagerVac.Build(v);
                _vesselModified = true;
                vacSimCountAfterRebuild = 0;
                atmoSimCountAfterRebuild = 0;
                Debug.Log($"[MechJebModuleStageStats][RebuildVesselAndTryStartSims] rebuilt in: { _stopwatchBuild.ElapsedMilliseconds}ms. sleeping till next build");
                _stopwatchBuild.Restart();
            }
        }
        // not yet implemented. can just do a full rebuild for now
        // private void UpdateVessels(Vessel vessel)
        // {
        //     _vesselManagerAtmo.Update();
        //     _vesselManagerVac.Update();
        //     _vesselModified = true;
        // }
     




        public void RequestUpdate() //deprecated, but 16 references made this a nightmare
        {
            //GetResults();
            //TryStartSimulation();
        }


        // private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        // private long totalTime = 0;
        // private int numCompletions = -1;


    // private void StartSimulation()
        // {
        //     if (HighLogic.LoadedSceneIsEditor)
        //     {
        //         if (_vabRebuildTimer > 0)
        //         {
        //             PartSet.BuildPartSets(EditorLogic.fetch.ship.parts, null);
        //             _vabRebuildTimer--;
        //             _vesselModified = true;
        //         }
        //     }
        //     else
        //         Vessel.UpdateResourceSetsIfDirty();

        //     RunSimulation();
        // }

        //private bool SimulationReady() => _vesselManagerAtmo.FuelFlowSimulation.IsReady && _vesselManagerVac.FuelFlowSimulation.IsReady;

// private void GetResults()
//         {
//             if (_vesselManagerAtmo.FuelFlowSimulation.IsStopped)
//             {
//                 if (_vesselManagerAtmo.FuelFlowSimulation.IsCompleted)
//                 {   
//                     AtmoStats.Clear();
//                     foreach (FuelStats item in _vesselManagerAtmo.FuelFlowSimulation.Segments)
//                         AtmoStats.Add(item);

//                     AtmoT = _vesselManagerAtmo.T;
//                     AtmoR = _vesselManagerAtmo.R;
//                     AtmoV = _vesselManagerAtmo.V;
//                     AtmoU = _vesselManagerAtmo.U;
//                 }
//                 else
//                 {
//                     Debug.Log("[MechJebModuleStageStats] atmo stats failed");
//                     if (_vesselManagerAtmo.FuelFlowSimulation.Exception != null)
//                         Debug.Log(_vesselManagerAtmo.FuelFlowSimulation.Exception);
//                 }

//                 if (!_vesselManagerAtmo.FuelFlowSimulation.TryMarkReady())
//                     throw new Exception("[MechJebModuleStageStats] Tried to mark a running atmo stage stats as ready.");
//                 else
//                 {
//                     if(numCompletions == -1) {
//                         stopwatch.Restart();
//                         numCompletions++;
//                     } 
//                     else
//                     {
//                         long timeSinceLast = stopwatch.ElapsedMilliseconds;
//                         numCompletions++;
//                         totalTime += timeSinceLast;
//                         Debug.Log("Sim completed, time since last: " + timeSinceLast.ToString() + 
//                         ",   average: " + (totalTime / numCompletions).ToString()
//                         +", totalTime: " + totalTime.ToString()
//                         + ", numCompletions: " + numCompletions.ToString());
//                         stopwatch.Restart();
//                     }
                    
//                 }
//             }

//             if (_vesselManagerVac.FuelFlowSimulation.IsStopped)
//             {
//                 if (_vesselManagerVac.FuelFlowSimulation.IsCompleted)
//                 {
//                     VacStats.Clear();
//                     foreach (FuelStats item in _vesselManagerVac.FuelFlowSimulation.Segments)
//                         VacStats.Add(item);

//                     VacT = _vesselManagerVac.T;
//                     VacR = _vesselManagerVac.R;
//                     VacV = _vesselManagerVac.V;
//                     VacU = _vesselManagerVac.U;
//                 }
//                 else
//                 {
//                     Debug.Log("[MechJebModuleStageStats] vac stats failed");
//                     if (_vesselManagerVac.FuelFlowSimulation.Exception != null)
//                         Debug.Log(_vesselManagerVac.FuelFlowSimulation.Exception);
//                 }

//                 if (!_vesselManagerVac.FuelFlowSimulation.TryMarkReady())
//                     throw new Exception("[MechJebModuleStageStats] Tried to mark a running vac stage stats as ready.");
//             }
//         }

        
        // private void RunSimulation()
        // {
        //     using ProfilerMarker.AutoScope auto = _newRunSimulationProfile.Auto();

        //     CelestialBody simBody = HighLogic.LoadedSceneIsEditor ? EditorBody : Vessel.mainBody;

        //     double staticPressureKpa = HighLogic.LoadedSceneIsEditor || !LiveSLT
        //         ? simBody.atmosphere ? simBody.GetPressure(AltSLT) : 0
        //         : Vessel.staticPressurekPa;
        //     double atmDensity = (HighLogic.LoadedSceneIsEditor || !LiveSLT
        //         ? simBody.GetDensity(simBody.GetPressure(AltSLT), simBody.GetTemperature(0))
        //         : Vessel.atmDensity) / 1.225;
        //     double mach = HighLogic.LoadedSceneIsEditor ? Mach : Vessel.mach;

        //     // XXX: we do a rebuild every time in the editor because apparently I don't know the right callbacks/magic to
        //     // make rebuilding only on reconfiguration work.
        //     if (_vesselModified || HighLogic.LoadedSceneIsEditor)
        //     {
        //         using ProfilerMarker.AutoScope auto2 = _newBuildProfile.Auto();

        //         IShipconstruct v = HighLogic.LoadedSceneIsEditor ? (IShipconstruct)EditorLogic.fetch.ship : Vessel;
        //         _vesselManagerAtmo.Build(v);
        //         _vesselManagerVac.Build(v);
        //         _vesselModified = false;
        //     }
        //     else
        //     {
        //         using ProfilerMarker.AutoScope auto2 = _newUpdateProfile.Auto();

        //         _vesselManagerAtmo.Update();
        //         _vesselManagerVac.Update();
        //     }

            // using (_newVacProfile.Auto())
            // {
            //     _vesselManagerVac.DVLinearThrust = DVLinearThrust;
            //     _vesselManagerVac.SetConditions(0, 0, 0);
            //     _vesselManagerVac.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
            //         VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());
            //     if (!_vesselManagerVac.TryStartFuelFlowSimulationJob())
            //         throw new Exception("[MechJebModuleStageStats] could not start vac stats job");
            //     else
            //         _stopwatch.Restart();
            // }

            // using (_newAtmoProfile.Auto())
            // {
            //     _vesselManagerAtmo.DVLinearThrust = DVLinearThrust;
            //     _vesselManagerAtmo.SetConditions(atmDensity, staticPressureKpa * PhysicsGlobals.KpaToAtmospheres, mach);
            //     _vesselManagerAtmo.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
            //         VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());
            //     //_vesselManagerAtmo.PrintVessel();
            //     if (!_vesselManagerAtmo.TryStartFuelFlowSimulationJob())
            //         throw new Exception("[MechJebModuleStageStats] could not start atmo stats job");
            //     else
            //         _stopwatch.Restart();
            // }
//         }
// // private void TryStartSimulation()
//         {
//             CelestialBody simBody = HighLogic.LoadedSceneIsEditor ? EditorBody : Vessel.mainBody;
//             double staticPressureKpa;
//             double atmDensity;
//             double mach;

            

//             if(HighLogic.LoadedSceneIsEditor)
//             {
                
//                 mach = Mach;
//             }
//             else if(HighLogic.LoadedSceneIsFlight)
//             {
//                 staticPressureKpa = Vessel.staticPressurekPa;
//                 mach = Vessel.mach;
//                 atmDensity = Vessel.atmDensity;
//             }
//             else
//             {
//                 Debug.Log("[MechJebModuleStageStats] reached an invald HighLogic scene. How did we get here?");
//                 return;
//             }

//             atmDensity = atmDensity / 1.225;
//             //if (!SimulationReady())
//              //   return;

//             //the sim finished and it's been refreshInterval since we simulated,

//             if(LiveSLT)
//             {
//                 staticPressureKpa = Vessel.staticPressurekPa;
//             } else if(simBody.atmosphere)
//             {
                
//             } else
//             {
//                 staticPressureKpa = 0;
//             }


//             // if (HighLogic.LoadedSceneIsEditor) // let's make sure we have something to sim on
//             // {
//             //     if (EditorBody is null) return;
//             // }
//             // else
//             // {
//             //     if (Vessel is null) return;
//             // }

//             // StartSimulation(); //everything is good, let's go!
//         }


    }
}

/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MechJebLib.FuelFlowSimulation.PartModules;
using MechJebLib.Utils;
using static MechJebLib.Utils.Statics;
using static System.Math;
using System.Diagnostics;

namespace MechJebLib.FuelFlowSimulation
{
            //FIXME: needs deep cleaning
    public class FuelFlowSimulation : AsyncJob
    {
        public readonly List<FuelStats> VesselSegmentStats = new List<FuelStats>(); //we shouldn't be exposing this, but I don't want to guard it rn
        // should be exposing a true readonly way to access this since the type "List" is mutable by the consumer
        private const int MAX_VESSEL_SEGMENTS = 100; //how would we even get this many segments?
        private readonly List<FuelStats> _vesselSegments = new List<FuelStats>();
        private readonly List<FuelStats> _stageSegments = new List<FuelStats>();
        private FuelStats _currentSegment;
        public bool DVLinearThrust = false; // include cos losses. The original author surely implied "thrust acting in a straight line"
        public bool CosineLoss = true;
        private readonly HashSet<SimPart> _partsWithResourceDrains = new HashSet<SimPart>();
        private readonly HashSet<SimPart> _partsWithRCSDrains = new HashSet<SimPart>();



        //------------------------------------------------DEPRECATED BREAKLINE ---------------------------------------------------------------------------------------------------------
        
        private const int MAXSTEPS = 10_000; // :skull:
        private double _time; //maybe we need a global time? I don't see where it would be used outside of SimulateStage()

        private readonly HashSet<SimPart> _partsWithRCSDrains2 = new HashSet<SimPart>(); //why was this duplicated?!?
        private bool _allocatedFirstSegment; //not needed. C# updates List.Count actively and is just as fast.


        public override void Run(object? o = null)
        {
            if (o == null)
                throw new ArgumentNullException(nameof(o));

            if (!(o is SimVessel vessel))
                throw new ArgumentException("o is not a SimVessel", nameof(o));

            _vesselSegments.Clear();
            SimulateVessel(vessel); //aggressive inlining will optimize this and remove the function calls.

            //the sim is done, quickly hard-copy the segments into our public list
            for(int i = 0; i < _vesselSegments.Count; i++)
            {
                VesselSegmentStats.Add(_vesselSegments[i]); // struct will make this a hard copy
            }

        //------------------------------------------------DEPRECATED BREAKLINE ---------------------------------------------------------------------------------------------------------
            //_time = 0;
            //_segmentsInStage.Clear();
            vessel.MainThrottle = 1.0;

            vessel.ActivateEnginesAndRCS();

            while (vessel.CurrentStage >= 0) // FIXME: should stop mutating vessel.CurrentStage
            // why do we need to stop mutating it? the vessel has been decoupled from ksp at this point
            {
                SimulateStage(vessel);
                ClearResiduals();
                ComputeRcsMaxValues(vessel);
                FinishSegment(vessel);
                vessel.Stage();
            }

            _segmentsInStage.Reverse();

            _partsWithResourceDrains.Clear();
        }

        private void SimulateVessel(SimVessel vessel)
        {
            /* PSEUDO TIME
            We've been passed a SimVessel. Do we trust the ActiveEngines list or verify? We'll trust for now because that's the ideal goal
            If the list is empty, then we have to turn some engines on to get some results
            We have an existing function to check if we're allowed to stage which seems to work flawlessly. Let's utilize that.
            We need to do this at the start of each segment, so let's start the loop

            */
            SimulateInitialSegment(vessel);
            while (vessel.CurrentStage >= 0)
            {
                if(!canStage(vessel)) //we're not allowed to stage. the main reason would be engines are already burning.
                {
                    // if we un-nest this, then we have to double
                    // checks on the while loop because our last stage didn't clean-up properly

                    // we're burning an engine (or would drop an engine we *could* burn)
                
                }
                    
                vessel.Stage();
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SimulateInitialSegment(SimVessel vessel) //was getting hard to put this logic in the main loop
        {
            //things different about the initial segment:
            // 1. We have the latest data from the live vessel.
            // 2. Engines are in a semi-known state. We have a list of active, but not a true list of which can be active.
            //      a. Are we already burning? How many and from what stages?
            //      b. if not burning, when we start this initial burn, will we activate/burn something from a later stage?
            //      b. For example, if the user enables a later stage engine while on the launch pad, the later stage will not
            //      b. enter the current stage. it will stay in its stage and burn from a separate stage (weird ksp edge case)

            //First, let's check if we're commanding a burn.
            if(vessel.MainThrottle > 0)
            {
                for(int i = 0; i < vessel.ActiveEngines.Count; i++)
                {
                    SimModuleEngines engine = vessel.ActiveEngines;
                    
                }
            }
            if(canStage(vessel))
            {
                
            }
            
        }


        private void SimulateSegment(SimVessel vessel)
        {
            
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool canStage(SimVessel vessel) // I need this, but not exactly how it is.. 
        {
            // always stage if all the engines are burned out
            if (vessel.ActiveEngines.Count == 0)
                return true;

            for (int i = 0; i < vessel.ActiveEngines.Count; i++)
            {
                SimModuleEngines e = vessel.ActiveEngines[i];

                if (e.Part.IsSepratron)
                    continue;

                // never stage an active engine
                if (e.Part.DecoupledInStage >= vessel.CurrentStage - 1)
                    return false;

                // never drop fuel that could be used
                if (e.WouldDropAccessibleFuelTank(vessel.CurrentStage - 1))
                    return false;
            }

            // do not trigger a stage that doesn't decouple anything -- until the engines burn out
            if (vessel.PartsRemainingInStage[vessel.CurrentStage - 1].Count == vessel.PartsRemainingInStage[vessel.CurrentStage].Count)
                return false;

            return vessel.CurrentStage > 0;
        }

        private void SimulateRCS(SimVessel vessel, bool max)
        {
            vessel.SaveRcsStatus();
            _partsWithRCSDrains2.Clear();

            double lastmass = vessel.Mass;

            int steps = MAXSTEPS;

            while (true)
            {
                if (steps-- == 0)
                    throw new Exception("FuelFlowSimulation hit max steps of " + MAXSTEPS + " steps in rcs calculations");

                vessel.UpdateRcsStats();
                vessel.UpdateActiveRcs();
                if (vessel.ActiveRcs.Count == 0)
                    break;

                UpdateRcsDrains(vessel);
                double dt = MinimumRcsTimeStep();

                ApplyRcsDrains(dt);
                vessel.HardRecalculateMass();
                FinishRcsSegment(max, dt, lastmass, vessel.Mass, vessel.RcsThrust);
                lastmass = vessel.Mass;
            }

            UnapplyRcsDrains();
            vessel.ResetRcsStatus();
            vessel.HardRecalculateMass();
        }

        private void UnapplyRcsDrains()
        {
            foreach (SimPart p in _partsWithRCSDrains2)
                p.UnapplyRCSDrains();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeRcsMinValues(SimVessel vessel) => SimulateRCS(vessel, false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeRcsMaxValues(SimVessel vessel) => SimulateRCS(vessel, true);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeRcsUllageTime(SimVessel vessel)
        {
            _currentSegment.RcsUllageTime = 0.0;

            if (!vessel.ActiveEngineNeedsUllage()) return;

            // FIXME: read this from RFSettings via reflection
            double translateAxialCoefficientY = 1.5;
            // magic calculation that gives time to ullage up to 0.996 guaranteed ignition
            double a = vessel.RcsThrust / vessel.Mass;
            double rcsUllageTime = 0.35 / (translateAxialCoefficientY * a);

            _currentSegment.RcsUllageTime = rcsUllageTime;
        }
        //rewrote this quick and dirty to implement the throw condition better and remove the awkward active engine check
        // before that PR was merged into dev, we were checking if the thrust in the sim was stable, which obviously it will be
        // because we're the only ones that can influence it!
        // pad spoolup can be ignored (will fix this later) because the vessel is expected start the mission fully fueled with full thrust
        // we only need to calculate spoolup when already flying, but to do it properly we also have to be comparing our results to actual
        private void SimulateStage(SimVessel vessel)
        {
            vessel.HardRecalculateMass();
            vessel.UpdateEngineStats(); //update ISP, FlowMultiplier, MassFlowRate, recalc thrust and consumption rates
            vessel.UpdateActiveEngines(); //clear activeengines, iterate engines left on vessel, verify burn capability, 
            // then add verified back to activeengines
            //after, since ActiveEngines was just sanitized, iterate ActiveEngines, check burn status again

            GetNextSegment(vessel); //prepare to start writing
            ComputeRcsMinValues(vessel); //FIXME: similar to SimulateStage (we're in it right now)

            vessel.UpdateActiveRcs(); //I'm guessing compute heavily mutates this and then sets it back.. focusing on engines for now
            ComputeRcsUllageTime(vessel);

            UpdateResourceDrainsAndResiduals(vessel);

            for (int segments = MAX_SEGMENTS_PER_STAGE; segments > 0; segments--)
            {
                if (AllowedToStage(vessel)) //if we can stage at this point, we're done here.
                    return;

                double dt = MinimumTimeStep(); //misnomer.. we're getting the LARGEST time step possible in here.
                _time += dt; //add this largest possible time step to our running total

                ApplyResourceDrains(dt); //drain the fuel

                vessel.HardRecalculateMass(); //update the mass
                vessel.UpdateEngineStats(); //update the engines
                vessel.UpdateActiveEngines(); //verify and update the engines again
                UpdateResourceDrainsAndResiduals(vessel); //update drains and residuals again
            }
            



            // int activeEngines = vessel.ActiveEngines.Count;

            // for (int steps = MAXSTEPS; steps > 0; steps--)
            // {
                

            //     // if (dt >= 0.02 && activeEngines != vessel.ActiveEngines.Count) //if our max time step is large and our tracked engine count changes
            //     // {
            //     //     //we must have reached the end of a stage or an engine burned out
            //     //     ClearResiduals();
            //     //     ComputeRcsMaxValues(vessel);
            //     //     FinishSegment(vessel);
            //     //     GetNextSegment(vessel);
            //     //     activeEngines = vessel.ActiveEngines.Count;
            //     // }

            //     //_time += dt;
            //     ApplyResourceDrains(dt);

            //     vessel.UpdateMass();
            //     vessel.UpdateEngineStats();
            //     vessel.UpdateActiveEngines();
            //     UpdateResourceDrainsAndResiduals(vessel);
            // }

            throw new Exception($"FuelFlowSimulation hit max segments of {MAX_SEGMENTS_PER_STAGE} steps");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyRcsDrains(double dt)
        {
            foreach (SimPart part in _partsWithRCSDrains)
                part.ApplyRCSDrains(dt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyResourceDrains(double dt)
        {
            foreach (SimPart part in _partsWithResourceDrains)
                part.ApplyResourceDrains(dt);
        }

        private void UpdateRcsDrains(SimVessel vessel)
        {
            foreach (SimPart part in _partsWithRCSDrains)
                part.ClearRCSDrains();

            _partsWithRCSDrains.Clear();

            for (int i = 0; i < vessel.ActiveRcs.Count; i++)
            {
                SimModuleRCS e = vessel.ActiveRcs[i];
                foreach (int resourceId in e.PropellantFlowModes.Keys)
                {
                    switch (e.PropellantFlowModes[resourceId])
                    {
                        case SimFlowMode.NO_FLOW:
                            UpdateRCSDrainsInPart(e.Part, e.ResourceConsumptions[resourceId], resourceId);
                            break;
                        case SimFlowMode.ALL_VESSEL:
                        case SimFlowMode.ALL_VESSEL_BALANCE:
                            UpdateRCSDrainsInParts(vessel.PartsRemainingInStage[vessel.CurrentStage], e.ResourceConsumptions[resourceId],
                                resourceId, false);
                            break;
                        case SimFlowMode.STAGE_PRIORITY_FLOW:
                        case SimFlowMode.STAGE_PRIORITY_FLOW_BALANCE:
                            UpdateRCSDrainsInParts(vessel.PartsRemainingInStage[vessel.CurrentStage], e.ResourceConsumptions[resourceId],
                                resourceId, true);
                            break;
                        case SimFlowMode.STAGE_STACK_FLOW:
                        case SimFlowMode.STAGE_STACK_FLOW_BALANCE:
                        case SimFlowMode.STACK_PRIORITY_SEARCH:
                            UpdateRCSDrainsInParts(e.Part.CrossFeedPartSet, e.ResourceConsumptions[resourceId], resourceId, true);
                            break;
                        case SimFlowMode.NULL:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        private readonly List<SimPart> _sourcesRCS = new List<SimPart>();

        private void UpdateRCSDrainsInParts(IList<SimPart> parts, double resourceConsumption, int resourceId, bool usePriority)
        {
            int maxPriority = int.MinValue;

            _sourcesRCS.Clear();

            for (int i = 0; i < parts.Count; i++)
            {
                SimPart p = parts[i];

                if (!p.TryGetResource(resourceId, out SimResource resource))
                    continue;

                if (resource.Free)
                    continue;

                if (resource.Amount <= resource.ResidualThreshold + p.ResourceRequestRemainingThreshold)
                    continue;

                if (usePriority)
                {
                    if (p.ResourcePriority < maxPriority)
                        continue;

                    if (p.ResourcePriority > maxPriority)
                    {
                        _sourcesRCS.Clear();
                        maxPriority = p.ResourcePriority;
                    }
                }

                _sourcesRCS.Add(p);
            }

            for (int i = 0; i < _sourcesRCS.Count; i++)
                UpdateRCSDrainsInPart(_sourcesRCS[i], resourceConsumption / _sourcesRCS.Count, resourceId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateRCSDrainsInPart(SimPart p, double resourceConsumption, int resourceId)
        {
            _partsWithRCSDrains.Add(p);
            _partsWithRCSDrains2.Add(p);
            p.AddRCSDrain(resourceId, resourceConsumption);
        }

        private void ClearResiduals()
        {
            foreach (SimPart part in _partsWithResourceDrains)
                part.ClearResiduals();
        }

        private void UpdateResourceDrainsAndResiduals(SimVessel vessel)
        {
            foreach (SimPart part in _partsWithResourceDrains)
            {
                part.ClearResourceDrains();
                part.ClearResiduals();
            }

            _partsWithResourceDrains.Clear();

            for (int i = 0; i < vessel.ActiveEngines.Count; i++)
            {
                SimModuleEngines e = vessel.ActiveEngines[i];
                foreach (int resourceId in e.PropellantFlowModes.Keys)
                    switch (e.PropellantFlowModes[resourceId])
                    {
                        case SimFlowMode.NO_FLOW:
                            UpdateResourceDrainsAndResidualsInPart(e.Part, e.ResourceConsumptions[resourceId], resourceId, e.ModuleResiduals);
                            break;
                        case SimFlowMode.ALL_VESSEL:
                        case SimFlowMode.ALL_VESSEL_BALANCE:
                            UpdateResourceDrainsAndResidualsInParts(vessel.PartsRemainingInStage[vessel.CurrentStage],
                                e.ResourceConsumptions[resourceId],
                                resourceId, false, e.ModuleResiduals);
                            break;
                        case SimFlowMode.STAGE_PRIORITY_FLOW:
                        case SimFlowMode.STAGE_PRIORITY_FLOW_BALANCE:
                            UpdateResourceDrainsAndResidualsInParts(vessel.PartsRemainingInStage[vessel.CurrentStage],
                                e.ResourceConsumptions[resourceId],
                                resourceId, true, e.ModuleResiduals);
                            break;
                        case SimFlowMode.STAGE_STACK_FLOW:
                        case SimFlowMode.STAGE_STACK_FLOW_BALANCE:
                        case SimFlowMode.STACK_PRIORITY_SEARCH:
                            UpdateResourceDrainsAndResidualsInParts(e.Part.CrossFeedPartSet, e.ResourceConsumptions[resourceId], resourceId, true,
                                e.ModuleResiduals);
                            break;
                        case SimFlowMode.NULL:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
            }
        }

        private readonly List<SimPart> _sources = new List<SimPart>();

        private void UpdateResourceDrainsAndResidualsInParts(IList<SimPart> parts, double resourceConsumption, int resourceId, bool usePriority,
            double residual)
        {
            int maxPriority = int.MinValue;

            _sources.Clear();

            for (int i = 0; i < parts.Count; i++)
            {
                SimPart p = parts[i];

                if (!p.TryGetResource(resourceId, out SimResource resource))
                    continue;

                if (resource.Free)
                    continue;

                if (resource.Amount <= residual * resource.MaxAmount + p.ResourceRequestRemainingThreshold)
                    continue;

                if (usePriority)
                {
                    if (p.ResourcePriority < maxPriority)
                        continue;

                    if (p.ResourcePriority > maxPriority)
                    {
                        _sources.Clear();
                        maxPriority = p.ResourcePriority;
                    }
                }

                _sources.Add(p);
            }

            for (int i = 0; i < _sources.Count; i++)
                UpdateResourceDrainsAndResidualsInPart(_sources[i], resourceConsumption / _sources.Count, resourceId, residual);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateResourceDrainsAndResidualsInPart(SimPart p, double resourceConsumption, int resourceId, double residual)
        {
            _partsWithResourceDrains.Add(p);
            p.AddResourceDrain(resourceId, resourceConsumption);
            p.UpdateResourceResidual(residual, resourceId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double MinimumRcsTimeStep()
        {
            double maxTime = RCSMaxTime();

            return maxTime < double.MaxValue && maxTime > 0.001 ? maxTime : 0.001;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double RCSMaxTime()
        {
            double maxTime = double.MaxValue;

            foreach (SimPart part in _partsWithRCSDrains)
                maxTime = Min(part.RCSMaxTime(), maxTime);

            return maxTime;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double MinimumTimeStep()
        {
            double maxTime = ResourceMaxTime();

            return maxTime < double.MaxValue && maxTime > 0.001 ? maxTime : 0.001;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double ResourceMaxTime()
        {
            double maxTime = double.MaxValue;

            foreach (SimPart part in _partsWithResourceDrains)
                maxTime = Min(part.ResourceMaxTime(), maxTime);

            return maxTime;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FinishRcsSegment(bool max, double deltaTime, double startMass, double endMass, double rcsThrust)
        {
            double rcsDeltaV = rcsThrust * deltaTime / (startMass - endMass) * Log(startMass / endMass);
            double rcsISP = rcsDeltaV / (G0 * Log(startMass / endMass));

            if (_currentSegment.RcsISP == 0)
                _currentSegment.RcsISP = rcsISP;
            if (_currentSegment.RcsThrust == 0)
                _currentSegment.RcsThrust = rcsThrust;

            if (max)
            {
                _currentSegment.MaxRcsDeltaV += rcsDeltaV;
                if (_currentSegment.RcsStartTMR == 0)
                    _currentSegment.RcsStartTMR = rcsThrust / startMass;
            }
            else
            {
                _currentSegment.MinRcsDeltaV += rcsDeltaV;
                _currentSegment.RcsEndTMR = _currentSegment.RcsThrust / endMass;
                _currentSegment.RcsMass += startMass - endMass;
                _currentSegment.RcsDeltaTime += deltaTime;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FinishSegment(SimVessel vessel)
        {
            if (!_allocatedFirstSegment)
                return;

            double startMass = _currentSegment.StartMass;
            double thrust = _currentSegment.Thrust;
            double endMass = vessel.Mass;
            double deltaTime = _time - _currentSegment.StartTime;
            double deltaV = startMass > endMass ? thrust * deltaTime / (startMass - endMass) * Log(startMass / endMass) : 0;
            double isp = startMass > endMass ? deltaV / (G0 * Log(startMass / endMass)) : 0;

            _currentSegment.ControllableMass = ComputeControllableMass(vessel);
            _currentSegment.DeltaTime = deltaTime;
            _currentSegment.EndMass = endMass;
            _currentSegment.DeltaV = deltaV;
            _currentSegment.Isp = isp;

            _segmentsInStage.Add(_currentSegment);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double ComputeControllableMass(SimVessel vessel)
        {
            double max = 0;
            foreach (SimModuleAvionics avionics in vessel.AvionicsRemainingInStage[vessel.CurrentStage])
                max = Max(avionics.ControllableMass, max);
            return max;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GetNextSegment(SimVessel vessel)
        {
            double stagedMass = 0;
            if (_allocatedFirstSegment)
                stagedMass = _currentSegment.EndMass - vessel.Mass;
            else
                _allocatedFirstSegment = true;

            _currentSegment = new FuelStats
            {
                KSPStage = vessel.CurrentStage,
                Thrust = DVLinearThrust ? vessel.ThrustMagnitude : vessel.ThrustNoCosLoss,
                MaxThrust = vessel.ThrustMaxMagnitude,
                MinThrust = vessel.ThrustMinMagnitude,
                StartTime = _time,
                StartMass = vessel.Mass,
                SpoolUpTime = vessel.SpoolupCurrent,
                StagedMass = stagedMass
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllowedToStage(SimVessel vessel) // I need this, but not exactly how it is.. let's move it up and rewrite
        {
            // always stage if all the engines are burned out
            if (vessel.ActiveEngines.Count == 0)
                return true;

            for (int i = 0; i < vessel.ActiveEngines.Count; i++)
            {
                SimModuleEngines e = vessel.ActiveEngines[i];

                if (e.Part.IsSepratron)
                    continue;

                // never stage an active engine
                if (e.Part.DecoupledInStage >= vessel.CurrentStage - 1)
                    return false;

                // never drop fuel that could be used
                if (e.WouldDropAccessibleFuelTank(vessel.CurrentStage - 1))
                    return false;
            }

            // do not trigger a stage that doesn't decouple anything -- until the engines burn out
            if (vessel.PartsRemainingInStage[vessel.CurrentStage - 1].Count == vessel.PartsRemainingInStage[vessel.CurrentStage].Count)
                return false;

            return vessel.CurrentStage > 0;
        }
    }
}

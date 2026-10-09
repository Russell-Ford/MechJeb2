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

namespace MechJebLib.FuelFlowSimulation
{
    public class FuelFlowSimulation : AsyncJob
    {
        // this actually acts as a fail-safe during our wake-up phase
        // Unfortunately, I can't tell you EXACTLY what it does,
        // but I haven't found a vessel that hits the conditional outside of wake-up
        private const int MAXSTEPS = 100;

        public readonly List<FuelStats> Segments = new List<FuelStats>();
        private FuelStats _currentSegment;
        private double _time;
        public bool DVLinearThrust = true; // include cos losses
        private readonly HashSet<SimPart> _partsWithResourceDrains = new HashSet<SimPart>();
        private readonly HashSet<SimPart> _partsWithRCSDrains = new HashSet<SimPart>();
        private readonly HashSet<SimPart> _partsWithRCSDrains2 = new HashSet<SimPart>();
        private bool _allocatedFirstSegment;

        public override void Run(object? o = null)
        {
            if (o == null)
                throw new ArgumentNullException(nameof(o));

            if (!(o is SimVessel vessel))
                throw new ArgumentException("o is not a SimVessel", nameof(o));

            _allocatedFirstSegment = false;
            _time = 0;
            Segments.Clear();
            vessel.MainThrottle = 1.0;

            vessel.ActivateEnginesAndRCS();

            while (vessel.CurrentStage >= 0) // FIXME: should stop mutating vessel.CurrentStage
            {
                SimulateStage(vessel);
                ClearResiduals();
                ComputeRcsMaxValues(vessel);
                FinishSegment(vessel);
                vessel.Stage();
            }

            Segments.Reverse();

            _partsWithResourceDrains.Clear();
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
                vessel.UpdateMass();
                FinishRcsSegment(max, dt, lastmass, vessel.Mass, vessel.RcsThrust);
                lastmass = vessel.Mass;
            }

            UnapplyRcsDrains();
            vessel.ResetRcsStatus();
            vessel.UpdateMass();
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

        private void SimulateStage(SimVessel vessel)
        {
            vessel.UpdateMass();

            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: Updated mass.");
            vessel.UpdateEngineStats();

            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: Updated engine stats.");
            vessel.UpdateActiveEngines();

            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: Activated {vessel.ActiveEngines.Count} engines. Getting next segment...");

            GetNextSegment(vessel);
            ComputeRcsMinValues(vessel);

            vessel.UpdateActiveRcs();
            ComputeRcsUllageTime(vessel);

            UpdateResourceDrainsAndResiduals(vessel); // Gemini look here

            int activeEngines = vessel.ActiveEngines.Count;
            // these logger calls get removed in release builds
            //AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: +++++++++ STAGE {vessel.CurrentStage} +++++++");

            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: beFOR. Stage: { vessel.CurrentStage }.  ActiveEngines.Count: { activeEngines }");

            for (int steps = MAXSTEPS; steps > 0; steps--)
            {
                if (AllowedToStage(vessel))
                {
                    AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: Leave for loop to stage. Current step: {steps}");
                    return;
                }
                    

                double dt = MaximumTimeStep();
                AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: Couldn't stage in for loop. Current step: {steps}, dt: {dt}");

                if (dt >= 0.02 && activeEngines != vessel.ActiveEngines.Count)
                {
                    AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: Conditional branch hit. Current step: {steps}, dt: {dt}");
                    ClearResiduals();
                    ComputeRcsMaxValues(vessel);
                    FinishSegment(vessel);
                    GetNextSegment(vessel);
                    activeEngines = vessel.ActiveEngines.Count;
                }

                _time += dt;
                ApplyResourceDrains(dt);

                vessel.UpdateMass();
                vessel.UpdateEngineStats();
                vessel.UpdateActiveEngines();
                UpdateResourceDrainsAndResiduals(vessel);
            }

            throw new Exception("FuelFlowSimulation hit max steps of " + MAXSTEPS + " steps");
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
            // 1. CAPTURE STATE BEFORE CLEANUP
            int enginesCount = vessel.ActiveEngines.Count;
            int initialDrainPartsCount = _partsWithResourceDrains.Count;
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
            // 3. TARGETED POST-EVALUATION LOGGING (The "Peeking" Window)
            // Only spams when actively looping inside SimulateStage, tracking if drain lists are collapsing
            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: Drains Refreshed -> " +
                $"ActiveEngines: {enginesCount} | " +
                $"DrainedPartsCount: {_partsWithResourceDrains.Count} (Was: {initialDrainPartsCount})");
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

            // High-visibility tracking for fine-grained resource updates
            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: Part Drain Added -> '{p.Name}' | ResourceID: {resourceId} | Rate: {resourceConsumption:F5}/s | ResidualTarget: {residual:F4}");
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
        // the maximum amount of time that we can drain current resources
        private double MaximumTimeStep()
        {
            double maxTime = ResourceMaxTime();

            return maxTime < double.MaxValue && maxTime > 0.001 ? maxTime : 0.001;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double ResourceMaxTime()
        {
            double maxTime = double.MaxValue;
            SimPart? bottleneckPart = null;

            foreach (SimPart part in _partsWithResourceDrains)
            {
                double partMaxTime = part.ResourceMaxTime();
                if (partMaxTime < maxTime)
                {
                    maxTime = partMaxTime;
                    bottleneckPart = part;
                }
            }

            // EXPLICIT CALLOUT: If the calculated step is collapsing down to the floor limit, flag the specific part
            if (maxTime <= 0.02 && bottleneckPart != null)
            {
                AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: CRITICAL -> dt Bottleneck caused by Part: '{bottleneckPart.Name}' | Max Time Allowed by Tank: {maxTime:F6}s");
            }

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


            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim]: Finishing segment with values:{_currentSegment.ToVerboseLogString()}");


            Segments.Add(_currentSegment);
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
            {
                stagedMass = _currentSegment.EndMass - vessel.Mass;
                AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: First segment already allocated. StagedMass = {stagedMass}");
            }
            else
            {
                AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: Allocating first segment.");
                _allocatedFirstSegment = true;
            }
                

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
            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: _currentSegment: " +
    $"Thrust={_currentSegment.Thrust:F3} | MaxThrust={_currentSegment.MaxThrust:F3} | MinThrust={_currentSegment.MinThrust:F3} | " +
    $"StartTime={_currentSegment.StartTime:F4} | StartMass={_currentSegment.StartMass:F4} | StagedMass={_currentSegment.StagedMass:F4} | " +
    $"SpoolUpTime={_currentSegment.SpoolUpTime:F4}");

        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllowedToStage(SimVessel vessel)
        {
            // Always stage if all active engines are burned out/gone
            if (vessel.ActiveEngines.Count == 0)
            {
                AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: AllowedToStage -> TRUE (No active engines)");
                return true;
            }

            for (int i = 0; i < vessel.ActiveEngines.Count; i++)
            {
                SimModuleEngines e = vessel.ActiveEngines[i];

                // Sepratrons are treated as disposable boosters and don't block staging logic
                if (e.Part.IsSepratron)
                    continue;

                // CRITICAL: Block staging if a running, non-sepratron engine is scheduled to be discarded in the next stage
                if (e.Part.DecoupledInStage >= vessel.CurrentStage - 1)
                {
                    AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: AllowedToStage -> FALSE (Would drop active engine: {e.Part.Name})");
                    return false;
                }

                // CRITICAL: Block staging if the separation would throw away tanks containing accessible fuel
                if (e.WouldDropAccessibleFuelTank(vessel.CurrentStage - 1))
                {
                    AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: AllowedToStage -> FALSE (Would drop usable fuel tanks)");
                    return false;
                }
            }

            // Optimization: Prevent dead/empty staging triggers that drop 0 parts, unless engines are fully burned out (handled above)
            if (vessel.PartsRemainingInStage[vessel.CurrentStage - 1].Count == vessel.PartsRemainingInStage[vessel.CurrentStage].Count)
            {
                AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: AllowedToStage -> FALSE (Next stage drops 0 parts)");
                return false;
            }

            // Final sanity check: Ensure we aren't already at the lowest stage (Stage 0)
            bool clearToStage = vessel.CurrentStage > 0;
            AsyncDevLogger.Log($"[MechJeb2][FuelFlowSim][s{vessel.CurrentStage}]: AllowedToStage -> {clearToStage.ToString().ToUpper()} (Final evaluation)");

            return clearToStage;
        }

    }
}

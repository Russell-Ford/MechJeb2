/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using MechJebLib.FuelFlowSimulation.PartModules;
using MechJebLib.Primitives;
using MechJebLib.Utils;
using static System.FormattableString;
using static MechJebLib.Utils.Statics;

namespace MechJebLib.FuelFlowSimulation
{
    public class SimVessel : IDisposable
    {
        private static readonly ObjectPool<SimVessel> _pool = new ObjectPool<SimVessel>(New, Clear);

        public readonly List<SimPart> Parts = new List<SimPart>(30);
        public readonly DictOfLists<int, SimPart> PartsRemainingInStage = new DictOfLists<int, SimPart>(10);
        public readonly DictOfLists<int, SimModuleAvionics> AvionicsRemainingInStage = new DictOfLists<int, SimModuleAvionics>(10);
        public readonly DictOfLists<int, SimModuleEngines> EnginesDroppedInStage = new DictOfLists<int, SimModuleEngines>(10);
        public readonly DictOfLists<int, SimModuleEngines> EnginesActivatedInStage = new DictOfLists<int, SimModuleEngines>(10);
        public readonly DictOfLists<int, SimModuleRCS> RCSActivatedInStage = new DictOfLists<int, SimModuleRCS>(10);
        public readonly DictOfLists<int, SimModuleRCS> RCSDroppedInStage = new DictOfLists<int, SimModuleRCS>(10);
        public readonly List<SimModuleEngines> ActiveEngines = new List<SimModuleEngines>(10);
        public readonly List<SimModuleRCS> ActiveRcs = new List<SimModuleRCS>(10);

        public bool HasLaunchClamp;
        public int CurrentStage;
        private int _savedStage;
        public double MainThrottle = 1.0;
        public double Mass;
        public V3 ThrustCurrent;
        public V3 ThrustMax;
        public V3 ThrustMin;
        public double RcsThrust;
        public double ThrustMagnitude;
        public double ThrustMinMagnitude;
        public double ThrustMaxMagnitude;
        public double ThrustNoCosLoss;
        public double SpoolupCurrent;
        public double ATMPressure;
        public double ATMDensity;
        public double MachNumber;
        public double T;
        public V3 R, V, U;

        private double currentSpoolupThrust;

        // CurrentStage gets scribbled over by the FuelFlowSimulation, SetCurrentStage() is intended to be used in
        // the VesselBuilder and DecouplingAnalyzer to figure out the right value, ResetCurrentStage() is called by
        // the VesselUpdater to reset it back.
        public void SetCurrentStage(int stage)
        {
            CurrentStage = stage;
            _savedStage = stage;
        }

        public void ResetCurrentStage(int stage)
        {
            if (stage < _savedStage)
                SetCurrentStage(stage);
            else
                CurrentStage = _savedStage;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetConditions(double atmDensity, double atmPressure, double machNumber)
        {
            ATMDensity = atmDensity;
            ATMPressure = atmPressure;
            MachNumber = machNumber;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetInitial(double t, V3 r, V3 v, V3 u)
        {
            T = t;
            R = r;
            V = v;
            U = u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Stage()
        {
            if (CurrentStage < 0)
                return;

            CurrentStage--;

            ActivateEnginesAndRCS();

            HardRecalculateMass();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ActiveEngineNeedsUllage()
        {
            // not yet implemented
            foreach (SimModuleEngines e in ActiveEngines)
            {
                if (e.Ullage)
                    return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ActivateEnginesAndRCS()
        {
            foreach (SimModuleEngines e in EnginesActivatedInStage[CurrentStage])
                if (e.IsEnabled)
                    e.Activate();

            foreach (SimModuleRCS r in RCSActivatedInStage[CurrentStage])
                if (r.IsEnabled)
                    r.Activate();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateActiveEngines()
        {
            ActiveEngines.Clear();

            // FIXME: why am I not iterating over the last ActiveEngines?
            for (int i = -1; i < CurrentStage; i++)
            {
                foreach (SimModuleEngines e in EnginesDroppedInStage[i])
                {
                    //if (e.MassFlowRate <= 0) continue; we haven't spooled yet. MFR = 0.



                    e.VerifyEngineCanBurn(); //we're gonna spool! 
                    //Make sure we can draw resources, RF didn't command autocutoff, and we didn't accidentally start a dead engine
                    //we could also check these earlier instead of just turning them all off
                    //e.UpdateEngineStatus(); 

                    if (!e.IsOperational) //final chance to catch an invalid engine.. leaving here for now
                        continue;

                    ActiveEngines.Add(e);
                }
            }


            ComputeThrustAndSpoolup();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateActiveRcs()
        {
            ActiveRcs.Clear();

            // FIXME: why am I not iterating over the last ActiveRcs?
            for (int i = -1; i < CurrentStage; i++)
            {
                foreach (SimModuleRCS r in RCSDroppedInStage[i])
                {
                    if (r.MassFlowRate <= 0) continue;

                    r.UpdateRCSStatus();

                    if (!r.RcsEnabled)
                        continue;

                    ActiveRcs.Add(r);
                }
            }

            ComputeRcsThrust();
        }



        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeRcsThrust()
        {
            RcsThrust = 0;

            for (int i = 0; i < ActiveRcs.Count; i++)
            {
                SimModuleRCS r = ActiveRcs[i];

                r.Update();
                RcsThrust += r.Thrust;
            }
        }
        public void TrySpoolupEngines()
        {
            // when we get here, we've activated the engines in the current stage
            // then we've set their flow rates, consumption rates, isp
            // then verified that it can do the things we've just commanded of it (we turn the engine on before checking)
            // so in summary: we just turned the engines (of our loosely-KSP-decoupled SimVessel) on
            // let's spool them up!
            if(currentSpoolupThrust < ThrustCurrent.magnitude)
            {
                for (int i = 0; i < ActiveEngines.Count; i++) //active engines has been sanitized here
                {
                    SimModuleEngines e = ActiveEngines[i];

                    // if (!e.IsOperational) not necessary since we sanitized earlier
                    //    continue;

                    SpoolupCurrent += e.ThrustCurrent.magnitude * e.ModuleSpoolupTime;

                    e.Update();
                    ThrustCurrent += e.ThrustCurrent;
                    ThrustMin += e.ThrustMin;
                    ThrustMax += e.ThrustMax;
                    ThrustNoCosLoss += e.ThrustCurrent.magnitude;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeThrustAndSpoolup()
        {
            //zero the vessel before we start
            //Logger.Print($"Russ: Zeroing thrust values from: ThrustCurrent.magnitude: { ThrustCurrent.magnitude}, SpoolupCurrent: {SpoolupCurrent}");
            ThrustCurrent = V3.zero;
            ThrustMax = V3.zero;
            ThrustMin = V3.zero;
            ThrustMagnitude = 0;
            ThrustMinMagnitude = 0;
            ThrustMaxMagnitude = 0;
            ThrustNoCosLoss = 0;
            if(SpoolupCurrent == 0)
            {
                //we haven't spooled up yet, let's do that
            } else
            {
                //we're spooled up
            }
            SpoolupCurrent = 0;

            for (int i = 0; i < ActiveEngines.Count; i++) //active engines has been sanitized here
            {
                SimModuleEngines e = ActiveEngines[i];

                if (!e.IsOperational) // leaving for now in case we didn't properly sanitize (some of the checks in UpdateActiveEngines just continue)
                    continue;

                SpoolupCurrent += e.ThrustCurrent.magnitude * e.ModuleSpoolupTime;

                e.Update();
                ThrustCurrent += e.ThrustCurrent;
                ThrustMin += e.ThrustMin;
                ThrustMax += e.ThrustMax;
                ThrustNoCosLoss += e.ThrustCurrent.magnitude;
            }

            ThrustMagnitude = ThrustCurrent.magnitude;
            ThrustMinMagnitude = ThrustMin.magnitude;
            ThrustMaxMagnitude = ThrustMax.magnitude;
            SpoolupCurrent /= ThrustCurrent.magnitude;
        }

        

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            foreach (SimPart p in Parts)
                p.Dispose();
            _pool.Release(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SimVessel Borrow() => _pool.Borrow();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SimVessel New() => new SimVessel();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Clear(SimVessel v)
        {
            v.HasLaunchClamp = false;
            v.Parts.Clear();
            v.PartsRemainingInStage.Clear();
            v.AvionicsRemainingInStage.Clear();
            v.EnginesDroppedInStage.Clear();
            v.EnginesActivatedInStage.Clear();
            v.RCSActivatedInStage.Clear();
            v.RCSDroppedInStage.Clear();
            v.ActiveEngines.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateRcsStats()
        {
            for (int i = -1; i < CurrentStage; i++)
                foreach (SimModuleRCS e in RCSDroppedInStage[i])
                    e.Update();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateEngineStats()
        {
            for (int i = -1; i < CurrentStage; i++)
                foreach (SimModuleEngines e in EnginesDroppedInStage[i])
                    e.Update();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SaveRcsStatus()
        {
            for (int i = -1; i < CurrentStage; i++)
                foreach (SimModuleRCS r in RCSDroppedInStage[i])
                    r.SaveStatus();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetRcsStatus()
        {
            for (int i = -1; i < CurrentStage; i++)
                foreach (SimModuleRCS r in RCSDroppedInStage[i])
                    r.ResetStatus();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SimVessel:");
            sb.AppendLine(Invariant($"  CurrentStage={CurrentStage} HasLaunchClamp={HasLaunchClamp} MainThrottle={MainThrottle}"));
            sb.AppendLine(Invariant($"  ATMPressure={ATMPressure} ATMDensity={ATMDensity} MachNumber={MachNumber}"));
            sb.AppendLine(Invariant($"  T={T} R={R} V={V} U={U}"));
            sb.AppendLine(Invariant($"  Parts ({Parts.Count}):"));
            foreach (SimPart part in Parts)
                sb.AppendLine(part.ToString().Indent(4));

            return sb.ToString().TrimEnd();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HardRecalculateMass() //what was this doing in here??? why are we.. I see part.UpdateMass has hidden launch clamp logic
        {
            Mass = 0;

            foreach (SimPart part in PartsRemainingInStage[CurrentStage])
            {
                part.UpdateMass();
                Mass += part.Mass;
            }
        }
        
    }
}

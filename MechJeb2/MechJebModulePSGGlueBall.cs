/*
 * Copyright Lamont Granquist (lamont@scriptkiddie.org)
 * Dual licensed under the MIT (MIT-LICENSE) license
 * and GPLv2 (GPLv2-LICENSE) license or any later version.
 */

extern alias JetBrainsAnnotations;
using System;
using MechJebLib.FuelFlowSimulation;
using MechJebLib.Primitives;
using MechJebLib.PSG;
using UnityEngine;
using static MechJebLib.Utils.Statics;
using static System.Math;

#nullable enable

namespace MuMech
{
    /// <summary>
    ///     This class isolates glue between MJ and the PSG optimizer.
    ///     Optimized to bridge the solution template pipeline for instant warm-starting.
    /// </summary>
    public class MechJebModulePSGGlueBall : ComputerModule
    {
        private double _blockOptimizerUntilTime;

        public int SuccessfulConverges;
        public int LastLmStatus;
        public int MaxLmIterations;
        public int LastLmIterations;

        public string? LastFailureMessage;
        public double Staleness;
        public double LastInfeasibility;
        private double _lastTime;

        // Strict real-world wall-clock pacing ceiling
        private double _nextAllowedStandardSimTime;

        // Solution Template Container to bridge the Warm-Start pipeline across runs
        private Solution? _lastValidActiveSolution = null;

        public MechJebModulePSGGlueBall(MechJebCore core) : base(core) { }

        private MechJebModuleAscentSettings _ascentSettings => Core.AscentSettings;

        private Ascent? _ascent;

        protected override void OnModuleEnabled()
        {
            Debug.Log("Enabling PSG GlueBall");
            SuccessfulConverges = LastLmStatus = MaxLmIterations = 0;
            LastLmStatus = LastLmIterations = 0;
            Staleness = LastInfeasibility = _lastTime = 0;
            LastFailureMessage = null;
            _ascent = null;
            _nextAllowedStandardSimTime = 0.0;
            _lastValidActiveSolution = null; // Clear tracking on reset
        }

        protected override void OnModuleDisabled()
        {
            Debug.Log("Disabling PSG GlueBall");
            _ascent = null;
            _lastValidActiveSolution = null;
        }

        public override void OnFixedUpdate() => Core.StageStats.RequestUpdate();

        public override void OnStart(PartModule.StartState state) => GameEvents.onStageActivate.Add(HandleStageEvent);

        public override void OnDestroy() => GameEvents.onStageActivate.Remove(HandleStageEvent);

        private void HandleStageEvent(int data)
        {
            _blockOptimizerUntilTime = VesselState.Time + _ascentSettings.OptimizerPauseTime;

            // Staging events alter the physical rocket structure completely. 
            // We wipe the old solution template to force a clean, fresh bootstrap pass 
            // matching the new mass metrics when the pause expires.
            _lastValidActiveSolution = null;
        }

        private bool IsUnguided(int s) => _ascentSettings.UnguidedStages.Contains(s);

        private bool IsFixed(int s) => _ascentSettings.FixedStages.Contains(s);

        private void HandleDoneTask()
        {
            if (!(_ascent is { IsCompleted: true }))
                return;

            try
            {
                Optimizer? psg = _ascent.GetOptimizer();
                if (psg == null)
                    return;

                LastLmStatus = psg.TerminationType;
                LastLmIterations = psg.Iterations;
                LastInfeasibility = psg.PrimalFeasibility;

                if (LastLmIterations > MaxLmIterations)
                    MaxLmIterations = LastLmIterations;

                if (psg.Success() && psg.Solution != null)
                {
                    Core.Guidance.SetSolution(psg.Solution);
                    SuccessfulConverges += 1;
                    _lastTime = VesselState.Time;
                    Staleness = 0;
                    LastFailureMessage = null;

                    // PIPELINE BRIDGE STEP 1: Intercept and cache the newly converged flight solution template
                    _lastValidActiveSolution = psg.Solution;
                }
                else
                {
                    Debug.Log("failed guidance, znorm: " + psg.PrimalFeasibility);

                    // If the optimizer fails to update or falls out of bounds, we do NOT clear 
                    // the previous solution immediately. We keep it as a fallback template 
                    // so the next execution can attempt a warm-start recovery optimization pass.
                }
            }
            finally
            {
                _ascent.TryMarkReady();
            }
        }

        private void GatherException()
        {
            if (!(_ascent is { IsFaulted: true }))
                return;

            LastFailureMessage = _ascent.Exception?.Message;

            if (_ascent.Exception != null)
                Debug.Log(_ascent.Exception);

            // On a hard thread fault, wipe the template cache to ensure a clean recovery state machine
            _lastValidActiveSolution = null;
        }

        private void MarkReady()
        {
            if (_ascent == null)
                return;

            if (_ascent.IsCompleted || _ascent.IsFaulted)
            {
                if (!_ascent.TryMarkReady())
                {
                    Debug.LogWarning("[MechJebModulePSGGlueBall] Delayed resetting active ascent stream; worker busy.");
                }
            }
        }

        public void SetTarget(double peR, double apR, double attR, double inclination, double lan, double fpa, bool attachAltFlag, bool lanflag)
        {
            if (_lastTime == 0)
                _lastTime = VesselState.Time;

            Staleness = VesselState.Time - _lastTime;

            // 1. DYNAMIC PHYSICS GUARD: Protects the optimizer variables during stage structural separations
            if (_blockOptimizerUntilTime > VesselState.Time)
                return;

            // 2. ABSOLUTE PERFORMANCE CEILING: Enforces the 2-second real-world lockout 
            // to completely insulate your main-thread graphics frames from compilation thrashing
            if (_nextAllowedStandardSimTime > UnityEngine.Time.realtimeSinceStartup)
                return;

            if (_ascent is { IsRunning: true })
                return;

            GatherException();

            HandleDoneTask();

            MarkReady();

            if (_ascentSettings.OptimizeStageFlag)
            {
                if (apR > 0 && apR < peR) apR = peR;
                if (attR < peR) attR = peR;
                if (apR > 0 && attR > apR) attR = apR;
            }

            if (Vessel.VesselOffGround())
            {
                bool hasGuided = false;

                for (int mjPhase = Core.StageStats.VacStats.Count - 1; mjPhase >= 0; mjPhase--)
                {
                    double dv = Core.StageStats.VacStats[mjPhase].DeltaV;
                    int kspStage = Core.StageStats.VacStats[mjPhase].KSPStage;

                    if (kspStage < _ascentSettings.LastStage) break;
                    if (IsCurrentCoastAfterStage(kspStage)) continue;
                    if (dv == 0) continue;

                    if (!IsUnguided(kspStage)) hasGuided = true;
                }

                if (!hasGuided) return;
            }

            if (!Core.Guidance.IsReady()) return;

            bool vesselOutsideAtmosphere = !MainBody.atmosphere || VesselState.AltitudeASL > MainBody.RealMaxAtmosphereAltitude();

            if (Core.Guidance.IsCoasting() && Core.Guidance.CurrentPhaseTgo > 20 && vesselOutsideAtmosphere)
                return;

            if (Core.Guidance.Solution != null)
            {
                if (Core.Guidance.CurrentPhaseTgo < _ascentSettings.PreStageTime)
                {
                    _blockOptimizerUntilTime = VesselState.Time + _ascentSettings.OptimizerPauseTime;
                    return;
                }
            }

            double argp = _ascentSettings.DesiredArgP;
            bool argpflag = _ascentSettings.DesiredArgPFlag;

            Ascent.AscentBuilder ascentBuilder = Ascent.Builder()
               .Initial(Core.StageStats.VacR, Core.StageStats.VacV, Core.StageStats.VacU, Core.StageStats.VacT
                  , MainBody.gravParameter, MainBody.Radius)
               .SetTarget(peR, apR, attR, Deg2Rad(inclination), Deg2Rad(lan), argp, fpa, attachAltFlag, lanflag, argpflag);

            if (MainBody.atmosphere)
            {
                double r1 = MainBody.atmosphereDepth * 0.15;
                double rho0 = MainBody.atmDensityASL;
                double rho1 = MainBody.GetDensity(MainBody.GetPressure(r1), MainBody.GetTemperature(r1));
                double h0 = r1 / Log(rho0 / rho1);
                double cd = _ascentSettings.Cd;
                double aRef = _ascentSettings.Aref;
                double qAlphaMax = _ascentSettings.LimitQa;
                double qMax = Core.Thrust.LimitDynamicPressure ? Core.Thrust.MaxDynamicPressure.Val : 0.0;
                V3 w = 2 * PI / MainBody.rotationPeriod * V3.northpole;

                ascentBuilder.AerodynamicConstants(cd, aRef, rho0, qAlphaMax, qMax, h0, w);
            }

            // --- PIPELINE BRIDGE STEP 2: TEMPLATE INJECTION ---
            // Priority 1: Inject our freshly cached pipeline solution to lock onto the warm-start track instantly.
            if (_lastValidActiveSolution != null)
            {
                ascentBuilder.OldSolution(_lastValidActiveSolution);
            }
            // Priority 2: Fall back to core steering guidance matrices if our tracking buffer is clear.
            else if (Core.Guidance.Solution != null)
            {
                ascentBuilder.OldSolution(Core.Guidance.Solution);
            }

            bool hasCoast = false;

            for (int mjPhase = Core.StageStats.VacStats.Count - 1; mjPhase >= 0; mjPhase--)
            {
                FuelStats fuelStats = Core.StageStats.VacStats[mjPhase];
                int kspStage = Core.StageStats.VacStats[mjPhase].KSPStage;
                double ispCurrent = Core.StageStats.AtmoStats[mjPhase].Isp;
                double minThrottle = Core.StageStats.VacStats[mjPhase].MinThrust / Core.StageStats.VacStats[mjPhase].MaxThrust;
                if (kspStage < _ascentSettings.LastStage) break;
                bool massContinuity = false;
                if ((!hasCoast && Core.Guidance.IsCoasting()) || !Core.Guidance.hasCoasted)
                {
                    if ((kspStage == _ascentSettings.CoastStage && (CoastingBefore() || CoastingDuring())) ||
                    (kspStage == _ascentSettings.CoastStage - 1 && CoastingAfter()))
                    {
                        if (CoastingDuring() && !Core.Guidance.hasCoasted)
                        {
                            if (fuelStats.DeltaV > _ascentSettings.MinDeltaV)
                            {
                                ascentBuilder.AddStage(fuelStats.StartMass * 1000, fuelStats.EndMass * 1000, fuelStats.MaxThrust * 1000, fuelStats.Isp,
                                kspStage, mjPhase, IsUnguided(kspStage), !IsFixed(kspStage), ispCurrent: ispCurrent, minThrottle: minThrottle);
                                massContinuity = true;
                            }
                        }
                        hasCoast = true;
                        double maxt = _ascentSettings.MaxCoast;
                        double mint = _ascentSettings.MinCoast;
                        if (Core.Guidance.IsCoasting())
                        {
                            maxt = Max(maxt - (VesselState.Time - Core.Guidance.StartCoast), 0);
                            mint = Max(mint - (VesselState.Time - Core.Guidance.StartCoast), 0);
                        }
                        bool unguidedCoast = IsUnguided(kspStage);
                        double mf = CoastingDuring() ? fuelStats.EndMass * 1000 : fuelStats.StartMass * 1000;
                        ascentBuilder.AddCoast(fuelStats.StartMass * 1000, mf, mint, maxt, _ascentSettings.CoastStage, mjPhase, unguidedCoast, CoastingDuring());
                    }
                }
                if (fuelStats.DeltaV < _ascentSettings.MinDeltaV) continue;
                ascentBuilder.AddStage(fuelStats.StartMass * 1000, fuelStats.EndMass * 1000, fuelStats.MaxThrust * 1000, fuelStats.Isp,
                kspStage, mjPhase, IsUnguided(kspStage), !IsFixed(kspStage), massContinuity, ispCurrent, minThrottle);
            }
            _ascent = ascentBuilder.Build();
            // Boot the permanent background thread wrapper loop
            if (!_ascent.TryStartJob(_ascent))
            {
                Debug.LogWarning("[MechJebModulePSGGlueBall] Overlapping optimization pass dropped.");
                return;
            }
            // Re-establish our rock-solid 2-second real-world performance block
            _nextAllowedStandardSimTime = UnityEngine.Time.realtimeSinceStartup + 2.0;
        }
        private bool IsCurrentCoastAfterStage(int kspStage)
        {
            if (kspStage == Vessel.currentStage && Core.Guidance.IsCoasting() && CoastingAfter())
                return true;
            return false;
        }
        private bool CoastingBefore() => _ascentSettings.CoastLocation == -1;
        private bool CoastingDuring() => _ascentSettings.CoastLocation == 0;
        private bool CoastingAfter() => _ascentSettings.CoastLocation == 1;
    }
}

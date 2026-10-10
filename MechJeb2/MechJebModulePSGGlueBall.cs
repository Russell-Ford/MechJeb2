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
    ///     This class isolates a bunch of glue between MJ and the PSG optimizer that I don't
    ///     yet understand how to write correctly.
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
        }

        protected override void OnModuleDisabled()
        {
            Debug.Log("Disabling PSG GlueBall");
            _ascent = null;
        }

        //public override void OnFixedUpdate() => Core.StageStats.RequestUpdate();

        public override void OnStart(PartModule.StartState state) => GameEvents.onStageActivate.Add(HandleStageEvent);

        public override void OnDestroy() => GameEvents.onStageActivate.Remove(HandleStageEvent);

        private void HandleStageEvent(int data) => _blockOptimizerUntilTime = VesselState.Time + _ascentSettings.OptimizerPauseTime;

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
                }
                else
                {
                    Debug.Log("failed guidance, znorm: " + psg.PrimalFeasibility);
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
        }

        private void MarkReady()
        {
            if (_ascent == null)
                return;

            // Replaces 'IsStopped' by explicitly checking the persistent worker thread terminal states
            if (_ascent.IsCompleted || _ascent.IsFaulted)
            {
                // Safely reset the user-mode gate. If it fails because the thread is 
                // mid-transition, log a warning instead of hard-crashing the module lifecycle.
                if (!_ascent.TryMarkReady())
                {
                    Debug.LogWarning("[MechJebModulePSGGlueBall] Delayed resetting ascent stream; worker thread is busy.");
                }
            }
        }


        // Track whether the optimizer has locked onto a valid trajectory envelope
        private bool _isConverged = false;
        private double _nextAllowedStandardSimTime;

        public void SetTarget(double peR, double apR, double attR, double inclination, double lan, double fpa, bool attachAltFlag, bool lanflag)
        {
            // initialize the first time we hit SetTarget to avoid initial large staleness values
            if (_lastTime == 0)
                _lastTime = VesselState.Time;

            Staleness = VesselState.Time - _lastTime;

            // 1. DYNAMIC PHYSICS GUARD: Always honor post-staging safety windows first
            if (_blockOptimizerUntilTime > VesselState.Time)
                return;

            // 2. CONVERGENCE CADENCE GUARD: If we have a stable path, enforce the real-world lock.
            // If we are NOT converged, this shield drops completely to spam the background thread!
            if (_isConverged && _nextAllowedStandardSimTime > UnityEngine.Time.realtimeSinceStartup)
                return;

            if (_ascent is { IsRunning: true })
                return;

            GatherException();

            HandleDoneTask();

            MarkReady(); // duplicate ready mark from HandleDoneTask's finally block

            // Evaluate if our thread has successfully updated guidance models
            if (_ascent != null)
            {
                Optimizer? psg = _ascent.GetOptimizer();
                if (psg != null)
                {
                    _isConverged = psg.Success() && psg.Solution != null;
                }
            }

            if (_ascentSettings.OptimizeStageFlag)
            {
                // Except if we're using AttR for a fixed time rocket;
                // If 0 < apR < peR then circularize at peR (apR < 0 means hyperbolic orbit)
                if (apR > 0 && apR < peR)
                    apR = peR;
                // Clamp the AttR between peR and apR
                if (attR < peR)
                    attR = peR;
                if (apR > 0 && attR > apR)
                    attR = apR;
            }

            if (Vessel.VesselOffGround())
            {
                bool hasGuided = false;

                for (int mjPhase = Core.StageStats.VacStats.Count - 1; mjPhase >= 0; mjPhase--)
                {
                    double dv = Core.StageStats.VacStats[mjPhase].DeltaV;
                    int kspStage = Core.StageStats.VacStats[mjPhase].KSPStage;

                    // Stop if we've reached the LastStage
                    if (kspStage < _ascentSettings.LastStage)
                        break;

                    // skip the current stage if we are doing a coast after it
                    if (IsCurrentCoastAfterStage(kspStage))
                        continue;

                    // skip the zero length stages
                    if (dv == 0)
                        continue;

                    if (!IsUnguided(kspStage))
                        hasGuided = true;
                }

                // if we have only inertially guided stages we can't run the optimizer
                if (!hasGuided)
                    return;
            }

            // check for readiness (not terminal guidance and not finished)
            if (!Core.Guidance.IsReady())
                return;

            bool vesselOutsideAtmosphere = !MainBody.atmosphere || VesselState.AltitudeASL > MainBody.RealMaxAtmosphereAltitude();

            // suspend guidance for long coast phases out of the atmosphere
            if (Core.Guidance.IsCoasting() && Core.Guidance.CurrentPhaseTgo > 20 && vesselOutsideAtmosphere)
                return;

            if (Core.Guidance.Solution != null)
            {
                // check for prestaging as the current stage gets low
                if (Core.Guidance.CurrentPhaseTgo < _ascentSettings.PreStageTime)
                {
                    _blockOptimizerUntilTime = VesselState.Time + _ascentSettings.OptimizerPauseTime;
                    return;
                }
            }

            double argp = _ascentSettings.DesiredArgP;
            bool argpFlag = _ascentSettings.DesiredArgPFlag;

            Ascent.AscentBuilder ascentBuilder = Ascent.Builder()
               .Initial(Core.StageStats.VacR, Core.StageStats.VacV, Core.StageStats.VacU, Core.StageStats.VacT
                  , MainBody.gravParameter, MainBody.Radius)
               .SetTarget(peR, apR, attR, Deg2Rad(inclination), Deg2Rad(lan), argp, fpa, attachAltFlag, lanflag, argpFlag);

            // note that we have to solve the atmospheric solution even when flying in vacuum, or the optimzer may decide to dip back
            // into the atmosphere again and burn up the rocket.
            if (MainBody.atmosphere)
            {
                // This very crudely fits an exponential between "sea" level and 15% of the way to space to find rho0 and h0
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

            if (Core.Guidance.Solution != null)
                ascentBuilder.OldSolution(Core.Guidance.Solution);

            bool hasCoast = false;

            for (int mjPhase = Core.StageStats.VacStats.Count - 1; mjPhase >= 0; mjPhase--)
            {
                FuelStats fuelStats = Core.StageStats.VacStats[mjPhase];
                int kspStage = Core.StageStats.VacStats[mjPhase].KSPStage;
                double ispCurrent = Core.StageStats.AtmoStats[mjPhase].Isp;
                double minThrottle = Core.StageStats.VacStats[mjPhase].MinThrust / Core.StageStats.VacStats[mjPhase].MaxThrust;

                if (kspStage < _ascentSettings.LastStage)
                    break;

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

                // skip sep motors.  we already avoid running if the bottom stage has burned below this margin.
                if (fuelStats.DeltaV < _ascentSettings.MinDeltaV)
                    continue;

                ascentBuilder.AddStage(fuelStats.StartMass * 1000, fuelStats.EndMass * 1000, fuelStats.MaxThrust * 1000, fuelStats.Isp,
                    kspStage, mjPhase, IsUnguided(kspStage), !IsFixed(kspStage), massContinuity, ispCurrent, minThrottle);
            }

            _ascent = ascentBuilder.Build();

            // POKE THE PERMANENT BACKGROUND THREAD
            if (!_ascent.TryStartJob())
            {
                Debug.LogWarning("[MechJebModulePSGGlueBall] Overlapping optimization pass skipped: Ascent thread is busy.");
                return;
            }

            // 4. SET FUTURE LOCKOUT CEILING ONLY ON SUCCESSFUL CONVERGENCE
            if (_isConverged)
            {
                // Lock to 2.0 seconds of real-world wall-clock time to protect baseline FPS
                _nextAllowedStandardSimTime = UnityEngine.Time.realtimeSinceStartup + 1.0;
            }
            else
            {
                // If it failed to converge, clear the lockout window entirely so the very 
                // next graphic frame spams a fresh modification pass until it links back up.
                _nextAllowedStandardSimTime = 0.0;
            }
        }


        private bool IsCurrentCoastAfterStage(int kspStage)
        {
            if (kspStage == Vessel.currentStage && Core.Guidance.IsCoasting() && CoastingAfter())
                return true;

            return false;
        }

        private bool CoastingBefore() => _ascentSettings.CoastLocation == -1;
        private bool CoastingDuring() => _ascentSettings.CoastLocation == 0;
        private bool CoastingAfter()  => _ascentSettings.CoastLocation == 1;
    }
}

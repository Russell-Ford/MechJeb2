/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using System;
using MechJebLib.Utils;
using static MechJebLib.Utils.Statics;

namespace MechJebLib.PSG
{
    /// <summary>
    ///     Decoupled Ascent optimizer runner that executes trajectory calculations
    ///     exclusively on a long-lived, kernel-gated background thread loop.
    /// </summary>
    public partial class Ascent : PersistentAsyncJob
    {
        private readonly Problem _problem;
        private Optimizer? _optimizer;
        private readonly PhaseCollection _phases;
        private readonly bool _fixedBurnTime;
        private readonly AscentGuesser _guesser;
        private readonly Solution? _solution;

        // Pass a unique thread tracking identifier down to the hybrid gate loop initializer
        private Ascent(Problem problem, PhaseCollection phases, Solution? oldSolution, bool fixedBurnTime)
            : base("MechJeb_PSG_OptimizerThread", 0.0)
        {
            _problem = problem;
            _phases = phases;
            _solution = oldSolution;
            _fixedBurnTime = fixedBurnTime;
            _guesser = new AscentGuesser(_problem);
        }

        public override void Run(object? o = null)
        {
            // Clear tracking blocks from the previous calculation run
            _optimizer = null;

            if (_solution == null)
            {
                _optimizer = _fixedBurnTime
                    ? InitialBootstrappingFixed()
                    : InitialBootstrappingOptimized();
            }
            else
            {
                _optimizer = ConvergedOptimization(_solution);
            }
        }

        public Optimizer? GetOptimizer() => _optimizer;

        private Optimizer? ConvergedOptimization(Solution oldSolution)
        {
            Optimizer.ObjectiveType cost = _fixedBurnTime ? Optimizer.ObjectiveType.MAX_ENERGY : Optimizer.ObjectiveType.MIN_TIME;
            var psg = new Optimizer(_problem, _phases, _problem.Terminal, cost);
            psg.TranscribePreviousSolution(oldSolution);
            Solution? solution = psg.Run();

            // GRACEFUL RECOVERY: Drop the thread crash and let the module status 
            // panel handle the convergence failure smoothly via state values.
            if (!psg.Success() || solution == null)
            {
                DebugPrint("[Ascent Thread] Converged optimizer failed to resolve trajectory.");
                return psg;
            }

            return psg;
        }

        private Optimizer? InitialBootstrappingFixed()
        {
            PhaseCollection bootPhases = _phases.DeepCopy();

            foreach (Phase p in bootPhases)
                p.Unguided = false;

            var psg = new Optimizer(_problem, bootPhases, _problem.Terminal, Optimizer.ObjectiveType.MAX_ENERGY);
            Solution solution = _guesser.InitialGuess(bootPhases, _problem.Terminal.IncT(), _problem.Terminal.TargetOrbitalEnergy());
            psg.TranscribePreviousBootSolution(solution);
            Solution? solution2 = psg.Run();

            if (!psg.Success() || solution2 == null)
            {
                DebugPrint("[Ascent Thread] Target unreachable during initial fixed bootstrapping pass.");
                return psg;
            }

            PhaseCollection bootphases2 = _phases.DeepCopy();

            var psg2 = new Optimizer(_problem, bootphases2, _problem.Terminal, Optimizer.ObjectiveType.MAX_ENERGY);
            psg2.TranscribePreviousBootSolution(solution2);
            Solution? solution3 = psg2.Run();

            if (!psg2.Success() || solution3 == null)
            {
                DebugPrint("[Ascent Thread] Target unreachable during structural loop relaxation.");
                return psg2;
            }

            return psg2;
        }

        private Optimizer? InitialBootstrappingOptimized()
        {
            Optimizer? psg = InitialBootstrappingOptimizedWithQAlpha();
            if (psg == null) return null;

            Solution? solution = psg.Solution;

            if (psg.Objective == Optimizer.ObjectiveType.MIN_THRUST_ACCEL || solution == null)
                return psg;

            var psg2 = new Optimizer(_problem, psg.Phases, _problem.Terminal, Optimizer.ObjectiveType.MIN_THRUST_ACCEL);
            psg2.TranscribePreviousBootSolution(solution);
            Solution? solution2 = psg2.Run();

            if (!psg2.Success() || solution2 == null)
                return psg;

            return psg2;
        }

        private Optimizer? InitialBootstrappingOptimizedWithQAlpha()
        {
            Optimizer? psg = InitialBootstrappingOptimizedWithoutQAlpha();
            if (psg == null) return null;

            Solution? solution = psg.Solution;

            if ((_problem.Rho0InvQAlphaMax <= 0 && _problem.Rho0InvQMax <= 0) || solution == null)
                return psg;

            DebugPrint("*** PHASE 6: Imposing QAlpha Constraints ***");
            var psg2 = new Optimizer(_problem, psg.Phases, _problem.Terminal, Optimizer.ObjectiveType.MIN_TIME);
            psg2.TranscribePreviousBootSolution(solution);
            Solution? solution2 = psg2.Run();

            if (!psg2.Success() || solution2 == null)
            {
                DebugPrint("[Ascent Thread] Dynamic pressure boundary verification (MaxQ/QAlpha) failed.");
                return psg2;
            }

            return psg2;
        }

        private Optimizer? InitialBootstrappingOptimizedWithoutQAlpha()
        {
            PhaseCollection bootPhases = _phases.DeepCopy();

            for (int p = 0; p < bootPhases.Count; p++)
            {
                bootPhases[p].Tagged = false;
                if (bootPhases[p].Unguided)
                {
                    bootPhases[p].Unguided = false;
                    bootPhases[p].Tagged = true;
                }
            }

            Problem problemNoQa = _problem.WithoutDynamicPressure();

            DebugPrint("*** PHASE 1: DOING INITIAL ALL-GUIDED ROCKET ***");
            var psg = new Optimizer(problemNoQa, bootPhases, _problem.Terminal.GetFPA(), Optimizer.ObjectiveType.MIN_TIME);
            Solution? solution = _guesser.InitialGuess(bootPhases, _problem.Terminal.IncT(), _problem.Terminal.TargetOrbitalEnergy());
            psg.TranscribePreviousBootSolution(solution);
            solution = psg.Run();

            if (!psg.Success() || solution == null)
            {
                DebugPrint("[Ascent Thread] Core guidance path configuration unreachable.");
                return psg;
            }

            bool reConverge = false;

            foreach (Phase p in bootPhases)
            {
                if (!p.Tagged) continue;

                reConverge = true;
                p.Unguided = true;
                p.Tagged = false;
            }

            if (reConverge)
            {
                DebugPrint("*** PHASE 4: ADDING BACK UNGUIDED STAGES ***");
                psg = new Optimizer(problemNoQa, bootPhases, _problem.Terminal.GetFPA(), Optimizer.ObjectiveType.MIN_TIME);
                psg.TranscribePreviousBootSolution(solution);
                solution = psg.Run();

                if (!psg.Success() || solution == null)
                {
                    DebugPrint("[Ascent Thread] Failed to reconcile unguided transitions safely.");
                    return psg;
                }
            }

            if (_problem.Terminal.IsFPA())
                return psg;

            DebugPrint("*** PHASE 5: RELAXING TO FREE ATTACHMENT ***");
            var psg2 = new Optimizer(problemNoQa, bootPhases, _problem.Terminal, Optimizer.ObjectiveType.MIN_TIME);
            psg2.TranscribePreviousBootSolution(solution);
            Solution? solution2 = psg2.Run();

            if (!psg2.Success() || solution2 == null)
            {
                DebugPrint("*** FREE ATTACHMENT FAILED, FALLING BACK TO PERIAPSIS ***");
                return psg;
            }

            if (solution.Vgo(solution2.T0) < solution2.Vgo(solution2.T0))
            {
                DebugPrint($"*** PERIAPSIS ATTACHMENT IS MORE OPTIMAL ({solution.Vgo(solution2.T0)} < {solution2.Vgo(solution2.T0)}) THAN FREE ATTACHMENT SOLUTION ***");
                return psg;
            }

            return psg2;
        }

        public static AscentBuilder Builder() => new AscentBuilder();
    }
}

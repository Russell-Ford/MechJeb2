/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using System.Collections.Generic;
using MechJebLib.FuelFlowSimulation;
using MechJebLib.Primitives;
using UnityEngine;
using static MechJebLib.Utils.Statics;

namespace MechJebLibBindings.FuelFlowSimulation
{
    // FIXME: the SimVesselManager needs to be broken out of MechJebLib eventually to isolate the parts that
    // need to link against KSP GameObjects (MechJebLibBindings.dll or something like that)
    // wait, wasn't this done already? we're in LibBindings right now.
    //
    // FIXME2: this is also a monolith from hell because we didn't use the Factory pattern.
    // SimVesselFactory/Builder -> loosly-decoupled SimVessel -> SimVesselManager/SimVesselUpdater/SimRunner <- Run(simVessel))
    //                    decouple loosely here so that we maintain part -> simPart maps
    // **************** DO NOT use the ksp part references in the sim or you will crash Unity ******************
    // There's a little more to it than this, but this I'd rather just work with the monolith I cleaned up for now.
    public partial class SimVesselManager
    {
        public List<FuelStats> Segments => FuelFlowSimulation.VesselSegmentStats; //final, sanitized output from the sim. let the sim sanitize for us

        private readonly SimVesselBuilder _builder;
        private readonly SimVesselUpdater _updater;
        private SimVessel _vessel;
        private IShipconstruct _kspVessel;
        //this should really be private and called only through SimVesselManager. Leave it as is until dependents migrate.
        public readonly MechJebLib.FuelFlowSimulation.FuelFlowSimulation FuelFlowSimulation = new MechJebLib.FuelFlowSimulation.FuelFlowSimulation();
        public bool DVLinearThrust = true; // include cos losses

        private readonly Dictionary<Part, SimPart> _partMapping = new Dictionary<Part, SimPart>();
        private readonly Dictionary<SimPart, Part> _inversePartMapping = new Dictionary<SimPart, Part>();
        private readonly Dictionary<SimPartModule, PartModule> _inversePartModuleMapping = new Dictionary<SimPartModule, PartModule>();
        public readonly List<FuelStats> StageStats = new List<FuelStats>();

        public double T => _vessel.T;
        public V3     R => _vessel.R;
        public V3     V => _vessel.V;
        public V3     U => _vessel.U;

        public SimVesselManager()
        {
            _builder = new SimVesselBuilder(this);
            _updater = new SimVesselUpdater(this);
            _vessel = SimVessel.Borrow();
            _kspVessel = null!;
        }

        public void Build(IShipconstruct vessel)
        {
            Clear();
            _builder.BuildVessel(vessel);
            _builder.BuildParts();
            Update();
            _builder.UpdateLinks();
            _builder.UpdateCrossFeedSet();
            _builder.UpdateSymmetryParts();
            DecouplingAnalyzer.Analyze(_vessel);
            _builder.UpdateEngineSet();
        }

        public void PrintVessel() => Print($"{_vessel}");

        public void Update() => _updater.Update();

        public void SetConditions(double atmDensity, double atmPressure, double machNumber) =>
            _vessel.SetConditions(atmDensity, atmPressure, machNumber);

        public void SetInitial(double t, V3 r, V3 v, V3 u) => _vessel.SetInitial(t, r, v, u);

        public bool TryStartFuelFlowSimulationJob()
        {
            FuelFlowSimulation.DVLinearThrust = DVLinearThrust;
            return FuelFlowSimulation.TryStartJob(_vessel);
        }

        public bool TryGetResults()
        {
            if (!FuelFlowSimulation.IsCompleted || !FuelFlowSimulation.IsReady) //Run wrapper will mark this
            {
                Debug.Log("[SimVesselManager] Asked for results before sim completed. ");
                return false;
            }

            StageStats.Clear();
            foreach (FuelStats stage in FuelFlowSimulation.VesselSegmentStats)
                StageStats.Add(stage);

            return true;
        }

        private void Clear()
        {
            _partMapping.Clear();
            _inversePartMapping.Clear();
            _inversePartModuleMapping.Clear();
        }

        public void Release()
        {
            Clear();
            _vessel.Dispose();
        }
    }
}

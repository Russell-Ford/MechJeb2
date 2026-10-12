extern alias JetBrainsAnnotations;
using System;
using System.Collections.Generic;
using MechJebLib.FuelFlowSimulation;
using MechJebLib.Primitives;
using UnityEngine;

namespace MuMech
{
    public class MechJebModuleStageStats : ComputerModule
    {
        [ToggleInfoItem("#MechJeb_DVincludecosinelosses", InfoItem.Category.Thrust, showInEditor = true)]
        public readonly bool DVLinearThrust = true;

        // --- THE PUBLIC FRONTIER ---
        // Downstream telemetry displays, display helper classes, and autopilot loops 
        // fetch data seamlessly from the global provider.
        public IReadOnlyList<FuelStats> AtmoStats => StageStatsProvider.AtmoStats;
        public IReadOnlyList<FuelStats> VacStats => StageStatsProvider.VacStats;

        public int AtmoDataVersion => StageStatsProvider.AtmoDataVersion;
        public int VacDataVersion => StageStatsProvider.VacDataVersion;

        // UI Fallback target for MainBody tracking
        public CelestialBody MainBody => Vessel != null ? Vessel.mainBody : FlightGlobals.currentMainBody;

        // --- BACKWARDS COMPATIBLE TELEMETRY VECTOR REDIRECTIONS ---
        public double AtmoT => StageStatsProvider.AtmoT;
        public double VacT => StageStatsProvider.VacT;
        public V3 AtmoR => StageStatsProvider.AtmoR;
        public V3 VacR => StageStatsProvider.VacR;
        public V3 AtmoV => StageStatsProvider.AtmoV;
        public V3 VacV => StageStatsProvider.VacV;
        public V3 AtmoU => StageStatsProvider.AtmoU;
        public V3 VacU => StageStatsProvider.VacU;

        // --- BACKWARDS COMPATIBLE CONFIGURATION FIELDS ---
        // Kept as fields so IMGUI bindings work out of the box with your legacy window panels,
        // but now synchronized upstream via the Helper / Engine layers.
        public CelestialBody EditorBody;
        public bool LiveSLT = true;
        public double AltSLT = 0;
        public double Mach = 0;

        public MechJebModuleStageStats(MechJebCore core) : base(core)
        {
            // Enabled = true;
        }

        public override void OnUpdate()
        {
            // Intentionally Left Blank: 
            // - Flight processing loops run completely on FlightStageStatsEngine.
            // - Editor processing is event/GUI driven on EditorStageStatsEngine.
            // This safely bypasses core suppression traps entirely.
        }

        public override void OnStart(PartModule.StartState state)
        {
            // Intentionally Left Blank: 
            // All legacy proxy driver assignments and structural game event subscriptions are deleted.
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            // Intentionally Left Blank: Zero allocations or custom proxies to release here.
        }

        /// <summary>
        /// Backwards compatible endpoint used by legacy calculators to force an updated calculation check.
        /// </summary>
        public void RequestUpdate()
        {
        }
    }
}

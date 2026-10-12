using System;
using System.Collections.Generic;
using KSP.Localization;
using MechJebLib.FuelFlowSimulation;
using MechJebLibBindings;
using UnityEngine;
using UnityEngine.Profiling;
using static MechJebLib.Utils.Statics;

namespace MuMech
{
    // Stage Stats display is much too complicated to bury in a single method in MechJebModuleInfoItems
    // However, that's where it lived, so there is a lot of persistent state currently stored there.
    // I'm not familiar enough with how to un-bury a lot of that data.
    // This helper class will load all the persistent data from the MechJebModuleInfoItems, and then operate on its own.
    // It will write back changes to that data store.
    // Eventually, we should figure out how to not need that store at all.
    public class MechJebStageStatsHelper
    {
        private bool showStagedMass, showBurnedMass, showInitialMass, showFinalMass, showThrust, showVacInitialTWR, showAtmoInitialTWR;
        private bool showAtmoMaxTWR, showVacMaxTWR, showAtmoDeltaV, showVacDeltaV, showTime, showISP, showEmpty, showRcs, timeSeconds, liveSLT;
        private bool showAtmoCumulativeDeltaV, showVacCumulativeDeltaV, showControllableMass, showRcsUllageTime;
        private int TWRBody;
        private float altSLTScale, machScale;
        private int StageDisplayState { get => infoItems.StageDisplayState; set => infoItems.StageDisplayState = value; }
        private readonly MechJebModuleInfoItems infoItems;
        private readonly MechJebCore core;
        private readonly MechJebModuleStageStats stats;
        private readonly List<FuelStats> _localAtmoSnapshot = new List<FuelStats>(32);
        private readonly List<FuelStats> _localVacSnapshot = new List<FuelStats>(32);
        private int _lastRenderedAtmoVersion = -1;
        private int _lastRenderedVacVersion = -1;
        private static int _lastEditorArbitratedFrame = -1;

        public MechJebStageStatsHelper(MechJebModuleInfoItems items)
        {
            infoItems = items;
            core = items.Core;
            stats = core.GetComputerModule<MechJebModuleStageStats>();
            showStagedMass = items.showStagedMass;
            showBurnedMass = items.showBurnedMass;
            showInitialMass = items.showInitialMass;
            showFinalMass = items.showFinalMass;
            showVacInitialTWR = items.showVacInitialTWR;
            showAtmoInitialTWR = items.showAtmoInitialTWR;
            showAtmoMaxTWR = items.showAtmoMaxTWR;
            showVacMaxTWR = items.showVacMaxTWR;
            showAtmoDeltaV = items.showAtmoDeltaV;
            showVacDeltaV = items.showVacDeltaV;
            showAtmoCumulativeDeltaV = items.showAtmoCumulativeDeltaV;
            showVacCumulativeDeltaV = items.showVacCumulativeDeltaV;
            showControllableMass = items.showControllableMass;
            showRcsUllageTime = items.showRcsUllageTime;
            showTime = items.showTime;
            showISP = items.showISP;
            showThrust = items.showThrust;
            showRcs = items.showRcs;
            showEmpty = items.showEmpty;
            timeSeconds = items.timeSeconds;
            liveSLT = items.liveSLT;
            altSLTScale = items.altSLTScale;
            machScale = items.machScale;
            TWRBody = items.TWRBody;

            bodies = HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor
                ? FlightGlobals.Bodies.ConvertAll(b => b.GetName()).ToArray()
                : new[] { "None" };
            InitializeStageInfo();
            SetVisibility(StageDisplayState);
        }

        private enum StageData
        {
            KSPStage, InitialMass, FinalMass, StagedMass, BurnedMass, Thrust, VacInitialTWR, VacMaxTWR, AtmoInitialTWR, AtmoMaxTWR,
            Isp, AtmoDeltaV, VacDeltaV, Time, AtmoCumulativeDeltaV, VacCumulativeDeltaV, ControllableMass, RcsUllageTime
        }

        private static readonly List<StageData> AllStages = new List<StageData>
        {
            StageData.KSPStage,
            StageData.ControllableMass,
            StageData.InitialMass,
            StageData.FinalMass,
            StageData.StagedMass,
            StageData.BurnedMass,
            StageData.Thrust,
            StageData.VacInitialTWR,
            StageData.VacMaxTWR,
            StageData.AtmoInitialTWR,
            StageData.AtmoMaxTWR,
            StageData.Isp,
            StageData.RcsUllageTime,
            StageData.AtmoCumulativeDeltaV,
            StageData.VacCumulativeDeltaV,
            StageData.AtmoDeltaV,
            StageData.VacDeltaV,
            StageData.Time
        };

        private static readonly string[] StageDisplayStates = { Localizer.Format("#MechJeb_InfoItems_button1"), Localizer.Format("#MechJeb_InfoItems_button2"), Localizer.Format("#MechJeb_InfoItems_button3"), Localizer.Format("#MechJeb_InfoItems_button4") }; //"Short stats""Long stats""Full stats""Custom"

        //private FuelFlowSimulation.FuelStats[] vacStats;
        //private FuelFlowSimulation.FuelStats[] atmoStats;
        private readonly string[] bodies;

        private readonly List<int> stages = new List<int>(8);

        private readonly Dictionary<StageData, bool> stageVisibility = new Dictionary<StageData, bool>(12);
        private readonly Dictionary<StageData, List<string>> stageDisplayInfo = new Dictionary<StageData, List<string>>(12);
        private readonly Dictionary<StageData, string> stageHeaderData = new Dictionary<StageData, string>(12);

        private void InitializeStageInfo()
        {
            stageVisibility.Clear();
            stageDisplayInfo.Clear();
            foreach (StageData ident in AllStages)
            {
                stageVisibility.Add(ident, false);
                stageDisplayInfo.Add(ident, new List<string>(16));
            }

            InitalizeStageHeaderData();
        }

        private void InitalizeStageHeaderData()
        {
            const string SPACING = "   ";

            stageHeaderData.Clear();
            stageHeaderData.Add(StageData.KSPStage, "Stage" + SPACING);
            stageHeaderData.Add(StageData.ControllableMass, "Avionics" + SPACING);
            stageHeaderData.Add(StageData.InitialMass, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn1 + SPACING);
            stageHeaderData.Add(StageData.FinalMass, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn2 + SPACING);
            stageHeaderData.Add(StageData.StagedMass, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn3 + SPACING);
            stageHeaderData.Add(StageData.BurnedMass, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn4 + SPACING);
            stageHeaderData.Add(StageData.Thrust, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn13 + SPACING);
            stageHeaderData.Add(StageData.VacInitialTWR, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn5 + SPACING);
            stageHeaderData.Add(StageData.VacMaxTWR, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn6 + SPACING);
            stageHeaderData.Add(StageData.AtmoInitialTWR, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn7 + SPACING);
            stageHeaderData.Add(StageData.AtmoMaxTWR, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn8 + SPACING);
            stageHeaderData.Add(StageData.Isp, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn9 + SPACING);
            stageHeaderData.Add(StageData.RcsUllageTime, "RCS Ullage" + SPACING);
            stageHeaderData.Add(StageData.AtmoDeltaV, (showRcs ? "RCS ΔVmin" : CachedLocalizer.Instance.MechJebInfoItemsStatsColumn10) + SPACING);
            stageHeaderData.Add(StageData.VacDeltaV, (showRcs ? "RCS ΔVmax" : CachedLocalizer.Instance.MechJebInfoItemsStatsColumn11) + SPACING);
            stageHeaderData.Add(StageData.AtmoCumulativeDeltaV, "Σ Atmo ΔV" + SPACING);
            stageHeaderData.Add(StageData.VacCumulativeDeltaV, "Σ Vac ΔV" + SPACING);
            stageHeaderData.Add(StageData.Time, CachedLocalizer.Instance.MechJebInfoItemsStatsColumn12 + SPACING);
        }

        private double _isp(int index) => showRcs ? _localVacSnapshot[index].RcsISP : _localVacSnapshot[index].Isp;
        private double _thrust(int index) => showRcs ? _localVacSnapshot[index].RcsThrust : _localVacSnapshot[index].Thrust;
        private double _burnedMass(int index) => showRcs ? _localVacSnapshot[index].RcsMass : _localVacSnapshot[index].ResourceMass;
        private double _deltaTime(int index) => showRcs ? _localVacSnapshot[index].RcsDeltaTime : _localVacSnapshot[index].DeltaTime;
        private double _atmoDv(int index) => showRcs ? _localVacSnapshot[index].MinRcsDeltaV : _localAtmoSnapshot[index].DeltaV;
        private double _vacDv(int index) => showRcs ? _localVacSnapshot[index].MaxRcsDeltaV : _localVacSnapshot[index].DeltaV;

        private double _vacStartTWR(int index, double geeASL) =>
            showRcs ? _localVacSnapshot[index].RcsStartTWR(geeASL) : _localVacSnapshot[index].StartTWR(geeASL);

        private double _vacEndTWR(int index, double geeASL) =>
            showRcs ? _localVacSnapshot[index].RcsMaxTWR(geeASL) : _localVacSnapshot[index].MaxTWR(geeASL);

        private double _atmoStartTWR(int index, double geeASL) =>
            showRcs ? _localAtmoSnapshot[index].RcsStartTWR(geeASL) : _localAtmoSnapshot[index].StartTWR(geeASL);

        private double _atmoEndTWR(int index, double geeASL) =>
            showRcs ? _localAtmoSnapshot[index].RcsMaxTWR(geeASL) : _localAtmoSnapshot[index].MaxTWR(geeASL);

        private void GatherStages(List<int> stages)
        {
            stages.Clear();
            for (int i = 0; i < _localAtmoSnapshot.Count; i++)
                if (infoItems.showEmpty)
                    stages.Add(i);
                else if (infoItems.showRcs && _localAtmoSnapshot[i].MinRcsDeltaV > 0)
                    stages.Add(i);
                else if (!infoItems.showRcs && _localAtmoSnapshot[i].DeltaV > 0)
                    stages.Add(i);
        }

        private double CalculateCumulativeVacDeltaV(List<int> stages, int currentStageIndex)
        {
            double cumulative = 0.0;

            for (int i = currentStageIndex; i < stages.Count; i++)
                cumulative += _vacDv(stages[i]);

            return cumulative;
        }

        private double CalculateCumulativeAtmoDeltaV(List<int> stages, int currentStageIndex)
        {
            double cumulative = 0.0;

            for (int i = currentStageIndex; i < stages.Count; i++)
                cumulative += _atmoDv(stages[i]);

            return cumulative;
        }

        private void UpdateStageDisplayInfo(List<int> stages, double geeASL)
        {
            foreach (KeyValuePair<StageData, List<string>> kvp in stageDisplayInfo)
                kvp.Value.Clear();

            foreach (int index in stages)
            {
                int currentStageIndex = stages.IndexOf(index);

                // FIXED: Point everything to _localAtmoSnapshot and _localVacSnapshot 
                // to guarantee that the display string rows remain static between passes
                stageDisplayInfo[StageData.KSPStage].Add($"{_localAtmoSnapshot[index].KSPStage}   ");

                if (stageVisibility[StageData.ControllableMass])
                    stageDisplayInfo[StageData.ControllableMass].Add($"{_localAtmoSnapshot[index].ControllableMass:F3} t   ");

                if (stageVisibility[StageData.InitialMass])
                    stageDisplayInfo[StageData.InitialMass].Add($"{_localAtmoSnapshot[index].StartMass:F3} t   ");

                if (stageVisibility[StageData.FinalMass])
                    stageDisplayInfo[StageData.FinalMass].Add($"{_localAtmoSnapshot[index].EndMass:F3} t   ");

                if (stageVisibility[StageData.StagedMass])
                    stageDisplayInfo[StageData.StagedMass].Add($"{_localAtmoSnapshot[index].StagedMass:F3} t   ");

                if (stageVisibility[StageData.BurnedMass])
                    stageDisplayInfo[StageData.BurnedMass].Add($"{_burnedMass(index):F3} t   ");

                if (stageVisibility[StageData.Thrust])
                    stageDisplayInfo[StageData.Thrust].Add($"{_thrust(index):F3} kN   ");

                if (stageVisibility[StageData.VacInitialTWR])
                    stageDisplayInfo[StageData.VacInitialTWR].Add($"{_vacStartTWR(index, geeASL):F2}   ");

                if (stageVisibility[StageData.VacMaxTWR])
                    stageDisplayInfo[StageData.VacMaxTWR].Add($"{_vacEndTWR(index, geeASL):F2}   ");

                if (stageVisibility[StageData.AtmoInitialTWR])
                    stageDisplayInfo[StageData.AtmoInitialTWR].Add($"{_atmoStartTWR(index, geeASL):F2}   ");

                if (stageVisibility[StageData.AtmoMaxTWR])
                    stageDisplayInfo[StageData.AtmoMaxTWR].Add($"{_atmoEndTWR(index, geeASL):F2}   ");

                if (stageVisibility[StageData.Isp])
                    stageDisplayInfo[StageData.Isp].Add($"{_isp(index):F2}   ");

                if (stageVisibility[StageData.RcsUllageTime])
                {
                    stageDisplayInfo[StageData.RcsUllageTime].Add(timeSeconds
                        ? $"{_localAtmoSnapshot[index].RcsUllageTime:F2}s   "
                        : $"{GuiUtils.TimeToDHMS(_localAtmoSnapshot[index].RcsUllageTime, 1)}   ");
                }

                if (stageVisibility[StageData.AtmoDeltaV])
                    stageDisplayInfo[StageData.AtmoDeltaV].Add($"{_atmoDv(index):F0} m/s   ");

                if (stageVisibility[StageData.VacDeltaV])
                    stageDisplayInfo[StageData.VacDeltaV].Add($"{_vacDv(index):F0} m/s   ");

                if (stageVisibility[StageData.AtmoCumulativeDeltaV])
                    stageDisplayInfo[StageData.AtmoCumulativeDeltaV].Add($"{CalculateCumulativeAtmoDeltaV(stages, currentStageIndex):F0} m/s   ");

                if (stageVisibility[StageData.VacCumulativeDeltaV])
                    stageDisplayInfo[StageData.VacCumulativeDeltaV].Add($"{CalculateCumulativeVacDeltaV(stages, currentStageIndex):F0} m/s   ");

                if (stageVisibility[StageData.Time])
                {
                    stageDisplayInfo[StageData.Time].Add(timeSeconds
                        ? $"{_deltaTime(index):F2}s   "
                        : $"{GuiUtils.TimeToDHMS(_deltaTime(index), 1)}   ");
                }
            }
        }

        private void SetAllStageVisibility(bool state)
        {
            foreach (StageData data in AllStages)
                stageVisibility[data] = state;

            stageVisibility[StageData.KSPStage] = true;
        }

        // This should only be called before Layout phase, and never in Repaint phase
        public void UpdateStageStats()
        {
            double geeASL = HighLogic.LoadedSceneIsEditor ? FlightGlobals.Bodies[TWRBody].GeeASL : stats.MainBody.GeeASL;

            StageStatsProvider.FlushMailboxToGUI();

            // 1. SURGICAL ATMO VERSION GATE
            int currentAtmoVersion = StageStatsProvider.AtmoDataVersion;
            if (currentAtmoVersion != _lastRenderedAtmoVersion)
            {
                _localAtmoSnapshot.Clear();
                _localAtmoSnapshot.AddRange(StageStatsProvider.AtmoStats);
                _lastRenderedAtmoVersion = currentAtmoVersion;
            }

            // 2. SURGICAL VAC VERSION GATE
            int currentVacVersion = StageStatsProvider.VacDataVersion;
            if (currentVacVersion != _lastRenderedVacVersion)
            {
                _localVacSnapshot.Clear();
                _localVacSnapshot.AddRange(StageStatsProvider.VacStats);
                _lastRenderedVacVersion = currentVacVersion;
            }

            // Process layout drawing using the granularly updated cache lists
            GatherStages(stages);
            UpdateStageDisplayInfo(stages, geeASL);
        }

        public void AllStageStats()
        {
            Profiler.BeginSample("AllStageStats.UI1");

            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            GUILayout.Label(CachedLocalizer.Instance.MechJebInfoItemsLabel1); //"Stage stats"

            if (GUILayout.Button(timeSeconds ? "s" : "dhms", GuiUtils.LayoutNoExpandWidth))
            {
                timeSeconds = !timeSeconds;
                infoItems.timeSeconds = timeSeconds;
            }

            if (GUILayout.Button(
                    showEmpty ? CachedLocalizer.Instance.MechJebInfoItemsShowEmpty : CachedLocalizer.Instance.MechJebInfoItemsHideEmpty,
                    GuiUtils.LayoutNoExpandWidth))
            {
                showEmpty = !showEmpty;
                infoItems.showEmpty = showEmpty;
            }

            if (GUILayout.Button(StageDisplayStates[StageDisplayState], GuiUtils.LayoutNoExpandWidth))
            {
                StageDisplayState = (StageDisplayState + 1) % StageDisplayStates.Length;
                SetVisibility(StageDisplayState);
            }

            if (GUILayout.Button(showRcs ? "RCS" : "Engine", GuiUtils.LayoutNoExpandWidth))
            {
                showRcs = !showRcs;
                infoItems.showRcs = showRcs;
                InitalizeStageHeaderData();

                SetVisibility(StageDisplayState);
            }

            if (!HighLogic.LoadedSceneIsEditor)
            {
                if (GUILayout.Button(
                        liveSLT ? CachedLocalizer.Instance.MechJebInfoItemsButton5 : CachedLocalizer.Instance.MechJebInfoItemsButton6,
                        GuiUtils.LayoutNoExpandWidth)) //"Live SLT" "0Alt SLT"
                {
                    liveSLT = !liveSLT;
                    infoItems.liveSLT = liveSLT;
                }

                stats.LiveSLT = liveSLT;
            }

            GUILayout.EndHorizontal();

            if (HighLogic.LoadedSceneIsEditor)
            {
                GUILayout.BeginHorizontal();

                // 1. Celestial Body Selector Combo-Box
                int oldTWRBody = TWRBody;
                TWRBody = GuiUtils.ComboBox.Box(TWRBody, bodies, this, false);
                infoItems.TWRBody = TWRBody;

                // Push the chosen celestial body directly to the Editor Engine
                EditorStageStatsEngine.EditorBody = FlightGlobals.Bodies[TWRBody];

                GUILayout.BeginVertical();

                // 2. Altitude (AltSLT) Tracking Slider
                GUILayout.BeginHorizontal();
                float oldAltScale = altSLTScale;
                altSLTScale = GUILayout.HorizontalSlider(altSLTScale, 0, 1, GuiUtils.LayoutExpandWidth);
                infoItems.altSLTScale = altSLTScale;

                // Calculate and push altitude changes to the Editor Engine context
                EditorStageStatsEngine.AltSLT = Math.Pow(altSLTScale, 2) * EditorStageStatsEngine.EditorBody.atmosphereDepth;
                GUILayout.Label(EditorStageStatsEngine.AltSLT.ToSI() + "m", GuiUtils.LayoutWidth(80));
                GUILayout.EndHorizontal();

                // 3. Mach Velocity Tracking Slider
                GUILayout.BeginHorizontal();
                float oldMachScale = machScale;
                machScale = GUILayout.HorizontalSlider(machScale, 0, 1, GuiUtils.LayoutExpandWidth);
                infoItems.machScale = machScale;

                // Calculate and push Mach changes to the Editor Engine context
                EditorStageStatsEngine.Mach = Math.Pow(machScale * 2, 3);
                GUILayout.Label(EditorStageStatsEngine.Mach.ToString("F1") + " M", GuiUtils.LayoutWidth(80));
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
                GUILayout.EndHorizontal();

                // 4. GRANULAR TRIGGER PASS: If any value changed this frame, signal the async worker to fire
                if (TWRBody != oldTWRBody || altSLTScale != oldAltScale || machScale != oldMachScale)
                {
                    var editorEngine = UnityEngine.Object.FindObjectOfType<EditorStageStatsEngine>();
                    editorEngine?.TriggerEditorRecalculation();
                }
            }
            else
            {
                // Flight Mode Fallback Route
                EditorStageStatsEngine.EditorBody = stats.MainBody;
            }

            Profiler.EndSample();

            Profiler.BeginSample("AllStageStats.DrawColumns");

            GUILayout.BeginHorizontal();
            //DrawStageStatsColumn(CachedLocalizer.Instance.MechJeb_InfoItems_StatsColumn0, stages.Select(s => s.ToString()).ToList());

            bool buttonNotPressed = true;
            foreach (StageData info in AllStages)
            {
                if (stageVisibility[info])
                    buttonNotPressed &= stageVisibility[info] = !DrawStageStatsColumn(stageHeaderData[info], stageDisplayInfo[info]);
            }

            stageVisibility[StageData.KSPStage] = true;

            if (!buttonNotPressed)
            {
                StageDisplayState = 3;
                SaveStageVisibility();
            }

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            Profiler.EndSample();
        }

        private static GUIStyle _columnStyle;

        public static GUIStyle ColumnStyle =>
            _columnStyle ??= new GUIStyle(GuiUtils.YellowOnHover) { alignment = TextAnchor.MiddleRight, wordWrap = false, padding = new RectOffset(2, 2, 0, 0) };

        private bool DrawStageStatsColumn(string header, in List<string> data)
        {
            GUILayout.BeginVertical();
            bool ret = GUILayout.Button(header, ColumnStyle);
            foreach (string datum in data) GUILayout.Label(datum, ColumnStyle);
            GUILayout.EndVertical();
            return ret;
        }

        private void LoadStageVisibility()
        {
            stageVisibility[StageData.StagedMass] = showStagedMass;
            stageVisibility[StageData.BurnedMass] = showBurnedMass;
            stageVisibility[StageData.InitialMass] = showInitialMass;
            stageVisibility[StageData.FinalMass] = showFinalMass;
            stageVisibility[StageData.Thrust] = showThrust;
            stageVisibility[StageData.VacInitialTWR] = showVacInitialTWR;
            stageVisibility[StageData.AtmoInitialTWR] = showAtmoInitialTWR;
            stageVisibility[StageData.AtmoMaxTWR] = showAtmoMaxTWR;
            stageVisibility[StageData.VacMaxTWR] = showVacMaxTWR;
            stageVisibility[StageData.AtmoDeltaV] = showAtmoDeltaV;
            stageVisibility[StageData.VacDeltaV] = showVacDeltaV;
            stageVisibility[StageData.Time] = showTime;
            stageVisibility[StageData.Isp] = showISP;
            stageVisibility[StageData.AtmoCumulativeDeltaV] = showAtmoCumulativeDeltaV;
            stageVisibility[StageData.VacCumulativeDeltaV] = showVacCumulativeDeltaV;
            stageVisibility[StageData.ControllableMass] = showControllableMass;
            stageVisibility[StageData.RcsUllageTime] = showRcsUllageTime;
        }

        private void SaveStageVisibility()
        {
            showStagedMass = infoItems.showStagedMass = stageVisibility[StageData.StagedMass];
            showBurnedMass = infoItems.showBurnedMass = stageVisibility[StageData.BurnedMass];
            showInitialMass = infoItems.showInitialMass = stageVisibility[StageData.InitialMass];
            showFinalMass = infoItems.showFinalMass = stageVisibility[StageData.FinalMass];
            showThrust = infoItems.showThrust = stageVisibility[StageData.Thrust];
            showVacInitialTWR = infoItems.showVacInitialTWR = stageVisibility[StageData.VacInitialTWR];
            showAtmoInitialTWR = infoItems.showAtmoInitialTWR = stageVisibility[StageData.AtmoInitialTWR];
            showAtmoMaxTWR = infoItems.showAtmoMaxTWR = stageVisibility[StageData.AtmoMaxTWR];
            showVacMaxTWR = infoItems.showVacMaxTWR = stageVisibility[StageData.VacMaxTWR];
            showAtmoDeltaV = infoItems.showAtmoDeltaV = stageVisibility[StageData.AtmoDeltaV];
            showVacDeltaV = infoItems.showVacDeltaV = stageVisibility[StageData.VacDeltaV];
            showTime = infoItems.showTime = stageVisibility[StageData.Time];
            showISP = infoItems.showISP = stageVisibility[StageData.Isp];
            showAtmoCumulativeDeltaV = infoItems.showAtmoCumulativeDeltaV = stageVisibility[StageData.AtmoCumulativeDeltaV];
            showVacCumulativeDeltaV = infoItems.showVacCumulativeDeltaV = stageVisibility[StageData.VacCumulativeDeltaV];
            showControllableMass = infoItems.showControllableMass = stageVisibility[StageData.ControllableMass];
            showRcsUllageTime = infoItems.showRcsUllageTime = stageVisibility[StageData.RcsUllageTime];
        }

        private void SetVisibility(int state)
        {
            switch (state)
            {
                case 0:
                    SetAllStageVisibility(false);
                    stageVisibility[StageData.ControllableMass] = ReflectionUtils.IsLoadedRP0;
                    stageVisibility[StageData.VacInitialTWR] = true;
                    stageVisibility[StageData.AtmoInitialTWR] = true;
                    stageVisibility[StageData.VacCumulativeDeltaV] = true;
                    stageVisibility[StageData.VacDeltaV] = true;
                    stageVisibility[StageData.AtmoDeltaV] = true;
                    stageVisibility[StageData.Time] = true;
                    break;
                case 1:
                    SetAllStageVisibility(true);
                    stageVisibility[StageData.ControllableMass] = ReflectionUtils.IsLoadedRP0;
                    stageVisibility[StageData.RcsUllageTime] = ReflectionUtils.IsLoadedRP0;
                    stageVisibility[StageData.AtmoCumulativeDeltaV] = false;
                    stageVisibility[StageData.AtmoMaxTWR] = false;
                    stageVisibility[StageData.Thrust] = false;
                    stageVisibility[StageData.StagedMass] = false;
                    stageVisibility[StageData.BurnedMass] = false;
                    stageVisibility[StageData.Isp] = false;
                    break;
                case 2:
                    SetAllStageVisibility(true);
                    stageVisibility[StageData.RcsUllageTime] = ReflectionUtils.IsLoadedRP0;
                    stageVisibility[StageData.ControllableMass] = ReflectionUtils.IsLoadedRP0;
                    break;
                case 3:
                    LoadStageVisibility();
                    break;
            }
        }
    }
}

using System;
using System.Data.Odbc;
using System.Reflection;
using MechJebLib.FuelFlowSimulation.PartModules;
using UnityEngine;
using static MechJebLibBindings.ReflectionUtils;

namespace MechJebLibBindings
{
    public static class RealFuelsBridge
    {
        public static readonly bool IsRealFuelsLoadedCorrectly;
        private static readonly ClassContext _rfModuleEnginesRf = Assembly("RealFuels").Class("RealFuels.ModuleEnginesRF");
        //unity will catch these if the class is null, right? right!? 
        // https://imgflip.com/memegenerator/322841258/Anakin-Padme-4-Panel
        // I ordered these somewhat relatively based on their importance
        // IsOperational: The engine is actively burning
        private static readonly FieldContext _isOperational = _rfModuleEnginesRf.Field("isOperational");
        // CalculatedResiduals: Pre-calculated by RF. Not sure if this is an estimate or if we'll be displaying the dice roll early.
        private static readonly FieldContext _calculatedResiduals = _rfModuleEnginesRf.Field("calculatedResiduals");
        // InstantThrottle: Do we need to calculate spoolup time?
        private static readonly FieldContext _instantThrottle = _rfModuleEnginesRf.Field("instantThrottle"); 
        // AtmosphereCurve: Why don't we just get the latest thrust output?
        private static readonly FieldContext _atmosphereCurve = _rfModuleEnginesRf.Field("atmosphereCurve"); 
        private static readonly FieldContext _spoolUpTime = _rfModuleEnginesRf.Field("effectiveSpoolUpTime");
        private static readonly FieldContext _autoCutoff = _rfModuleEnginesRf.Field("autoCutoff");
        //ullage returns a custom type. let's return a hard-coded value for now.
        //private static readonly FieldContext _rfUllage = Assembly("RealFuels").Class("RealFuels.ModuleEnginesRF").Field("ullage");
        private static readonly FieldContext _massFlowRate = _rfModuleEnginesRf.Field("massFlowRate"); 
        //the absolute final thrust value real fuels has put out to ksp. grabbed directly from the cat's mouth
        private static readonly FieldContext _currentThrust = _rfModuleEnginesRf.Field("finalThrust"); 
        // real fuels properly impelements maxThrust within ksp because it should not change (unless performance failure/loss of thrust failure)
        private static readonly FieldContext _maxNominalThrust = _rfModuleEnginesRf.Field("maxThrust"); 




        // maxSpecificImpulse: We'd have to go into the config files for this.
        // real fuels utilizes a different class to pull that data, and it updates the PAW with the current ISP
        //private static readonly FieldContext _maxSpecificImpulse = _rfModuleEnginesRf.Field("FINDME"); 
        
        // is massFlowRate the only way to update the drains? we can try to integrate massflowrate later
        // it will be a small deviation during the spoolup phase (and we can catch it by using the real current thrust)
        
        
        // private double _throttle    => Part.Vessel.MainThrottle;
        // private double _atmPressure => Part.Vessel.ATMPressure;
        // private double _atmDensity  => Part.Vessel.ATMDensity;
        // private double _machNumber  => Part.Vessel.MachNumber;



        private static readonly bool _isRealFuelsLoadedCorrectly;
        //private static readonly bool _isRP0LoadedCorrectly;
        // Public static field matching the pattern of the original developer
        
        static RealFuelsBridge()
        {
            // Evaluate both assembly presence and reflection validity with explicit primitives
            IsRealFuelsLoadedCorrectly = Validate();
        }

        // ************
        // BE CAREFUL HERE - WE CAN PASS A NON-RF SIMMODULEENGINE
        // ************

        public static double GetEffectiveSpoolupTime(ModuleEngines realModuleEnginesRF)
        {
            if(IsRealFuelsLoadedCorrectly && realModuleEnginesRF.name == "")
                return 0.0;
            else
                return _rfSpoolUpTime.GetValue<double>(realModuleEnginesRF);
        }

        public static double GetSpecificImpulse(SimModuleEngines realModuleEnginesRF)
        {
            if(IsRealFuelsLoadedCorrectly)
                return 0.0;
            else
                return _rfSpecificImpulse.GetValue<double>(realModuleEnginesRF);
        }

        public static double GetAtmoDensity(SimModuleEngines realModuleEnginesRF)
        {
            if(IsRealFuelsLoadedCorrectly)
                return 0.0;
            else
                return _rfSpecificImpulse.GetValue<double>(realModuleEnginesRF);
        }


        private static bool Validate()
        {
            return IsRealFuelsLoadedCorrectly 
                && _rfModuleEnginesRf.IsValid 
                && _rfSpoolUpTime.IsValid 
                && _rfAutoCutoff.IsValid 
                && _rfUllage.IsValid;
        }
        
    }
}

/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

namespace MechJebLib.FuelFlowSimulation
{
    public struct FuelStats
    {
        public int KSPStage;            // startt on the pad (or loaded a save and we have no idea the state of anything)
        public double ControllableMass; // assuming rp-1, this is the first thing we have to check before igniting
        public bool IsSpoolup;          //FIXME: let MechJebUI decide what to do with the spoolup segments
        //they can: Show separately, Combine, or Ignore
        public double SpoolUpTime;      //if it's a spoolup segment, how long was it?
        public double DeltaTime;        // SpoolupSegment? DeltaTime == SpoolUpTime otherwise dt = max burn time before residuals
        public double CurrentThrust;     // Live updated from RF. Don't make the UI search for it, just pack it in here.
        public double CurrentSpecificImpulse; // Live updated from RF, just pass to UI
        public double MaxNominalSpecificImpulse; // Say your thanks to Konstantin Eduardovich Tsiolkovsky
        public double StartMass; // for making these calculations possible.
        public double EndMass; // Formulated in 1903.
        public double ResourceMass; // I present to you:
        public double DeltaV; // calculated via the Tsiolkovsky Rocket Equation.

        //  He also conceptualized the multi-stage rocket, space stations, artificial gravity, HLOX propellants, and more theoretical works
        //  A deaf school teacher from Kaluga, Russia. His work directly inspired the likes of Sergey Korolev and his fellow soviets.
        //  His work still brings inspiration into the Earth to this very day.
        // A true pioneer in forging the early path forward for humanity.
        // "Earth is the cradle of humanity, but one cannot live in a cradle forever."
        //                                      -Konstantin Eduardovich Tsiolkovsky

        public double MaxThrust; // is this just max accel?
        public double ThrustMassRatio;
        public double MaxThrustMassRatio;
        public double CurrentThrustWeightRatio;
        public double MaxThrustWeightRatio;
        public double StagedMass;
        public double MaxAccel     => EndMass > 0 ? Thrust / EndMass : 0;

        
        
        
        
        public double StartTime; // ??
        public double Thrust;
        
        public double MinThrust; // ??
        
        


        public bool IsRcsSegment;
        public double MaxRcsDeltaV; //FIXME? should we move these into a separate struct and track RCS segments separately?
        //either open another thread for it or implement a scheduler to share this one. sounds like a headache, so I'll leave it in here.
        public double MinRcsDeltaV;
        public double RcsISP;
        public double RcsDeltaTime;
        public double RcsThrust;
        public double RcsMass;
        public double RcsStartTMR;
        public double RcsEndTMR;
        public double RcsUllageTime;
        public double RcsThrustWeightRatioMin;
        public double RcsThrustWeightRatioMax;
        
        public double MaxAccele;
        

        
        //public double ResourceMass => StartMass - EndMass;

        public double RcsStartTWR(double geeASL) => RcsStartTMR / (9.80665 * geeASL);
        public double RcsMaxTWR(double geeASL)   => RcsEndTMR / (9.80665 * geeASL);

        public double StartTWR(double geeASL) => StartMass > 0 ? Thrust / (9.80665 * geeASL * StartMass) : 0;

        public double MaxTWR(double geeASL) => MaxAccel / (9.80665 * geeASL);

        public override string ToString() =>
            $"KSP Stage: {KSPStage.ToString()} Thrust: {Thrust.ToString()} Time: {DeltaTime.ToString()} StartMass: {StartMass.ToString()} EndMass: {EndMass.ToString()} DeltaV: {DeltaV.ToString()} ISP: {Isp.ToString()}";
    }
}

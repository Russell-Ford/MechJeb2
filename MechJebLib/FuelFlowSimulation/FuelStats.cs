/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

namespace MechJebLib.FuelFlowSimulation
{
    public struct FuelStats
    {
        public double DeltaTime;
        public double DeltaV;
        public double EndMass;
        public double Isp;
        public int KSPStage;
        public double StagedMass;
        public double StartMass;
        public double StartTime;
        public double Thrust;
        public double MinThrust;
        public double MaxThrust;
        public double SpoolUpTime;
        public double MaxRcsDeltaV;
        public double MinRcsDeltaV;
        public double RcsISP;
        public double RcsDeltaTime;
        public double RcsThrust;
        public double RcsMass;
        public double RcsStartTMR;
        public double RcsEndTMR;
        public double RcsUllageTime;
        public double ControllableMass;

        public double MaxAccel     => EndMass > 0 ? Thrust / EndMass : 0;
        public double ResourceMass => StartMass - EndMass;

        public double RcsStartTWR(double geeASL) => RcsStartTMR / (9.80665 * geeASL);
        public double RcsMaxTWR(double geeASL)   => RcsEndTMR / (9.80665 * geeASL);

        public double StartTWR(double geeASL) => StartMass > 0 ? Thrust / (9.80665 * geeASL * StartMass) : 0;

        public double MaxTWR(double geeASL) => MaxAccel / (9.80665 * geeASL);

        public string ToVerboseLogString()
        {
            return $"[FuelStats State] Stage: {KSPStage} | dt: {DeltaTime:F6} | dV: {DeltaV:F4} | " +
                   $"Mass(Start: {StartMass:F4}, End: {EndMass:F4}, Staged: {StagedMass:F4}, Controllable: {ControllableMass:F4}, Resource: {ResourceMass:F4}) | " +
                   $"Thrust(Cur: {Thrust:F2}, Min: {MinThrust:F2}, Max: {MaxThrust:F2}) | Isp: {Isp:F2} | SpoolUp: {SpoolUpTime:F4} | " +
                   $"RCS(dV_Max: {MaxRcsDeltaV:F4}, dV_Min: {MinRcsDeltaV:F4}, ISP: {RcsISP:F2}, dt: {RcsDeltaTime:F4}, Thrust: {RcsThrust:F2}, Mass: {RcsMass:F4}, Ullage: {RcsUllageTime:F4}, TMR_Start: {RcsStartTMR:F4}, TMR_End: {RcsEndTMR:F4})";
        }

        public override string ToString() =>
            $"KSP Stage: {KSPStage.ToString()} Thrust: {Thrust.ToString()} Time: {DeltaTime.ToString()} StartMass: {StartMass.ToString()} EndMass: {EndMass.ToString()} DeltaV: {DeltaV.ToString()} ISP: {Isp.ToString()}";
    }
}

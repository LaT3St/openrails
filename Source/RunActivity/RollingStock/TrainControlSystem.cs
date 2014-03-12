﻿// COPYRIGHT 2013, 2014 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

// Define this to log the wheel configurations on cars as they are loaded.
//#define DEBUG_WHEELS

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using MSTS.Formats;
using MSTS.Parsers;
using ORTS.Scripting.Api;
using ORTS.Viewer3D.Popups;

namespace ORTS
{
    public class ScriptedTrainControlSystem
    {
        public bool VigilanceAlarm { get; set; }
        public bool VigilanceEmergency { get; set; }
        public bool OverspeedWarning { get; set; }
        public bool PenaltyApplication { get; set; }
        public float CurrentSpeedLimitMpS { get; set; }
        public float NextSpeedLimitMpS { get; set; }
        public TrackMonitorSignalAspect CabSignalAspect { get; set; }

        Train.TrainInfo TrainInfo = new Train.TrainInfo();

        readonly MSTSLocomotive Locomotive;
        readonly Simulator Simulator;

        List<float> SignalSpeedLimits = new List<float>();
        List<Aspect> SignalAspects = new List<Aspect>();
        List<float> SignalDistances = new List<float>();
        List<float> PostSpeedLimits = new List<float>();
        List<float> PostDistances = new List<float>();

        MonitoringDevice VigilanceMonitor;
        MonitoringDevice OverspeedMonitor;
        MonitoringDevice EmergencyStopMonitor;
        MonitoringDevice AWSMonitor;

        public bool AlerterButtonPressed;
        public bool Activated;
        bool IsAlerterEnabled;

        string ScriptName;
        string SoundFileName;
        TrainControlSystem Script;

        public Dictionary<TrainControlSystem, string> Sounds = new Dictionary<TrainControlSystem, string>();

        const float GravityMpS2 = 9.80665f;

        public ScriptedTrainControlSystem() { }

        public ScriptedTrainControlSystem(MSTSLocomotive locomotive)
        {
            Locomotive = locomotive;
            Simulator = Locomotive.Simulator;
        }

        public ScriptedTrainControlSystem(ScriptedTrainControlSystem other, MSTSLocomotive newLocomotive)
        {
            Locomotive = newLocomotive;
            Simulator = newLocomotive.Simulator;
            ScriptName = other.ScriptName;
            SoundFileName = other.SoundFileName;
            if (other.VigilanceMonitor != null) VigilanceMonitor = new MonitoringDevice(other.VigilanceMonitor);
            if (other.OverspeedMonitor != null) OverspeedMonitor = new MonitoringDevice(other.OverspeedMonitor);
            if (other.EmergencyStopMonitor != null) EmergencyStopMonitor = new MonitoringDevice(other.EmergencyStopMonitor);
            if (other.AWSMonitor != null) AWSMonitor = new MonitoringDevice(other.AWSMonitor);
        }

        public void Parse(string lowercasetoken, STFReader stf)
        {
            switch (lowercasetoken)
            {
                case "engine(vigilancemonitor": VigilanceMonitor = new MonitoringDevice(stf); break;
                case "engine(overspeedmonitor": OverspeedMonitor = new MonitoringDevice(stf); break;
                case "engine(emergencystopmonitor": EmergencyStopMonitor = new MonitoringDevice(stf); break;
                case "engine(awsmonitor": AWSMonitor = new MonitoringDevice(stf); break;
                case "engine(ortstraincontrolsystem" : ScriptName = stf.ReadStringBlock(null); break;
                case "engine(ortstraincontrolsystemsound": SoundFileName = stf.ReadStringBlock(null); break;
            }
        }

        public class MonitoringDevice
        {
            public float MonitorTimeS = 66; // Time from alerter reset to applying emergency brake
            public float AlarmTimeS = 60; // Time from alerter reset to audible and visible alarm
            public float PenaltyTimeS;
            public bool EmergencyCutsPower;
            public bool EmergencyShutsDownEngine;
            public bool ResetOnZeroSpeed = true;
            public float CriticalLevelMpS;
            public float ResetLevelMpS;
            public bool AppliesFullBrake = true;
            public bool AppliesEmergencyBrake;

            // Following are for OverspeedMonitor only
            public bool ResetOnResetButton;
            public float TriggerOnOverspeedMpS;
            public bool TriggerOnTrackOverspeed;
            public float TriggerOnTrackOverspeedMarginMpS = 4;
            public float AlarmTimeBeforeOverspeedS = 5;

            public MonitoringDevice(STFReader stf)
            {
                stf.MustMatch("(");
                stf.ParseBlock(new STFReader.TokenProcessor[] {
                    new STFReader.TokenProcessor("monitoringdevicemonitortimelimit", () => { MonitorTimeS = stf.ReadFloatBlock(STFReader.UNITS.Time, MonitorTimeS); }),
                    new STFReader.TokenProcessor("monitoringdevicealarmtimelimit", () => { AlarmTimeS = stf.ReadFloatBlock(STFReader.UNITS.Time, AlarmTimeS); }),
                    new STFReader.TokenProcessor("monitoringdevicepenaltytimelimit", () => { PenaltyTimeS = stf.ReadFloatBlock(STFReader.UNITS.Time, PenaltyTimeS); }),
                    new STFReader.TokenProcessor("monitoringdeviceappliescutspower", () => { EmergencyCutsPower = stf.ReadBoolBlock(EmergencyCutsPower); }),
                    new STFReader.TokenProcessor("monitoringdeviceappliesshutsdownengine", () => { EmergencyShutsDownEngine = stf.ReadBoolBlock(EmergencyShutsDownEngine); }),
                    new STFReader.TokenProcessor("monitoringdeviceresetonzerospeed", () => { ResetOnZeroSpeed = stf.ReadBoolBlock(ResetOnZeroSpeed); }),
                    new STFReader.TokenProcessor("monitoringdeviceresetonresetbutton", () => { ResetOnResetButton = stf.ReadBoolBlock(ResetOnResetButton); }),
                    new STFReader.TokenProcessor("monitoringdevicetriggeronoverspeed", () => { TriggerOnOverspeedMpS = stf.ReadFloatBlock(STFReader.UNITS.Speed, TriggerOnOverspeedMpS); }),
                    new STFReader.TokenProcessor("monitoringdevicetriggerontrackoverspeed", () => { TriggerOnTrackOverspeed = stf.ReadBoolBlock(TriggerOnTrackOverspeed); }),
                    new STFReader.TokenProcessor("monitoringdevicetriggerontrackoverspeedmargin", () => { TriggerOnTrackOverspeedMarginMpS = stf.ReadFloatBlock(STFReader.UNITS.Speed, TriggerOnTrackOverspeedMarginMpS); }),
                    new STFReader.TokenProcessor("monitoringdevicecriticallevel", () => { CriticalLevelMpS = stf.ReadFloatBlock(STFReader.UNITS.Speed, CriticalLevelMpS); }),
                    new STFReader.TokenProcessor("monitoringdeviceresetlevel", () => { ResetLevelMpS = stf.ReadFloatBlock(STFReader.UNITS.Speed, ResetLevelMpS); }),
                    new STFReader.TokenProcessor("monitoringdevicealarmtimebeforeoverspeed", () => { AlarmTimeBeforeOverspeedS = stf.ReadFloatBlock(STFReader.UNITS.Time, AlarmTimeBeforeOverspeedS); }),
                    new STFReader.TokenProcessor("monitoringdeviceappliesfullbrake", () => { AppliesFullBrake = stf.ReadBoolBlock(AppliesFullBrake); }),
                    new STFReader.TokenProcessor("monitoringdeviceappliesemergencybrake", () => { AppliesEmergencyBrake = stf.ReadBoolBlock(AppliesEmergencyBrake); }),
                });
            }

            public MonitoringDevice() { }

            public MonitoringDevice(MonitoringDevice other)
            {
                MonitorTimeS = other.MonitorTimeS;
                AlarmTimeS = other.AlarmTimeS;
                PenaltyTimeS = other.PenaltyTimeS;
                EmergencyCutsPower = other.EmergencyCutsPower;
                EmergencyShutsDownEngine = other.EmergencyShutsDownEngine;
                ResetOnZeroSpeed = other.ResetOnZeroSpeed;
                CriticalLevelMpS = other.CriticalLevelMpS;
                ResetLevelMpS = other.ResetLevelMpS;
                AppliesFullBrake = other.AppliesFullBrake;
                AppliesEmergencyBrake = other.AppliesEmergencyBrake;
                ResetOnResetButton = other.ResetOnResetButton;
                TriggerOnOverspeedMpS = other.TriggerOnOverspeedMpS;
                TriggerOnTrackOverspeed = other.TriggerOnTrackOverspeed;
                TriggerOnTrackOverspeedMarginMpS = other.TriggerOnTrackOverspeedMarginMpS;
                AlarmTimeBeforeOverspeedS = other.AlarmTimeBeforeOverspeedS;
            }
        }

        public ScriptedTrainControlSystem Clone(MSTSLocomotive newLocomotive)
        {
            return new ScriptedTrainControlSystem(this, newLocomotive);
        }

        public void Initialize()
        {
            IsAlerterEnabled = Simulator.Settings.Alerter;

            if (ScriptName != null && ScriptName != "MSTS")
            {
                var pathArray = new string[] { Path.Combine(Path.GetDirectoryName(Locomotive.WagFilePath), "Script") };
                Script = Simulator.ScriptManager.Load(pathArray, ScriptName) as TrainControlSystem;
            }

            if (Script == null)
            {
                Script = new MSTSTrainControlSystem();
                ((MSTSTrainControlSystem)Script).VigilanceMonitor = VigilanceMonitor;
                ((MSTSTrainControlSystem)Script).OverspeedMonitor = OverspeedMonitor;
                ((MSTSTrainControlSystem)Script).EmergencyStopMonitor = EmergencyStopMonitor;
                ((MSTSTrainControlSystem)Script).AWSMonitor = AWSMonitor;
                ((MSTSTrainControlSystem)Script).EmergencyCausesThrottleDown = Locomotive.EmergencyCausesThrottleDown;
                ((MSTSTrainControlSystem)Script).EmergencyEngagesHorn = Locomotive.EmergencyEngagesHorn;
            }

            if (SoundFileName != null)
            {
                var soundPathArray = new[] {
                    Path.Combine(Path.GetDirectoryName(Locomotive.WagFilePath), "SOUND"),
                    Path.Combine(Program.Simulator.BasePath, "SOUND"),
                };
                var soundPath = ORTSPaths.GetFileFromFolders(soundPathArray, SoundFileName);
                if (File.Exists(soundPath))
                    Sounds.Add(Script, soundPath);
            }

            Script.ClockTime = () => (float)Simulator.ClockTime;
            Script.DistanceM = () => Locomotive.DistanceM;
            Script.IsBrakeEmergency = () => Locomotive.TrainBrakeController.GetIsEmergency();
            Script.IsBrakeFullService = () => Locomotive.TrainBrakeController.GetIsFullBrake();
            Script.SpeedMpS = () => Math.Abs(Locomotive.SpeedMpS);
            Script.CurrentSignalSpeedLimitMpS = () => Locomotive.Train.allowedMaxSpeedSignalMpS;
            Script.CurrentPostSpeedLimitMpS = () => Locomotive.Train.allowedMaxSpeedLimitMpS;
            Script.IsAlerterEnabled = () => this.IsAlerterEnabled;
            Script.AlerterSound = () => Locomotive.AlerterSnd;
            Script.SetHorn = (value) => Locomotive.SignalEvent(value ? Event.HornOn : Event.HornOff);
            Script.SetFullBrake = () => Locomotive.TrainBrakeController.SetFullBrake();
            Script.SetEmergencyBrake = () => Locomotive.TrainBrakeController.SetEmergency();
            Script.SetThrottleController = (value) => Locomotive.ThrottleController.SetValue(value);
            Script.SetDynamicBrakeController = (value) => Locomotive.DynamicBrakeController.SetValue(value);
            Script.SetVigilanceAlarmDisplay = (value) => this.VigilanceAlarm = value;
            Script.SetVigilanceEmergencyDisplay = (value) => this.VigilanceEmergency = value;
            Script.SetOverspeedWarningDisplay = (value) => this.OverspeedWarning = value;
            Script.SetPenaltyApplicationDisplay = (value) => this.PenaltyApplication = value;
            Script.SetCurrentSpeedLimitMpS = (value) => this.CurrentSpeedLimitMpS = value;
            Script.SetNextSpeedLimitMpS = (value) => this.NextSpeedLimitMpS = value;
            Script.SetNextSignalAspect = (value) => this.CabSignalAspect = (TrackMonitorSignalAspect)value;
            Script.SetVigilanceAlarm = (value) => Locomotive.SignalEvent(value ? Event.VigilanceAlarmOn : Event.VigilanceAlarmOff);
            Script.TriggerSoundInfo1 = () => this.HandleEvent(Event.TrainControlSystemInfo1, Script);
            Script.TriggerSoundInfo2 = () => this.HandleEvent(Event.TrainControlSystemInfo2, Script);
            Script.TriggerSoundPenalty1 = () => this.HandleEvent(Event.TrainControlSystemPenalty1, Script);
            Script.TriggerSoundPenalty2 = () => this.HandleEvent(Event.TrainControlSystemPenalty2, Script);
            Script.TriggerSoundSystemActivate = () => this.HandleEvent(Event.TrainControlSystemActivate, Script);
            Script.TriggerSoundSystemDeactivate = () => this.HandleEvent(Event.TrainControlSystemDeactivate, Script);
            Script.TrainSpeedLimitMpS = () => TrainInfo.allowedSpeedMpS;
            Script.NextSignalSpeedLimitMpS = (value) => NextSignalItem<float>(value, ref SignalSpeedLimits, Train.TrainObjectItem.TRAINOBJECTTYPE.SIGNAL);
            Script.NextSignalAspect = (value) => NextSignalItem<Aspect>(value, ref SignalAspects, Train.TrainObjectItem.TRAINOBJECTTYPE.SIGNAL);
            Script.NextSignalDistanceM = (value) => NextSignalItem<float>(value, ref SignalDistances, Train.TrainObjectItem.TRAINOBJECTTYPE.SIGNAL);
            Script.NextPostSpeedLimitMpS = (value) => NextSignalItem<float>(value, ref PostSpeedLimits, Train.TrainObjectItem.TRAINOBJECTTYPE.SPEEDPOST);
            Script.NextPostDistanceM = (value) => NextSignalItem<float>(value, ref PostDistances, Train.TrainObjectItem.TRAINOBJECTTYPE.SPEEDPOST);
            Script.SpeedCurve = (arg1, arg2, arg3, arg4, arg5) => SpeedCurve(arg1, arg2, arg3, arg4, arg5);
            Script.DistanceCurve = (arg1, arg2, arg3, arg4, arg5) => DistanceCurve(arg1, arg2, arg3, arg4, arg5);
            Script.SetPantographsDown = () => 
            { 
                Locomotive.SignalEvent(Event.Pantograph1Down);
                Locomotive.SignalEvent(Event.Pantograph2Down);
                Locomotive.Train.SignalEvent(Event.Pantograph1Down);
                Locomotive.Train.SignalEvent(Event.Pantograph2Down);
            };

            Script.Initialize();
            Activated = true;
        }

        T NextSignalItem<T>(int forsight, ref List<T> list, Train.TrainObjectItem.TRAINOBJECTTYPE type)
        {
            if (forsight < 0) forsight = 0;
            if (forsight >= list.Count) SearchTrainInfo(forsight, type);
            return list[forsight < list.Count ? forsight : list.Count - 1];
        }

        void SearchTrainInfo(float forsight, Train.TrainObjectItem.TRAINOBJECTTYPE searchFor)
        {
            if (SignalSpeedLimits.Count == 0)
                TrainInfo = Locomotive.Train.GetTrainInfo();

            var signalsFound = 0;
            var postsFound = 0;

            foreach (var foundItem in Locomotive.Train.MUDirection == Direction.Reverse ? TrainInfo.ObjectInfoBackward : TrainInfo.ObjectInfoForward)
            {
                switch (foundItem.ItemType)
                {
                    case Train.TrainObjectItem.TRAINOBJECTTYPE.SIGNAL:
                        signalsFound++;
                        if (signalsFound > SignalSpeedLimits.Count)
                        {
                            SignalSpeedLimits.Add(foundItem.AllowedSpeedMpS);
                            SignalAspects.Add((Aspect)foundItem.SignalState);
                            SignalDistances.Add(foundItem.DistanceToTrainM);
                        }
                        break;

                    case Train.TrainObjectItem.TRAINOBJECTTYPE.SPEEDPOST:
                        postsFound++;
                        if (postsFound > PostSpeedLimits.Count)
                        {
                            PostSpeedLimits.Add(foundItem.AllowedSpeedMpS);
                            PostDistances.Add(foundItem.DistanceToTrainM);
                        }
                        break;

                    case Train.TrainObjectItem.TRAINOBJECTTYPE.AUTHORITY:
                        signalsFound++;
                        if (signalsFound > SignalSpeedLimits.Count)
                        {
                            SignalSpeedLimits.Add(0f);
                            SignalAspects.Add(Aspect.Stop);
                            SignalDistances.Add(foundItem.DistanceToTrainM);
                        }
                        break;
                }

                if (searchFor == Train.TrainObjectItem.TRAINOBJECTTYPE.SIGNAL && signalsFound > forsight ||
                    searchFor == Train.TrainObjectItem.TRAINOBJECTTYPE.SPEEDPOST && postsFound > forsight)
                {
                    break;
                }
            }

            if (searchFor == Train.TrainObjectItem.TRAINOBJECTTYPE.SIGNAL && signalsFound == 0)
            {
                SignalSpeedLimits.Add(-1);
                SignalAspects.Add(Aspect.None);
                SignalDistances.Add(float.MaxValue);
            }
            if (searchFor == Train.TrainObjectItem.TRAINOBJECTTYPE.SPEEDPOST && postsFound == 0)
            {
                PostSpeedLimits.Add(-1);
                PostDistances.Add(float.MaxValue);
            }
        }

        private void HandleEvent(Event evt, TrainControlSystem script)
        {
            foreach (var eventHandler in Locomotive.EventHandlers)
                eventHandler.HandleEvent(evt, script);
        }

        private static float SpeedCurve(float targetDistanceM, float targetSpeedMpS, float slope, float delayS, float decelerationMpS2)
        {
            if (targetSpeedMpS < 0)
                targetSpeedMpS = 0;

            decelerationMpS2 -= GravityMpS2 * slope;

            float squareSpeedComponent = targetSpeedMpS * targetSpeedMpS
                + (delayS * delayS) * decelerationMpS2 * decelerationMpS2
                + 2f * targetDistanceM * decelerationMpS2;

            float speedComponent = delayS * decelerationMpS2;

            return (float)Math.Sqrt(squareSpeedComponent) - speedComponent;
        }

        private static float DistanceCurve(float currentSpeedMpS, float targetSpeedMpS, float slope, float delayS, float decelerationMpS2)
        {
            if (targetSpeedMpS < 0)
                targetSpeedMpS = 0;

            float brakingDistanceM = (currentSpeedMpS * currentSpeedMpS - targetSpeedMpS * targetSpeedMpS)
                / (2 * (decelerationMpS2 - GravityMpS2 * slope));

            float delayDistanceM = delayS * currentSpeedMpS;

            return brakingDistanceM + delayDistanceM;
        }
        
        public void Update()
        {
            if (Script == null)
                return;

            SignalSpeedLimits.Clear();
            SignalAspects.Clear();
            SignalDistances.Clear();
            PostSpeedLimits.Clear();
            PostDistances.Clear();

            IsAlerterEnabled = Simulator.Settings.Alerter
                & !(Simulator.Settings.AlerterDisableExternal & Simulator.Confirmer.Viewer.Camera.Style != ORTS.Viewer3D.Camera.Styles.Cab);

            Script.Update();
        }

        public void AlerterPressed(bool pressed)
        {
            AlerterButtonPressed = pressed;
            SendEvent(pressed ? TCSEvent.AlerterPressed : TCSEvent.AlerterReleased);
        }

        public void AlerterReset()
        {
            SendEvent(TCSEvent.AlerterReset);
        }

        public void SetEmergency()
        {
            if (Script != null)
                Script.SetEmergency();
            else
                Locomotive.TrainBrakeController.SetEmergency();
        }

        void SendEvent(TCSEvent evt)
        {
            SendEvent(evt, String.Empty);
        }

        void SendEvent(TCSEvent evt, string message)
        {
            if (Script != null)
                Script.HandleEvent(evt, message);
        }
    }

    public class MSTSTrainControlSystem : TrainControlSystem
    {
        Timer VigilanceAlarmTimer;
        Timer VigilanceEmergencyTimer;
        Timer VigilancePenaltyTimer;
        Timer OverspeedAlarmTimer;
        Timer OverspeedPenaltyTimer;

        bool OverspeedAlarm;
        bool VigilanceAlarm;
        bool VigilanceEmergency;
        bool OverspeedWarning;

        float VigilanceAlarmTimeoutS;
        float CurrentSpeedLimitMpS;

        public ScriptedTrainControlSystem.MonitoringDevice VigilanceMonitor;
        public ScriptedTrainControlSystem.MonitoringDevice OverspeedMonitor;
        public ScriptedTrainControlSystem.MonitoringDevice EmergencyStopMonitor;
        public ScriptedTrainControlSystem.MonitoringDevice AWSMonitor;
        public bool EmergencyCausesThrottleDown;
        public bool EmergencyEngagesHorn;

        public MSTSTrainControlSystem() { }

        public override void Initialize()
        {
            VigilanceAlarmTimer = new Timer(this);
            VigilanceEmergencyTimer = new Timer(this);
            VigilancePenaltyTimer = new Timer(this);
            OverspeedAlarmTimer = new Timer(this);
            OverspeedPenaltyTimer = new Timer(this);

            if (VigilanceMonitor != null)
            {
                if (VigilanceMonitor.MonitorTimeS > VigilanceMonitor.AlarmTimeS)
                    VigilanceAlarmTimeoutS = VigilanceMonitor.MonitorTimeS - VigilanceMonitor.AlarmTimeS;
                VigilanceAlarmTimer.Setup(VigilanceMonitor.AlarmTimeS);
                VigilanceEmergencyTimer.Setup(VigilanceAlarmTimeoutS);
                VigilancePenaltyTimer.Setup(VigilanceMonitor.PenaltyTimeS);
                VigilanceAlarmTimer.Start();
            }
            if (OverspeedMonitor != null)
            {
                OverspeedAlarmTimer.Setup(Math.Max(OverspeedMonitor.AlarmTimeS, OverspeedMonitor.AlarmTimeBeforeOverspeedS));
                OverspeedPenaltyTimer.Setup(OverspeedMonitor.PenaltyTimeS);
            }
            Activated = true;
        }

        public override void Update()
        {
            SetNextSignalAspect(NextSignalAspect(0));

            CurrentSpeedLimitMpS = CurrentSignalSpeedLimitMpS() >= 0 ? CurrentSignalSpeedLimitMpS() : TrainSpeedLimitMpS();
            if (CurrentSpeedLimitMpS > TrainSpeedLimitMpS())
                CurrentSpeedLimitMpS = TrainSpeedLimitMpS();

            SetCurrentSpeedLimitMpS(CurrentSpeedLimitMpS);
            SetNextSpeedLimitMpS(NextSignalSpeedLimitMpS(0) >= 0 && NextSignalSpeedLimitMpS(0) < TrainSpeedLimitMpS() ? NextSignalSpeedLimitMpS(0) : TrainSpeedLimitMpS());

            if (VigilanceMonitor != null)
                UpdateVigilance();
            if (OverspeedMonitor != null)
                UpdateSpeedControl();

            if (!IsBrakeEmergency() && !IsBrakeFullService())
                SetPenaltyApplicationDisplay(false);
        }

        public override void HandleEvent(TCSEvent evt, string message)
        {
            switch(evt)
            {
                case TCSEvent.AlerterPressed:
                case TCSEvent.AlerterReleased:
                case TCSEvent.AlerterReset:
                    if (!Activated || VigilanceEmergency)
                        return;

                    if (VigilanceMonitor != null)
                    {
                        VigilanceAlarmTimer.Start();
                        VigilanceAlarm = VigilanceAlarmTimer.Triggered;
                        if (AlerterSound())
                            SetVigilanceAlarm(false);
                    }

                    if (OverspeedMonitor != null)
                        if (OverspeedWarning && OverspeedMonitor.ResetOnResetButton)
                            OverspeedAlarmTimer.Start();
                    break;
            }
        }

        public override void SetEmergency()
        {
            SetPenaltyApplicationDisplay(true);
            if (EmergencyStopMonitor != null && !EmergencyStopMonitor.AppliesEmergencyBrake)
            {
                if (IsBrakeFullService() || IsBrakeEmergency())
                    return;
                EngageFullBrake();
            }
            else
            {
                if (IsBrakeEmergency())
                    return;
                SetEmergencyBrake();
            }
            if (EmergencyCausesThrottleDown) SetThrottleController(0f);
            if (EmergencyStopMonitor != null && EmergencyStopMonitor.EmergencyCutsPower) SetPantographsDown();
            if (EmergencyEngagesHorn) SetHorn(true);
        }

        void EngageFullBrake()
        {
            SetFullBrake();
        }

        void UpdateVigilance()
        {
            if (AlerterSound() && !IsAlerterEnabled())
                HandleEvent(TCSEvent.AlerterPressed, String.Empty);

            VigilanceAlarm = VigilanceAlarmTimer.Triggered;
            VigilanceEmergency = VigilanceEmergencyTimer.Triggered;
            SetVigilanceAlarmDisplay(VigilanceAlarm);
            SetVigilanceEmergencyDisplay(VigilanceEmergency);

            if (VigilanceEmergency)
            {
                SetPenaltyApplicationDisplay(true);
                if (VigilanceMonitor.AppliesEmergencyBrake)
                    SetEmergency();
                else if (VigilanceMonitor.AppliesFullBrake)
                    EngageFullBrake();

                if (!VigilancePenaltyTimer.Started)
                    VigilancePenaltyTimer.Start();
                if (SpeedMpS() < 0.1f && VigilancePenaltyTimer.Triggered)
                {
                    VigilanceEmergencyTimer.Stop();
                    VigilancePenaltyTimer.Stop();
                }
                if (AlerterSound())
                    SetVigilanceAlarm(false);
                return;
            }

            if (VigilanceAlarm)
            {
                if (VigilanceMonitor.ResetOnZeroSpeed && SpeedMpS() < 0.1f
                    || SpeedMpS() <= VigilanceMonitor.ResetLevelMpS)
                {
                    HandleEvent(TCSEvent.AlerterPressed, String.Empty);
                    return;
                }
                if (!VigilanceEmergencyTimer.Started)
                    VigilanceEmergencyTimer.Start();
                if (!AlerterSound())
                    SetVigilanceAlarm(true);
            }
            else
            {
                VigilanceEmergencyTimer.Stop();
                if (VigilancePenaltyTimer.Triggered)
                    VigilancePenaltyTimer.Stop();
            }
        }

        void UpdateSpeedControl()
        {
            var overspeedWarning = false;

            // Not sure about the difference of the following two. Seems both of them are used.
            if (OverspeedMonitor.TriggerOnOverspeedMpS > 0)
                overspeedWarning |= SpeedMpS() > OverspeedMonitor.TriggerOnOverspeedMpS;
            if (OverspeedMonitor.CriticalLevelMpS > 0)
                overspeedWarning |= SpeedMpS() > OverspeedMonitor.CriticalLevelMpS;
            if (OverspeedMonitor.TriggerOnTrackOverspeed)
                overspeedWarning |= SpeedMpS() > CurrentSpeedLimitMpS + OverspeedMonitor.TriggerOnTrackOverspeedMarginMpS;

            OverspeedWarning = overspeedWarning;
            SetOverspeedWarningDisplay(overspeedWarning);

            OverspeedAlarm = OverspeedAlarmTimer.Triggered;

            if (OverspeedAlarm && IsAlerterEnabled())
            {
                SetPenaltyApplicationDisplay(true);
                if (OverspeedMonitor.AppliesEmergencyBrake)
                    SetEmergency();
                else if (OverspeedMonitor.AppliesFullBrake)
                    EngageFullBrake();

                if (!OverspeedPenaltyTimer.Started)
                    OverspeedPenaltyTimer.Start();

                if (SpeedMpS() < 0.1f && OverspeedPenaltyTimer.Triggered)
                {
                    OverspeedAlarmTimer.Stop();
                    OverspeedPenaltyTimer.Stop();
                }
                return;
            }
            if (OverspeedWarning)
            {
                if (!OverspeedAlarmTimer.Started)
                    OverspeedAlarmTimer.Start();
            }
            else
            {
                OverspeedAlarmTimer.Stop();
                if (OverspeedPenaltyTimer.Triggered)
                    OverspeedPenaltyTimer.Stop();
            }
        }
    }
}

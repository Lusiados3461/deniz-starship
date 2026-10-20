using System;
using System.Linq;
using System.Collections;
using System.IO;
using UnityEngine;
using ModuleWheels;
using KSP.UI.Screens;
using StarshipLaunchExpansion.Modules;
using Expansions.Serenity;
using MuMech;

// Only enabled while validating this pack, and restricted to its separate test save.
[KSPAddon(KSPAddon.Startup.MainMenu,true)]
public class DenizStarshipTestBridge : MonoBehaviour
{
    string Dir {get{return Path.Combine(KSPUtil.ApplicationRootPath,"DenizStarshipValidation");}}
    float last;
    // Read-only wall-clock FPS and native UT progress; optional test-save only.
    float benchmarkStartWall,benchmarkFps,benchmarkWindow;
    int benchmarkStartFrame;
    double benchmarkStartUT,benchmarkSimRatio;
    GameScenes benchmarkScene;
    bool benchmarkReady;
    void BenchmarkTick(){
        if(!TestSave){benchmarkReady=false;return;}
        float wall=Time.realtimeSinceStartup;double ut=Planetarium.GetUniversalTime();
        if(!benchmarkReady || benchmarkScene!=HighLogic.LoadedScene){
            benchmarkReady=true;benchmarkScene=HighLogic.LoadedScene;
            benchmarkStartWall=wall;benchmarkStartFrame=Time.frameCount;benchmarkStartUT=ut;
            benchmarkWindow=0;benchmarkFps=0;benchmarkSimRatio=0;return;
        }
        float elapsed=wall-benchmarkStartWall;
        if(elapsed<1)return;
        benchmarkWindow=elapsed;benchmarkFps=(Time.frameCount-benchmarkStartFrame)/elapsed;
        benchmarkSimRatio=HighLogic.LoadedSceneIsFlight?(ut-benchmarkStartUT)/elapsed:0;
        benchmarkStartWall=wall;benchmarkStartFrame=Time.frameCount;benchmarkStartUT=ut;
    }
    public void OnGUI(){
        if(!TestSave || !File.Exists(Path.Combine(Dir,"enabled")) || !benchmarkReady)return;
        var style=new GUIStyle(GUI.skin.box);style.fontSize=18;style.alignment=TextAnchor.MiddleLeft;
        GUI.Box(new Rect(Mathf.Max(10,Screen.width-470),75,440,83),
            "A10 FPS "+benchmarkFps.ToString("F1")+"  ("+benchmarkWindow.ToString("F2")+"s window)\n"+
            "Native sim/wall "+benchmarkSimRatio.ToString("F3")+"  pause="+(Time.timeScale==0)+"\n"+
            HighLogic.LoadedScene+"  warp="+TimeWarp.CurrentRate,style);
    }

    string lastCommandID="",lastCommandOp="",lastCommandResult="";
    public void Awake(){DontDestroyOnLoad(this);GameEvents.onStageActivate.Add(StageAudit);GameEvents.onFloatingOriginShift.Add(C1ObserveOriginShift);}
    public void OnDestroy(){if(c1PilotTower!=null){c1PilotTower.CaptureStarting-=C1PilotCaptureStarting;c1PilotTower.CaptureCompleted-=C1PilotCaptureCompleted;}if(c1ReturnVessel!=null)c1ReturnVessel.OnFlyByWire-=C1ReturnInput;if(c1PilotVessel!=null)c1PilotVessel.OnFlyByWire-=C1PilotInput;GameEvents.onStageActivate.Remove(StageAudit);GameEvents.onFloatingOriginShift.Remove(C1ObserveOriginShift);foreach(var v in FlightGlobals.Vessels){v.OnFlyByWire-=MissionLandingInput;v.OnFlyByWire-=MissionRcsCourseInput;}}
    void StageAudit(int stage){if(TestSave)File.AppendAllText(Path.Combine(Dir,"staging-audit.log"),DateTime.UtcNow.ToString("o")+" stage="+stage+"\n"+Environment.StackTrace+"\n");}
    public void Update(){BenchmarkTick();A9ObserveFirstReady();if(!File.Exists(Path.Combine(Dir,"enabled"))){Destroy(this);return;};if(Time.realtimeSinceStartup-last<.5)return;last=Time.realtimeSinceStartup;try{MissionWatchdog();A6FlightWatchdog();if((a6RcsManual || a6RcsDynamicLateral) && Time.timeScale>0 && Time.realtimeSinceStartup-a6RcsHeartbeat>15 && TestSave && FlightGlobals.ActiveVessel!=null)A6WatchdogPause(FlightGlobals.ActiveVessel);if(a6RcsDynamicLateral && Time.timeScale>0)A6LateralSteering();Snapshot();var f=Path.Combine(Dir,"command.cfg");if(File.Exists(f)){var c=ConfigNode.Load(f);File.Delete(f);lastCommandID=c.GetValue("requestID")??"";lastCommandOp=c.GetValue("op")??"";lastCommandResult="running";try{if(c.HasValue("issuedAtUtc")){var issued=DateTime.Parse(c.GetValue("issuedAtUtc"),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal);double age=(DateTime.UtcNow-issued).TotalSeconds;if(age>15 || age < -5)throw new Exception("Stale queued command rejected before execution");}Run(c);lastCommandResult="completed";}catch(Exception error){lastCommandResult="error: "+error.Message;throw;}}}catch(Exception e){File.AppendAllText(Path.Combine(Dir,"errors.txt"),e+"\n");}}
    bool TestSave {get{return HighLogic.SaveFolder=="Deniz Starship Missions Test";}}
    // A7/A8 development controls call installed native maneuver/landing APIs.
    // Flight state and fuel remain owned by KSP's normal simulation.
    bool missionManaged;
    bool missionNativeKerbinLanding;
    bool missionPauseLocked;
    bool missionFeedbackLanding;
    bool missionFeedbackDuna;
    double missionLandingNorthBiasDegrees,missionLandingEarlyPitchBiasDegrees,missionLandingMinimumCoast=.6;
    bool missionLandingSite;
    double missionLandingSiteLatitude,missionLandingSiteLongitude,missionLandingSiteDistance,missionLandingVerticalFactor=24;
    Vector3d missionLandingSiteWantedVelocity;
    bool missionLandingContact;
    bool missionLandingBurnStarted;
    double missionLandingIgnitionThreshold,missionLandingIgnitionRadar,missionLandingCoastStartUT;
    string missionLandingPhase="Off",missionLandingControlFile="";
    double missionLandingPreviousUT,missionLandingPreviousVertical,missionLandingPreviousEngineVertical,missionLandingExternal,missionLandingNextLog;
    double missionLandingWantedVertical,missionLandingTargetThrottle,missionLandingTargetPitch;
    double missionLandingPredictedHorizontal,missionLandingEstimatedHorizontalThrust;
    ConfigNode missionLandingLastAir;
    Guid missionOwner;
    uint[] missionParts=new uint[0];
    string[] missionCrew=new string[0];
    float missionHeartbeat;
    string missionWatchdogReason="";
    double missionIntegratedThrustDv;
    long missionImpulseSamples;
    bool MissionIntact(Vessel v) {
        return TestSave && HighLogic.LoadedSceneIsFlight && v!=null && v.id==missionOwner &&
            v.parts.Select(p=>p.flightID).OrderBy(x=>x).SequenceEqual(missionParts) &&
            v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(missionCrew);
    }
    void FixedUpdate() {
        C1ObserveConstraintFrame();
        C1ReturnTick();
        C1PilotTick();
        ReleaseObservationTick();
        MissionRcsCourseTick();
        if(!missionManaged || !MissionIntact(FlightGlobals.ActiveVessel) || Time.timeScale<=0)return;
        var v=FlightGlobals.ActiveVessel;if(v.packed)return;
        if((missionNativeKerbinLanding || missionFeedbackLanding) && v.mainBody.name=="Kerbin" && !v.Landed && !v.Splashed){
            string reason=null;
            if(missionFeedbackLanding && v.radarAltitude>150 && v.parts.Where(p=>p.partInfo.name=="DenizStarshipCore").SelectMany(p=>p.Resources.Cast<PartResource>()).Where(r=>r.resourceName=="LqdMethane" || r.resourceName=="Oxidizer").Sum(r=>r.amount*r.info.density)<4)reason="Ordinary landing pilot four-tonne final reserve guard";
            if(v.radarAltitude<250 && v.verticalSpeed < -80)reason="Native Kerbin landing low-altitude descent safety guard";
            if(v.parts.Max(p=>Math.Max(p.temperature/p.maxTemp,p.skinTemperature/p.skinMaxTemp))>.85)reason="Native Kerbin landing thermal safety guard";
            if(reason!=null){
                missionWatchdogReason=reason;missionPauseLocked=true;
                var off=new ConfigNode();off.AddValue("op","controllersoff");ControllerCommand(off,v);
                foreach(var engine in v.FindPartModulesImplementing<ModuleEngines>())engine.Shutdown();
                FlightInputHandler.state.mainThrottle=0;v.ActionGroups.SetGroup(KSPActionGroup.RCS,false);FlightDriver.SetPause(true,false);
                File.AppendAllText(Path.Combine(Dir,"mission-watchdog.log"),DateTime.UtcNow.ToString("o")+" "+reason+"\n");return;
            }
        }
        double mass=v.GetTotalMass();
        if(mass>0){missionIntegratedThrustDv+=v.FindPartModulesImplementing<ModuleEngines>().Sum(x=>x.finalThrust)/mass*Time.fixedDeltaTime;missionImpulseSamples++;}
    }
    // Opt-in ordinary translation input before atmosphere. The vector below is
    // an observation integral/controller setpoint, never a vessel velocity write.
    bool missionRcsCourseActive,missionRcsCourseObserving,missionRcsCourseDone;
    Vector3d missionRcsCourseRequest,missionRcsCourseDv,missionRcsCourseEast,missionRcsCourseNorth,missionRcsCourseUp,missionRcsCourseForce;
    double missionRcsCourseStartUT,missionRcsCourseScalarDv;
    string missionRcsCourseFile="";
    System.Collections.Generic.Dictionary<ModuleRCSFX,bool[]> missionRcsCourseRotation=new System.Collections.Generic.Dictionary<ModuleRCSFX,bool[]>();
    void MissionRcsCourseStop(Vessel v){
        missionRcsCourseActive=false;missionRcsCourseObserving=false;
        v.OnFlyByWire-=MissionRcsCourseInput;
        FlightInputHandler.state.X=FlightInputHandler.state.Y=FlightInputHandler.state.Z=0;
        foreach(var pair in missionRcsCourseRotation)if(pair.Key!=null){pair.Key.enablePitch=pair.Value[0];pair.Key.enableYaw=pair.Value[1];pair.Key.enableRoll=pair.Value[2];}
        missionRcsCourseRotation.Clear();
    }
    void MissionRcsCoursePause(Vessel v,string reason){
        missionWatchdogReason=reason;missionPauseLocked=true;
        var off=new ConfigNode();off.AddValue("op","controllersoff");ControllerCommand(off,v);
        foreach(var e in v.FindPartModulesImplementing<ModuleEngines>())e.Shutdown();
        FlightInputHandler.state.mainThrottle=0;v.ActionGroups.SetGroup(KSPActionGroup.RCS,false);FlightDriver.SetPause(true,false);
        File.AppendAllText(Path.Combine(Dir,"mission-watchdog.log"),DateTime.UtcNow.ToString("o")+" "+reason+"\n");
    }
    void MissionRcsCourseTick(){
        if(!missionRcsCourseObserving || Time.timeScale<=0)return;
        var v=FlightGlobals.ActiveVessel;
        if(!MissionIntact(v) || v.packed || Time.realtimeSinceStartup-missionHeartbeat>15){if(v!=null)MissionRcsCoursePause(v,"Preentry RCS exact mission/heartbeat/unpack guard");return;}
        if(v.mainBody.name!="Kerbin" || v.altitude<110000 || v.altitude>250000 || v.ctrlState.mainThrottle>.0001f || v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust)>.01f || v.angularVelocity.magnitude>.08f || v.parts.Max(p=>Math.Max(p.temperature/p.maxTemp,p.skinTemperature/p.skinMaxTemp))>.5 || v.parts.Where(p=>p.partInfo.name=="DenizStarshipCore").SelectMany(p=>p.Resources.Cast<PartResource>()).Where(r=>r.resourceName=="LqdMethane" || r.resourceName=="Oxidizer").Sum(r=>r.amount*r.info.density)<40 || Planetarium.GetUniversalTime()-missionRcsCourseStartUT>45){MissionRcsCoursePause(v,"Preentry RCS altitude/main/attitude/heat/fuel/duration guard");return;}
        Vector3d force=Vector3d.zero;
        // Native ModuleRCS.FixedUpdate uses -(useZaxis?forward:up)*thrustForces.
        // Reading these fields does not apply force. Frame ordering is disclosed.
        foreach(var r in v.FindPartModulesImplementing<ModuleRCSFX>())if(r.rcsEnabled && !r.isJustForShow && r.thrustForces!=null && r.thrusterTransforms!=null){
            for(int i=0;i<Math.Min(r.thrustForces.Length,r.thrusterTransforms.Count);i++)force-=(Vector3d)(r.useZaxis?r.thrusterTransforms[i].forward:r.thrusterTransforms[i].up)*r.thrustForces[i];
        }
        missionRcsCourseForce=force;double mass=v.GetTotalMass();
        missionRcsCourseDv+=force/mass*Time.fixedDeltaTime;missionRcsCourseScalarDv+=force.magnitude/mass*Time.fixedDeltaTime;
        var row=new ConfigNode("NATIVE_RCS_COURSE_FRAME");row.AddValue("UT",Planetarium.GetUniversalTime());row.AddValue("forceWorld_kN",force);row.AddValue("mass_t",mass);row.AddValue("fixedDeltaTime",Time.fixedDeltaTime);row.AddValue("integratedWorld_m_per_s",missionRcsCourseDv);row.AddValue("integratedEast_m_per_s",Vector3d.Dot(missionRcsCourseDv,missionRcsCourseEast));row.AddValue("integratedNorth_m_per_s",Vector3d.Dot(missionRcsCourseDv,missionRcsCourseNorth));row.AddValue("integratedUp_m_per_s",Vector3d.Dot(missionRcsCourseDv,missionRcsCourseUp));row.AddValue("scalarSampledRcsDeltaV",missionRcsCourseScalarDv);row.AddValue("remaining_m_per_s",(missionRcsCourseRequest-missionRcsCourseDv).magnitude);row.AddValue("translationX",v.ctrlState.X);row.AddValue("translationY",v.ctrlState.Y);row.AddValue("translationZ",v.ctrlState.Z);row.AddValue("parts",v.parts.Count);row.AddValue("crew",v.GetCrewCount());row.AddValue("mainThrust",v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust));row.AddValue("RCS",v.ActionGroups[KSPActionGroup.RCS]);row.AddValue("done",missionRcsCourseDone);File.AppendAllText(Path.Combine(Dir,missionRcsCourseFile),row.ToString()+"\n");
        if(missionRcsCourseDv.magnitude>5.5 || missionRcsCourseScalarDv>7 || (Planetarium.GetUniversalTime()-missionRcsCourseStartUT>.3 && Vector3d.Dot(missionRcsCourseDv,missionRcsCourseRequest)<-.05)){MissionRcsCoursePause(v,"Preentry RCS measured magnitude/sign guard");return;}
        if(missionRcsCourseActive && (missionRcsCourseRequest-missionRcsCourseDv).magnitude<.03){missionRcsCourseActive=false;missionRcsCourseDone=true;FlightInputHandler.state.X=FlightInputHandler.state.Y=FlightInputHandler.state.Z=0;FlightDriver.SetPause(true,false);}
    }
    void MissionRcsCourseInput(FlightCtrlState input){
        if(!missionRcsCourseObserving)return;
        input.mainThrottle=0;input.X=input.Y=input.Z=0;
        var v=FlightGlobals.ActiveVessel;
        if(!missionRcsCourseActive || Time.timeScale<=0 || !MissionIntact(v) || Time.realtimeSinceStartup-missionHeartbeat>15)return;
        var remaining=missionRcsCourseRequest-missionRcsCourseDv;
        // Primary native IL constructs inputLin=(X,Z,Y), rotates by
        // ReferenceTransform, dots with the exhaust axis, applies opposite force.
        var local=v.ReferenceTransform.InverseTransformDirection((Vector3)(-remaining.normalized*.2));
        input.X=local.x;input.Z=local.y;input.Y=local.z;
    }
    // Development-only direct ordinary input pilot. No physical-state/force,
    // resource, orbit, temperature-limit or clock setters are used here.
    void MissionLandingInput(FlightCtrlState input){
        if(!missionFeedbackLanding || Time.timeScale<=0)return;
        var v=FlightGlobals.ActiveVessel;
        if(!MissionIntact(v) || Time.realtimeSinceStartup-missionHeartbeat>15){MissionWatchdog();input.mainThrottle=0;return;}
        if(v.packed)return;
        if(missionFeedbackDuna){MissionDunaLandingInput(input,v);return;}
        double ut=Planetarium.GetUniversalTime(),vertical=v.verticalSpeed,horizontal=v.horizontalSrfSpeed;
        var c=v.FindPartModulesImplementing<DenizStarshipControls>().First(x=>x.part.partInfo.name=="DenizStarshipCore");
        if(c.legsExtended && !c.legsLocked && v.FindPartModulesImplementing<ModuleWheelDeployment>().Count()==4 && v.FindPartModulesImplementing<ModuleWheelDeployment>().All(x=>x.position>.999f))c.LegLock();
        if(v.Landed || v.Splashed){
            input.mainThrottle=0;FlightInputHandler.state.mainThrottle=0;
            if(!missionLandingContact){
                missionLandingContact=true;missionLandingPhase="Native surface contact";
                var contact=MissionLandingRecord(v,ut);contact.AddValue("exactImpactSpeed","Unavailable; last native airborne / first native contact bracket");
                if(missionLandingLastAir!=null)contact.AddNode(missionLandingLastAir.CreateCopy());
                File.AppendAllText(Path.Combine(Dir,missionLandingControlFile),contact.ToString()+"\n");
                foreach(var e in v.FindPartModulesImplementing<ModuleEngines>())e.Shutdown();
                var core=v.GetMasterMechJeb();core.attitude.users.Remove(this);
                v.ActionGroups.SetGroup(KSPActionGroup.RCS,false);v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);v.ActionGroups.SetGroup(KSPActionGroup.SAS,true);
                FlightDriver.SetPause(true,false);
            }
            return;
        }
        double mass=v.GetTotalMass(),pitch=90-Vector3d.Angle(v.GetTransform().up,v.upAxis);
        var engines=v.FindPartModulesImplementing<ModuleEngines>().Where(e=>e.EngineIgnited).ToArray();
        double available=engines.Sum(e=>e.maxThrust*e.realIsp/e.atmosphereCurve.Evaluate(0)),maxA=available/mass;
        double engineVertical=engines.Sum(e=>e.finalThrust)/mass*Math.Sin(pitch*Math.PI/180),dt=ut-missionLandingPreviousUT;
        if(missionLandingPreviousUT>0 && dt>.001 && dt<.2){
            double external=(vertical-missionLandingPreviousVertical)/dt-.5*(engineVertical+missionLandingPreviousEngineVertical);
            missionLandingExternal=.85*missionLandingExternal+.15*Math.Max(-25,Math.Min(60,external));
        }
        missionLandingPreviousUT=ut;missionLandingPreviousVertical=vertical;missionLandingPreviousEngineVertical=engineVertical;
        // Actual contact06 rebounded after V-2.506/H3.062. Earlier real
        // frames showed horizontal velocity reversing while attitude lagged.
        // Use ordinary input only: anticipate two seconds of the estimated
        // current axial thrust, with a slower .8m/s terminal descent floor.
        // Estimate excludes individual nozzle gimbal; log it as an estimate.
        double height=Math.Max(0,v.radarAltitude-27);
        Vector3d siteError=Vector3d.zero;
        missionLandingVerticalFactor=24;
        if(missionLandingSite){
            var here=v.mainBody.GetWorldSurfacePosition(v.latitude,v.longitude,v.altitude);
            siteError=v.mainBody.GetWorldSurfacePosition(missionLandingSiteLatitude,missionLandingSiteLongitude,v.altitude)-here;
            siteError-=v.upAxis*Vector3d.Dot(siteError,v.upAxis);missionLandingSiteDistance=siteError.magnitude;
            // Ordinary input-only slower approach permits measured horizontal
            // correction above terminal height; all fixed safety guards remain.
            if(height>100 && missionLandingSiteDistance>80)
                missionLandingVerticalFactor=12+12*Math.Max(0,Math.Min(1,(height-400)/600));
        }
        missionLandingWantedVertical=-Math.Min(400,Math.Max(.8,Math.Sqrt((height<25?2:missionLandingVerticalFactor)*height)));
        double feed=height<25?0:12,gain=height<25?2:.8;
        double requiredVertical=Math.Max(.2,feed+gain*(missionLandingWantedVertical-vertical)-missionLandingExternal);
        var hVelocity=v.srf_velocity-v.upAxis*Vector3d.Dot(v.srf_velocity,v.upAxis);
        var thrustAxis=(Vector3d)v.GetTransform().up;
        var hThrustAxis=thrustAxis-v.upAxis*Vector3d.Dot(thrustAxis,v.upAxis);
        missionLandingEstimatedHorizontalThrust=horizontal>.001?Vector3d.Dot(hThrustAxis,hVelocity.normalized)*engines.Sum(e=>e.finalThrust)/mass:0;
        missionLandingPredictedHorizontal=Math.Max(0,horizontal+2*missionLandingEstimatedHorizontalThrust);
        Vector3d guideError=hVelocity;
        if(missionLandingSite && height>100){
            double remaining=Math.Max(4,2*Math.Sqrt(height/missionLandingVerticalFactor)+1.5);
            double blend=Math.Max(0,Math.Min(1,(height-100)/150));
            missionLandingSiteWantedVelocity=siteError/remaining;
            if(missionLandingSiteWantedVelocity.magnitude>80)missionLandingSiteWantedVelocity=missionLandingSiteWantedVelocity.normalized*80;
            missionLandingSiteWantedVelocity*=blend;
            guideError=hVelocity+hThrustAxis*engines.Sum(e=>e.finalThrust)/mass*2-missionLandingSiteWantedVelocity;
            missionLandingPredictedHorizontal=guideError.magnitude;
        }else missionLandingSiteWantedVelocity=Vector3d.zero;
        bool guidingSite=missionLandingSite && height>100;
        double wantedPitch=horizontal>25?Math.Atan2(Math.Max(8,-vertical),guidingSite?guideError.magnitude:horizontal)*180/Math.PI:Math.Atan2(requiredVertical,Math.Max(.05,missionLandingPredictedHorizontal/3))*180/Math.PI;
        if(horizontal>200)wantedPitch+=missionLandingEarlyPitchBiasDegrees;
        wantedPitch=Math.Max(25,Math.Min(89.9,wantedPitch));if(height<60)wantedPitch=Math.Max(85,wantedPitch);
        missionLandingTargetPitch=wantedPitch;
        Vector3d horizontalDirection=guideError.sqrMagnitude>1e-8?-guideError.normalized:Vector3d.zero;
        if(horizontal>100 && missionLandingNorthBiasDegrees>0){
            var here=v.mainBody.GetWorldSurfacePosition(v.latitude,v.longitude,v.altitude);
            var north=(v.mainBody.GetWorldSurfacePosition(v.latitude+.0001,v.longitude,v.altitude)-here).normalized;
            north-=hVelocity.normalized*Vector3d.Dot(north,hVelocity.normalized);north.Normalize();
            double bias=missionLandingNorthBiasDegrees*Math.PI/180;
            horizontalDirection=horizontalDirection*Math.Cos(bias)+north*Math.Sin(bias);
        }
        Vector3d direction=v.upAxis*Math.Sin(wantedPitch*Math.PI/180)+horizontalDirection*Math.Cos(wantedPitch*Math.PI/180);
        v.GetMasterMechJeb().attitude.attitudeTo(direction,AttitudeReference.INERTIAL,this);
        missionLandingTargetThrottle=maxA>1?Math.Max(0,Math.Min(1,requiredVertical/(maxA*Math.Max(.05,Math.Sin(pitch*Math.PI/180))))):0;
        double brakingNet=Math.Max(5,.9*maxA*Math.Max(.1,Math.Sin(pitch*Math.PI/180))+Math.Min(0,missionLandingExternal));
        missionLandingIgnitionThreshold=vertical*vertical/(2*brakingNet)+300+.6*Math.Abs(vertical);
        if(!missionLandingBurnStarted){
            if(missionLandingCoastStartUT==0)missionLandingCoastStartUT=ut;
            if(ut-missionLandingCoastStartUT>=missionLandingMinimumCoast && v.radarAltitude<=missionLandingIgnitionThreshold){missionLandingBurnStarted=true;missionLandingIgnitionRadar=v.radarAltitude;}
            else missionLandingTargetThrottle=0;
        }
        input.mainThrottle=(float)missionLandingTargetThrottle;FlightInputHandler.state.mainThrottle=input.mainThrottle;
        missionLandingPhase=!missionLandingBurnStarted?"Ordinary coast to measured braking threshold":horizontal>25?"Ordinary retrograde braking":"Ordinary terminal velocity feedback";
        var row=MissionLandingRecord(v,ut);missionLandingLastAir=row;
        if(ut>=missionLandingNextLog){File.AppendAllText(Path.Combine(Dir,missionLandingControlFile),row.ToString()+"\n");missionLandingNextLog=ut+.1;}
    }
    // A8 opt-in frame-by-frame ordinary throttle and native MJ vector attitude.
    // KSP owns position, velocity, mass, fuel, temperature, clock and contact.
    // Read-only terrain rays along each actual native wheel suspension axis.
    // Hub-to-ground distance alone omits2.736m native suspension travel.
    bool DunaTerrainObservation(Vessel v,out double clearance,out Vector3d normal){
        clearance=double.PositiveInfinity;normal=Vector3d.zero;int count=0;
        foreach(var p in v.parts.Where(p=>p.partInfo.name=="DenizStarshipLeg")){
            var wheel=p.FindModuleImplementing<ModuleWheelBase>();
            var controller=DiagnosticMember(wheel,"Wheel")??DiagnosticMember(wheel,"wheel");
            var hub=DiagnosticMember(controller,"WheelCenter");var down=DiagnosticMember(controller,"WheelDown");var collider=DiagnosticMember(controller,"wheelCollider");
            if(!(hub is Vector3) || !(down is Vector3) || collider==null)continue;
            var axis=((Vector3)down).normalized;
            double reach=Convert.ToDouble(DiagnosticMember(collider,"suspensionDistance"))+Convert.ToDouble(DiagnosticMember(collider,"radius"));
            foreach(var hit in Physics.RaycastAll((Vector3)hub-axis*200,axis,400).OrderBy(h=>h.distance)){
                if(hit.collider==null || hit.collider.gameObject.layer!=15 || hit.collider.GetComponentInParent<Part>()!=null || Vector3d.Dot(hit.normal,v.upAxis)<=0)continue;
                clearance=Math.Min(clearance,hit.distance-200-reach);normal+=(Vector3d)hit.normal;count++;break;
            }
        }
        if(count!=4 || normal.sqrMagnitude<.1)return false;
        normal.Normalize();return Vector3d.Angle(normal,v.upAxis)<=7;
    }
    void MissionDunaLandingInput(FlightCtrlState input,Vessel v){
        double ut=Planetarium.GetUniversalTime();
        string reason=null;
        var control=v.FindPartModulesImplementing<DenizStarshipControls>().FirstOrDefault(x=>x.part.partInfo.name=="DenizStarshipCore");
        var tank=v.parts.FirstOrDefault(p=>p.partInfo.name=="DenizStarshipCore");
        var panels=v.FindPartModulesImplementing<ModuleDeployableSolarPanel>();
        if(!ExactA8DunaBase(v) || v.parts.Count!=44 || v.GetCrewCount()!=2 || control==null || !control.legsExtended || !control.legsLocked || control.cargoOpen || !control.cargoLocked || v.ActionGroups[KSPActionGroup.RCS] || v.ActionGroups[KSPActionGroup.SAS])reason="A8 exact44part native controls/locks/RCS-SAS guard";
        if(v.radarAltitude>700 || v.srf_velocity.magnitude>60 || v.angularVelocity.magnitude>.2f || v.dynamicPressurekPa>2 || v.parts.Max(p=>Math.Max(p.temperature/p.maxTemp,p.skinTemperature/p.skinMaxTemp))>.8 || v.parts.SelectMany(p=>p.Resources.Cast<PartResource>()).Where(r=>r.resourceName=="ElectricCharge").Sum(r=>r.amount)<1000)reason="A8 native frame altitude/speed/spin/pressure/heat/battery guard";
        if(tank==null || tank.Resources.Cast<PartResource>().Where(r=>r.resourceName=="LqdMethane" || r.resourceName=="Oxidizer").Any(r=>r.amount/r.maxAmount<.04))reason="A8 native four-percent core mixture reserve guard";
        if(panels.Count!=4 || panels.Any(p=>p.deployState==ModuleDeployablePart.DeployState.BROKEN))reason="A8 native panel integrity guard";
        if(reason!=null){input.mainThrottle=0;MissionRcsCoursePause(v,reason);return;}
        if(v.Landed || v.Splashed){
            input.mainThrottle=0;FlightInputHandler.state.mainThrottle=0;
            if(!missionLandingContact){
                missionLandingContact=true;missionLandingPhase="A8 first native surface contact";
                var contact=MissionLandingRecord(v,ut);contact.AddValue("exactImpactSpeed","Unavailable; adjacent native airborne/contact bracket only");
                if(missionLandingLastAir!=null)contact.AddNode(missionLandingLastAir.CreateCopy());
                File.AppendAllText(Path.Combine(Dir,missionLandingControlFile),contact.ToString()+"\n");
                foreach(var e in v.FindPartModulesImplementing<ModuleEngines>())e.Shutdown();
                v.GetMasterMechJeb().attitude.users.Remove(this);
                v.ActionGroups.SetGroup(KSPActionGroup.RCS,false);v.ActionGroups.SetGroup(KSPActionGroup.SAS,false);
                FlightDriver.SetPause(true,false);
            }
            return;
        }
        double vertical=v.verticalSpeed,mass=v.GetTotalMass(),pitch=90-Vector3d.Angle(v.GetTransform().up,v.upAxis);
        var engines=v.FindPartModulesImplementing<ModuleEngines>().Where(e=>e.EngineIgnited).ToArray();
        double available=engines.Sum(e=>e.maxThrust*e.realIsp/e.atmosphereCurve.Evaluate(0)),maxA=available/mass;
        if(maxA<5 || engines.Any(e=>e.part.partInfo.name=="DenizCargoEngineMedium") || pitch<70){input.mainThrottle=0;MissionRcsCoursePause(v,"A8 existing main engine authority/pitch guard");return;}
        double gravity=v.mainBody.gravParameter/Math.Pow(v.mainBody.Radius+v.altitude,2);
        // The 27m conservative radar-to-foot reference is an estimate from
        // actual22.8m contact. Final metres use a slow0.45m/s descent floor.
        double observedClearance=0;Vector3d groundNormal=v.upAxis;
        bool terrainObserved=v.radarAltitude<100 && DunaTerrainObservation(v,out observedClearance,out groundNormal);
        double height=Math.Max(0,terrainObserved?observedClearance:v.radarAltitude-27);
        missionLandingWantedVertical=-Math.Min(20,Math.Max(.35,Math.Sqrt(3*height)));
        double feed=height>.1 && height<133.333?1.5:0;
        double requiredVertical=Math.Max(0,gravity+feed+1.2*(missionLandingWantedVertical-vertical));
        var hVelocity=v.srf_velocity-v.upAxis*Vector3d.Dot(v.srf_velocity,v.upAxis);
        var thrustAxis=(Vector3d)v.GetTransform().up;
        var hAxis=thrustAxis-v.upAxis*Vector3d.Dot(thrustAxis,v.upAxis);
        var predicted=hVelocity+hAxis*engines.Sum(e=>e.finalThrust)/mass*1.5;
        var horizontalAcceleration=-predicted*.6;
        double limitDegrees=height>80?15:height>25?8:height>8?2:1;
        double horizontalLimit=Math.Max(gravity,requiredVertical)*Math.Tan(limitDegrees*Math.PI/180);
        if(horizontalAcceleration.magnitude>horizontalLimit)horizontalAcceleration=horizontalAcceleration.normalized*horizontalLimit;
        double terrainBlend=terrainObserved?Math.Max(0,Math.Min(1,(8-height)/6)):0;
        // Last2m hold measured terrain normal; preceding6m blend gradually.
        // Native horizontal velocity remains observed, never assigned.
        Vector3d guideUp=(v.upAxis*(1-terrainBlend)+groundNormal*terrainBlend).normalized;
        Vector3d direction=guideUp*Math.Max(gravity,requiredVertical)+(height<2 && terrainObserved?Vector3d.zero:horizontalAcceleration);
        var attitude=v.GetMasterMechJeb().attitude;
        attitude.attitudeTo(direction.normalized,AttitudeReference.INERTIAL,this);
        attitude.SetAxisControl(true,true,false);
        missionLandingTargetPitch=90-Vector3d.Angle(direction,v.upAxis);
        missionLandingPredictedHorizontal=predicted.magnitude;
        missionLandingEstimatedHorizontalThrust=hAxis.magnitude*engines.Sum(e=>e.finalThrust)/mass;
        missionLandingExternal=-gravity;
        missionLandingTargetThrottle=Math.Max(0,Math.Min(.6,requiredVertical/(maxA*Math.Max(.3,Math.Sin(pitch*Math.PI/180)))));
        input.mainThrottle=(float)missionLandingTargetThrottle;FlightInputHandler.state.mainThrottle=input.mainThrottle;
        missionLandingBurnStarted|=input.mainThrottle>0;
        missionLandingPhase=vertical>0?"A8 ordinary ascending coast to descent":height>133?"A8 ordinary20mps descent and horizontal braking":"A8 ordinary soft terminal descent";
        var row=MissionLandingRecord(v,ut);row.AddValue("estimatedFootHeight",height);row.AddValue("footReferenceRadar",27);row.AddValue("nativeWheelTravelTerrainObserved",terrainObserved);row.AddValue("fullNativeWheelTravelClearance",observedClearance);row.AddValue("measuredTerrainNormalWorld",groundNormal);row.AddValue("measuredTerrainBlend",terrainBlend);row.AddValue("horizontalLeadSeconds",1.5);row.AddValue("tiltLimitDegrees",limitDegrees);row.AddValue("ordinaryVectorRollAxisFree",true);missionLandingLastAir=row;
        if(ut>=missionLandingNextLog){File.AppendAllText(Path.Combine(Dir,missionLandingControlFile),row.ToString()+"\n");missionLandingNextLog=ut+.05;}
    }
    ConfigNode MissionLandingRecord(Vessel v,double ut){
        var n=new ConfigNode("NATIVE_PILOT_FRAME");n.AddValue("UT",ut);n.AddValue("radarAlt",v.radarAltitude);n.AddValue("verticalSpeed",v.verticalSpeed);n.AddValue("horizontalSpeed",v.horizontalSrfSpeed);n.AddValue("situation",v.situation);n.AddValue("phase",missionLandingPhase);
        n.AddValue("burnStarted",missionLandingBurnStarted);n.AddValue("ignitionThreshold",missionLandingIgnitionThreshold);n.AddValue("ignitionRadar",missionLandingIgnitionRadar);n.AddValue("targetVerticalSpeed",missionLandingWantedVertical);n.AddValue("targetThrottle",missionLandingTargetThrottle);n.AddValue("targetPitch",missionLandingTargetPitch);n.AddValue("externalVerticalAcceleration",missionLandingExternal);
        n.AddValue("measuredSiteGuidance",missionLandingSite);n.AddValue("siteLatitude",missionLandingSiteLatitude);n.AddValue("siteLongitude",missionLandingSiteLongitude);n.AddValue("siteDistance",missionLandingSiteDistance);n.AddValue("siteDesiredHorizontalVelocity",missionLandingSiteWantedVelocity);n.AddValue("verticalProfileFactor",missionLandingVerticalFactor);
        n.AddValue("earlyPitchBiasDegrees",missionLandingEarlyPitchBiasDegrees);n.AddValue("northwardBrakingBiasDegrees",missionLandingNorthBiasDegrees);n.AddValue("minimumCoastSeconds",missionLandingMinimumCoast);n.AddValue("latitude",v.latitude);n.AddValue("longitude",v.longitude);
        n.AddValue("predictedHorizontalSpeed",missionLandingPredictedHorizontal);n.AddValue("estimatedAxialHorizontalThrustAcceleration",missionLandingEstimatedHorizontalThrust);n.AddValue("attitudeLeadSeconds",2);n.AddValue("surfaceVelocity",v.srf_velocity);n.AddValue("bodyUpAxis",v.upAxis);n.AddValue("shipThrustAxis",v.GetTransform().up);n.AddValue("nativeMainThrottle",v.ctrlState.mainThrottle);n.AddValue("nativeThrust",v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust));n.AddValue("scalarEngineDeltaV",missionIntegratedThrustDv);n.AddValue("parts",v.parts.Count);n.AddValue("crew",v.GetCrewCount());return n;
    }
    bool HasRoverAncestor(Part p){for(var ancestor=p.parent;ancestor!=null;ancestor=ancestor.parent)if(ancestor.partInfo.name=="DenizPayload.roverBody")return true;return false;}
    Guid releaseCarrier;
    uint[] releasePayloadIDs=new uint[0];
    bool releaseWatching;
    string releaseReason="";
    double releaseLastSampleUT;
    int releaseSamples;
    void ReleaseObservationTick(){
        if(!releaseWatching || !TestSave || !HighLogic.LoadedSceneIsFlight || Time.timeScale<=0)return;
        var carrier=FlightGlobals.Vessels.FirstOrDefault(v=>v.id==releaseCarrier);
        if(carrier==null || carrier.packed){releaseReason="Carrier unavailable";releaseWatching=false;FlightDriver.SetPause(true,false);return;}
        var rover=FlightGlobals.Vessels.FirstOrDefault(v=>v!=carrier && v.loaded && !v.packed && v.parts.Any(p=>releasePayloadIDs.Contains(p.flightID)));
        double ut=Planetarium.GetUniversalTime();
        if(rover!=null || ut-releaseLastSampleUT>=.1){
            var n=new ConfigNode();n.AddValue("UT",ut);n.AddValue("carrierID",carrier.id);n.AddValue("carrierParts",carrier.parts.Count);n.AddValue("carrierAngular",carrier.angularVelocity.magnitude);
            foreach(var p in carrier.parts.Concat(rover==null?new Part[0]:rover.parts.ToArray()).Where(p=>releasePayloadIDs.Contains(p.flightID) || p.partInfo.name=="DenizCargoArm" || p.partInfo.name=="DenizPayloadRelease")){
                var pn=n.AddNode("PART_BODY");pn.AddValue("id",p.flightID);pn.AddValue("name",p.partInfo.name);pn.AddValue("vesselID",p.vessel.id);pn.AddValue("rootLocalPosition",carrier.rootPart.transform.InverseTransformPoint(p.transform.position));
                if(p.rb!=null){pn.AddValue("angularVelocity",p.rb.angularVelocity);pn.AddValue("angularSpeed",p.rb.angularVelocity.magnitude);pn.AddValue("velocity",p.rb.velocity);if(carrier.rootPart.rb!=null)pn.AddValue("velocityRelativeToCarrierRoot",p.rb.velocity-carrier.rootPart.rb.velocity);}
            }
            File.AppendAllText(Path.Combine(Dir,"mun-release-observation.cfg"),n.ToString()+"\n");releaseLastSampleUT=ut;releaseSamples++;
        }
        if(rover!=null){releaseReason="Native payload separation observed; paused on first observed physics frame";releaseWatching=false;FlightDriver.SetPause(true,false);}
        else if(releaseSamples>3000){releaseReason="Observation sample limit";releaseWatching=false;FlightDriver.SetPause(true,false);}
    }
    void MissionWatchdog() {
        if(!missionManaged || Time.timeScale<=0)return;
        var v=FlightGlobals.ActiveVessel;
        if(MissionIntact(v) && Time.realtimeSinceStartup-missionHeartbeat<=15)return;
        missionWatchdogReason=MissionIntact(v)?"Native mission heartbeat lost":"Native vessel/part/crew guard changed";
        missionManaged=false;missionPauseLocked=true;
        if(TestSave && HighLogic.LoadedSceneIsFlight && v!=null){
            TimeWarp.fetch.CancelAutoWarp();TimeWarp.SetRate(0,true);
            var off=new ConfigNode();off.AddValue("op","controllersoff");ControllerCommand(off,v);
            foreach(var e in v.FindPartModulesImplementing<ModuleEngines>())e.Shutdown();
            FlightInputHandler.state.mainThrottle=0;FlightInputHandler.state.wheelThrottle=0;
            v.ActionGroups.SetGroup(KSPActionGroup.RCS,false);FlightDriver.SetPause(true,false);
        }
        File.AppendAllText(Path.Combine(Dir,"mission-watchdog.log"),DateTime.UtcNow.ToString("o")+" "+missionWatchdogReason+"\n");
    }
    // Exact immutable native IDs from the accepted actual A8 Base launch.
    // Ordinary native landing/UI controls only; no physical state assignment.
    readonly uint[] a8DunaFullIDs=new uint[]{68442959u,164606122u,261972312u,292561016u,425179023u,591052743u,661214190u,729358812u,748278955u,1070109257u,1087676920u,1138422621u,1171018439u,1227279675u,1276354407u,1304681850u,1461348588u,1533037926u,1700836021u,1846744820u,1930470685u,2034910789u,2241667006u,2346186657u,2417193326u,2478575221u,2552467366u,2676563186u,2771696987u,2786795243u,2862168125u,2902279053u,2922222368u,2980158285u,3310447595u,3491517784u,3494474002u,3537683853u,3624383355u,3659683180u,3661623640u,3971773761u,4140857503u,4286989676u};
    readonly uint[] a8DunaCarrierIDs=new uint[]{68442959u,292561016u,591052743u,661214190u,729358812u,1070109257u,1087676920u,1138422621u,1171018439u,1276354407u,1461348588u,1930470685u,2034910789u,2241667006u,2346186657u,2417193326u,2478575221u,2771696987u,2786795243u,2902279053u,3310447595u,3494474002u,3624383355u,3661623640u,3971773761u};
    readonly uint[] a8DunaPayloadIDs=new uint[]{164606122u,261972312u,425179023u,748278955u,1227279675u,1304681850u,1533037926u,1700836021u,1846744820u,2552467366u,2676563186u,2862168125u,2922222368u,2980158285u,3491517784u,3537683853u,3659683180u,4140857503u,4286989676u};
    bool ExactA8DunaBase(Vessel v) {
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || v==null || v.mainBody.name!="Duna" || v.packed || v.parts.Count==0)return false;
        var ids=A6PartIDs(v);
        bool carrier=v.id.ToString()=="c726e031-e224-439d-a6ca-c90e3610430c" && v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(new[]{"Eldas Kerman","Virdin Kerman"}) && (ids.SequenceEqual(a8DunaFullIDs) || ids.SequenceEqual(a8DunaCarrierIDs));
        bool payload=v.GetCrewCount()==0 && ids.SequenceEqual(a8DunaPayloadIDs) && v.parts.Any(p=>p.partInfo.name=="DenizLargeHabitat") && v.parts.Count(p=>p.partInfo.name=="DenizCargoEngineMedium")==1;
        return carrier || payload;
    }
    bool MissionCommand(ConfigNode c,Vessel v) {
        string op=c.GetValue("op");
        if(!op.StartsWith("mission"))return false;
        bool actualMunRover=v.GetCrewCount()==0 && v.parts.Count==16 && v.mainBody.name=="Mun" && v.parts.Any(p=>p.partInfo.name=="DenizPayload.roverBody") && v.FindPartModulesImplementing<ModuleWheelBase>().Count()==4;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || (!v.vesselName.StartsWith("D Starship ") && !actualMunRover && !ExactA8DunaBase(v)) || v.parts.Count==0)
            throw new Exception("Dedicated existing recorded Starship/payload flight required");
        if(op=="missionheartbeat"){
            if(!missionManaged || !MissionIntact(v))throw new Exception("Exact armed native mission required");
            missionHeartbeat=Time.realtimeSinceStartup;return true;
        }
        if(v.ctrlState.mainThrottle>.0001f || Time.timeScale>0)throw new Exception("Paused unpowered native mission setup required");
        if(op=="missionambient"){
            // Existing stock rendering slider only; no simulation/physics write.
            float value=(float)Number(c,"value");if(value<0 || value>.5f)throw new Exception("Bounded stock ambient-light slider0..0.5 required");
            GameSettings.AMBIENTLIGHT_BOOSTFACTOR=value;return true;
        }
        if(op=="missioncooling"){
            bool carrier=(v.vesselName=="D Starship 3 Rover" || v.vesselName=="D Starship 1 Empty" || v.vesselName=="D Starship 5 Refuel") && v.parts.Count==25;
            bool baseCarrier=v.vesselName=="D Starship 6C Base" && v.parts.Count==44;
            if((!carrier && !baseCarrier) || v.GetCrewCount()!=2 || v.id.ToString()!=c.GetValue("id") || v.ActionGroups[KSPActionGroup.RCS])throw new Exception("Exact paused unpowered two-crew recorded carrier required for native cooling controls");
            string enabled=c.GetValue("enabled");if(enabled!="true" && enabled!="false")throw new Exception("Explicit native cooling state required");
            var tanks=v.parts.Where(p=>p.partInfo.name=="DenizStarshipCore" || p.partInfo.name=="DenizStarshipCargo").SelectMany(p=>p.Modules.Cast<PartModule>()).Where(m=>m.GetType().Name=="ModuleCryoTank").ToArray();
            if(tanks.Length!=2 || tanks.Any(m=>!Convert.ToBoolean(DiagnosticMember(m,"CoolingAllowed"))))throw new Exception("Two existing native coolable carrier tanks required");
            // Same installed PAW events as normal play; no resource, thermal,
            // cooling-cost or time assignment.
            foreach(var tank in tanks)tank.Events[enabled=="true"?"Enable":"Disable"].Invoke();
            if(tanks.Any(m=>Convert.ToBoolean(DiagnosticMember(m,"CoolingEnabled"))!=(enabled=="true")))throw new Exception("Native cooling event readback failed");
            return true;
        }
        if(op=="missionrcscourse"){
            if(!missionManaged || !MissionIntact(v) || v.mainBody.name!="Kerbin" || v.vesselName!="D Starship 3 Rover" || v.parts.Count!=25 || !v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(new[]{"Liston Kerman","Tanley Kerman"}) || v.packed || v.Landed || v.Splashed || v.altitude<110000 || v.altitude>250000 || v.ActionGroups[KSPActionGroup.RCS] || v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust)>.01f || v.angularVelocity.magnitude>.08f || v.parts.Max(p=>Math.Max(p.temperature/p.maxTemp,p.skinTemperature/p.skinMaxTemp))>.3)throw new Exception("Armed paused exact25part/two-crew cool unpowered preentry110..250km required");
            double east=Number(c,"east"),north=Number(c,"north");if(double.IsNaN(east) || double.IsNaN(north) || double.IsInfinity(east) || double.IsInfinity(north) || Math.Sqrt(east*east+north*north)<.1 || Math.Sqrt(east*east+north*north)>5)throw new Exception("Finite bounded0.1..5m/s ordinary RCS request required");
            string file=c.GetValue("file");if(string.IsNullOrEmpty(file) || Path.GetFileName(file)!=file || !file.StartsWith("A7-rcs-course-") || !file.EndsWith(".cfg") || File.Exists(Path.Combine(Dir,file)))throw new Exception("Unique native RCS observation filename required");
            MissionRcsCourseStop(v);var here=v.mainBody.GetWorldSurfacePosition(v.latitude,v.longitude,v.altitude);missionRcsCourseEast=(v.mainBody.GetWorldSurfacePosition(v.latitude,v.longitude+.0001,v.altitude)-here).normalized;missionRcsCourseNorth=(v.mainBody.GetWorldSurfacePosition(v.latitude+.0001,v.longitude,v.altitude)-here).normalized;missionRcsCourseUp=v.upAxis;missionRcsCourseRequest=missionRcsCourseEast*east+missionRcsCourseNorth*north;missionRcsCourseDv=Vector3d.zero;missionRcsCourseScalarDv=0;missionRcsCourseStartUT=Planetarium.GetUniversalTime();missionRcsCourseFile=file;missionRcsCourseDone=false;
            var backup=new ConfigNode();backup.AddValue("vessel",v.id);foreach(var r in v.FindPartModulesImplementing<ModuleRCSFX>()){missionRcsCourseRotation[r]=new[]{r.enablePitch,r.enableYaw,r.enableRoll};var n=backup.AddNode("RCS_ROTATION");n.AddValue("partID",r.part.flightID);n.AddValue("pitch",r.enablePitch);n.AddValue("yaw",r.enableYaw);n.AddValue("roll",r.enableRoll);r.enablePitch=r.enableYaw=r.enableRoll=false;}backup.Save(Path.Combine(Dir,file+"-rotation-backup.cfg"));
            v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);v.ActionGroups.SetGroup(KSPActionGroup.SAS,true);v.ActionGroups.SetGroup(KSPActionGroup.RCS,true);missionRcsCourseActive=missionRcsCourseObserving=true;missionHeartbeat=Time.realtimeSinceStartup;v.OnFlyByWire-=MissionRcsCourseInput;v.OnFlyByWire+=MissionRcsCourseInput;return true;
        }
        if(op=="missionswitch"){
            if(missionManaged || a6FlightManaged || v.id.ToString()!=c.GetValue("ownID") || v.ActionGroups[KSPActionGroup.RCS] || v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust)>.01f)throw new Exception("Paused exact current unpowered source required for native GUID switching");
            var target=FlightGlobals.Vessels.SingleOrDefault(x=>x.id.ToString()==c.GetValue("id"));
            if(ExactA8DunaBase(v)){
                // Only the immutable actual44/25part Base and its actual19part
                // payload, nearby and unpacked on Duna; ordinary focus change.
                // Explicit native part arrays prevent a name-based surface handoff.
                if(target==null || target==v || !ExactA8DunaBase(target) || Vector3d.Distance(v.GetWorldPos3D(),target.GetWorldPos3D())>2500 || target.ActionGroups[KSPActionGroup.RCS] || target.ctrlState.mainThrottle>.0001f || target.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust)>.01f || string.Join(",",A6PartIDs(v).Select(x=>x.ToString()).ToArray())!=c.GetValue("parts") || string.Join(",",A6PartIDs(target).Select(x=>x.ToString()).ToArray())!=c.GetValue("targetParts"))throw new Exception("Exact nearby unpowered native Duna Base/payload UID pair required");
                FlightGlobals.ForceSetActiveVessel(target);return true;
            }
            if(v.mainBody.name!="Kerbin" || v.orbit.PeA<75000 || target==null || target==v || target.mainBody!=v.mainBody || target.orbit.PeA<75000 || !target.vesselName.StartsWith("D Starship "))throw new Exception("Existing same-body stable exact orbital source/target GUID required");
            int count=A6PartIDs(target).Length;
            if((target.GetCrewCount()!=2 || !new[]{25,39,44}.Contains(count)) && (target.GetCrewCount()!=0 || count!=14 || !(target.loaded?target.parts.Any(p=>p.partInfo.name=="DenizMethaloxPod"):target.protoVessel.protoPartSnapshots.Any(p=>p.partName=="DenizMethaloxPod"))))throw new Exception("Existing native carrier or actual fourteen-part methalox pod only");
            FlightGlobals.ForceSetActiveVessel(target);return true;
        }
        if(op=="missiona8pair"){
            if(missionManaged || a6FlightManaged || a6RcsOriginal!=null || v.id.ToString()!=c.GetValue("ownID") || v.id.ToString()!="c726e031-e224-439d-a6ca-c90e3610430c" || v.vesselName!="D Starship 6C Base" || v.parts.Count!=44 || v.mainBody.name!="Kerbin" || v.orbit.PeA<75000 || v.ActionGroups[KSPActionGroup.RCS] || !v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(new[]{"Eldas Kerman","Virdin Kerman"}))throw new Exception("Exact recorded paused actual A8 Base44/two crew required for native pair registration");
            var own=A6PartIDs(v);if(string.Join(",",own.Select(x=>x.ToString()).ToArray())!=c.GetValue("parts"))throw new Exception("Explicit actual44 Base partUID readback required");
            var target=FlightGlobals.Vessels.SingleOrDefault(x=>x.id.ToString()==c.GetValue("id"));
            if(target==null || target==v || target.mainBody!=v.mainBody || target.orbit.PeA<75000 || target.GetCrewCount()!=0 || A6PartIDs(target).Length!=14 || !target.vesselName.StartsWith("D Starship 5 Refuel"))throw new Exception("Actual fresh fourteen-part orbital pod required; native distant proto is allowed");
            var pod=A6PartIDs(target);if(string.Join(",",pod.Select(x=>x.ToString()).ToArray())!=c.GetValue("podParts"))throw new Exception("Explicit actual14 pod partUID readback required");
            double methane,oxidizer;int ports;
            if(target.loaded){var tank=target.parts.Single(p=>p.partInfo.name=="DenizMethaloxPod");methane=tank.Resources["LqdMethane"].amount;oxidizer=tank.Resources["Oxidizer"].amount;ports=target.FindPartModulesImplementing<ModuleDockingNode>().Count(x=>x.nodeType=="size1");}
            else{var snapshots=target.protoVessel.protoPartSnapshots;var tank=snapshots.Single(p=>p.partName=="DenizMethaloxPod");methane=tank.resources.Single(r=>r.resourceName=="LqdMethane").amount;oxidizer=tank.resources.Single(r=>r.resourceName=="Oxidizer").amount;ports=snapshots.Count(p=>p.partName=="dockingPort2");}
            if(methane<2300 || oxidizer<799 || ports!=2)throw new Exception("Fresh transported native methalox and two existing stock size1 ports required");
            a8CarrierOwner=v.id;a8PodOwner=target.id;a8CarrierPartIDs=own;a8PodPartIDs=pod;a8CarrierCrew=v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).ToArray();
            var report=new ConfigNode("A8_NATIVE_PAIR");report.AddValue("UT",Planetarium.GetUniversalTime());report.AddValue("carrier",v.id);report.AddValue("pod",target.id);foreach(var id in own)report.AddValue("carrierPartID",id);foreach(var id in pod)report.AddValue("podPartID",id);foreach(var crew in a8CarrierCrew)report.AddValue("crew",crew);report.Save(Path.Combine(Dir,"a8-pair-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".cfg"));return true;
        }
        if(op=="missionhire"){
            if(HighLogic.CurrentGame.Mode!=Game.Modes.SANDBOX || v.situation!=Vessel.Situations.PRELAUNCH || v.GetCrewCount()!=0 || missionManaged)
                throw new Exception("Paused empty native sandbox prelaunch only for crew recruitment");
            var names=c.GetValue("names").Split(',');if(names.Length!=2 || names.Distinct().Count()!=2)throw new Exception("Two named existing applicants required");
            var roster=HighLogic.CurrentGame.CrewRoster;
            var applicants=names.Select(name=>roster.Applicants.Single(k=>k.name==name && k.rosterStatus==ProtoCrewMember.RosterStatus.Available)).ToArray();
            foreach(var applicant in applicants)roster.HireApplicant(applicant);
            if(applicants.Any(k=>k.type!=ProtoCrewMember.KerbalType.Crew || k.rosterStatus!=ProtoCrewMember.RosterStatus.Available))throw new Exception("Native recruitment readback failed");
            return true;
        }
        if(op=="missionarm"){
            if(v.id.ToString()!=c.GetValue("id") ||
               string.Join(",",v.parts.Select(p=>p.flightID).OrderBy(x=>x).Select(x=>x.ToString()).ToArray())!=c.GetValue("parts") ||
               string.Join(",",v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).ToArray())!=c.GetValue("crew"))
                throw new Exception("Explicit current native GUID/partUID/crew readback required");
            missionOwner=v.id;missionParts=v.parts.Select(p=>p.flightID).OrderBy(x=>x).ToArray();
            missionCrew=v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).ToArray();
            missionIntegratedThrustDv=0;missionImpulseSamples=0;missionWatchdogReason="";missionPauseLocked=false;
            missionManaged=true;missionHeartbeat=Time.realtimeSinceStartup;return true;
        }
        var core=v.GetMasterMechJeb();if(core==null)throw new Exception("Installed native MechJeb required");
        var tc=core.GetComputerModule<MechJebModuleTargetController>();
        if(op=="missionplan"){
            if(v.Landed || v.Splashed || v.packed)throw new Exception("Actual unpacked flight orbit required for planning");
            string plan=c.GetValue("plan");double ut=Planetarium.GetUniversalTime()+120;
            v.RemoveAllManeuverNodes();
            System.Collections.Generic.List<ManeuverParameters> nodes;
            if(plan=="transfer"){
                var body=FlightGlobals.Bodies.Single(x=>x.name==c.GetValue("body"));
                if(body.referenceBody!=v.mainBody)throw new Exception("Native same-primary transfer target required");
                tc.Set(body);var operation=new OperationGeneric();operation.simpleTransfer=true;nodes=operation.MakeNodes(v.orbit,ut,tc);
            }else if(plan=="interplanetary"){
                bool windowCarrier=v.vesselName=="D Starship 1 Empty" && v.parts.Count==25;
                bool baseCarrier=v.vesselName=="D Starship 6C Base" && v.parts.Count==44;
                if(v.mainBody.name!="Kerbin" || (!windowCarrier && !baseCarrier) || v.GetCrewCount()!=2 || v.orbit.PeA<75000 || v.orbit.eccentricity>.2 || c.GetValue("body")!="Duna")throw new Exception("Exact actual two-crew Kerbin parking orbit required for A8 Duna planning");
                var body=FlightGlobals.Bodies.Single(x=>x.name=="Duna");
                if(body.referenceBody!=v.mainBody.referenceBody)throw new Exception("Native sibling-planet target required");
                tc.Set(body);var operation=new OperationInterplanetaryTransfer();nodes=operation.MakeNodes(v.orbit,ut,tc);
                if(!string.IsNullOrEmpty(operation.getErrorMessage()))throw new Exception("Native interplanetary planner warning: "+operation.getErrorMessage());
            }else if(plan=="course"){
                var body=FlightGlobals.Bodies.Single(x=>x.name==c.GetValue("body"));tc.Set(body);
                // The Operation wrapper replaces the requested earliest UT with Orbit.StartUT.
                // Call the same installed native calculator with a real future lower bound.
                double nodeUT;var dv=OrbitalManeuverCalculator.DeltaVAndTimeForCheapestCourseCorrection(v.orbit,ut,body.orbit,body,body.Radius+Number(c,"altitude"),out nodeUT);
                nodes=new System.Collections.Generic.List<ManeuverParameters>{new ManeuverParameters(dv,nodeUT)};
            }else if(plan=="return"){
                var operation=new OperationMoonReturn();operation.moonReturnAltitude.val=Number(c,"altitude");nodes=operation.MakeNodes(v.orbit,ut,tc);
            }else if(plan=="periapsis"){
                // Ordinary native maneuver calculator and stock node only.
                // Bring the actual safe Duna hyperbolic approach into low-orbit range.
                double altitude=Number(c,"altitude");
                if(!ExactA8DunaBase(v) || v.parts.Count!=44 || v.GetCrewCount()!=2 || missionManaged ||
                   v.orbit.PeA<50000 || v.orbit.timeToPe<1000 || v.altitude<1000000 ||
                   v.ActionGroups[KSPActionGroup.RCS] || v.FindPartModulesImplementing<ModuleEngines>().Sum(x=>x.finalThrust)>.01f ||
                   altitude<80000 || altitude>200000 || c.GetValue("body")!="Duna")
                    throw new Exception("Exact unpowered actual Duna Base44 safe approach required; planned Pe80..200km");
                ut=Planetarium.GetUniversalTime()+300;
                nodes=new System.Collections.Generic.List<ManeuverParameters>{new ManeuverParameters(OrbitalManeuverCalculator.DeltaVToChangePeriapsis(v.orbit,ut,v.mainBody.Radius+altitude),ut)};
            }else if(plan=="circularize"){
                ut=v.orbit.NextPeriapsisTime(ut);
                nodes=new System.Collections.Generic.List<ManeuverParameters>{new ManeuverParameters(OrbitalManeuverCalculator.DeltaVToCircularize(v.orbit,ut),ut)};
            }else throw new Exception("Unsupported ordinary native maneuver plan");
            if(nodes==null || nodes.Count==0 || nodes.Count>4)throw new Exception("Native planner did not produce bounded nodes");
            foreach(var node in nodes){if(double.IsNaN(node.UT) || double.IsInfinity(node.UT) || node.UT<Planetarium.GetUniversalTime() || double.IsNaN(node.dV.magnitude) || double.IsInfinity(node.dV.magnitude) || node.dV.magnitude>6000)throw new Exception("Invalid native planned node");v.PlaceManeuverNode(v.orbit,node.dV,node.UT);}
            return true;
        }
        if(op=="missiona8feedbacklanding"){
            if(!missionManaged || !MissionIntact(v) || !ExactA8DunaBase(v) || v.parts.Count!=44 || v.GetCrewCount()!=2 || v.situation!=Vessel.Situations.FLYING || v.radarAltitude<100 || v.radarAltitude>700 || v.packed || v.angularVelocity.magnitude>.08f || 90-Vector3d.Angle(v.GetTransform().up,v.upAxis)<85 || v.srf_velocity.magnitude>60 || v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust)>.01f || v.ActionGroups[KSPActionGroup.RCS] || v.ActionGroups[KSPActionGroup.SAS])throw new Exception("Exact armed upright paused actualDuna44part/two native zero source100..700m required");
            string file=c.GetValue("file");if(string.IsNullOrEmpty(file) || Path.GetFileName(file)!=file || !file.StartsWith("A8-feedback-") || !file.EndsWith(".cfg") || File.Exists(Path.Combine(Dir,file)))throw new Exception("New unique A8 native frame telemetry filename required");
            missionFeedbackDuna=true;missionFeedbackLanding=true;missionLandingControlFile=file;missionLandingContact=false;missionLandingBurnStarted=false;missionLandingLastAir=null;missionLandingNextLog=0;missionLandingPhase="A8 ordinary native frame input prepared";missionLandingSite=false;missionLandingNorthBiasDegrees=missionLandingEarlyPitchBiasDegrees=0;missionHeartbeat=Time.realtimeSinceStartup;
            v.OnFlyByWire-=MissionLandingInput;v.OnFlyByWire+=MissionLandingInput;
            core.attitude.users.Add(this);core.attitude.SetAxisControl(true,true,false);core.attitude.SetOmegaTarget(double.NaN,double.NaN,double.NaN);
            return true;
        }
        if(op=="missionfeedbacklanding"){
            missionFeedbackDuna=false;
            if(!missionManaged || !MissionIntact(v) || v.mainBody.name!="Kerbin" || v.vesselName!="D Starship 3 Rover" || v.parts.Count!=25 || v.GetCrewCount()!=2 || v.situation!=Vessel.Situations.FLYING || v.radarAltitude<1800 || v.radarAltitude>5000 || v.packed)
                throw new Exception("Exact armed paused recorded25part/two-crew Kerbin approach at1.8..5km required");
            string file=c.GetValue("file");
            if(string.IsNullOrEmpty(file) || Path.GetFileName(file)!=file || !file.StartsWith("A7-feedback-") || !file.EndsWith(".cfg") || File.Exists(Path.Combine(Dir,file)))throw new Exception("New bounded pilot telemetry filename required");
            missionLandingNorthBiasDegrees=c.HasValue("northBiasDegrees")?Number(c,"northBiasDegrees"):0;
            missionLandingMinimumCoast=c.HasValue("minimumCoastSeconds")?Number(c,"minimumCoastSeconds"):.6;
            missionLandingEarlyPitchBiasDegrees=c.HasValue("earlyPitchBiasDegrees")?Number(c,"earlyPitchBiasDegrees"):0;
            if(missionLandingNorthBiasDegrees<0 || missionLandingNorthBiasDegrees>25 || missionLandingEarlyPitchBiasDegrees<0 || missionLandingEarlyPitchBiasDegrees>35 || missionLandingMinimumCoast<.6 || missionLandingMinimumCoast>1.3)throw new Exception("Bounded early ordinary northward0..25degree/pitch0..35degree bias/0.6..1.3second coast required");
            missionLandingSite=c.HasValue("latitude") || c.HasValue("longitude");missionLandingSiteDistance=0;missionLandingSiteWantedVelocity=Vector3d.zero;
            if(missionLandingSite){
                missionLandingSiteLatitude=Number(c,"latitude");missionLandingSiteLongitude=Number(c,"longitude");
                if(Math.Abs(missionLandingSiteLatitude-v.latitude)>.5 || Math.Abs(missionLandingSiteLongitude-v.longitude)>.5 || SurfaceDistance(v.mainBody,v.latitude,v.longitude,missionLandingSiteLatitude,missionLandingSiteLongitude)>5000 || v.mainBody.pqsController==null)throw new Exception("Nearby measured native landing site within5km required");
                double la=missionLandingSiteLatitude,lo=missionLandingSiteLongitude,deg=180/Math.PI/v.mainBody.Radius,edeg=deg/Math.Cos(la*Math.PI/180);
                double ground=v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la,lo))-v.mainBody.Radius;
                double dx=(v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la,lo+10*edeg))-v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la,lo-10*edeg)))/20;
                double dz=(v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la+10*deg,lo))-v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la-10*deg,lo)))/20;
                if(ground<5 || Math.Atan(Math.Sqrt(dx*dx+dz*dz))*180/Math.PI>5)throw new Exception("Measured dry terrain with estimated slope below5degrees required");
            }
            missionLandingControlFile=file;missionLandingContact=false;missionLandingBurnStarted=false;missionLandingIgnitionRadar=0;missionLandingCoastStartUT=0;missionLandingPhase="Initial braking";missionLandingLastAir=null;
            missionLandingPreviousUT=0;missionLandingNextLog=0;missionLandingExternal=-v.mainBody.gravParameter/Math.Pow(v.mainBody.Radius+v.altitude,2);
            missionFeedbackLanding=true;missionHeartbeat=Time.realtimeSinceStartup;
            v.OnFlyByWire-=MissionLandingInput;v.OnFlyByWire+=MissionLandingInput;
            core.attitude.users.Add(this);core.attitude.SetAxisControl(true,true,true);core.attitude.SetOmegaTarget(double.NaN,double.NaN,double.NaN);
            return true;
        }
        if(op=="missionexecute" || op=="missionland"){
            if(!missionManaged || !MissionIntact(v))throw new Exception("Armed exact native flight required");
            var thrust=core.GetComputerModule<MechJebModuleThrustController>();if(!thrust.users.Contains(thrust))thrust.users.Add(thrust);
            if(op=="missionexecute"){
                if(v.patchedConicSolver==null || v.patchedConicSolver.maneuverNodes.Count==0)throw new Exception("Existing real maneuver node required");
                var executor=core.GetComputerModule<MechJebModuleNodeExecutor>();executor.autowarp=true;executor.ExecuteOneNode(this);
            }else{
                bool kerbin=v.mainBody.name=="Kerbin" && v.vesselName=="D Starship 3 Rover" && v.parts.Count==25 && v.GetCrewCount()==2 && v.altitude<12000 && v.radarAltitude>1500 && v.situation==Vessel.Situations.FLYING;
                bool duna=ExactA8DunaBase(v);
                if((v.mainBody.atmosphere || v.mainBody.name!="Mun") && !kerbin && !duna)throw new Exception("A7 legacy or exact actual A8 Duna Base44/25/19 native landing only");
                missionNativeKerbinLanding=kerbin;
                var landing=core.GetComputerModule<MechJebModuleLandingAutopilot>();landing.touchdownSpeed.val=.5;landing.deployGears=true;landing.deployChutes=false;landing.rcsAdjustment=false;landing.LandUntargeted(this);
            }
            missionHeartbeat=Time.realtimeSinceStartup;return true;
        }
        if(op=="missionwarp"){
            int rate=int.Parse(c.GetValue("rate"));
            if(rate<0 || rate>6 || (rate>0 && (v.Landed || v.Splashed || v.altitude<Math.Max(15000,v.mainBody.atmosphereDepth+10000) || v.orbit.PeA<10000)))throw new Exception("Bounded safe unpowered coast required");
            TimeWarp.fetch.CancelAutoWarp();TimeWarp.fetch.Mode=TimeWarp.Modes.HIGH;TimeWarp.SetRate(rate,true);return true;
        }
        if(op=="missionterrain"){
            // Read-only native PQS height samples; no input/state/resource writes.
            bool legacyTerrain=v.mainBody.bodyName=="Kerbin" && v.vesselName=="D Starship 3 Rover" && v.parts.Count==25 && v.GetCrewCount()==2;
            if((!legacyTerrain && !ExactA8DunaBase(v)) || v.ActionGroups[KSPActionGroup.RCS] || v.FindPartModulesImplementing<ModuleEngines>().Sum(x=>x.finalThrust)>.01f)throw new Exception("Paused exact unpowered recorded Kerbin return for terrain observer");
            string file=c.GetValue("file");if(string.IsNullOrEmpty(file) || Path.GetFileName(file)!=file || !(file.StartsWith("A7-terrain-") || (ExactA8DunaBase(v) && file.StartsWith("A8-terrain-"))) || !file.EndsWith(".cfg") || File.Exists(Path.Combine(Dir,file)))throw new Exception("New bounded terrain observation filename required");
            double lat=Number(c,"latitude"),lon=Number(c,"longitude");
            if(Math.Abs(lat-v.latitude)>.5 || Math.Abs(lon-v.longitude)>.5 || v.mainBody.pqsController==null)throw new Exception("Nearby native terrain only");
            var report=new ConfigNode("NATIVE_TERRAIN_OBSERVATION");report.AddValue("UT",Planetarium.GetUniversalTime());report.AddValue("latitude",lat);report.AddValue("longitude",lon);report.AddValue("method","Native PQS procedural height; approximate local slope sampled10m east/north. Not terrain collider contact or physical assignment.");
            double deg=180/Math.PI/v.mainBody.Radius,eastDeg=deg/Math.Cos(lat*Math.PI/180);
            for(int east=-16;east<=16;east++)for(int north=-16;north<=16;north++){
                double la=lat+north*100*deg,lo=lon+east*100*eastDeg;
                double h=v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la,lo))-v.mainBody.Radius;
                double dx=(v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la,lo+10*eastDeg))-v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la,lo-10*eastDeg)))/20;
                double dz=(v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la+10*deg,lo))-v.mainBody.pqsController.GetSurfaceHeight(v.mainBody.GetRelSurfaceNVector(la-10*deg,lo)))/20;
                var q=report.AddNode("POINT");q.AddValue("eastMeters",east*100);q.AddValue("northMeters",north*100);q.AddValue("latitude",la);q.AddValue("longitude",lo);q.AddValue("height",h);q.AddValue("eastGradient",dx);q.AddValue("northGradient",dz);q.AddValue("slopeDegrees",Math.Atan(Math.Sqrt(dx*dx+dz*dz))*180/Math.PI);
            }
            report.Save(Path.Combine(Dir,file));return true;
        }
        if(op=="missionlegsuspension"){
            // Ordinary stock PAW fields/events only; never physical state, fuel,
            // wheel collider, forces, orbit or clock setters.
            bool kerbinReturn=v.mainBody.bodyName=="Kerbin" && v.vesselName=="D Starship 3 Rover" && v.parts.Count==25 && v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(new[]{"Liston Kerman","Tanley Kerman"}) && v.altitude<5000 && (v.Landed || (v.situation==Vessel.Situations.FLYING && v.radarAltitude>1800));
            bool dunaCarrier=ExactA8DunaBase(v) && v.GetCrewCount()==2;
            if(!HighLogic.LoadedSceneIsFlight || (v.mainBody.bodyName!="Mun" && !kerbinReturn && !dunaCarrier) || Time.timeScale!=0 || v.GetCrewCount()!=2 || (!new[]{25,41}.Contains(v.parts.Count) && !dunaCarrier) || v.ctrlState.mainThrottle!=0 || v.ActionGroups[KSPActionGroup.RCS] || v.FindPartModulesImplementing<ModuleEngines>().Sum(x=>x.finalThrust)>.01f)throw new Exception("Paused intact unpowered Mun carrier or exact recorded Kerbin Rover return only for ordinary leg PAW settings");
            float spring=(float)Number(c,"spring"),damper=(float)Number(c,"damper");
            if(spring<.5f || spring>3 || damper<.5f || damper>3)throw new Exception("Stock leg spring/damper test range0.5..3 required");
            var legs=v.parts.Where(x=>x.partInfo.name=="DenizStarshipLeg").Select(x=>x.FindModuleImplementing<ModuleWheelSuspension>()).ToArray();if(legs.Length!=4 || legs.Any(x=>x==null))throw new Exception("Exactly four existing custom stock-based legs required");
            bool automatic=c.HasValue("auto") && bool.Parse(c.GetValue("auto"));
            foreach(var leg in legs){
                if(leg.autoSpringDamper!=automatic)leg.Events["EvtAutoSpringDamperToggle"].Invoke();
                if(!automatic){leg.Fields["springTweakable"].SetValue(spring,leg);leg.Fields["damperTweakable"].SetValue(damper,leg);}
            }
            return true;
        }
        if(op=="missionreleasewatch" || op=="missionreleaseforce"){
            if(v.mainBody.bodyName!="Mun" || !v.Landed || v.parts.Count!=41 || v.GetCrewCount()!=2 || Time.timeScale!=0 || v.ctrlState.mainThrottle!=0 || v.ActionGroups[KSPActionGroup.RCS])throw new Exception("Paused intact unpowered41part crewed Mun carrier required");
            var collar=v.parts.Single(p=>p.partInfo.name=="DenizPayloadRelease").FindModuleImplementing<ModuleDecouple>();
            if(collar==null || collar.isDecoupled)throw new Exception("Existing attached payload collar required");
            if(op=="missionreleaseforce"){
                float percent=(float)Number(c,"percent");if(percent<0 || percent>100)throw new Exception("Development-only native editor ejection setting0..100 required");
                collar.Fields["ejectionForcePercent"].SetValue(percent,collar);return true;
            }
            releasePayloadIDs=v.parts.Where(p=>p.partInfo.name=="DenizPayload.roverBody" || HasRoverAncestor(p)).Select(p=>p.flightID).OrderBy(x=>x).ToArray();
            if(releasePayloadIDs.Length!=16)throw new Exception("Exact existing16part rover branch required");
            releaseCarrier=v.id;releaseWatching=true;releaseReason="";releaseLastSampleUT=-1;releaseSamples=0;return true;
        }
        if(op=="missionwheels"){
            float throttle=float.Parse(c.GetValue("value"),System.Globalization.CultureInfo.InvariantCulture);
            if(v.GetCrewCount()!=0 || !v.FindPartModulesImplementing<ModuleWheelBase>().Any() || Math.Abs(throttle)>1 || !v.Landed)throw new Exception("Real landed uncrewed rover wheel control required");
            v.ActionGroups.SetGroup(KSPActionGroup.Brakes,throttle==0);FlightInputHandler.state.wheelThrottle=throttle;return true;
        }
        if(op=="missionrover"){
            double speed=Number(c,"speed");
            if(!missionManaged || !MissionIntact(v) || v.GetCrewCount()!=0 || !v.Landed || speed<0 || speed>3 || !v.FindPartModulesImplementing<ModuleWheelBase>().Any())throw new Exception("Armed real landed uncrewed rover and bounded native speed required");
            var rover=core.GetComputerModule<MechJebModuleRoverController>();rover.ControlSpeed=true;rover.speed.val=speed;rover.ControlHeading=true;rover.heading.val=Number(c,"heading");rover.StabilityControl=true;rover.WarpToDaylight=false;rover.users.Add(this);rover.enabled=true;v.ActionGroups.SetGroup(KSPActionGroup.Brakes,speed==0);return true;
        }
        throw new Exception("Unsupported mission control");
    }
    void MissionSnapshot(ConfigNode n,Vessel v) {
        n.AddValue("mainBody",v.mainBody.name);n.AddValue("bodyRadius",v.mainBody.Radius);n.AddValue("bodyMu",v.mainBody.gravParameter);n.AddValue("bodyAtmosphereDepth",v.mainBody.atmosphereDepth);
        n.AddValue("timeToPe",v.orbit.timeToPe);n.AddValue("crewNames",string.Join(",",v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).ToArray()));
        n.AddValue("missionLandingBurnStarted",missionLandingBurnStarted);n.AddValue("missionLandingIgnitionThreshold",missionLandingIgnitionThreshold);n.AddValue("missionLandingIgnitionRadar",missionLandingIgnitionRadar);n.AddValue("missionPauseLocked",missionPauseLocked);n.AddValue("missionFeedbackLanding",missionFeedbackLanding);n.AddValue("missionFeedbackDuna",missionFeedbackDuna);n.AddValue("missionLandingContact",missionLandingContact);n.AddValue("missionLandingPhase",missionLandingPhase);n.AddValue("missionLandingControlFile",missionLandingControlFile);n.AddValue("missionLandingTargetThrottle",missionLandingTargetThrottle);n.AddValue("missionLandingWantedVertical",missionLandingWantedVertical);n.AddValue("missionNativeKerbinLanding",missionNativeKerbinLanding);n.AddValue("requestedMainThrottle",FlightInputHandler.state.mainThrottle);n.AddValue("missionManaged",missionManaged);n.AddValue("missionHeartbeatAge",Time.realtimeSinceStartup-missionHeartbeat);n.AddValue("missionWatchdogReason",missionWatchdogReason);
        n.AddValue("missionRcsCourseActive",missionRcsCourseActive);n.AddValue("missionRcsCourseObserving",missionRcsCourseObserving);n.AddValue("missionRcsCourseDone",missionRcsCourseDone);n.AddValue("missionRcsCourseFile",missionRcsCourseFile);n.AddValue("missionRcsCourseEastDeltaV",Vector3d.Dot(missionRcsCourseDv,missionRcsCourseEast));n.AddValue("missionRcsCourseNorthDeltaV",Vector3d.Dot(missionRcsCourseDv,missionRcsCourseNorth));n.AddValue("missionRcsCourseUpDeltaV",Vector3d.Dot(missionRcsCourseDv,missionRcsCourseUp));n.AddValue("missionRcsCourseScalarDeltaV",missionRcsCourseScalarDv);n.AddValue("missionRcsCourseRemaining",(missionRcsCourseRequest-missionRcsCourseDv).magnitude);
        n.AddValue("missionIntegratedThrustDv",missionIntegratedThrustDv);n.AddValue("releaseWatching",releaseWatching);n.AddValue("releaseReason",releaseReason);n.AddValue("releaseSamples",releaseSamples);n.AddValue("missionImpulseSamples",missionImpulseSamples);n.AddValue("wheelThrottle",v.ctrlState.wheelThrottle);
        var orbit=v.orbit;for(int i=0;orbit!=null && orbit.referenceBody!=null && i<8;i++,orbit=orbit.nextPatch){var p=n.AddNode("ORBIT_PATCH");p.AddValue("body",orbit.referenceBody.name);p.AddValue("startUT",orbit.StartUT);p.AddValue("endUT",orbit.EndUT);p.AddValue("Ap",orbit.ApA);p.AddValue("Pe",orbit.PeA);p.AddValue("eccentricity",orbit.eccentricity);p.AddValue("endTransition",orbit.patchEndTransition);if(orbit.nextPatch==orbit || orbit.patchEndTransition==Orbit.PatchTransitionType.FINAL)break;}
        var core=v.GetMasterMechJeb();if(core!=null){var node=core.GetComputerModule<MechJebModuleNodeExecutor>();n.AddValue("nodeExecutorEnabled",node.enabled);n.AddValue("nodeExecutorUsers",node.users.Count);n.AddValue("nodeRemainingDv",typeof(MechJebModuleNodeExecutor).GetField("remainingDeltaV",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(node));n.AddValue("nodeBurnTriggered",node.burnTriggered);n.AddValue("nodeAligned",node.alignedForBurn);var landing=core.GetComputerModule<MechJebModuleLandingAutopilot>();n.AddValue("landingEnabled",landing.enabled);n.AddValue("landingStatus",landing.status??"");}
    }

    Guid a9Owner;
    uint[] a9PartIDs=new uint[0];
    string[] a9Crew=new string[0];
    KerbalEVA a9InputEva;
    string a9ObserveCrew="";
    double a9FirstReadyUT;
    void A9ObserveFirstReady(){
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || a9ObserveCrew.Length==0 || Time.timeScale<=0)return;
        var v=FlightGlobals.ActiveVessel;if(v==null || v.vesselName!=a9ObserveCrew)return;
        var e=v.FindPartModulesImplementing<KerbalEVA>().SingleOrDefault();
        if(e==null || !e.Ready)return;
        a9FirstReadyUT=Planetarium.GetUniversalTime();a9ObserveCrew="";FlightDriver.SetPause(true,false);
    }
    KFSMEventCondition a9ClimbCondition,a9DescendCondition,a9StopCondition;
    int a9LadderDirection;
    double a9LadderEndUT;
    float a9InputWallEnd;
    static readonly System.Reflection.BindingFlags A9Flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
    Vessel A9Carrier(){return FlightGlobals.Vessels.Single(x=>x.id==a9Owner);}
    bool A9Intact(Vessel carrier){return carrier.loaded && !carrier.packed && carrier.parts.Select(p=>p.flightID).OrderBy(x=>x).SequenceEqual(a9PartIDs);}
    void A9StopInput(){
        if(a9InputEva!=null){
            a9InputEva.On_ladderClimb.OnCheckCondition=a9ClimbCondition;
            a9InputEva.On_ladderDescend.OnCheckCondition=a9DescendCondition;
            a9InputEva.On_ladderStop.OnCheckCondition=a9StopCondition;
            typeof(KerbalEVA).GetField("ladderTgtRPos",A9Flags).SetValue(a9InputEva,Vector3.zero);
        }
        a9InputEva=null;a9LadderDirection=0;
    }
    bool A9LadderCondition(KFSMState st,KFSMEventCondition native){
        var eva=a9InputEva;
        if(eva==null)return native(st);
        bool active=TestSave && FlightGlobals.ActiveVessel==eva.vessel && !eva.vessel.packed &&
            Time.realtimeSinceStartup<a9InputWallEnd && Planetarium.GetUniversalTime()<a9LadderEndUT &&
            eva.OnALadder && eva.CurrentLadderPart!=null && A9Intact(A9Carrier());
        var ladder=typeof(KerbalEVA).GetField("currentLadder",A9Flags).GetValue(eva) as Collider;
        bool inverse=(bool)typeof(KerbalEVA).GetField("invLadderAxis",A9Flags).GetValue(eva);
        Vector3 intent=active && ladder!=null?ladder.transform.up*a9LadderDirection*(inverse?-1:1):Vector3.zero;
        // This is stock movement INPUT, not a transform, position or velocity.
        typeof(KerbalEVA).GetField("ladderTgtRPos",A9Flags).SetValue(eva,intent);
        return native(st);
    }
    Collider[] A9CharacterColliders(KerbalEVA eva){
        return ((Collider[])typeof(KerbalEVA).GetField("characterColliders",A9Flags).GetValue(eva) ?? new Collider[0]).Where(x=>x!=null && x.enabled).ToArray();
    }
    bool A9Command(ConfigNode c,Vessel v){
        string op=c.GetValue("op");if(!op.StartsWith("a9"))return false;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight)throw new Exception("Dedicated native A9 flight required");
        if(op=="a9register"){
            bool empty=v.vesselName=="D Starship 1 Empty" && v.mainBody.name=="Kerbin";
            bool landedDuna=v.vesselName=="D Starship 6C Base" && v.Landed && v.parts.Count==25 && ExactA8DunaBase(v);
            if(Time.timeScale!=0 || v.id.ToString()!=c.GetValue("id") || (!empty && !landedDuna) ||
               (v.parts.Count!=45 && v.parts.Count!=25) || v.GetCrewCount()!=2 || v.ctrlState.mainThrottle!=0 ||
               string.Join(",",v.parts.Select(p=>p.flightID).OrderBy(x=>x).Select(x=>x.ToString()).ToArray())!=c.GetValue("parts") ||
               string.Join(",",v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).ToArray())!=c.GetValue("crew"))
                throw new Exception("Exact paused native Empty45/25 or original landedDuna25 two-crew GUID/UID registration required");
            A9StopInput();a9Owner=v.id;a9PartIDs=v.parts.Select(p=>p.flightID).OrderBy(x=>x).ToArray();a9Crew=v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).ToArray();return true;
        }
        if(a9Owner==Guid.Empty)throw new Exception("Register actual A9 ship first");
        var carrier=A9Carrier();if(!A9Intact(carrier) || carrier.ctrlState.mainThrottle!=0 || carrier.FindPartModulesImplementing<ModuleEngines>().Sum(x=>x.finalThrust)>.01f || carrier.ActionGroups[KSPActionGroup.RCS])throw new Exception("Intact unpacked unpowered registered native ship required");
        if(op=="a9eva"){
            if(Time.timeScale!=0 || v!=carrier || !a9Crew.Contains(c.GetValue("crew")))throw new Exception("Paused registered actual cabin crew required");
            var cabin=carrier.parts.Single(p=>p.partInfo.name=="DenizStarshipCabin");
            var crew=cabin.protoModuleCrew.Single(k=>k.name==c.GetValue("crew"));
            if(cabin.airlock==null || crew.KerbalRef==null)throw new Exception("Actual native hatch/crew model absent");
            a9ObserveCrew=crew.name;a9FirstReadyUT=0;var eva=FlightEVA.SpawnEVA(crew.KerbalRef);
            if(eva==null)throw new Exception("Stock EVA refused; native obstruction checks retained");return true;
        }
        if(op=="a9ship"){
            if(Time.timeScale!=0 || c.GetValue("id")!=a9Owner.ToString())throw new Exception("Paused exact registered carrier required");
            A9StopInput();FlightGlobals.ForceSetActiveVessel(carrier);return true;
        }
        var activeEva=v.FindPartModulesImplementing<KerbalEVA>().SingleOrDefault();
        if(activeEva==null || !a9Crew.Contains(v.vesselName) || v.parts.Count!=1)throw new Exception("Actual named registered crew EVA required");
        if(op=="a9board"){
            if(Time.timeScale!=0)throw new Exception("Pause at actual hatch before boarding");
            var cabin=carrier.parts.Single(p=>p.partInfo.name=="DenizStarshipCabin");
            var trigger=typeof(KerbalEVA).GetField("currentAirlockTrigger",A9Flags).GetValue(activeEva) as Collider;
            var part=typeof(KerbalEVA).GetField("currentAirlockPart",A9Flags).GetValue(activeEva) as Part;
            if(part!=cabin || trigger==null || !trigger.enabled || !A9CharacterColliders(activeEva).Any(x=>trigger.bounds.Intersects(x.bounds)) || Vector3.Distance(activeEva.transform.position,cabin.airlock.position)>3)
                throw new Exception("Native cabin airlock trigger/proximity required; no remote boarding");
            A9StopInput();activeEva.BoardPart(cabin);return true;
        }
        if(op=="a9grab"){
            var triggers=typeof(KerbalEVA).GetField("currentLadderTriggers",A9Flags).GetValue(activeEva) as System.Collections.Generic.List<Collider>;
            if(Time.timeScale!=0 || !activeEva.Ready || activeEva.fsm==null || !(bool)typeof(KerbalEVA).GetProperty("VesselUnderControl",A9Flags).GetValue(activeEva,null) || EVAConstructionModeController.MovementRestricted ||
               triggers==null || !triggers.Any(t=>t!=null && t.enabled && A9CharacterColliders(activeEva).Any(x=>t.bounds.Intersects(x.bounds))))
                throw new Exception("Native live ladder trigger/body contact and controlled ready EVA required");
            // Explicit grab INPUT replaces only the F key. The real stock event,
            // current-state eligibility, colliders and native ladder physics remain.
            activeEva.fsm.RunEvent(activeEva.On_ladderGrabStart);return true;
        }
        if(op=="a9lamp"){activeEva.ToggleLamp();return true;}
        if(op=="a9ladder"){
            int direction=int.Parse(c.GetValue("direction"));double seconds=Number(c,"seconds");
            if(direction<-1 || direction>1 || seconds<0 || seconds>12)throw new Exception("Bounded native ladder intent required");
            A9StopInput();if(direction==0)return true;
            if(!activeEva.OnALadder || activeEva.CurrentLadderPart==null || activeEva.fsm==null)throw new Exception("Actual stock ladder contact required");
            a9InputEva=activeEva;a9LadderDirection=direction;a9LadderEndUT=Planetarium.GetUniversalTime()+seconds;a9InputWallEnd=Time.realtimeSinceStartup+45;
            a9ClimbCondition=activeEva.On_ladderClimb.OnCheckCondition;a9DescendCondition=activeEva.On_ladderDescend.OnCheckCondition;a9StopCondition=activeEva.On_ladderStop.OnCheckCondition;
            activeEva.On_ladderClimb.OnCheckCondition=st=>A9LadderCondition(st,a9ClimbCondition);
            activeEva.On_ladderDescend.OnCheckCondition=st=>A9LadderCondition(st,a9DescendCondition);
            activeEva.On_ladderStop.OnCheckCondition=st=>A9LadderCondition(st,a9StopCondition);return true;
        }
        throw new Exception("Unknown A9 native command");
    }
    void A9Snapshot(ConfigNode n,Vessel active){
        n.AddValue("a9RegisteredShip",a9Owner);n.AddValue("a9FirstReadyUT",a9FirstReadyUT);n.AddValue("a9ObserveCrew",a9ObserveCrew);n.AddValue("a9LadderInputDirection",a9LadderDirection);n.AddValue("a9LadderInputEndUT",a9LadderEndUT);
        foreach(var v in FlightGlobals.Vessels.Where(x=>x.loaded && (x.id==a9Owner || x.FindPartModulesImplementing<KerbalEVA>().Any()))){
            var row=n.AddNode("A9_CREW_VESSEL");row.AddValue("id",v.id);row.AddValue("name",v.vesselName);row.AddValue("parts",v.parts.Count);row.AddValue("crew",string.Join(",",v.GetVesselCrew().Select(k=>k.name).ToArray()));row.AddValue("rootPosition",v.rootPart.transform.position);row.AddValue("situation",v.situation);
            var cabin=v.parts.FirstOrDefault(p=>p.partInfo.name=="DenizStarshipCabin");
            if(cabin!=null){row.AddValue("hatchPosition",cabin.airlock==null?Vector3.zero:cabin.airlock.position);row.AddValue("hatchObstructed",cabin.airlock!=null && FlightEVA.HatchIsObstructed(cabin,cabin.airlock));}
            foreach(var p in v.parts.Where(p=>p.partInfo.name=="telescopicLadderBay" || p.partInfo.name=="domeLight1")){
                var equipment=row.AddNode("EQUIPMENT");equipment.AddValue("name",p.partInfo.name);equipment.AddValue("id",p.flightID);foreach(var module in p.Modules.Cast<PartModule>()){var m=equipment.AddNode("MODULE");module.Save(m);}
            }
            foreach(var eva in v.FindPartModulesImplementing<KerbalEVA>()){
                row.AddValue("onLadder",eva.OnALadder);row.AddValue("ladderPart",eva.CurrentLadderPart==null?"":eva.CurrentLadderPart.partInfo.name);row.AddValue("ladderPartID",eva.CurrentLadderPart==null?0:eva.CurrentLadderPart.flightID);row.AddValue("fsm",eva.fsm==null?"":(eva.fsm.currentStateName??""));row.AddValue("lampOn",eva.lampOn);row.AddValue("Ready",eva.Ready);row.AddValue("partColliderNull",eva.part.collider==null);row.AddValue("enabledCharacterColliderCount",A9CharacterColliders(eva).Length);row.AddValue("autoGrabLadderOnStart",eva.autoGrabLadderOnStart);row.AddValue("ladderJointed",eva.IsLadderJointed);var ladders=typeof(KerbalEVA).GetField("currentLadderTriggers",A9Flags).GetValue(eva) as System.Collections.Generic.List<Collider>;row.AddValue("ladderTriggerCount",ladders==null?-1:ladders.Count);if(ladders!=null)foreach(var cl in ladders){var ln=row.AddNode("LADDER_TRIGGER");ln.AddValue("name",cl==null?"":cl.name);ln.AddValue("enabled",cl!=null && cl.enabled);ln.AddValue("position",cl==null?Vector3.zero:cl.transform.position);}
                var airlock=typeof(KerbalEVA).GetField("currentAirlockPart",A9Flags).GetValue(eva) as Part;row.AddValue("airlockPart",airlock==null?"":airlock.partInfo.name);row.AddValue("airlockPartID",airlock==null?0:airlock.flightID);
                var trigger=typeof(KerbalEVA).GetField("currentAirlockTrigger",A9Flags).GetValue(eva) as Collider;row.AddValue("airlockTrigger",trigger==null?"":trigger.name);
                if(a9Owner!=Guid.Empty){var ship=A9Carrier();row.AddValue("shipRootLocalPosition",ship.rootPart.transform.InverseTransformPoint(eva.transform.position));var hatch=ship.parts.Single(p=>p.partInfo.name=="DenizStarshipCabin").airlock;row.AddValue("hatchDistance",hatch==null?-1:Vector3.Distance(hatch.position,eva.transform.position));}
            }
        }
    }


    uint[] b2IDs=new uint[0];string b2ContactFile="";
    bool B2Command(ConfigNode c,Vessel v){
        if(c.GetValue("op")=="b3releasepercent"){
            // Ordinary native PAW ejection percentage only; never pose, force,
            // velocity, resource, orbit or clock assignment. Exact actual source.
            if(!TestSave || !HighLogic.LoadedSceneIsFlight || v.packed || Time.timeScale!=0 || v.id.ToString()!=c.GetValue("id") || v.id.ToString()!="288fef68-3fc0-4c68-98e0-f519d50117ff" || v.vesselName!="D Starship 2C Hubble" || v.parts.Count!=36 || !v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(new[]{"Malberry Kerman","Virby Kerman"}) || v.mainBody.bodyName!="Kerbin" || v.altitude<75000 || v.orbit.PeA<75000 || v.ctrlState.mainThrottle!=0 || v.ActionGroups[KSPActionGroup.RCS] || v.FindPartModulesImplementing<ModuleEngines>().Any(e=>e.finalThrust>.01f))throw new Exception("Exact paused intact unpowered actual Hubble orbit required");
            uint[] nativeRequested=c.GetValue("parts").Split(',').Select(uint.Parse).OrderBy(x=>x).ToArray();
            if(!nativeRequested.SequenceEqual(v.parts.Select(p=>p.flightID).OrderBy(x=>x)))throw new Exception("Exact original36UIDs required");
            var controls=v.FindPartModulesImplementing<DenizStarshipControls>().First();
            var arms=v.FindPartModulesImplementing<ModuleRoboticServoPiston>();
            var collar=v.parts.Single(p=>p.partInfo.name=="DenizPayloadRelease").FindModuleImplementing<ModuleDecouple>();
            if(controls.DoorOpenProgress<.99f || !controls.cargoOpen || arms.Count!=1 || arms[0].currentExtension<9.4f || !arms[0].servoIsLocked || collar==null || collar.isDecoupled || collar.part.children.Count!=1)throw new Exception("Native open nose/full locked arm/attached collar required");
            float percent=(float)Number(c,"percent");if(percent<0 || percent>100)throw new Exception("Native PAW range0..100 required");
            collar.Fields["ejectionForcePercent"].SetValue(percent,collar);return true;
        }
        if(c.GetValue("op")!="b2watch")return false;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || v.packed || Time.timeScale!=0 || v.ctrlState.mainThrottle!=0 || !v.vesselName.StartsWith("D Starship ") || !new[]{0,2}.Contains(v.GetCrewCount()) || v.parts.Count<25 || v.parts.Count>64 || !v.parts.Any(p=>p.partInfo.name=="DenizStarshipCore") || v.id.ToString()!=c.GetValue("id"))throw new Exception("Exact paused native Deniz carrier fixture required");
        uint[] requested=c.GetValue("parts").Split(',').Select(uint.Parse).OrderBy(x=>x).ToArray();
        if(!requested.SequenceEqual(v.parts.Select(p=>p.flightID).OrderBy(x=>x)))throw new Exception("Exact current part identities required");
        string file=c.GetValue("file");if(Path.GetFileName(file)!=file || !file.StartsWith("b2-") || !file.EndsWith(".log") || File.Exists(Path.Combine(Dir,file)))throw new Exception("Unique native contact evidence file required");
        b2IDs=requested;b2ContactFile=file;File.WriteAllText(Path.Combine(Dir,file),"# Native OnCollisionEnter observer only; zero records does not prove absence of every contact.\n");
        foreach(var p in v.parts){var observer=p.gameObject.GetComponent<DenizB2ContactRecorder>();if(observer==null)observer=p.gameObject.AddComponent<DenizB2ContactRecorder>();observer.file=file;}
        return true;
    }
    void B2Snapshot(ConfigNode n,Vessel active){
        if(b2IDs.Length==0)return;var row=n.AddNode("B2_OBSERVER");row.AddValue("file",b2ContactFile);row.AddValue("registeredParts",b2IDs.Length);
        foreach(var vessel in FlightGlobals.Vessels.Where(x=>x.loaded && !x.packed))foreach(var p in vessel.parts.Where(x=>b2IDs.Contains(x.flightID))){var q=row.AddNode("LIVE_PART");q.AddValue("id",p.flightID);q.AddValue("name",p.partInfo.name);q.AddValue("vesselID",vessel.id);q.AddValue("vessel",vessel.vesselName);q.AddValue("pos",p.transform.position-active.rootPart.transform.position);q.AddValue("rotation",p.transform.rotation);q.AddValue("velocity",p.Rigidbody==null?Vector3.zero:p.Rigidbody.velocity);q.AddValue("vesselSpin",vessel.angularVelocity.magnitude);}
    }
    Guid b1Owner;uint[] b1IDs=new uint[0];
    static readonly System.Reflection.BindingFlags B1Flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
    PartModule B1Core(Vessel v){return v.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).First(x=>x.GetType().FullName=="BoosterGuidance.BoosterGuidanceCore");}
    Delegate B1Wire(Vessel v){var field=typeof(Vessel).GetField("OnFlyByWire",B1Flags);if(field==null)throw new Exception("Native callback backing field unavailable");return field.GetValue(v) as Delegate;}
    int B1Callbacks(Vessel v,PartModule core){var d=B1Wire(v);return d==null?0:d.GetInvocationList().Count(x=>x.Target==core && x.Method.Name=="Fly");}
    void B1Call(PartModule core,string method,Type[] types,object[] args){core.GetType().GetMethod(method,types).Invoke(core,args);}
    bool B1Command(ConfigNode c,Vessel v){
        string op=c.GetValue("op");if(!op.StartsWith("b1"))return false;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || v.packed || v.GetCrewCount()!=0 || v.parts.Count!=11 || !new[]{"Deniz Super Heavy","D Booster Practice"}.Contains(v.vesselName) || !v.parts.Any(p=>p.partInfo.name=="DenizSuperHeavy"))throw new Exception("Exact native uncrewed11part booster required");
        var core=B1Core(v);var type=core.GetType();
        if(op=="b1register"){
            if(Time.timeScale!=0 || v.ctrlState.mainThrottle!=0 || v.id.ToString()!=c.GetValue("id") || string.Join(",",v.parts.Select(p=>p.flightID).OrderBy(x=>x).Select(x=>x.ToString()).ToArray())!=c.GetValue("parts"))throw new Exception("Paused exact native GUID/UID registration required");
            B1Callbacks(v,core);b1Owner=v.id;b1IDs=v.parts.Select(p=>p.flightID).OrderBy(x=>x).ToArray();return true;
        }
        if(v.id!=b1Owner || !v.parts.Select(p=>p.flightID).OrderBy(x=>x).SequenceEqual(b1IDs))throw new Exception("Registered intact native booster required");
        var param=new KSPActionParam(KSPActionGroup.None,KSPActionType.Activate);
        if(op=="b1disable" || op=="b1cleanup"){
            int count=op=="b1cleanup"?B1Callbacks(v,core):1;if(count>10)throw new Exception("Unexpected duplicate callback count");
            for(int i=0;i<count;i++)B1Call(core,"DisableGuidance",new[]{typeof(KSPActionParam)},new object[]{param});
            return true;
        }
        if(Time.timeScale!=0 || v.ctrlState.mainThrottle!=0)throw new Exception("Pause/zero manual throttle before native guidance setup");
        if(op=="b1target"){
            double lat=Number(c,"latitude"),lon=Number(c,"longitude"),alt=Number(c,"altitude");if(Math.Abs(lat)>5 || lon< -80 || lon> -60 || alt<0 || alt>500)throw new Exception("Bounded native KSC-area target required");
            B1Call(core,"SetTarget",new[]{typeof(double),typeof(double),typeof(double)},new object[]{lat,lon,alt});return true;
        }
        if(op=="b1enable"){
            // The exact native action invoked by the phase buttons. Repeated
            // subscription remains unmodified for the deliberate reproduction.
            B1Call(core,"EnableGuidance",new[]{typeof(KSPActionParam)},new object[]{param});return true;
        }
        if(op=="b1phase"){
            string phase=c.GetValue("phase");if(!new[]{"BoostBack","Coasting","ReentryBurn","AeroDescent","LandingBurn"}.Contains(phase))throw new Exception("Native guidance phase required");
            Type phaseType=type.Assembly.GetType("BoosterGuidance.BLControllerPhase");B1Call(core,"SetPhase",new[]{phaseType},new[]{Enum.Parse(phaseType,phase)});return true;
        }
        if(op=="b1settings"){
            foreach(string field in new[]{"reentryBurnMaxAoA","aeroDescentMaxAoA","landingBurnMaxAoA","degreeRange"})if(c.HasValue(field)){
                float value=(float)Number(c,field);if(value<1 || value>30)throw new Exception("Native steering angle1..30 required");type.GetField(field).SetValue(core,value);
            }
            foreach(string field in new[]{"deployLandingGear","enableRCS"})if(c.HasValue(field))type.GetField(field).SetValue(core,bool.Parse(c.GetValue(field)));
            if(c.HasValue("touchdownMargin")){int value=int.Parse(c.GetValue("touchdownMargin"));if(value<20 || value>100)throw new Exception("Native touchdown margin20..100 required");type.GetField("touchdownMargin").SetValue(core,value);}
            if(c.HasValue("landingEngines") && c.GetValue("landingEngines")=="active")B1Call(core,"SetLandingBurnEngines",Type.EmptyTypes,new object[0]);
            B1Call(core,"Changed",Type.EmptyTypes,new object[0]);return true;
        }
        throw new Exception("Unknown native B1 command");
    }
    void B1Snapshot(ConfigNode n,Vessel v){
        var core=v.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).FirstOrDefault(x=>x.GetType().FullName=="BoosterGuidance.BoosterGuidanceCore");if(core==null)return;
        var b=n.AddNode("B1_GUIDANCE");b.AddValue("corePartID",core.part.flightID);b.AddValue("corePart",core.part.partInfo.name);b.AddValue("assemblyVersion",core.GetType().Assembly.GetName().Version);b.AddValue("nativeFlyCallbacks",B1Callbacks(v,core));b.AddValue("registered",v.id==b1Owner);
        var controller=core.GetType().GetField("controller",B1Flags).GetValue(core);b.AddValue("controllerNull",controller==null);
        if(controller!=null){b.AddValue("phase",DiagnosticMember(controller,"phase"));b.AddValue("enabled",DiagnosticMember(controller,"enabled"));b.AddValue("info",DiagnosticMember(controller,"info"));}
        foreach(string field in new[]{"tgtSet","tgtLatitude","tgtLongitude","tgtAlt","reentryBurnAlt","reentryBurnTargetSpeed","reentryBurnMaxAoA","aeroDescentMaxAoA","landingBurnMaxAoA","landingBurnEngines","touchdownMargin","touchdownSpeed","degreeRange","enableRCS","deployLandingGear","last_t","last_throttle"})b.AddValue(field,DiagnosticMember(core,field));
        var controllers=core.GetType().GetField("controllers").GetValue(null) as System.Collections.ICollection;b.AddValue("staticControllerCount",controllers==null?-1:controllers.Count);
        object[] args=new object[]{v,0d,0d,false,null};core.GetType().Assembly.GetType("BoosterGuidance.KSPUtils").GetMethod("ComputeMinMaxThrust").Invoke(null,args);b.AddValue("nativeMinThrust",args[1]);b.AddValue("nativeMaxThrust",args[2]);
    }

    void Run(ConfigNode c){string op=c.GetValue("op");if(TestSave || op=="load")File.AppendAllText(Path.Combine(Dir,"commands-audit.log"),DateTime.UtcNow.ToString("o")+" "+c.ToString()+"\n");if(op=="load"){HighLogic.SaveFolder="Deniz Starship Missions Test";var g=GamePersistence.LoadGame("persistent",HighLogic.SaveFolder,true,false);g.startScene=GameScenes.SPACECENTER;HighLogic.CurrentGame=g;g.Start();return;}if(!TestSave)throw new Exception("Test save required");if(op=="resume"){ResumeFlight(c);return;}if(op=="ui"){if(c.GetValue("value")=="false")KSP.UI.UIMasterController.Instance.HideUI();else KSP.UI.UIMasterController.Instance.ShowUI();return;}if(op=="render"){GameSettings.REFLECTION_PROBE_REFRESH_MODE=1;GameSettings.REFLECTION_PROBE_TEXTURE_RESOLUTION=0;FlightCamera.fetch.reflectionProbe.OnSettingsUpdate();FlightCamera.fetch.reflectionProbe.Enable(true);FlightCamera.fetch.reflectionProbe.ForceRender();return;}if(op=="resolution"){GameSettings.SCREEN_RESOLUTION_WIDTH=1280;GameSettings.SCREEN_RESOLUTION_HEIGHT=720;Screen.SetResolution(1280,720,false);return;}if(op=="launch"){FlightDriver.StartWithNewLaunch(c.GetValue("craft"),"Squad/Flags/default","LaunchPad",new VesselCrewManifest());return;}if(EditorCommand(c))return;var v=FlightGlobals.ActiveVessel;if(v==null)throw new Exception("Active vessel absent for "+op);if(B2Command(c,v) || B1Command(c,v) || A9Command(c,v) || MissionCommand(c,v) || A6RcsCommand(c,v) || A6Command(c,v) || BargeCommand(c,v) || NavigationCommand(c,v))return;if(ControllerCommand(c,v) || AttitudeCommand(c,v))return;var m=v.FindPartModulesImplementing<DenizStarshipControls>().FirstOrDefault();if(op=="crew"){var cabin=v.parts.First(x=>x.partInfo.name=="DenizStarshipCabin");foreach(var kerbal in HighLogic.CurrentGame.CrewRoster.Crew.Where(x=>x.rosterStatus==ProtoCrewMember.RosterStatus.Available).Take(2)){cabin.AddCrewmember(kerbal);kerbal.rosterStatus=ProtoCrewMember.RosterStatus.Assigned;}v.SpawnCrew();return;}if(op=="sas"){v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);v.ActionGroups.SetGroup(KSPActionGroup.SAS,true);return;}if(op=="day"){TimeWarp.SetRate(0,true);Planetarium.SetUniversalTime(2834957);return;}if(op=="fuel"){double fraction=double.Parse(c.GetValue("value"),System.Globalization.CultureInfo.InvariantCulture);foreach(var r in v.parts.SelectMany(p=>p.Resources.Cast<PartResource>()).Where(r=>r.resourceName=="LqdMethane"||r.resourceName=="Oxidizer"))r.amount=r.maxAmount*fraction;return;}if(op=="event"){var targetModule=c.HasValue("module")?v.parts.Where(p=>!c.HasValue("partID") || p.flightID.ToString()==c.GetValue("partID")).SelectMany(p=>p.Modules.Cast<PartModule>()).First(x=>x.GetType().Name==c.GetValue("module")):m;targetModule.Events[c.GetValue("name")].Invoke();return;}if(op=="ag"){v.ActionGroups.ToggleGroup((KSPActionGroup)Enum.Parse(typeof(KSPActionGroup),c.GetValue("name")));return;}if(op=="stage"){StageManager.ActivateNextStage();return;}if(op=="throttle"){FlightInputHandler.state.mainThrottle=float.Parse(c.GetValue("value"),System.Globalization.CultureInfo.InvariantCulture);return;}if(op=="save"){GamePersistence.SaveGame(c.GetValue("name"),HighLogic.SaveFolder,SaveMode.OVERWRITE);return;}if(op=="pause"){if(c.GetValue("value")=="false" && missionPauseLocked)throw new Exception("Native safety pause locked; preserve failure and explicitly load a recorded safe source before resuming");FlightDriver.SetPause(c.GetValue("value")=="true",false);return;}if(op=="camera"){var target=v.parts.First(p=>p.partInfo.name==c.GetValue("name"));var fc=FlightCamera.fetch;FlightCamera.SetTarget(target);if(c.HasValue("distance"))fc.SetDistanceImmediate(float.Parse(c.GetValue("distance")));if(c.HasValue("pitch"))FlightCamera.CamPitch=float.Parse(c.GetValue("pitch"))*Mathf.Deg2Rad;if(c.HasValue("heading"))FlightCamera.CamHdg=float.Parse(c.GetValue("heading"))*Mathf.Deg2Rad;return;}if(op=="time"){Planetarium.SetUniversalTime(double.Parse(c.GetValue("value")));return;}if(op=="orbit"){var b=v.mainBody;v.Landed=false;v.Splashed=false;v.landedAt=string.Empty;v.situation=Vessel.Situations.ORBITING;OrbitPhysicsManager.HoldVesselUnpack(60);foreach(var vv in FlightGlobals.Vessels)vv.GoOnRails();var o=v.orbitDriver.orbit;o.inclination=0;o.eccentricity=0;o.semiMajorAxis=b.Radius+150000;o.LAN=0;o.argumentOfPeriapsis=0;o.meanAnomalyAtEpoch=c.HasValue("phase")?double.Parse(c.GetValue("phase"))*Math.PI/180:0;o.epoch=Planetarium.GetUniversalTime();o.referenceBody=b;o.Init();o.UpdateFromUT(Planetarium.GetUniversalTime());v.orbitDriver.pos=o.pos.xzy;v.orbitDriver.vel=o.vel;FloatingOrigin.ResetTerrainShaderOffset();return;}if(op=="switch"){var target=FlightGlobals.Vessels.FirstOrDefault(x=>x!=v && (x.vesselName.Contains(c.GetValue("name")) || x.parts.Any(p=>p.partInfo.name==c.GetValue("name")) || (x.protoVessel!=null && x.protoVessel.protoPartSnapshots.Any(p=>p.partName==c.GetValue("name")))));if(target!=null)FlightGlobals.ForceSetActiveVessel(target);return;}if(op=="screenshot"){ScreenCapture.CaptureScreenshot(Path.Combine(Dir,c.GetValue("name")));return;}if(op=="hop"){StartCoroutine(Hop());return;}}
    static readonly string[] DirectControlModules={"MechJebModuleAscentAutopilot","MechJebModuleStagingController","MechJebModuleLandingAutopilot","MechJebModuleDockingAutopilot","MechJebModuleRendezvousAutopilot","MechJebModuleTranslatron","MechJebModuleThrustController","MechJebModuleNodeExecutor","MechJebModuleRoverController"};
    // Development-only A6 adapter: normal target/controller, stock docking and
    // conserved tank-to-tank transfer APIs. Never assigns an orbit, force,
    // transform, velocity, universal time, or an arbitrary resource amount.
    Vessel A6Vessel(ConfigNode c) {
        var target=FlightGlobals.Vessels.FirstOrDefault(x=>x.id.ToString()==c.GetValue("id"));
        if(target==null || target==FlightGlobals.ActiveVessel)throw new Exception("Existing separate recorded vessel ID required");
        return target;
    }
    ConfigNode a6RcsOriginal;
    Guid a6RcsOwner;
    bool a6RcsManual;
    bool a6RcsAimActive;
    bool a6RcsDynamicLateral;
    Guid a6RcsAimTarget;
    double a6RcsClosingSetpoint;
    double a6RcsLateralAxisError;
    Vector3d a6RcsGuideAcceleration;
    int a6RcsWatchdogPauses;
    double a6RcsLastWatchdogUT;
    Vector3d a6RcsAimDirection;
    float a6RcsHeartbeat;
    bool a6FlightManaged;
    float a6FlightHeartbeat;
    string a6LastWatchdogReason="";
    static readonly uint[] A6CarrierPartIDs={370542606u,449736489u,553387329u,556322552u,596900171u,601586276u,926864640u,1547079694u,1561431355u,2066632295u,2133709404u,2135779492u,2225690943u,2361875414u,2372144991u,2449328748u,2609943259u,2764110381u,3208203595u,3308039070u,3409162434u,3459997935u,3860430178u,4036961036u,4203272070u};
    static readonly uint[] A6PodPartIDs={371410702u,497191471u,636602465u,700773963u,1186606781u,1942586291u,2350029739u,2694305267u,2743059358u,3082611675u,3134122635u,3313186887u,4071278581u,4238059170u};
    uint[] A6PartIDs(Vessel v) {
        if(v==null)return new uint[0];
        return (v.loaded?v.parts.Select(p=>p.flightID):(v.protoVessel==null?Enumerable.Empty<uint>():v.protoVessel.protoPartSnapshots.Select(p=>p.flightID))).OrderBy(x=>x).ToArray();
    }
    Guid a8CarrierOwner=Guid.Empty,a8PodOwner=Guid.Empty;
    uint[] a8CarrierPartIDs=new uint[0],a8PodPartIDs=new uint[0];
    string[] a8CarrierCrew=new string[0];
    bool A8CarrierIntact(Vessel v) {
        return a8CarrierOwner!=Guid.Empty && v!=null && v.id==a8CarrierOwner &&
            v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(a8CarrierCrew) && A6PartIDs(v).SequenceEqual(a8CarrierPartIDs);
    }
    bool A8PodIntact(Vessel v) {
        return a8PodOwner!=Guid.Empty && v!=null && v.id==a8PodOwner && v.GetCrewCount()==0 && A6PartIDs(v).SequenceEqual(a8PodPartIDs);
    }
    bool A8FlightPairIntact(Vessel v) {
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || v==null || v.orbit==null || v.mainBody.name!="Kerbin" || v.orbit.PeA<75000 || a8CarrierOwner==Guid.Empty || a8PodOwner==Guid.Empty)return false;
        if(v.id!=a8CarrierOwner && v.id!=a8PodOwner)return false;
        var union=a8CarrierPartIDs.Concat(a8PodPartIDs).OrderBy(x=>x).ToArray();
        if(A6PartIDs(v).SequenceEqual(union))return v.GetVesselCrew().Select(k=>k.name).OrderBy(x=>x).SequenceEqual(a8CarrierCrew);
        bool carrier=A8CarrierIntact(v),pod=A8PodIntact(v);if(!carrier && !pod)return false;
        var other=FlightGlobals.Vessels.FirstOrDefault(x=>x.id==(carrier?a8PodOwner:a8CarrierOwner));
        return other!=null && other.mainBody==v.mainBody && other.orbit!=null && other.orbit.PeA>=75000 && (carrier?A8PodIntact(other):A8CarrierIntact(other));
    }
    bool A6FlightPairIntact(Vessel v) {
        if(A8FlightPairIntact(v))return true;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || v==null || v.orbit==null || v.mainBody.name!="Kerbin" || v.orbit.PeA<75000)return false;
        string own=v.id.ToString();bool carrier=own=="295b8784-f76a-4a18-a8db-53e65e4ac08a",pod=own=="fc2c7b92-c3b2-48fe-aad5-e629df1054da";
        if(!carrier && !pod)return false;
        var actual=A6PartIDs(v);
        if(actual.Length==39)return v.GetCrewCount()==2 && actual.SequenceEqual(A6CarrierPartIDs.Concat(A6PodPartIDs).OrderBy(x=>x));
        if(v.GetCrewCount()!=(carrier?2:0) || !actual.SequenceEqual(carrier?A6CarrierPartIDs:A6PodPartIDs))return false;
        string other=carrier?"fc2c7b92-c3b2-48fe-aad5-e629df1054da":"295b8784-f76a-4a18-a8db-53e65e4ac08a";
        var target=FlightGlobals.Vessels.FirstOrDefault(x=>x.id.ToString()==other);
        return target!=null && target.mainBody==v.mainBody && target.orbit!=null && target.orbit.PeA>=75000 && target.GetCrewCount()==(carrier?0:2) && A6PartIDs(target).SequenceEqual(carrier?A6PodPartIDs:A6CarrierPartIDs);
    }
    void A6FlightWatchdog() {
        if(!a6FlightManaged || Time.timeScale==0)return;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight){a6FlightManaged=false;return;}
        var v=FlightGlobals.ActiveVessel;
        bool intact=A6FlightPairIntact(v);
        if(intact && Time.realtimeSinceStartup-a6FlightHeartbeat<=15)return;
        a6FlightManaged=false;
        a6LastWatchdogReason=intact?"Native flight heartbeat lost":"Recorded flight pair guard lost";
        // Existing native controllers, emergency cutoff, normal warp and pause.
        // Never changes physical orbit, clock, transform, force or resources.
        if(TestSave && HighLogic.LoadedSceneIsFlight && v!=null && (new[]{"295b8784-f76a-4a18-a8db-53e65e4ac08a","fc2c7b92-c3b2-48fe-aad5-e629df1054da"}.Contains(v.id.ToString()) || (a8CarrierOwner!=Guid.Empty && (v.id==a8CarrierOwner || v.id==a8PodOwner)))) {
            A6RcsStop(v);
            var off=new ConfigNode();off.AddValue("op","controllersoff");ControllerCommand(off,v);
            FlightInputHandler.state.mainThrottle=0;
            v.ActionGroups.ToggleGroup(KSPActionGroup.Abort);
            foreach(var engine in v.FindPartModulesImplementing<ModuleEngines>())engine.Shutdown();
        }
        TimeWarp.SetRate(0,true);a6RcsWatchdogPauses++;a6RcsLastWatchdogUT=Planetarium.GetUniversalTime();FlightDriver.SetPause(true,false);
    }
    void A6RcsStop(Vessel v) {
        var core=v.GetMasterMechJeb();
        if(core!=null){var r=core.GetComputerModule<MechJebModuleRCSController>();r.users.Remove(this);core.attitude.users.Remove(this);core.attitude.SetOmegaTarget(double.NaN,double.NaN,double.NaN);}
        v.ActionGroups.SetGroup(KSPActionGroup.RCS,false);a6RcsManual=false;a6RcsAimActive=false;a6RcsDynamicLateral=false;
    }
    void A6WatchdogPause(Vessel v) {
        A6RcsStop(v);a6RcsWatchdogPauses++;a6RcsLastWatchdogUT=Planetarium.GetUniversalTime();
        FlightDriver.SetPause(true,false);
    }
    void A6LateralSteering() {
        var v=FlightGlobals.ActiveVessel;
        if(v==null)return;
        var target=FlightGlobals.Vessels.FirstOrDefault(x=>x.id==a6RcsAimTarget);
        if(!TestSave || !HighLogic.LoadedSceneIsFlight || v.id!=a6RcsOwner || v.id.ToString()!="295b8784-f76a-4a18-a8db-53e65e4ac08a" || v.parts.Count!=25 || v.GetCrewCount()!=2 || target==null || target.id.ToString()!="fc2c7b92-c3b2-48fe-aad5-e629df1054da" || v.mainBody!=target.mainBody || v.orbit.PeA<75000 || target.orbit.PeA<75000 || v.ctrlState.mainThrottle>.0001f || v.FindPartModulesImplementing<ModuleEngines>().Any(x=>x.EngineIgnited)) {
            A6WatchdogPause(v);throw new Exception("Dynamic lateral steering lost exact unpowered recorded A6 pair guard");
        }
        var core=v.GetMasterMechJeb();var tc=core.GetComputerModule<MechJebModuleTargetController>();tc.Set(target);
        if(tc.Distance<80 || tc.Distance>60000){A6WatchdogPause(v);throw new Exception("Dynamic lateral steering range guard");}
        var error=-tc.RelativePosition.normalized*a6RcsClosingSetpoint-tc.RelativeVelocity;
        // Read current native orbital positions only. Gravity compensation is
        // an attitude-planning vector, never a force/acceleration assignment.
        double ut=Planetarium.GetUniversalTime(),mu=v.mainBody.gravParameter;
        var ownPosition=v.orbit.getRelativePositionAtUT(ut).xzy;
        var targetPosition=target.orbit.getRelativePositionAtUT(ut).xzy;
        var ownGravity=-mu*ownPosition/Math.Pow(ownPosition.magnitude,3);
        var targetGravity=-mu*targetPosition/Math.Pow(targetPosition.magnitude,3);
        a6RcsGuideAcceleration=error/10+(targetGravity-ownGravity);
        // The gravity term remains smooth near zero velocity error. Project to
        // the nearest perpendicular hull axis, then limit the ordinary native
        // attitude setpoint to5degrees ahead of the present hull at each0.5s
        // update. No transform, orbit, velocity or force is set here.
        if(a6RcsGuideAcceleration.magnitude>.001) {
            var up=(Vector3d)v.GetTransform().up;
            var control=a6RcsGuideAcceleration.normalized;
            var direction=up-control*Vector3d.Dot(up,control);
            if(direction.sqrMagnitude<.0001)direction=Vector3d.Cross(control,core.vesselState.normalPlus);
            if(direction.sqrMagnitude<.0001)direction=Vector3d.Cross(control,(Vector3d)v.GetTransform().forward);
            if(direction.sqrMagnitude<.0001){A6WatchdogPause(v);throw new Exception("Nondegenerate dynamic lateral steering direction required");}
            a6RcsLateralAxisError=Vector3d.Angle(up,direction);
            a6RcsAimDirection=Vector3.Slerp((Vector3)up,(Vector3)direction.normalized,(float)Math.Min(1,5/Math.Max(.001,a6RcsLateralAxisError)));
            core.attitude.attitudeTo(a6RcsAimDirection,AttitudeReference.INERTIAL,this);core.attitude.SetAxisControl(true,true,true);core.attitude.SetOmegaTarget(double.NaN,double.NaN,double.NaN);
        }
        a6RcsAimActive=true;
    }
    bool A6RcsCommand(ConfigNode c,Vessel v) {
        string op=c.GetValue("op");
        if(!new[]{"a6rcsconfig","a6rcsvelocity","a6rcsstop","a6rcsrestore","a6rcsaim","a6rcsflush","a6rcsheartbeat"}.Contains(op))return false;
        bool a8Carrier=A8CarrierIntact(v),a8Pod=A8PodIntact(v);
        bool recordedEmpty=a8Carrier || (v.id.ToString()=="295b8784-f76a-4a18-a8db-53e65e4ac08a" && v.vesselName=="D Starship 1 Empty" && v.parts.Count==25 && v.GetCrewCount()==2);
        bool recordedPod=a8Pod || (v.id.ToString()=="fc2c7b92-c3b2-48fe-aad5-e629df1054da" && v.vesselName=="D Starship 5 Refuel Probe" && v.parts.Count==14 && v.GetCrewCount()==0 && v.parts.Any(p=>p.flightID==497191471));
        if(!HighLogic.LoadedSceneIsFlight || (!recordedEmpty && !recordedPod) || v.mainBody.name!="Kerbin" || v.orbit.PeA<75000 || v.ctrlState.mainThrottle>.0001f)
            throw new Exception("Intact unpowered actual recorded Empty or deployed pod orbit required");
        if(c.HasValue("id") && c.GetValue("id")!=((a8Carrier || a8Pod)?(a8Pod?a8CarrierOwner:a8PodOwner).ToString():(recordedPod?"295b8784-f76a-4a18-a8db-53e65e4ac08a":"fc2c7b92-c3b2-48fe-aad5-e629df1054da")))throw new Exception("Existing recorded A6 docking pair required");
        var core=v.GetMasterMechJeb();if(core==null)throw new Exception("Native MechJeb required");
        var rcs=core.GetComputerModule<MechJebModuleRCSController>();var balancer=core.GetComputerModule<MechJebModuleRCSBalancer>();
        if(op=="a6rcsstop"){A6RcsStop(v);return true;}
        if(op=="a6rcsheartbeat"){a6RcsHeartbeat=Time.realtimeSinceStartup;return true;}
        if(op=="a6rcsflush") {
            if(Time.timeScale!=0 || v.ActionGroups[KSPActionGroup.RCS] || a6RcsOriginal!=null || rcs.Tf.val!=1 || !rcs.rcsThrottle || !rcs.rcsForRotation || balancer.smartTranslation || balancer.users.Count!=0)throw new Exception("Paused restored canonical native RCS preferences required before native settings flush");
            core.OnSave(null);return true;
        }
        if(op=="a6rcsaim") {
            if(Time.timeScale!=0 || v.ActionGroups[KSPActionGroup.RCS] || v.FindPartModulesImplementing<ModuleEngines>().Any(x=>x.EngineIgnited))throw new Exception("Paused RCS-off/main-engine-off plane attitude setup required");
            var aimTarget=A6Vessel(c);if(aimTarget.mainBody!=v.mainBody || aimTarget.orbit.PeA<75000)throw new Exception("Recorded stable target required");
            var aimController=core.GetComputerModule<MechJebModuleTargetController>();aimController.Set(aimTarget);
            if(c.GetValue("dynamicLateral")=="true") {
                if(a8Carrier || a8Pod)throw new Exception("Legacy A6 dynamic lateral experiment is not an A8 control");
                if(!recordedEmpty || a6RcsOriginal==null || a6RcsOwner!=v.id)throw new Exception("Configured actual carrier only for dynamic lateral steering");
                a6RcsAimTarget=aimTarget.id;a6RcsClosingSetpoint=0;a6RcsDynamicLateral=true;a6RcsHeartbeat=Time.realtimeSinceStartup;A6LateralSteering();return true;
            }
            var normal=Vector3d.Cross(aimController.RelativePosition,aimController.RelativeVelocity);
            if(normal.sqrMagnitude<.0001 || aimController.Distance<500 || aimController.Distance>(recordedEmpty?20000:8000))throw new Exception("Nondegenerate current relative approach plane/range required");
            a6RcsAimDirection=normal.normalized;
            // Ordinary MechJeb attitude setpoint/reaction-wheel steering. Align
            // the hull's weak axial translation direction perpendicular to the
            // current relative position/velocity plane; no physical assignment.
            core.attitude.attitudeTo(a6RcsAimDirection,AttitudeReference.INERTIAL,this);a6RcsAimActive=true;
            return true;
        }
        if(op=="a6rcsconfig") {
            if(a6RcsOriginal!=null)throw new Exception("Restore prior native RCS settings before another configuration");
            if(Time.timeScale!=0 || v.ActionGroups[KSPActionGroup.RCS])throw new Exception("Paused RCS-off setup required");
            double tf=Number(c,"Tf"),percentage=Number(c,"percentage");
            bool nativeBalance=c.GetValue("balancer")!="false";
            if(c.HasValue("rotation") && !new[]{"true","false"}.Contains(c.GetValue("rotation")))throw new Exception("Native RCS rotation checkbox boolean required");
            bool nativeRotation=c.GetValue("rotation")=="true";
            bool manualPID=c.GetValue("manualPID")=="true";
            double kp=manualPID?Number(c,"Kp"):rcs.Kp.val,ki=manualPID?Number(c,"Ki"):rcs.Ki.val,kd=manualPID?Number(c,"Kd"):rcs.Kd.val;
            if(manualPID && (kp<.005 || kp>.2 || ki<0 || ki>.5 || kd<0 || kd>.5))throw new Exception("Bounded native manual PID UI gains required");
            if(!nativeBalance && balancer.users.Count!=0)throw new Exception("Exact recorded pair with no existing balancer users required for native UI balancer-off trial");
            if(tf<1 || tf>10 || percentage<1 || percentage>100)throw new Exception("Bounded native UI RCS parameters required");
            a6RcsOwner=v.id;a6RcsOriginal=new ConfigNode("A6_RCS_ORIGINAL");
            a6RcsOriginal.AddValue("vessel",v.id);a6RcsOriginal.AddValue("Tf",rcs.Tf.val);a6RcsOriginal.AddValue("manual",rcs.rcsManualPID);a6RcsOriginal.AddValue("throttle",rcs.rcsThrottle);a6RcsOriginal.AddValue("rotation",rcs.rcsForRotation);a6RcsOriginal.AddValue("smartTranslation",balancer.smartTranslation);a6RcsOriginal.AddValue("balancerWindowUser",balancer.users.Contains(core.GetComputerModule<MechJebModuleRCSBalancerWindow>()));
            a6RcsOriginal.AddValue("Kp",rcs.Kp.val);a6RcsOriginal.AddValue("Ki",rcs.Ki.val);a6RcsOriginal.AddValue("Kd",rcs.Kd.val);
            foreach(var m in v.FindPartModulesImplementing<ModuleRCSFX>()) {
                var n=a6RcsOriginal.AddNode("MODULE");n.AddValue("part",m.part.flightID);n.AddValue("moduleIndex",v.FindPartModulesImplementing<ModuleRCSFX>().Where(x=>x.part==m.part).ToList().IndexOf(m));n.AddValue("pitch",m.enablePitch);n.AddValue("yaw",m.enableYaw);n.AddValue("roll",m.enableRoll);n.AddValue("percentage",m.thrustPercentage);n.AddValue("power",m.thrusterPower);
                m.enablePitch=nativeRotation;m.enableYaw=nativeRotation;m.enableRoll=nativeRotation;m.thrustPercentage=(float)percentage;
            }
            a6RcsOriginal.Save(Path.Combine(Dir,"a6-rcs-original-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".cfg"));
            rcs.Tf.val=tf;rcs.rcsManualPID=manualPID;if(manualPID){rcs.Kp.val=kp;rcs.Ki.val=ki;rcs.Kd.val=kd;}rcs.rcsThrottle=false;rcs.rcsForRotation=nativeRotation;balancer.ResetThrusterForces();balancer.smartTranslation=nativeBalance;if(nativeBalance)balancer.users.Add(core.GetComputerModule<MechJebModuleRCSBalancerWindow>());balancer.enabled=nativeBalance;rcs.dirty=true;balancer.dirty=true;
            return true;
        }
        if(a6RcsOriginal==null || a6RcsOwner!=v.id)throw new Exception("Recorded native RCS configuration required");
        if(op=="a6rcsrestore") {
            A6RcsStop(v);
            if(c.GetValue("canonical")=="true") {
                var original=ConfigNode.Load(Path.Combine(Dir,"a6-rcs-original-20261006-082544.cfg"));
                if(original==null || original.GetValue("vessel")!=v.id.ToString() || original.GetValue("Tf")!="1" || original.GetValue("smartTranslation")!="False")throw new Exception("Recorded pre-isolation native defaults required");
                original.AddValue("balancerWindowUser",false);
                foreach(var n in original.GetNodes("MODULE"))n.AddValue("power",40.9600029f);
                foreach(var key in new[]{"Kp","Ki","Kd"})if(a6RcsOriginal.HasValue(key))original.AddValue(key,a6RcsOriginal.GetValue(key));a6RcsOriginal=original;
            }
            foreach(var key in new[]{"Kp","Ki","Kd"})if(a6RcsOriginal.HasValue(key)){double gain=double.Parse(a6RcsOriginal.GetValue(key),System.Globalization.CultureInfo.InvariantCulture);if(key=="Kp")rcs.Kp.val=gain;if(key=="Ki")rcs.Ki.val=gain;if(key=="Kd")rcs.Kd.val=gain;}rcs.Tf.val=double.Parse(a6RcsOriginal.GetValue("Tf"));rcs.rcsManualPID=bool.Parse(a6RcsOriginal.GetValue("manual"));rcs.rcsThrottle=bool.Parse(a6RcsOriginal.GetValue("throttle"));rcs.rcsForRotation=bool.Parse(a6RcsOriginal.GetValue("rotation"));balancer.ResetThrusterForces();balancer.smartTranslation=bool.Parse(a6RcsOriginal.GetValue("smartTranslation"));if(!bool.Parse(a6RcsOriginal.GetValue("balancerWindowUser")))balancer.users.Remove(core.GetComputerModule<MechJebModuleRCSBalancerWindow>());
            foreach(var n in a6RcsOriginal.GetNodes("MODULE")){var m=v.FindPartModulesImplementing<ModuleRCSFX>().Where(x=>x.part.flightID.ToString()==n.GetValue("part")).ElementAt(int.Parse(n.GetValue("moduleIndex")));m.enablePitch=bool.Parse(n.GetValue("pitch"));m.enableYaw=bool.Parse(n.GetValue("yaw"));m.enableRoll=bool.Parse(n.GetValue("roll"));m.thrustPercentage=float.Parse(n.GetValue("percentage"));m.thrusterPower=float.Parse(n.GetValue("power"));}
            rcs.dirty=true;balancer.dirty=true;a6RcsOriginal=null;core.OnSave(null);return true;
        }
        if(v.FindPartModulesImplementing<ModuleEngines>().Any(x=>x.EngineIgnited))throw new Exception("Shut down main engines for isolated RCS velocity control");
        var target=A6Vessel(c);double speed=Number(c,"speed");
        if(target.mainBody!=v.mainBody || target.orbit.PeA<75000 || speed<0 || speed>(recordedEmpty?60:2))throw new Exception("Existing stable pair and bounded pod0..2/carrier0..60m/s closing setpoint required");
        var tc=core.GetComputerModule<MechJebModuleTargetController>();tc.Set(target);
        if(tc.Distance<80 || tc.Distance>(recordedEmpty?60000:5000))throw new Exception("Isolated native RCS range80..60000m carrier /5000m pod required");
        rcs.users.Add(this);rcs.enabled=true;
        a6RcsClosingSetpoint=speed;
        // Native RelativePosition is own minus target; only a controller setpoint,
        // never an assignment to vessel/orbit velocity, transform or force.
        bool freshTarget=c.GetValue("currentOrbitVelocity")=="true";
        if(freshTarget && !recordedPod)throw new Exception("Recorded deployed pod only for current-orbit controller setpoint trial");
        // Native orbital calculation at current UT for a controller setpoint;
        // xzy converts orbital coordinates to the same world frame as GetVel.
        // Never writes the target or own physical orbit/velocity.
        var targetVelocity=freshTarget?target.orbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).xzy:target.orbit.GetVel();
        if(c.GetValue("relativeVelocity")=="true") {
            // Exact recorded Empty/pod role guards above apply to both native relative setpoint cases.
            if(recordedEmpty && ((speed>20 && tc.Distance<10000) || (speed>5 && tc.Distance<2000) || (speed>2 && tc.Distance<800)))throw new Exception("Carrier native closing setpoint tiers60far10km/20far2km/5far800m/2near required");
            // Native Drive recomputes actual relative velocity each physics
            // frame; a low-rate Python command no longer freezes an absolute
            // world velocity while orbital gravity changes both vessels.
            rcs.SetTargetRelative(-tc.RelativePosition.normalized*speed);
        } else rcs.SetTargetWorldVelocity(targetVelocity-tc.RelativePosition.normalized*speed);
        a6RcsManual=true;a6RcsHeartbeat=Time.realtimeSinceStartup;return true;
    }
    bool A6Command(ConfigNode c,Vessel v) {
        string op=c.GetValue("op");
        if(op=="a6flightheartbeat") {
            if(!A6FlightPairIntact(v))throw new Exception("Exact intact recorded carrier/pod pair required for native flight heartbeat");
            a6FlightManaged=true;a6FlightHeartbeat=Time.realtimeSinceStartup;return true;
        }
        if(!new[]{"a6target","a6rendezvous","a6dock","a6undock","a6transfer","a6autowarp"}.Contains(op))return false;
        if(!HighLogic.LoadedSceneIsFlight || v.mainBody.name!="Kerbin" || v.orbit.PeA<75000 || v.ctrlState.mainThrottle>.0001f)
            throw new Exception("Unpowered recorded Kerbin orbit required for A6 setup");
        var core=v.GetMasterMechJeb();
        if(op=="a6autowarp") {
            if(core==null || !v.vesselName.StartsWith("D Starship ") || !new[]{"true","false"}.Contains(c.GetValue("value")))throw new Exception("Native Starship node coast-warp boolean required");
            core.GetComputerModule<MechJebModuleNodeExecutor>().autowarp=c.GetValue("value")=="true";return true;
        }
        if(op=="a6target" || op=="a6rendezvous" || op=="a6dock") {
            if(core==null || !v.vesselName.StartsWith("D Starship "))throw new Exception("Existing Starship/MechJeb required");
            var target=A6Vessel(c);
            if(target.mainBody!=v.mainBody || target.orbit.PeA<75000)throw new Exception("Existing stable orbital target required");
            var tc=core.GetComputerModule<MechJebModuleTargetController>();
            if(op=="a6dock") {
                if(!target.loaded || target.packed || !v.loaded || v.packed)throw new Exception("Normally unpacked docking pair required");
                var own=v.FindPartModulesImplementing<ModuleDockingNode>().Single(x=>x.part.flightID.ToString()==c.GetValue("ownPort"));
                var other=target.FindPartModulesImplementing<ModuleDockingNode>().Single(x=>x.part.flightID.ToString()==c.GetValue("targetPort"));
                if(own.nodeType!=other.nodeType || Vector3.Distance(own.nodeTransform.position,other.nodeTransform.position)>1000)
                    throw new Exception("Matching reachable-range stock ports required");
                own.MakeReferenceTransform();tc.Set(other);
                var dock=core.GetComputerModule<MechJebModuleDockingAutopilot>();
                dock.speedLimit.val=.25;dock.overrideSafeDistance=false;dock.overrideTargetSize=false;dock.forceRol=false;
                dock.users.Add(this);dock.enabled=true;
            } else {
                tc.Set(target);
                if(op=="a6rendezvous") {
                    if(!A6FlightPairIntact(v) || (A8CarrierIntact(v)?target.id!=a8PodOwner:target.id.ToString()!="fc2c7b92-c3b2-48fe-aad5-e629df1054da"))throw new Exception("Exact recorded actual carrier/pod pair required for native rendezvous");
                    A6RcsStop(v);core.attitude.SetOmegaTarget(double.NaN,double.NaN,double.NaN);a6FlightManaged=true;a6FlightHeartbeat=Time.realtimeSinceStartup;
                    // controllersoff deliberately removes every controller user.
                    // Restore the stock ThrustController self-user established in
                    // its native OnStart; Drive requires base + autopilot users.
                    var thrust=core.GetComputerModule<MechJebModuleThrustController>();
                    if(!thrust.users.Contains(thrust))thrust.users.Add(thrust);
                    var rendezvous=core.GetComputerModule<MechJebModuleRendezvousAutopilot>();
                    rendezvous.desiredDistance.val=150;rendezvous.maxClosingSpeed.val=5;rendezvous.maxPhasingOrbits.val=5;
                    rendezvous.users.Add(this);rendezvous.enabled=true;
                }
            }
            return true;
        }
        if(op=="a6undock") {
            var node=v.FindPartModulesImplementing<ModuleDockingNode>().Single(x=>x.part.flightID.ToString()==c.GetValue("port"));
            if(node.otherNode==null || node.otherNode.part.vessel!=v || !node.state.StartsWith("Docked"))throw new Exception("Actually docked stock joint required");
            node.Undock();return true;
        }
        // Match the stock resource-transfer panel: withdraw from a real source,
        // deposit exactly that withdrawal. No .amount/.maxAmount assignments.
        var from=v.parts.Single(x=>x.flightID.ToString()==c.GetValue("from"));
        var to=v.parts.Single(x=>x.flightID.ToString()==c.GetValue("to"));
        string resource=c.GetValue("resource");double units=Number(c,"units");
        if(from==to || from.partInfo.name!="DenizMethaloxPod" || to.partInfo.name!="DenizStarshipCore" ||
           !new[]{"LqdMethane","Oxidizer"}.Contains(resource) || units<=0 || units>1000)
            throw new Exception("Bounded real pod-to-Starship methalox transfer required");
        if(!v.FindPartModulesImplementing<ModuleDockingNode>().Any(x=>x.otherNode!=null && x.otherNode.part.vessel==v && x.state.StartsWith("Docked") && x.crossfeed))
            throw new Exception("Actual crossfeed-enabled docked joint required before transfer");
        var source=from.Resources[resource];var sink=to.Resources[resource];
        if(source==null || sink==null || !source.flowState || !sink.flowState)throw new Exception("Existing unlocked matching tanks required");
        double amount=Math.Min(units,Math.Min(source.amount,sink.maxAmount-sink.amount));
        if(amount<=0)throw new Exception("No real source fuel or destination capacity");
        double before=source.amount+sink.amount,fromBefore=source.amount,toBefore=sink.amount;
        double removed=from.TransferResource(source,-amount,to);
        double added=-to.TransferResource(sink,removed,from);
        if(Math.Abs(removed-added)>1e-7 || Math.Abs(source.amount+sink.amount-before)>1e-7)
            throw new Exception("Stock transfer conservation/readback failure");
        File.AppendAllText(Path.Combine(Dir,"a6-transfer-audit.log"),DateTime.UtcNow.ToString("o")+" UT="+Planetarium.GetUniversalTime()+" resource="+resource+" moved="+added+" from="+from.flightID+":"+fromBefore+"->"+source.amount+" to="+to.flightID+":"+toBefore+"->"+sink.amount+" total="+before+"\n");
        return true;
    }
    void A6Snapshot(ConfigNode n,Vessel v) {
        n.AddValue("activeVesselID",v.id);n.AddValue("nativeRcsEnabled",v.ActionGroups[KSPActionGroup.RCS]);
        n.AddValue("a8PairRegistered",a8CarrierOwner!=Guid.Empty && a8PodOwner!=Guid.Empty);n.AddValue("a8CarrierID",a8CarrierOwner);n.AddValue("a8PodID",a8PodOwner);n.AddValue("a8FlightPairIntact",A8FlightPairIntact(v));
        foreach(var other in FlightGlobals.Vessels.Where(x=>x.orbit!=null && x.mainBody==v.mainBody)) {
            var vn=n.AddNode("A6_VESSEL");vn.AddValue("id",other.id);vn.AddValue("name",other.vesselName);vn.AddValue("crew",other.GetCrewCount());vn.AddValue("loaded",other.loaded);vn.AddValue("packed",other.packed);
            vn.AddValue("Ap",other.orbit.ApA);vn.AddValue("Pe",other.orbit.PeA);vn.AddValue("inclination",other.orbit.inclination);
            vn.AddValue("parts",other.loaded?other.parts.Count:(other.protoVessel==null?0:other.protoVessel.protoPartSnapshots.Count));
            vn.AddValue("relativeOrbitPosition",other.orbit.getRelativePositionAtUT(Planetarium.GetUniversalTime())-v.orbit.getRelativePositionAtUT(Planetarium.GetUniversalTime()));
            vn.AddValue("relativeOrbitVelocity",other.orbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime())-v.orbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()));
            if(!other.loaded || other.parts.Count==0)continue;
            foreach(var tank in other.parts.Where(p=>p.Resources.Contains("LqdMethane") || p.Resources.Contains("Oxidizer"))) {
                var tn=vn.AddNode("TANK");tn.AddValue("part",tank.partInfo.name);tn.AddValue("id",tank.flightID);
                foreach(PartResource r in tank.Resources)tn.AddValue(r.resourceName,r.amount+"/"+r.maxAmount);
            }
            foreach(var port in other.FindPartModulesImplementing<ModuleDockingNode>()) {
                var pn=vn.AddNode("PORT");pn.AddValue("part",port.part.partInfo.name);pn.AddValue("id",port.part.flightID);pn.AddValue("nodeType",port.nodeType);pn.AddValue("state",port.state);pn.AddValue("dockedPartUId",port.dockedPartUId);pn.AddValue("crossfeed",port.crossfeed);pn.AddValue("shielded",port.part.ShieldedFromAirstream);
                pn.AddValue("acquireRange",port.acquireRange);pn.AddValue("captureRange",port.captureRange);pn.AddValue("captureMaxRvel",port.captureMaxRvel);
                if(port.nodeTransform!=null) {
                    pn.AddValue("faceRootLocal",other.rootPart.transform.InverseTransformPoint(port.nodeTransform.position));
                    pn.AddValue("axisRootLocal",other.rootPart.transform.InverseTransformDirection(port.nodeTransform.forward));
                    var origin=port.nodeTransform.position+port.nodeTransform.forward*10;
                    foreach(var hit in Physics.RaycastAll(origin,-port.nodeTransform.forward,11).OrderBy(x=>x.distance)) {
                        var part=hit.collider.GetComponentInParent<Part>();if(part==null || part==port.part || part.vessel!=other)continue;
                        var hn=pn.AddNode("OUTWARD_CENTER_RAY_HIT");hn.AddValue("part",part.partInfo.name);hn.AddValue("id",part.flightID);hn.AddValue("distanceFrom10mOut",hit.distance);hn.AddValue("collider",hit.collider.name);
                    }
                }
            }
        }
        var core=v.GetMasterMechJeb();if(core==null)return;
        var rcs=core.GetComputerModule<MechJebModuleRCSController>();
        n.AddValue("a6FlightManaged",a6FlightManaged);n.AddValue("a6LastWatchdogReason",a6LastWatchdogReason);n.AddValue("a6FlightHeartbeatAge",Time.realtimeSinceStartup-a6FlightHeartbeat);n.AddValue("a6RcsSettingsRetained",a6RcsOriginal!=null);n.AddValue("a6RcsManual",a6RcsManual);n.AddValue("a6RcsAimActive",a6RcsAimActive);n.AddValue("a6RcsWatchdogPauses",a6RcsWatchdogPauses);n.AddValue("a6RcsLastWatchdogUT",a6RcsLastWatchdogUT);n.AddValue("a6RcsLateralAxisError",a6RcsLateralAxisError);n.AddValue("a6RcsGuideAcceleration",a6RcsGuideAcceleration);n.AddValue("nativeAngularVelocityVector",v.angularVelocity);n.AddValue("nativeRcsManualPID",rcs.rcsManualPID);n.AddValue("nativeRcsKp",rcs.Kp.val);n.AddValue("nativeRcsKi",rcs.Ki.val);n.AddValue("nativeRcsKd",rcs.Kd.val);n.AddValue("a6RcsDynamicLateral",a6RcsDynamicLateral);n.AddValue("a6RcsClosingSetpoint",a6RcsClosingSetpoint);n.AddValue("a6RcsAimDirection",a6RcsAimDirection);if(a6RcsAimActive)n.AddValue("a6RcsAimError",Vector3d.Angle(v.GetTransform().up,a6RcsAimDirection));n.AddValue("nativeRcsControllerEnabled",rcs.enabled);n.AddValue("nativeRcsControllerUsers",rcs.users.Count);n.AddValue("nativeRcsTf",rcs.Tf.val);n.AddValue("nativeRcsThrottle",rcs.rcsThrottle);n.AddValue("nativeRcsForRotation",rcs.rcsForRotation);n.AddValue("nativeRcsSmartTranslation",core.GetComputerModule<MechJebModuleRCSBalancer>().smartTranslation);n.AddValue("nativeRcsBalancerEnabled",core.GetComputerModule<MechJebModuleRCSBalancer>().enabled);n.AddValue("nativeRcsBalancerUsers",core.GetComputerModule<MechJebModuleRCSBalancer>().users.Count);n.AddValue("nativeRcsTargetVelocity",rcs.targetVelocity);n.AddValue("nativeRcsControlType",typeof(MechJebModuleRCSController).GetField("controlType",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(rcs));
        n.AddValue("nativeOwnOrbitalVelocity",core.vesselState.orbitalVelocity);n.AddValue("nativeOwnOrbitGetVel",v.orbit.GetVel());n.AddValue("nativeOwnCurrentCalculatedVel",v.orbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).xzy);if(core.target.NormalTargetExists){n.AddValue("nativeTargetOrbitGetVel",core.target.TargetOrbit.GetVel());n.AddValue("nativeTargetCurrentCalculatedVel",core.target.TargetOrbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).xzy);}n.AddValue("nativeRcsWorldVelocityDelta",typeof(MechJebModuleRCSController).GetField("worldVelocityDelta",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).GetValue(rcs));
        n.AddValue("flightCtrlXYZ",v.ctrlState.X+","+v.ctrlState.Y+","+v.ctrlState.Z);n.AddValue("flightCtrlPitchYawRoll",v.ctrlState.pitch+","+v.ctrlState.yaw+","+v.ctrlState.roll);
        var thrust=core.GetComputerModule<MechJebModuleThrustController>();
        if(thrust!=null){n.AddValue("nativeThrustTargetThrottle",thrust.targetThrottle);n.AddValue("nativeThrustBaseUserPresent",thrust.users.Contains(thrust));}
        var executor=core.GetComputerModule<MechJebModuleNodeExecutor>();if(executor!=null)n.AddValue("nodeAutowarp",executor.autowarp);
        if(v.patchedConicSolver!=null)foreach(var node in v.patchedConicSolver.maneuverNodes){var mn=n.AddNode("NATIVE_MANEUVER");mn.AddValue("UT",node.UT);mn.AddValue("deltaV",node.DeltaV);mn.AddValue("deltaVMagnitude",node.DeltaV.magnitude);mn.AddValue("stockRemainingBurnMagnitude",node.GetBurnVector(v.orbit).magnitude);var plannedPatch=node.nextPatch;for(int pi=0;plannedPatch!=null && plannedPatch.referenceBody!=null && pi<8;pi++,plannedPatch=plannedPatch.nextPatch){var pp=mn.AddNode("PLANNED_ORBIT_PATCH");pp.AddValue("body",plannedPatch.referenceBody.name);pp.AddValue("startUT",plannedPatch.StartUT);pp.AddValue("endUT",plannedPatch.EndUT);pp.AddValue("Ap",plannedPatch.ApA);pp.AddValue("Pe",plannedPatch.PeA);pp.AddValue("endTransition",plannedPatch.patchEndTransition);if(plannedPatch.nextPatch==plannedPatch || plannedPatch.patchEndTransition==Orbit.PatchTransitionType.FINAL)break;}}
        var target=core.GetComputerModule<MechJebModuleTargetController>();
        if(target!=null && target.NormalTargetExists) {n.AddValue("targetName",target.Name);n.AddValue("targetDistance",target.Distance);n.AddValue("targetRelativeVelocity",target.RelativeVelocity);n.AddValue("targetRelativeSpeed",target.RelativeVelocity.magnitude);}
        var rendezvous=core.GetComputerModule<MechJebModuleRendezvousAutopilot>();if(rendezvous!=null){n.AddValue("rendezvousStatus",rendezvous.status);n.AddValue("rendezvousEnabled",rendezvous.enabled);}
        var dock=core.GetComputerModule<MechJebModuleDockingAutopilot>();if(dock!=null){n.AddValue("dockingStatus",dock.status);n.AddValue("dockingEnabled",dock.enabled);n.AddValue("dockingStep",dock.dockingStep);n.AddValue("dockingZSeparation",dock.zSep);n.AddValue("dockingLateralSeparation",dock.lateralSep.magnitude);}
    }
    // Navigation tests only advance the simulation or resume an existing recorded
    // flight. These operations never set an orbit, position, velocity or fuel.
    void ResumeFlight(ConfigNode c)
    {
        string name=c.GetValue("name"), vessel=c.GetValue("vessel");
        if(Path.GetFileName(name)!=name || !(name.StartsWith("A1_") || name.StartsWith("A2_") || name.StartsWith("A3_") || name.StartsWith("A4_") || name.StartsWith("A5_") || name.StartsWith("A6_") || name.StartsWith("A7_") || name.StartsWith("A8_") || name.StartsWith("A9_") || name.StartsWith("C1_")) || name.EndsWith(".sfs"))throw new Exception("Named recorded A1-A9 or C1 flight required");
        bool c1=name.StartsWith("C1_") && c.HasValue("id") && vessel!=null;
        bool booster=vessel=="Deniz Super Heavy";
        bool barge=vessel=="Drone Ship - Landing Practice" && (name.StartsWith("A5_BargePlacement_") || name.StartsWith("A5_BargeFloating_"));
        if(vessel==null || (!vessel.StartsWith("D Starship ") && !booster && !barge && !c1))throw new Exception("Recorded carrier or exact Super Heavy required");
        string path=Path.Combine(KSPUtil.ApplicationRootPath,"saves",HighLogic.SaveFolder,name+".sfs");
        var nodes=ConfigNode.Load(path).GetNode("GAME").GetNode("FLIGHTSTATE").GetNodes("VESSEL");
        int index=c.HasValue("id")?Array.FindIndex(nodes,n=>Guid.Parse(n.GetValue("pid")).ToString()==c.GetValue("id") && n.GetValue("name")==vessel):Array.FindIndex(nodes,n=>n.GetValue("name")==vessel);
        if(index<0)throw new Exception("Recorded vessel absent");
        var parts=nodes[index].GetNodes("PART");int crew=parts.Sum(p=>p.GetValues("crew").Length);
        if(c1){
            bool b=crew==0 && (parts.Length==11 || parts.Length==20) && parts.Count(p=>p.GetValue("name")=="DenizSuperHeavy")==1 && (parts.Length==11 || parts.Count(p=>p.GetValue("name")=="DenizStarbaseMount")==1);
            bool carrier=crew==2 && vessel=="D Catch Flight" && new[]{25,36,45}.Contains(parts.Length) && parts.Count(p=>p.GetValue("name")=="DenizStarshipCore")==1 && parts.Count(p=>p.GetValue("name")=="DenizStarshipCabin")==1;
            if(!b && !carrier)throw new Exception("Exact recorded C1 uncrewed11/20 booster or original25/36/45 two-crew Empty flight required");
        }else if(barge){
            if(parts.Length!=1 || crew!=0 || parts[0].GetValue("name")!="SpaceXDroneship" || Math.Abs(Number(nodes[index],"lat"))>5 || Number(nodes[index],"lon") < -74.4 || Number(nodes[index],"lon") > -60)throw new Exception("Exact offshore uncrewed barge placement fixture required");
        }else if(booster){
            if(!(name.StartsWith("A4_") || name.StartsWith("A3_A4_") || name.StartsWith("A5_")) || parts.Length!=11 || crew!=0 || !parts.Any(p=>p.GetValue("name")=="DenizSuperHeavy"))throw new Exception("Intact recorded A4 eleven-part uncrewed booster required");
        }else if(name.StartsWith("A8_") && c.HasValue("id") && crew==0 && parts.Length==14 && parts.Any(p=>p.GetValue("name")=="DenizMethaloxPod") && vessel.StartsWith("D Starship 5 Refuel")){
            // Actual preserved payload, selected by GUID; no placement.
        }else if(name.StartsWith("A9_B2_") && c.HasValue("id") && crew==0 && new[]{"D Starship 1 Empty","D Starship 2C Hubble","D Starship 6C Base","D Starship 8 Tug","D Starship 8C Tug"}.Contains(vessel) && new[]{25,36,39,44,47,48,50,55,56,59,64}.Contains(parts.Length) && parts.Any(p=>p.GetValue("name")=="DenizStarshipCore")){
            // Exact original native uncrewed B2 fixture selected by GUID only.
        }else if(!(name.StartsWith("A7_") && vessel=="D Starship 3 Rover" && parts.Length==61 && crew==0 && nodes[index].GetValue("sit")=="PRELAUNCH" && parts.Any(p=>p.GetValue("name")=="DenizStarbaseMount") && parts.Any(p=>p.GetValue("name")=="DenizSuperHeavy")) && (parts.Length<25 || crew!=2))throw new Exception("Intact two-crew carrier or exact uncrewed A7 prelaunch checkpoint required");
        var game=GamePersistence.LoadGame(name,HighLogic.SaveFolder,true,false);
        missionPauseLocked=false;missionWatchdogReason="";
        FlightDriver.StartAndFocusVessel(game,index);
    }
    double Number(ConfigNode c,string key){return double.Parse(c.GetValue(key),System.Globalization.CultureInfo.InvariantCulture);}
    bool NavigationCommand(ConfigNode c,Vessel v)
    {
        string op=c.GetValue("op");
        if(op=="warp"){
            int rate=int.Parse(c.GetValue("rate"));
            if(rate<0 || rate>3 || (rate>0 && (v.altitude<75000 || v.ctrlState.mainThrottle>.0001f || v.mainBody.name!="Kerbin")))throw new Exception("Safe unpowered Kerbin orbit required for normal time warp");
            TimeWarp.fetch.CancelAutoWarp(0,true);TimeWarp.fetch.Mode=TimeWarp.Modes.HIGH;TimeWarp.SetRate(rate,true,false);return true;
        }
        if(op=="trajectories"){
            if(v.mainBody.name!="Kerbin")throw new Exception("Kerbin return target required");
            double lat=Number(c,"latitude"),lon=Number(c,"longitude"),angle=Number(c,"angle");
            if(Math.Abs(lat)>90 || Math.Abs(lon)>180 || Math.Abs(angle)>180)throw new Exception("Invalid trajectory target/profile");
            Trajectories.API.AlwaysUpdate=true;
            var settings=typeof(Trajectories.API).Assembly.GetType("Trajectories.Settings");
            foreach(string property in new[]{"DisplayTrajectories","DisplayTrajectoriesInFlight","MainGUIEnabled"})settings.GetProperty(property,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).SetValue(null,true,null);
            Trajectories.API.SetTarget(lat,lon,Number(c,"altitude"));
            Trajectories.API.ResetDescentProfile(angle*Math.PI/180);Trajectories.API.UpdateTrajectory();return true;
        }
        return false;
    }

    // Authorised A5 barge placement only. Call the installed VesselMover spawn
    // and gentle-place routines; never place a booster or carrier here.
    bool BargeCommand(ConfigNode c,Vessel v)
    {
        string op=c.GetValue("op");
        if(op!="bargeSpawn" && op!="bargePlace" && op!="bargeMove" && op!="bargeHold" && op!="deckcontactwatch")return false;
        if(v.mainBody.name!="Kerbin" || v.ctrlState.mainThrottle>.0001f)throw new Exception("Zero-throttle Kerbin test required");
        if(op=="deckcontactwatch"){
            bool booster=v.vesselName=="Deniz Super Heavy" && v.parts.Count==11 && v.GetCrewCount()==0;
            bool ship=v.vesselName=="D Starship 1 Empty" && v.parts.Count==25 && v.GetCrewCount()==2;
            if(!booster && !ship)throw new Exception("Exact intact booster or Empty carrier required for read-only deck watch");
            DenizDeckContactRecorder.Count=0;
            foreach(var part in v.parts)if(part.gameObject.GetComponent<DenizDeckContactRecorder>()==null)part.gameObject.AddComponent<DenizDeckContactRecorder>();
            return true;
        }
        File.AppendAllText(Path.Combine(Dir,"barge-adapter.log"),DateTime.UtcNow.ToString("o")+" begin "+op+" active="+v.vesselName+"\n");
        var assembly=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="VesselMover");
        if(op=="bargeSpawn"){
            if(!v.LandedOrSplashed || FlightGlobals.Vessels.Any(x=>x.vesselName=="Drone Ship - Landing Practice"))throw new Exception("Landed start and no existing test barge required");
            double lat=Number(c,"latitude"),lon=Number(c,"longitude");
            if(Math.Abs(lat)>5 || lon < -74.4 || lon > -60)throw new Exception("Near-KSC eastward test ocean coordinates required");
            string path=Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath,"Ships","VAB","Drone Ship - Landing Practice.craft"));
            var craft=ConfigNode.Load(path);var parts=craft.GetNodes("PART");
            if(parts.Length!=1 || !parts[0].GetValue("part").StartsWith("SpaceXDroneship_"))throw new Exception("Existing one-part drone ship required");
            var toolbar=assembly.GetType("VesselMover.VesselMoverToolbar");toolbar.GetField("addCrewMembers").SetValue(null,false);
            var type=assembly.GetType("VesselMover.VesselSpawn");var instance=type.GetField("instance").GetValue(null);
            File.AppendAllText(Path.Combine(Dir,"barge-adapter.log"),"before installed spawn instance="+(instance==null?"null":instance.GetType().FullName)+" path="+path+"\n");
            type.GetMethod("SpawnVesselFromCraftFile",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(instance,new object[]{path,new Vector3d(lat,lon,0),90f,0f,null});
            File.AppendAllText(Path.Combine(Dir,"barge-adapter.log"),"installed spawn returned; vessels="+FlightGlobals.Vessels.Count+"\n");
            return true;
        }
        if(v.vesselName!="Drone Ship - Landing Practice" || v.parts.Count!=1 || v.GetCrewCount()!=0 || v.rootPart.partInfo.name!="SpaceXDroneship")throw new Exception("Exact uncrewed one-part barge required");
        var move=assembly.GetType("VesselMover.VesselMove");var mover=move.GetField("Instance").GetValue(null);
        if(op=="bargeMove")throw new Exception("Direct offshore world jump retired after recorded failure; use native barge placement fixture");
        if(op=="bargeHold"){
            if(Math.Abs(v.latitude)>5 || v.longitude < -74.4 || v.longitude > -60 || v.altitude < -2 || v.altitude > 120)throw new Exception("Loaded offshore barge fixture required");
            // Authorised placement only: native save load supplies the offshore
            // coordinates. Temporarily satisfy StartMove's grounded predicate,
            // then clear it so installed EndMove can lower onto actual water.
            v.Landed=true;
            move.GetMethod("StartMove").Invoke(mover,new object[]{v,true});
            v.Landed=false;v.Splashed=false;v.landedAt=string.Empty;v.situation=Vessel.Situations.FLYING;
            if(!(bool)move.GetField("IsMovingVessel").GetValue(mover))throw new Exception("VesselMover did not hold loaded barge");
            return true;
        }
        if(!(bool)move.GetField("IsMovingVessel").GetValue(mover))throw new Exception("VesselMover moving barge required");
        move.GetMethod("EndMove").Invoke(mover,null);return true;
    }
    void BargeSnapshot(ConfigNode n,Vessel v)
    {
        var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="VesselMover");
        if(assembly!=null){var t=assembly.GetType("VesselMover.VesselMove");var o=t.GetField("Instance").GetValue(null);if(o!=null)n.AddValue("vesselMoverMoving",t.GetField("IsMovingVessel").GetValue(o));}
        n.AddValue("bargeContactCount",DenizDeckContactRecorder.Count);
        var barge=FlightGlobals.Vessels.FirstOrDefault(x=>x.vesselName=="Drone Ship - Landing Practice");
        if(barge==null)return;
        n.AddValue("bargeID",barge.id);n.AddValue("bargeLatitude",barge.latitude);n.AddValue("bargeLongitude",barge.longitude);n.AddValue("bargeAltitude",barge.altitude);n.AddValue("bargeLoaded",barge.loaded);n.AddValue("bargeSituation",barge.situation);n.AddValue("bargeHorizontalDistance",SurfaceDistance(v.mainBody,v.latitude,v.longitude,barge.latitude,barge.longitude));
        if(!barge.loaded || barge.packed || barge.parts.Count==0)return;
        n.AddValue("bargeParts",barge.parts.Count);n.AddValue("bargeBuoyancy",barge.rootPart.buoyancy);n.AddValue("bargeMass",barge.GetTotalMass());n.AddValue("bargeVerticalSpeed",barge.verticalSpeed);n.AddValue("bargeHorizontalSpeed",barge.horizontalSrfSpeed);
        n.AddValue("bargeBuoyancyUseSine",barge.rootPart.buoyancyUseSine);
        var buoyancy=barge.rootPart.GetComponent<PartBuoyancy>();
        if(buoyancy!=null){
            var diagnostics=n.AddNode("BARGE_BUOYANCY");
            foreach(var name in new[]{"depth","maxDepth","minDepth","displacement","submergedPortion","buoyantGeeForce","effectiveForce","lastBuoyantForce","dragScalar","localVelocityMag"}){
                var field=typeof(PartBuoyancy).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
                if(field!=null)diagnostics.AddValue(name,field.GetValue(buoyancy));
            }
        }
        var up=(barge.GetWorldPos3D()-barge.mainBody.position).normalized;
        foreach(var hit in Physics.RaycastAll((Vector3)(barge.rootPart.transform.position+up*100),(Vector3)(-up),200).OrderBy(h=>h.distance)){
            var p=hit.collider.GetComponentInParent<Part>();if(p==null || p.vessel!=barge)continue;
            n.AddValue("bargeDeckAltitude",barge.mainBody.GetAltitude(hit.point));n.AddValue("bargeDeckCollider",hit.collider.name);n.AddValue("bargeDeckNormalUp",Vector3d.Dot(hit.normal,up));break;
        }
    }

    double SurfaceDistance(CelestialBody body,double lat,double lon,double targetLat,double targetLon)
    {
        double a=lat*Math.PI/180,b=targetLat*Math.PI/180,d=(lon-targetLon)*Math.PI/180;
        return body.Radius*Math.Acos(Math.Max(-1,Math.Min(1,Math.Sin(a)*Math.Sin(b)+Math.Cos(a)*Math.Cos(b)*Math.Cos(d))));
    }
    void NavigationSnapshot(ConfigNode n,Vessel v)
    {
        n.AddValue("timeWarpRate",TimeWarp.CurrentRate);n.AddValue("timeWarpIndex",TimeWarp.CurrentRateIndex);n.AddValue("vesselPacked",v.packed);
        var center=v.mainBody.GetWorldSurfacePosition(v.latitude,v.longitude,v.altitude);
        var north=(v.mainBody.GetWorldSurfacePosition(Math.Min(89.9999,v.latitude+.0001),v.longitude,v.altitude)-center).normalized;
        var east=(v.mainBody.GetWorldSurfacePosition(v.latitude,v.longitude+.0001,v.altitude)-center).normalized;
        n.AddValue("surfaceVelocityHeading",(Math.Atan2(Vector3d.Dot(v.srf_velocity,east),Vector3d.Dot(v.srf_velocity,north))*180/Math.PI+360)%360);
        n.AddValue("vesselHeading",(Math.Atan2(Vector3d.Dot(v.GetTransform().up,east),Vector3d.Dot(v.GetTransform().up,north))*180/Math.PI+360)%360);
        n.AddValue("KSCRecordedPadDistance",SurfaceDistance(v.mainBody,v.latitude,v.longitude,-.097207323030493911,-74.557678325573391));
        double thrust=v.FindPartModulesImplementing<ModuleEngines>().Where(e=>e.EngineIgnited).Sum(e=>e.maxThrust*e.realIsp/e.atmosphereCurve.Evaluate(0));
        n.AddValue("activeAvailableThrust",thrust);n.AddValue("activeAvailableTWR",thrust/(v.GetTotalMass()*v.mainBody.gravParameter/Math.Pow(v.mainBody.Radius+v.altitude,2)));
        n.AddValue("trajectoriesVersion",Trajectories.API.GetVersion);n.AddValue("trajectoriesAlwaysUpdate",Trajectories.API.AlwaysUpdate);n.AddValue("trajectoriesHasTarget",Trajectories.API.HasTarget());
        var target=Trajectories.API.GetTarget();if(target.HasValue)n.AddValue("trajectoriesTargetLatLonAlt",target.Value);
        var angles=Trajectories.API.DescentProfileAngles;if(angles!=null)n.AddValue("trajectoriesProfileRadians",string.Join(",",angles.Select(x=>x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
        var impact=Trajectories.API.GetImpactPosition();n.AddValue("trajectoriesHasImpact",impact.HasValue);
        if(impact.HasValue){var world=v.mainBody.position+(Vector3d)impact.Value;double lat=v.mainBody.GetLatitude(world),lon=v.mainBody.GetLongitude(world);n.AddValue("trajectoriesImpactLatitude",lat);n.AddValue("trajectoriesImpactLongitude",lon);n.AddValue("trajectoriesImpactDistanceKSC",SurfaceDistance(v.mainBody,lat,lon,-.097207323030493911,-74.557678325573391));}
        var until=Trajectories.API.GetTimeTillImpact();if(until.HasValue)n.AddValue("trajectoriesTimeTillImpact",until.Value);
    }
    bool ControllerCommand(ConfigNode c,Vessel v)
    {
        if(c.GetValue("op")!="controllersoff")return false;
        MissionRcsCourseStop(v);
        a6FlightManaged=false;missionManaged=false;missionNativeKerbinLanding=false;missionFeedbackLanding=false;missionFeedbackDuna=false;
        v.OnFlyByWire-=MissionLandingInput;
        if(a6RcsManual || a6RcsAimActive)A6RcsStop(v);
        foreach(var core in v.FindPartModulesImplementing<MechJebCore>())foreach(string name in DirectControlModules){var module=core.GetComputerModule(name);if(module!=null){module.users.Clear();module.enabled=false;}}
        return true;
    }
    void ControllerSnapshot(ConfigNode n,Vessel v)
    {
        foreach(var core in v.FindPartModulesImplementing<MechJebCore>())foreach(string name in DirectControlModules){var module=core.GetComputerModule(name);if(module!=null){var c=n.AddNode("MJ_CONTROLLER");c.AddValue("part",core.part.partInfo.name);c.AddValue("module",name);c.AddValue("enabled",module.enabled);c.AddValue("users",module.users.Count);}}
    }
    bool AttitudeCommand(ConfigNode c,Vessel v)
    {
        if(c.GetValue("op")=="map"){if(c.GetValue("value")=="true")MapView.EnterMapView();else MapView.ExitMapView();return true;}
        if(c.GetValue("op")!="smartass")return false;
        if(!HighLogic.LoadedSceneIsFlight)throw new Exception("Flight required for SmartASS");
        var core=v.GetMasterMechJeb();if(core==null)throw new Exception("No installed MechJeb core on active vessel");
        var smart=core.GetComputerModule<MechJebModuleSmartASS>();if(smart==null)throw new Exception("SmartASS unavailable");
        string target=c.GetValue("target");
        if(target=="SURFACE_RETROGRADE_FREE"){
            if(!ExactA8DunaBase(v) || v.parts.Count!=44 || v.ActionGroups[KSPActionGroup.RCS] || !missionManaged || !MissionIntact(v))throw new Exception("Exact armed actual A8 Duna Base44 native attitude only");
            // Native MechJeb ADV UI equivalent: surface velocity / back, roll free.
            // Direction and normal engine inputs remain owned by installed controllers.
            smart.forceRol=false;smart.advReference=AttitudeReference.SURFACE_VELOCITY;
            smart.advDirection=(Vector6.Direction)Enum.Parse(typeof(Vector6.Direction),"BACK",true);
            smart.target=(MechJebModuleSmartASS.Target)Enum.Parse(typeof(MechJebModuleSmartASS.Target),"ADVANCED",true);
            smart.Engage(false);return true;
        }
        if(!new[]{"OFF","KILLROT","SURFACE","PROGRADE","RETROGRADE","SURFACE_PROGRADE","SURFACE_RETROGRADE","VERTICAL_PLUS"}.Contains(target))throw new Exception("Unsupported SmartASS target");
        if(target=="SURFACE"){
            double pitch=double.Parse(c.GetValue("pitch"),System.Globalization.CultureInfo.InvariantCulture);
            double heading=double.Parse(c.GetValue("heading"),System.Globalization.CultureInfo.InvariantCulture);
            if(pitch < -90 || pitch > 90 || heading < 0 || heading > 360)throw new Exception("Invalid surface attitude");
            smart.srfHdg.val=heading;smart.srfPit.val=pitch;smart.srfRol.val=0;
        }
        smart.target=(MechJebModuleSmartASS.Target)Enum.Parse(typeof(MechJebModuleSmartASS.Target),target);
        smart.Engage(false);return true;
    }
    void OrbitalSnapshot(ConfigNode n,Vessel v)
    {
        ControllerSnapshot(n,v);NavigationSnapshot(n,v);n.AddValue("mapView",MapView.MapIsEnabled);n.AddValue("orbitalSpeed",v.obt_speed);n.AddValue("timeToAp",v.orbit.timeToAp);n.AddValue("orbitalPeriod",v.orbit.period);n.AddValue("inclination",v.orbit.inclination);n.AddValue("eccentricity",v.orbit.eccentricity);n.AddValue("angularVelocity",v.angularVelocity.magnitude);
        n.AddValue("vesselPitch",Math.Asin(Math.Max(-1,Math.Min(1,Vector3d.Dot(v.GetTransform().up,v.upAxis))))*180/Math.PI);
        n.AddValue("angleOfAttack",v.srf_velocity.sqrMagnitude>1?Vector3d.Angle(v.GetTransform().up,v.srf_velocity):0);
        var core=v.GetMasterMechJeb();n.AddValue("mechJebPresent",core!=null);
        if(core!=null){var smart=core.GetComputerModule<MechJebModuleSmartASS>();if(smart!=null){n.AddValue("smartAssTarget",smart.target);n.AddValue("smartAssPitch",smart.srfPit.val);n.AddValue("smartAssHeading",smart.srfHdg.val);}}
        var engines=v.FindPartModulesImplementing<ModuleEngines>().Where(x=>x.EngineIgnited).ToList();double thrust=engines.Sum(x=>x.maxThrust);
        n.AddValue("activeVacuumIsp",thrust>0?engines.Sum(x=>x.maxThrust*x.atmosphereCurve.Evaluate(0))/thrust:0);
        n.AddValue("carrierPropellantMass",v.parts.Where(p=>p.partInfo.name=="DenizStarshipCore").SelectMany(p=>p.Resources.Cast<PartResource>()).Where(x=>x.resourceName=="LqdMethane"||x.resourceName=="Oxidizer").Sum(x=>x.amount*x.info.density));
    }
    // C1 read-only geometry inventory plus native tower-controller inputs.
    bool C1Inspect(ConfigNode c)
    {
        string op=c.GetValue("op");
        if(C1FmrsCommand(c) || C1ReturnCommand(c) || C1PilotCommand(c))return true;
        if(op!="c1constraints" && op!="c1inspect" && op!="c1arms" && op!="c1event" && op!="c1pinangle")return false;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight)throw new Exception("C1 dedicated test flight required");
        if(op=="c1constraints"){
            var diagnostic=new ConfigNode("C1_NATIVE_CONSTRAINTS");diagnostic.AddValue("UT",Planetarium.GetUniversalTime());diagnostic.AddValue("physicalStateAssigned",false);
            foreach(var vessel in FlightGlobals.Vessels.Where(x=>x.loaded && !x.packed))foreach(var p in vessel.parts){var pn=diagnostic.AddNode("PART");pn.AddValue("id",p.flightID);pn.AddValue("name",p.partInfo.name);pn.AddValue("vesselID",vessel.id);C1JointDiagnostics(pn,p);}
            diagnostic.Save(Path.Combine(Dir,Path.GetFileName(c.GetValue("file"))));return true;
        }
        var targets=FlightGlobals.Vessels.Where(v=>v.loaded).SelectMany(v=>v.parts).Where(p=>p.partInfo.name=="DenizSuperHeavy" || p.partInfo.name.Replace(".","_")=="DenizScaled_SLE_SS_OLIT_MZ" || p.partInfo.name.Replace(".","_")=="DenizScaled_SEP_26_BOOSTER_GRIDFIN" || p.partInfo.name=="DenizStarbaseMount").ToArray();
        if(op=="c1event" || op=="c1pinangle"){
            var p=targets.Single(x=>x.flightID.ToString()==c.GetValue("id"));
            var m=p.Modules.Cast<PartModule>().Single(x=>x.GetType().Name==c.GetValue("module"));
            if(op=="c1pinangle"){
                if(p.vessel.situation!=Vessel.Situations.PRELAUNCH || m.GetType().Name!="DenizBoosterCatchPins")throw new Exception("Only prelaunch bracket configuration allowed");
                m.Fields["pinYaw"].SetValue((float)Number(c,"angle"),m);m.Events["EnablePins"].Invoke();return true;
            }
            string ev=c.GetValue("name");if(ev!="EnablePins" && ev!="Calibrate" && ev!="ToggleCatch" && ev!="SafeRelease")throw new Exception("C1 event not allowed");m.Events[ev].Invoke();return true;
        }
        if(op=="c1arms"){
            var p=targets.Single(x=>x.flightID.ToString()==c.GetValue("id"));
            var a=p.FindModulesImplementing<ModuleSLEController>().Single(x=>x.moduleID=="SLEArmController");
            a.ExtensionLimit=0;a.OpenClose=true;a.OpenCloseLimit=(float)Number(c,"angle");a.ExtensionSpeed=10;return true;
        }
        var n=new ConfigNode("C1_GEOMETRY");n.AddValue("UT",Planetarium.GetUniversalTime());n.AddValue("scene",HighLogic.LoadedScene);n.AddValue("save",HighLogic.SaveFolder);
        foreach(var p in targets){
            var pn=n.AddNode("PART");pn.AddValue("name",p.partInfo.name);pn.AddValue("id",p.flightID);pn.AddValue("vessel",p.vessel.vesselName);pn.AddValue("vesselID",p.vessel.id);pn.AddValue("position",p.transform.position);pn.AddValue("up",p.transform.up);pn.AddValue("forward",p.transform.forward);pn.AddValue("scale",p.transform.lossyScale);
            foreach(var tr in p.GetComponentsInChildren<Transform>(true)){
                var tn=pn.AddNode("TRANSFORM");string path=tr.name;var parent=tr.parent;while(parent!=null && parent!=p.transform){path=parent.name+"/"+path;parent=parent.parent;}tn.AddValue("path",path);tn.AddValue("active",tr.gameObject.activeInHierarchy);tn.AddValue("local",p.transform.InverseTransformPoint(tr.position));tn.AddValue("world",tr.position);tn.AddValue("forward",tr.forward);tn.AddValue("up",tr.up);
                foreach(var co in tr.GetComponents<Collider>()){
                    var cn=tn.AddNode("COLLIDER");cn.AddValue("type",co.GetType().Name);cn.AddValue("enabled",co.enabled);cn.AddValue("trigger",co.isTrigger);cn.AddValue("layer",co.gameObject.layer);cn.AddValue("center",co.bounds.center);cn.AddValue("size",co.bounds.size);
                    var box=co as BoxCollider;if(box!=null){cn.AddValue("localCenter",box.center);cn.AddValue("localSize",box.size);}
                }
            }
            foreach(var a in p.FindModulesImplementing<ModuleSLEAnimate>()){var mn=pn.AddNode("ANIM");mn.AddValue("id",a.moduleID);mn.AddValue("current",a.ExtendedCurrent);mn.AddValue("limit",a.ExtensionLimit);}
        }
        C1Snapshot(n);n.Save(Path.Combine(Dir,Path.GetFileName(c.GetValue("file"))));return true;
    }
    void C1Snapshot(ConfigNode n){
        n.AddValue("C1ReturnActive",c1ReturnActive);n.AddValue("C1ReturnReason",c1ReturnReason);n.AddValue("C1ReturnHeight",c1ReturnHeight);n.AddValue("C1ReturnDistance",c1ReturnDistance);n.AddValue("C1ReturnWantedV",c1ReturnWantedV);n.AddValue("C1ReturnThrottle",c1ReturnThrottle);n.AddValue("C1PilotActive",c1PilotActive);n.AddValue("C1PilotReason",c1PilotReason);
        if(!TestSave || !HighLogic.LoadedSceneIsFlight)return;
        if(c1ReturnVessel!=null && c1ReturnVessel==FlightGlobals.ActiveVessel){var v=c1ReturnVessel;var q=n.AddNode("C1_EFFECTIVE_NATIVE_INPUT");q.AddValue("UT",Planetarium.GetUniversalTime());q.AddValue("nativeQuaternion",c1ReturnQuaternion);q.AddValue("coastTrim",c1ReturnCoastTrim);q.AddValue("precisionBrake",c1ReturnPrecisionBrake);q.AddValue("precisionApplied",c1ReturnPrecisionApplied);q.AddValue("rcsRotation",string.Join(",",v.FindPartModulesImplementing<ModuleRCSFX>().Select(x=>x.enablePitch+"/"+x.enableYaw+"/"+x.enableRoll)));q.AddValue("translation",new Vector3(v.ctrlState.X,v.ctrlState.Y,v.ctrlState.Z));q.AddValue("pitchYawRoll",new Vector3(v.ctrlState.pitch,v.ctrlState.yaw,v.ctrlState.roll));q.AddValue("referenceUp",v.ReferenceTransform.up);q.AddValue("referenceForward",v.ReferenceTransform.forward);q.AddValue("rootUp",v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy").transform.up);var d=B1Wire(v);q.AddValue("callbacks",d==null?"":string.Join("; ",d.GetInvocationList().Select(x=>x.Method.DeclaringType.FullName+"."+x.Method.Name)));}
        foreach(var p in FlightGlobals.Vessels.Where(v=>v.loaded).SelectMany(v=>v.parts))foreach(var m in p.Modules.Cast<PartModule>().Where(x=>x.GetType().Name=="DenizTowerCatch" || x.GetType().Name=="DenizBoosterCatchPins")){
            var mn=n.AddNode("C1_MODULE");m.Save(mn);mn.AddValue("type",m.GetType().Name);mn.AddValue("id",p.flightID);mn.AddValue("vesselID",p.vessel.id);
            foreach(string member in new[]{"catchStatus","contacts","SupportDetail","SupportGeometryCount","CurrentArmGap","targetError","relativeSpeed","Armed","TargetWorld","pinsEnabled","pinYaw","pinHeight"})DiagnosticValue(mn,member,DiagnosticMember(m,member));
        }
    }
    // C1 development pilot writes ordinary throttle/translation inputs only.
    // Native SAS supplies attitude; catch module supplies no return guidance.
    DenizTowerCatch c1PilotTower;
    Part c1PilotBooster;
    uint[] c1PilotIDs=new uint[0];
    Vessel c1PilotVessel;
    bool c1PilotActive;
    bool c1PilotRelease,c1PilotIgnited;
    bool c1PilotQuaternion;Vector3 c1PilotRollLocal;
    double c1PilotStartUT;
    float c1PilotHeartbeat;
    string c1PilotReason="",c1PilotFile="";
    System.Collections.Generic.Dictionary<ModuleRCSFX,bool[]> c1PilotRcs=new System.Collections.Generic.Dictionary<ModuleRCSFX,bool[]>();
    bool C1PilotCommand(ConfigNode c){
        if(c.GetValue("op")!="c1pilot")return false;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight)throw new Exception("C1 dedicated native flight required");
        if(c.GetValue("action")=="stop"){C1PilotStop("Pilot explicitly stopped",true);return true;}
        if(c.GetValue("action")=="heartbeat"){if(!c1PilotActive)throw new Exception("C1 pilot not running");c1PilotHeartbeat=Time.realtimeSinceStartup;return true;}
        var v=FlightGlobals.ActiveVessel;
        if(c1PilotActive || v==null || v.GetCrewCount()!=0 || (v.parts.Count!=20 && v.parts.Count!=11) || v.packed || v.radarAltitude>250)throw new Exception("Exact uncrewed native20/11part low pad-hop source required");
        c1PilotTower=FlightGlobals.Vessels.Where(x=>x.loaded).SelectMany(x=>x.FindPartModulesImplementing<DenizTowerCatch>()).Single(x=>x.part.flightID.ToString()==c.GetValue("tower"));
        c1PilotRelease=c.GetValue("phase")=="release";
        if(!c1PilotTower.calibrated || (!c1PilotRelease && !c1PilotTower.Armed) || (c1PilotRelease && c1PilotTower.capturedRootID==0))throw new Exception("Measured armed approach / intact caught release source required");
        c1PilotBooster=v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy");
        c1PilotIDs=v.parts.Where(p=>p.partInfo.name=="DenizSuperHeavy" || p.partInfo.name=="DenizStarshipLegBooster" || p.partInfo.name.Replace(".","_").StartsWith("DenizScaled_SEP_26_BOOSTER_")).Select(p=>p.flightID).ToArray();
        if(c1PilotIDs.Length!=11)throw new Exception("Exact11booster IDs required");
        c1PilotVessel=v;c1PilotStartUT=Planetarium.GetUniversalTime();c1PilotHeartbeat=Time.realtimeSinceStartup;c1PilotReason="Running ordinary input pad-hop pilot";c1PilotFile=Path.GetFileName(c.GetValue("file"));
        if(File.Exists(Path.Combine(Dir,c1PilotFile)))throw new Exception("Preserve previous C1 pilot trace");
        if(c.HasValue("attitude") && !new[]{"sas","quaternion"}.Contains(c.GetValue("attitude")))throw new Exception("Native final approach attitude mode required");
        c1PilotQuaternion=c.GetValue("attitude")=="quaternion";
        if(c1PilotQuaternion){var target=ConfigNode.Load(Path.Combine(Dir,Path.GetFileName(c.GetValue("source"))));var expected=target.GetValue("boosterIDs").Split(',').Select(uint.Parse).OrderBy(x=>x);if(c1PilotRelease || v.parts.Count!=11 || !c1PilotIDs.OrderBy(x=>x).SequenceEqual(expected) || target.GetValue("tower")!=c1PilotTower.part.flightID.ToString())throw new Exception("Original actual returned11 and measured native target required");c1PilotRollLocal=ConfigNode.ParseVector3(target.GetValue("rollLocal"));}
        foreach(var r in v.FindPartModulesImplementing<ModuleRCSFX>()){c1PilotRcs[r]=new[]{r.enablePitch,r.enableYaw,r.enableRoll};r.enablePitch=r.enableYaw=r.enableRoll=c1PilotQuaternion;}
        c1PilotTower.CaptureStarting+=C1PilotCaptureStarting;
        c1PilotIgnited=false;v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);v.ActionGroups.SetGroup(KSPActionGroup.SAS,!c1PilotQuaternion);v.ActionGroups.SetGroup(KSPActionGroup.RCS,true);v.OnFlyByWire+=C1PilotInput;c1PilotActive=true;return true;
    }
    void C1PilotCaptureStarting(Part root){
        if(!c1PilotActive || root!=c1PilotBooster)return;
        c1ConstraintTraceRoot=root;c1ConstraintTraceFrames=120;c1ConstraintTraceIndex=0;
        // Read after normal module origin subscribers have rebased their constraints.
        GameEvents.onFloatingOriginShift.Remove(C1ObserveOriginShift);GameEvents.onFloatingOriginShift.Add(C1ObserveOriginShift);
        C1CaptureBodies("before-native-joint",root);
        C1PilotStop("Both native contacts: shutting pilot before vessel coupling",false);
        c1PilotTower.CaptureCompleted+=C1PilotCaptureCompleted;
    }
    void C1PilotCaptureCompleted(Part root){
        if(root==c1PilotBooster){C1CaptureBodies("after-native-joint",root);C1PilotStop("Native attachment completed; pilot already shut engines",true);}
    }
    Part c1ConstraintTraceRoot;
    int c1ConstraintTraceFrames,c1ConstraintTraceIndex;
    void C1ObserveConstraintFrame(){
        if(!TestSave || Time.timeScale<=0 || c1ConstraintTraceFrames<=0)return;
        c1ConstraintTraceFrames--;c1ConstraintTraceIndex++;
        if(c1ConstraintTraceRoot==null)return;
        try{C1CaptureBodies("normal-constraint-frame-"+c1ConstraintTraceIndex,c1ConstraintTraceRoot);}catch(Exception error){File.AppendAllText(Path.Combine(Dir,"C1-constraint-observer-errors.txt"),error+"\n");}
    }
    void C1ObserveOriginShift(Vector3d offset,Vector3d nonFrame){
        if(!TestSave || c1ConstraintTraceFrames<=0 || c1ConstraintTraceRoot==null)return;
        try{C1CaptureBodies("origin-shift offset="+offset+" nonFrame="+nonFrame,c1ConstraintTraceRoot);}catch(Exception error){File.AppendAllText(Path.Combine(Dir,"C1-constraint-observer-errors.txt"),error+"\n");}
    }
    void C1CaptureBodies(string phase,Part root){
        var n=new ConfigNode("C1_CAPTURE_BODIES");n.AddValue("phase",phase);n.AddValue("UT",Planetarium.GetUniversalTime());
        foreach(var p in root.vessel.parts.Concat(c1PilotTower.vessel.parts).Distinct()){
            var q=n.AddNode("PART");q.AddValue("id",p.flightID);q.AddValue("pos",p.transform.position);q.AddValue("rot",p.transform.rotation);q.AddValue("orgPos",p.orgPos);q.AddValue("orgRot",p.orgRot);
            if(p.Rigidbody!=null){q.AddValue("rbPos",p.Rigidbody.position);q.AddValue("rbVel",p.Rigidbody.velocity);q.AddValue("rbSpin",p.Rigidbody.angularVelocity);q.AddValue("rbRot",p.Rigidbody.rotation);q.AddValue("rbMass",p.Rigidbody.mass);q.AddValue("rbInertia",p.Rigidbody.inertiaTensor);q.AddValue("rbInertiaRotation",p.Rigidbody.inertiaTensorRotation);q.AddValue("rbKinematic",p.Rigidbody.isKinematic);}
            if(p.attachJoint!=null){q.AddValue("hostAnchor",p.attachJoint.HostAnchor);q.AddValue("targetAnchor",p.attachJoint.TgtAnchor);}C1JointDiagnostics(q,p);
        }
        File.AppendAllText(Path.Combine(Dir,c1PilotFile),n.ToString()+"\n");
    }
    // Read-only actual PhysX anchors, including the stock clamp world joint
    // and autostruts omitted by Part.attachJoint-only diagnostics.
    void C1JointDiagnostics(ConfigNode n,Part p){
        n.AddValue("lossyScale",p.transform.lossyScale);
        foreach(var joint in p.GetComponents<ConfigurableJoint>()){
            var q=n.AddNode("NATIVE_JOINT");var rb=joint.GetComponent<Rigidbody>();var host=rb==null?joint.transform:rb.transform;
            Vector3 h=host.TransformPoint(joint.anchor);Vector3 t=joint.connectedBody==null?joint.connectedAnchor:joint.connectedBody.transform.TransformPoint(joint.connectedAnchor);
            q.AddValue("componentID",joint.GetInstanceID());q.AddValue("hostGameObject",joint.gameObject.name);q.AddValue("hostWorld",h);q.AddValue("connectedWorld",t);q.AddValue("anchorWorldError",Vector3.Distance(h,t));q.AddValue("anchor",joint.anchor);q.AddValue("connectedAnchor",joint.connectedAnchor);q.AddValue("connectedBody",joint.connectedBody==null?"WORLD":joint.connectedBody.gameObject.name);q.AddValue("configuredInWorldSpace",joint.configuredInWorldSpace);q.AddValue("autoConfigureConnectedAnchor",joint.autoConfigureConnectedAnchor);q.AddValue("xMotion",joint.xMotion);q.AddValue("yMotion",joint.yMotion);q.AddValue("zMotion",joint.zMotion);q.AddValue("targetPosition",joint.targetPosition);q.AddValue("targetRotation",joint.targetRotation);q.AddValue("breakForce",joint.breakForce);q.AddValue("breakTorque",joint.breakTorque);q.AddValue("angularXMotion",joint.angularXMotion);q.AddValue("angularYMotion",joint.angularYMotion);q.AddValue("angularZMotion",joint.angularZMotion);q.AddValue("axis",joint.axis);q.AddValue("secondaryAxis",joint.secondaryAxis);q.AddValue("angularYZSpring",joint.angularYZLimitSpring.spring);q.AddValue("angularYZDamper",joint.angularYZLimitSpring.damper);q.AddValue("angularYLimit",joint.angularYLimit.limit);q.AddValue("angularZLimit",joint.angularZLimit.limit);q.AddValue("linearXDriveSpring",joint.xDrive.positionSpring);q.AddValue("linearXDriveDamper",joint.xDrive.positionDamper);q.AddValue("angularXDriveSpring",joint.angularXDrive.positionSpring);q.AddValue("angularYZDriveSpring",joint.angularYZDrive.positionSpring);q.AddValue("enableCollision",joint.enableCollision);
        }
    }
    void C1PilotStop(string reason,bool pause){
        c1PilotReason=reason;c1PilotActive=false;
        if(c1PilotTower!=null){c1PilotTower.CaptureStarting-=C1PilotCaptureStarting;c1PilotTower.CaptureCompleted-=C1PilotCaptureCompleted;}
        if(c1PilotVessel!=null){c1PilotVessel.OnFlyByWire-=C1PilotInput;var core=c1PilotVessel.GetMasterMechJeb();if(core!=null)core.attitude.users.Remove(this);}
        FlightInputHandler.state.mainThrottle=0;FlightInputHandler.state.X=FlightInputHandler.state.Y=FlightInputHandler.state.Z=0;
        foreach(var r in c1PilotRcs)if(r.Key!=null){r.Key.enablePitch=r.Value[0];r.Key.enableYaw=r.Value[1];r.Key.enableRoll=r.Value[2];}c1PilotRcs.Clear();
        if(c1PilotBooster!=null && c1PilotBooster.vessel!=null){var v=c1PilotBooster.vessel;var core=v.GetMasterMechJeb();if(core!=null)core.attitude.users.Remove(this);if(c1PilotQuaternion)v.ActionGroups.SetGroup(KSPActionGroup.SAS,false);v.ActionGroups.SetGroup(KSPActionGroup.RCS,false);foreach(var e in v.FindPartModulesImplementing<ModuleEngines>())e.Shutdown();}
        if(pause)FlightDriver.SetPause(true,false);
    }
    void C1PilotTick(){
        if(!c1PilotActive || Time.timeScale<=0)return;
        if(!TestSave || c1PilotBooster==null || c1PilotTower==null){C1PilotStop("C1 pilot native references missing",true);return;}
        var v=c1PilotBooster.vessel;
        if(c1PilotQuaternion && v!=null){v.OnFlyByWire-=C1PilotInput;v.OnFlyByWire+=C1PilotInput;}
        if(v==null || !c1PilotIDs.All(id=>v.parts.Any(p=>p.flightID==id)) || Time.realtimeSinceStartup-c1PilotHeartbeat>15 || Planetarium.GetUniversalTime()-c1PilotStartUT>90 || v.radarAltitude>250 || v.angularVelocity.magnitude>.08 || Vector3d.Angle(c1PilotBooster.transform.up,v.upAxis)>8){C1PilotStop("C1 identity/heartbeat/90s/height/attitude guard",true);return;}
        if(c1PilotRelease && v!=c1PilotVessel){if(c1PilotVessel!=null)c1PilotVessel.OnFlyByWire-=C1PilotInput;c1PilotVessel=v;v.OnFlyByWire+=C1PilotInput;FlightGlobals.ForceSetActiveVessel(v);}
        if(c1PilotQuaternion && c1PilotTower.capturedRootID==0 && v.parts.SelectMany(p=>p.Resources.Cast<PartResource>()).Where(r=>r.resourceName=="LqdMethane" || r.resourceName=="Oxidizer").Sum(r=>r.amount*r.info.density)<4){C1PilotStop("C1 final approach four-tonne reserve guard",true);return;}
        // The normal booster OnActive deliberately shuts the cluster after
        // separation. The ordinary test pilot must explicitly select/activate
        // flight engines after native mount release, just as a pilot uses7.
        if(!c1PilotIgnited && (v.parts.Count==11 || c1PilotRelease)){
            c1PilotBooster.FindModuleImplementing<DenizStarshipControls>().Engines(false);c1PilotIgnited=true;
        }
        // Let native support contacts level the booster once one bracket has
        // landed. Keep ordinary hover throttle/translation and all catch gates.
        if(!c1PilotRelease && c1PilotTower.catchStatus=="Closing native arms" && c1PilotTower.contacts=="1 / 2")v.ActionGroups.SetGroup(KSPActionGroup.SAS,false);
        var n=new ConfigNode("C1_INPUT_FRAME");n.AddValue("UT",Planetarium.GetUniversalTime());n.AddValue("MET",v.missionTime);n.AddValue("parts",v.parts.Count);n.AddValue("radarAlt",v.radarAltitude);n.AddValue("error",c1PilotTower.TargetWorld-c1PilotBooster.transform.position);n.AddValue("velocity",v.srf_velocity);n.AddValue("throttle",v.ctrlState.mainThrottle);n.AddValue("thrust",v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust));n.AddValue("ignited",v.FindPartModulesImplementing<ModuleEngines>().Count(e=>e.EngineIgnited));n.AddValue("phase",c1PilotRelease?"release":"approach");n.AddValue("status",c1PilotTower.catchStatus);n.AddValue("contacts",c1PilotTower.contacts);n.AddValue("capturedRootID",c1PilotTower.capturedRootID);n.AddValue("ids",string.Join(",",c1PilotIDs));File.AppendAllText(Path.Combine(Dir,c1PilotFile),n.ToString()+"\n");
        if(!c1PilotRelease && c1PilotTower.capturedRootID!=0)C1PilotStop("Native capture detected; pilot shut engines",true);
    }
    void C1PilotInput(FlightCtrlState input){
        if(!c1PilotActive || Time.timeScale<=0)return;
        var v=c1PilotBooster.vessel;if(v==null || v!=c1PilotVessel){input.mainThrottle=0;return;}
        Vector3d up=v.upAxis;Vector3d error=(Vector3d)(c1PilotTower.TargetWorld-c1PilotBooster.transform.position);
        if(c1PilotRelease)error+=up*20;
        var armAnimations=c1PilotTower.part.FindModulesImplementing<ModuleSLEAnimate>().Where(a=>a.moduleID=="LeftArmAnim" || a.moduleID=="RightArmAnim");
        if(c1PilotTower.catchStatus=="Closing native arms" && 113.5-armAnimations.Sum(a=>a.ExtendedCurrent)<9)error-=up*.08;
        if(c1PilotQuaternion){var core=v.GetMasterMechJeb();if(c1PilotTower.catchStatus=="Closing native arms" && c1PilotTower.contacts=="1 / 2")core.attitude.users.Remove(this);else{var roll=(Vector3d)v.mainBody.transform.TransformDirection(c1PilotRollLocal);roll-=up*Vector3d.Dot(roll,up);roll.Normalize();core.attitude.attitudeTo(Quaternion.LookRotation((Vector3)up,(Vector3)roll),AttitudeReference.INERTIAL,this,true,true,true);}}
        double heightError=Vector3d.Dot(error,up);double desired=Math.Max(-2,Math.Min(2,heightError*.4));double g=v.graviticAcceleration.magnitude;
        var engines=v.FindPartModulesImplementing<ModuleEngines>().Where(e=>e.EngineIgnited);double available=engines.Sum(e=>e.maxThrust*e.realIsp/e.atmosphereCurve.Evaluate(0));double mass=v.parts.Where(p=>c1PilotIDs.Contains(p.flightID)).Sum(p=>p.mass+p.GetResourceMass());double acceleration=available/mass;
        input.mainThrottle=acceleration>0?(float)Math.Max(0,Math.Min(1,(g+2*(desired-v.verticalSpeed))/acceleration)):0;
        var hError=error-up*heightError;var hVel=v.srf_velocity-up*v.verticalSpeed;var demand=(Vector3)(c1PilotQuaternion?hError*.5-hVel*3:hError*.25-hVel*.8);var local=v.ReferenceTransform.InverseTransformDirection(-Vector3.ClampMagnitude(demand,.25f));input.X=local.x;input.Z=local.y;input.Y=local.z;
    }
    // C1 FMRS adapter calls the installed native UI operations. Replay is
    // disclosed separately from uninterrupted flight; no physical fields set.
    bool C1FmrsCommand(ConfigNode c){
        if(c.GetValue("op")!="c1fmrs")return false;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight)throw new Exception("Dedicated native C1 flight required");
        var core=FMRS.FMRS_Core.Instance as FMRS.FMRS_Core;if(core==null)throw new Exception("Native public FMRS Instance not ready");
        var v=FlightGlobals.ActiveVessel;string action=c.GetValue("action");
        if(action=="arm"){
            if(v.vesselName!="D Catch Flight" || v.parts.Count!=45 || v.GetCrewCount()!=2 || v.situation!=Vessel.Situations.PRELAUNCH || v.ctrlState.mainThrottle!=0)throw new Exception("Exact fresh C1 full45 two-crew pad source required");
            var settings=HighLogic.CurrentGame.Parameters.CustomParams<FMRS.FMRS_Settings>();var before=new ConfigNode("FMRS_SETTINGS_BEFORE_C1");before.AddValue("gameParameterAutoRecover",settings._SETTING_Auto_Recover);before.AddValue("gameParameterAutoCutOff",settings._SETTING_Auto_Cut_Off);before.AddValue("gameParameterThrottleLog",settings._SETTING_Throttle_Log);
            before.AddValue("_SETTING_Enabled",core._SETTING_Enabled);before.AddValue("_SETTING_Armed",core._SETTING_Armed);before.AddValue("_SETTING_Auto_Recover",FMRS.FMRS_Util._SETTING_Auto_Recover);before.AddValue("_SETTING_Auto_Cut_Off",FMRS.FMRS_Util._SETTING_Auto_Cut_Off);before.AddValue("_SETTING_Throttle_Log",FMRS.FMRS_Util._SETTING_Throttle_Log);
            string file=Path.Combine(Dir,Path.GetFileName(c.GetValue("file")));if(File.Exists(file))throw new Exception("Preserve prior FMRS settings record");before.Save(file);
            if(!core._SETTING_Enabled)core.toolbar_open();
            settings._SETTING_Auto_Recover=false;settings._SETTING_Auto_Cut_Off=true;settings._SETTING_Throttle_Log=false;core._SETTING_Armed=true;FMRS.FMRS_Util._SETTING_Auto_Recover=false;FMRS.FMRS_Util._SETTING_Auto_Cut_Off=true;FMRS.FMRS_Util._SETTING_Throttle_Log=false;
            core.write_save_values_to_file();
        }else if(action=="jump"){
            if(v.vesselName!="D Catch Flight" || v.parts.Count!=25 || v.GetCrewCount()!=2 || v.orbit.PeA<75000 || v.ctrlState.mainThrottle!=0 || Time.timeScale!=0)throw new Exception("Actual intact stable two-crew C1 orbit/paused/main0 required");
            Guid id=Guid.Parse(c.GetValue("id"));if(!core.Vessels_dropped.ContainsKey(id))throw new Exception("Native FMRS recorded dropped vessel required");
            string file=core.Vessels_dropped[id];var g=GamePersistence.LoadGame(file,HighLogic.SaveFolder+"/FMRS",false,false);var p=g.flightState.protoVessels.Single(x=>x.vesselID==id);
            if(p.protoPartSnapshots.Count!=11 || p.protoPartSnapshots.Sum(x=>x.protoModuleCrew.Count)!=0 || p.protoPartSnapshots.Count(x=>x.partName=="DenizSuperHeavy")!=1)throw new Exception("Native recorded eleven-part uncrewed booster required");
            core.jump_to_vessel(id,true);
        }else if(action=="commitcatch"){
            // FMRS tests only the first saved module when following a parent
            // vessel after native coupling. Expose its existing ancestry tag
            // for one synchronous native save, then restore module order.
            // This changes bookkeeping order only, never physical state.
            if(Time.timeScale!=0 || c1PilotActive || c1ReturnActive || v.parts.Count!=20 || v.GetCrewCount()!=0 || !v.Landed || v.ctrlState.mainThrottle!=0 || v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust)>.001 || !core.SwitchedToDropped || FMRS.FMRS_Util._SETTING_Auto_Recover)throw new Exception("Actual paused unassisted landed twenty-part native catch required");
            var source=ConfigNode.Load(Path.Combine(Dir,Path.GetFileName(c.GetValue("source"))));var all=source.GetValue("all45IDs").Split(',').Select(uint.Parse).OrderBy(x=>x).ToArray();
            var root=v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy");var tower=v.FindPartModulesImplementing<DenizTowerCatch>().Single(x=>x.part.flightID.ToString()==source.GetValue("tower"));
            if(tower.capturedRootID!=root.flightID || root.parent!=tower.part || root.attachJoint==null || tower.SupportGeometryCount!=2 || Math.Abs(tower.CurrentArmGap-8)>1 || tower.catchStatus!="Supported by native attachment")throw new Exception("Actual saved native catch joint and both arm geometry pairs required");
            var pm=root.Modules.Cast<PartModule>().Single(x=>x.moduleName=="FMRS_PM");Guid original=Guid.Parse((string)DiagnosticMember(pm,"parent_vessel"));
            var recoveryIDs=(System.Collections.Generic.List<Guid>)typeof(FMRS.FMRS_Core).GetField("loaded_vessels",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(core);
            if(recoveryIDs.Any(x=>x!=original) || recoveryIDs.Count>1 || !core.Vessels_dropped.ContainsKey(original) || v.id==original || v.parts.Where(p=>p!=root).Any(p=>p.Modules.Count>0 && p.Modules[0].moduleName=="FMRS_PM"))throw new Exception("Exact recorded booster recovery group and unique ancestry tag required");
            var before=GamePersistence.LoadGame("FMRS_main_save",HighLogic.SaveFolder,false,false);C1FmrsMainAudit(before,all,false,v.id,core._SAVE_Main_Vessel);
            var savedBooster=before.flightState.protoVessels.Single(x=>x.vesselID==original);var expectedBooster=source.GetValue("boosterIDs").Split(',').Select(uint.Parse).OrderBy(x=>x).ToArray();
            if(!savedBooster.protoPartSnapshots.Select(x=>x.flightID).OrderBy(x=>x).SequenceEqual(expectedBooster) || !v.parts.Select(x=>x.flightID).OrderBy(x=>x).SequenceEqual(all.Except(before.flightState.protoVessels.Single(x=>x.vesselID==core._SAVE_Main_Vessel).protoPartSnapshots.Select(x=>x.flightID)).OrderBy(x=>x)))throw new Exception("Actual original11 ancestry and current20 exact identity union required");
            var order=root.Modules.Cast<PartModule>().ToArray();int index=root.Modules.IndexOf(pm);if(index<=0)throw new Exception("Original ancestry ordering must be recorded");
            var audit=new ConfigNode("C1_NATIVE_FMRS_CATCH_COMMIT");audit.AddValue("UT",Planetarium.GetUniversalTime());audit.AddValue("originalBoosterGUID",original);audit.AddValue("actualCaughtGUID",v.id);audit.AddValue("nativeParentID",root.parent.flightID);audit.AddValue("originalTagIndex",index);audit.AddValue("originalModuleOrder",string.Join(",",order.Select(x=>x.moduleName)));audit.AddValue("physicalStateAssigned",false);
            string file=Path.Combine(Dir,Path.GetFileName(c.GetValue("file")));if(File.Exists(file))throw new Exception("Preserve prior native commit audit");
            // A caught-state reload can discard the original GUID from this
            // recovery-only list because stock coupling uses the tower GUID.
            // Re-register only the proven existing booster ancestry group.
            audit.AddValue("recordedRecoveryGroupReregistered",recoveryIDs.Count==0);if(recoveryIDs.Count==0)recoveryIDs.Add(original);
            try{root.Modules[0]=pm;root.Modules[index]=order[0];core.save_landed_vessel(false,false);}
            finally{for(int i=0;i<order.Length;i++)root.Modules[i]=order[i];}
            if(!root.Modules.Cast<PartModule>().SequenceEqual(order))throw new Exception("Original live module order restoration failed");
            audit.AddValue("liveModuleOrderRestored",true);audit.AddValue("nativeMergeCalled",true);audit.Save(file);
            var after=GamePersistence.LoadGame("FMRS_main_save",HighLogic.SaveFolder,false,false);C1FmrsMainAudit(after,all,true,v.id,core._SAVE_Main_Vessel);audit.AddValue("all45IDsExactlyOnce",true);audit.AddValue("mainCrewAlive",true);audit.AddValue("nativeCaught20AndUpper25",true);audit.Save(file);
        }else if(action=="main"){
            if(Time.timeScale!=0 || v.parts.Count!=20 || v.GetCrewCount()!=0 || v.ctrlState.mainThrottle!=0 || !core.SwitchedToDropped)throw new Exception("Paused actual C1 catch branch required");
            var source=ConfigNode.Load(Path.Combine(Dir,Path.GetFileName(c.GetValue("source"))));var all=source.GetValue("all45IDs").Split(',').Select(uint.Parse).OrderBy(x=>x).ToArray();C1FmrsMainAudit(GamePersistence.LoadGame("FMRS_main_save",HighLogic.SaveFolder,false,false),all,true,v.id,core._SAVE_Main_Vessel);
            core.jump_to_vessel("Main");
        }else if(action=="close"){
            if(Time.timeScale!=0 || v.ctrlState.mainThrottle!=0 || c1PilotActive || c1ReturnActive)throw new Exception("Paused safe C1 source required before native FMRS close");core.close_FMRS();
        }else if(action!="inspect")throw new Exception("C1 FMRS guarded native operations only");
        var n=new ConfigNode("C1_FMRS");n.AddValue("UT",Planetarium.GetUniversalTime());n.AddValue("action",action);
        n.AddValue("_SETTING_Enabled",core._SETTING_Enabled);n.AddValue("_SETTING_Armed",core._SETTING_Armed);n.AddValue("_SETTING_Auto_Recover",FMRS.FMRS_Util._SETTING_Auto_Recover);n.AddValue("_SETTING_Auto_Cut_Off",FMRS.FMRS_Util._SETTING_Auto_Cut_Off);n.AddValue("_SETTING_Throttle_Log",FMRS.FMRS_Util._SETTING_Throttle_Log);
        n.AddValue("gameParameterAutoRecover",HighLogic.CurrentGame.Parameters.CustomParams<FMRS.FMRS_Settings>()._SETTING_Auto_Recover);n.AddValue("gameParameterThrottleLog",HighLogic.CurrentGame.Parameters.CustomParams<FMRS.FMRS_Settings>()._SETTING_Throttle_Log);n.AddValue("_SAVE_Main_Vessel",core._SAVE_Main_Vessel);n.AddValue("_SAVE_Switched_To_Savefile",core._SAVE_Switched_To_Savefile??"");n.AddValue("_SAVE_Switched_To_Dropped",core._SAVE_Switched_To_Dropped);n.AddValue("_SAVE_Has_Launched",core._SAVE_Has_Launched);n.AddValue("Active",core.Active);n.AddValue("SwitchedToDropped",core.SwitchedToDropped);
        foreach(var entry in core.Vessels_dropped){var row=n.AddNode("Vessels_dropped");row.AddValue("id",entry.Key);row.AddValue("value",entry.Value);}
        foreach(var entry in core.Vessels_dropped_names){var row=n.AddNode("Vessels_dropped_names");row.AddValue("id",entry.Key);row.AddValue("value",entry.Value);}
        foreach(var entry in core.Vessel_State){var row=n.AddNode("Vessel_State");row.AddValue("id",entry.Key);row.AddValue("value",entry.Value);}
        var loaded=(IEnumerable)typeof(FMRS.FMRS_Core).GetField("loaded_vessels",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.DeclaredOnly).GetValue(core);foreach(var id in loaded)n.AddValue("loadedRecoveryID",id);
        n.Save(Path.Combine(Dir,Path.GetFileName(c.GetValue("output"))));return true;
    }
    // Test-only return pilot: native throttle/gimbal/MJ attitude, genuine fuel
    // consumption, no placement. A saved pad measurement supplies the target.
    Vessel c1ReturnVessel;uint[] c1ReturnIDs=new uint[0];bool c1ReturnActive,c1ReturnIgnited,c1ReturnBrake,c1ReturnNear;
    double c1ReturnStartUT,c1ReturnNextLog,c1ReturnPreviousUT,c1ReturnPreviousV,c1ReturnPreviousEngine,c1ReturnExternal=-9.81;
    double c1ReturnLat,c1ReturnLon,c1ReturnAlt,c1ReturnHeight,c1ReturnDistance,c1ReturnWantedV,c1ReturnThrottle;
    double c1ReturnHandoffHeight=40;
    Vector3 c1ReturnRollLocal;float c1ReturnHeartbeat;string c1ReturnFile="",c1ReturnReason="Off";
    bool c1ReturnQuaternion,c1ReturnCoastTrim,c1ReturnPrecisionBrake,c1ReturnPrecisionApplied;
    string c1ReturnOriginalRcsMode="";
    System.Collections.Generic.Dictionary<ModuleRCSFX,bool[]> c1ReturnRcs=new System.Collections.Generic.Dictionary<ModuleRCSFX,bool[]>();
    void C1FmrsMainAudit(Game game,uint[] all,bool caught,Guid caughtID,Guid mainID){
        if(game==null || game.flightState==null)throw new Exception("Native main mission source absent");
        var mission=game.flightState.protoVessels.Where(x=>x.protoPartSnapshots.Any(p=>all.Contains(p.flightID))).ToArray();var ids=mission.SelectMany(x=>x.protoPartSnapshots.Select(p=>p.flightID)).OrderBy(x=>x).ToArray();
        if(!ids.SequenceEqual(all))throw new Exception("Original45 native mission identities missing or duplicated");
        var upper=mission.Single(x=>x.vesselID==mainID);var crew=upper.protoPartSnapshots.SelectMany(p=>p.protoModuleCrew).ToArray();
        if(upper.protoPartSnapshots.Count!=25 || crew.Length!=2 || upper.orbitSnapShot.Load().PeA<75000 || crew.Any(x=>x.rosterStatus==ProtoCrewMember.RosterStatus.Dead))throw new Exception("Actual stable original25 two-live-crew main orbit required");
        if(caught){var joined=mission.Single(x=>x.vesselID==caughtID);if(mission.Length!=2 || joined.protoPartSnapshots.Count!=20 || !joined.landed || joined.protoPartSnapshots.Sum(p=>p.protoModuleCrew.Count)!=0)throw new Exception("Native main20 caught plus25 upper graph required");}
        else if(mission.Length!=3 || !mission.Select(x=>x.protoPartSnapshots.Count).OrderBy(x=>x).SequenceEqual(new[]{9,11,25}))throw new Exception("Recorded native premerge9/11/25 graph required");
    }
    bool C1ReturnCommand(ConfigNode c){
        if(c.GetValue("op")!="c1return")return false;
        if(!TestSave || !HighLogic.LoadedSceneIsFlight)throw new Exception("Dedicated native C1 flight required");
        var v=FlightGlobals.ActiveVessel;string action=c.GetValue("action");
        if(action=="prepare"){
            if(v.vesselName!="D Catch Flight" || v.parts.Count!=45 || v.GetCrewCount()!=2 || v.situation!=Vessel.Situations.PRELAUNCH || v.ctrlState.mainThrottle!=0)throw new Exception("Exact fresh C1 forty-five part pad source required");
            var tower=v.FindPartModulesImplementing<DenizTowerCatch>().Single(x=>x.part.flightID.ToString()==c.GetValue("tower"));var b=v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy");
            if(!tower.calibrated || !b.FindModuleImplementing<DenizBoosterCatchPins>().pinsEnabled)throw new Exception("Actual native calibration and enabled brackets required");
            var n=new ConfigNode("C1_NATIVE_PAD_TARGET");n.AddValue("UT",Planetarium.GetUniversalTime());n.AddValue("tower",tower.part.flightID);n.AddValue("latitude",v.mainBody.GetLatitude(tower.TargetWorld));n.AddValue("longitude",v.mainBody.GetLongitude(tower.TargetWorld));n.AddValue("altitude",v.mainBody.GetAltitude(tower.TargetWorld));n.AddValue("rollLocal",v.mainBody.transform.InverseTransformDirection(-b.transform.forward));
            var ids=v.parts.Where(p=>p.partInfo.name=="DenizSuperHeavy" || p.partInfo.name=="DenizStarshipLegBooster" || p.partInfo.name.Replace(".","_").StartsWith("DenizScaled_SEP_26_BOOSTER_")).Select(p=>p.flightID).ToArray();if(ids.Length!=11)throw new Exception("Exact original11 booster identities required");n.AddValue("boosterIDs",string.Join(",",ids));n.AddValue("all45IDs",string.Join(",",v.parts.Select(p=>p.flightID)));n.AddValue("crew",string.Join(",",v.GetVesselCrew().Select(x=>x.name)));
            string path=Path.Combine(Dir,Path.GetFileName(c.GetValue("file")));if(File.Exists(path))throw new Exception("Preserve original pad target measurement");n.Save(path);return true;
        }
        if(action=="stop"){C1ReturnStop("Explicit ordinary return cutoff",true);return true;}
        if(action=="heartbeat"){if(!c1ReturnActive)throw new Exception("Return pilot not running");c1ReturnHeartbeat=Time.realtimeSinceStartup;return true;}
        if(action!="start" || c1ReturnActive || c1PilotActive || v.parts.Count!=11 || v.GetCrewCount()!=0 || v.mainBody.name!="Kerbin" || v.packed || v.ctrlState.mainThrottle!=0 || v.altitude<10000 || v.altitude>150000)throw new Exception("Recorded intact native eleven-part Kerbin return/10–150km/main0 required");
        var source=ConfigNode.Load(Path.Combine(Dir,Path.GetFileName(c.GetValue("source"))));c1ReturnIDs=source.GetValue("boosterIDs").Split(',').Select(uint.Parse).ToArray();
        if(!v.parts.Select(p=>p.flightID).OrderBy(x=>x).SequenceEqual(c1ReturnIDs.OrderBy(x=>x)))throw new Exception("Actual original pad booster identities required");
        c1ReturnLat=Number(source,"latitude");c1ReturnLon=Number(source,"longitude");c1ReturnAlt=Number(source,"altitude");c1ReturnRollLocal=ConfigNode.ParseVector3(source.GetValue("rollLocal"));
        c1ReturnFile=Path.GetFileName(c.GetValue("file"));if(File.Exists(Path.Combine(Dir,c1ReturnFile)))throw new Exception("Preserve previous return trace");
        c1ReturnVessel=v;c1ReturnStartUT=Planetarium.GetUniversalTime();c1ReturnHeartbeat=Time.realtimeSinceStartup;c1ReturnIgnited=c1ReturnBrake=c1ReturnNear=false;c1ReturnPreviousUT=0;c1ReturnNextLog=0;c1ReturnExternal=-9.81;c1ReturnReason="Normal input return coast/brake";
        if(c.HasValue("rotation") && !new[]{"true","false"}.Contains(c.GetValue("rotation")))throw new Exception("Native RCS rotation checkbox boolean required");
        if(c.HasValue("attitude") && !new[]{"twoaxis","quaternion"}.Contains(c.GetValue("attitude")))throw new Exception("Native attitude mode required");
        if(c.HasValue("trim") && !new[]{"true","false"}.Contains(c.GetValue("trim")))throw new Exception("Coast translation boolean required");
        c1ReturnHandoffHeight=c.HasValue("handoffHeight")?Number(c,"handoffHeight"):40;if(c1ReturnHandoffHeight<10 || c1ReturnHandoffHeight>40)throw new Exception("Measured catch handoff height10–40m required");
        c1ReturnQuaternion=c.GetValue("attitude")=="quaternion";c1ReturnCoastTrim=c.GetValue("trim")!="false";
        if(c.HasValue("precisionBrake") && !new[]{"true","false"}.Contains(c.GetValue("precisionBrake")))throw new Exception("Native precision-brake checkbox boolean required");
        c1ReturnPrecisionBrake=c.GetValue("precisionBrake")=="true";c1ReturnPrecisionApplied=false;c1ReturnOriginalRcsMode=v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy").FindModuleImplementing<DenizStarshipControls>().rcsMode;
        bool rotation=c.GetValue("rotation")!="false";
        foreach(var r in v.FindPartModulesImplementing<ModuleRCSFX>()){c1ReturnRcs[r]=new[]{r.enablePitch,r.enableYaw,r.enableRoll};r.enablePitch=r.enableYaw=r.enableRoll=rotation;}
        v.ActionGroups.SetGroup(KSPActionGroup.SAS,false);v.ActionGroups.SetGroup(KSPActionGroup.RCS,true);v.OnFlyByWire+=C1ReturnInput;c1ReturnActive=true;return true;
    }
    void C1ReturnStop(string reason,bool pause){
        c1ReturnReason=reason;c1ReturnActive=false;
        foreach(var r in c1ReturnRcs)if(r.Key!=null){r.Key.enablePitch=r.Value[0];r.Key.enableYaw=r.Value[1];r.Key.enableRoll=r.Value[2];}c1ReturnRcs.Clear();
        if(c1ReturnVessel!=null){if(c1ReturnPrecisionApplied){var controls=c1ReturnVessel.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy").FindModuleImplementing<DenizStarshipControls>();if(c1ReturnOriginalRcsMode=="Docking precision")controls.RcsPrecision();else controls.RcsHigh();}c1ReturnVessel.OnFlyByWire-=C1ReturnInput;var core=c1ReturnVessel.GetMasterMechJeb();if(core!=null)core.attitude.users.Remove(this);c1ReturnVessel.ctrlState.mainThrottle=0;c1ReturnVessel.ctrlState.roll=0;c1ReturnVessel.ctrlState.X=c1ReturnVessel.ctrlState.Y=c1ReturnVessel.ctrlState.Z=0;c1ReturnVessel.ActionGroups.SetGroup(KSPActionGroup.RCS,false);}
        FlightInputHandler.state.mainThrottle=0;FlightInputHandler.state.roll=0;FlightInputHandler.state.X=FlightInputHandler.state.Y=FlightInputHandler.state.Z=0;if(pause)FlightDriver.SetPause(true,false);
    }
    void C1ReturnTick(){
        if(!c1ReturnActive || Time.timeScale<=0)return;var v=c1ReturnVessel;
        // Enabling native attitude can append MechJeb's callback after ours.
        // Keep one ordinary pilot callback last, so roll/translation requests
        // are applied after native pitch/yaw rather than overwritten by it.
        if(v!=null){v.OnFlyByWire-=C1ReturnInput;v.OnFlyByWire+=C1ReturnInput;}
        string reason=null;
        if(!TestSave || v==null || v!=FlightGlobals.ActiveVessel || v.packed || v.mainBody.name!="Kerbin" || v.GetCrewCount()!=0 || !v.parts.Select(p=>p.flightID).OrderBy(x=>x).SequenceEqual(c1ReturnIDs.OrderBy(x=>x)))reason="Return native identity/focus guard";
        else if(Time.realtimeSinceStartup-c1ReturnHeartbeat>15)reason="Return heartbeat guard";
        else if(Planetarium.GetUniversalTime()-c1ReturnStartUT>600)reason="Return six-hundred-second guard";
        else if(v.angularVelocity.magnitude>.4)reason="Return spin guard";
        else if(v.parts.Max(p=>Math.Max(p.temperature/p.maxTemp,p.skinTemperature/p.skinMaxTemp))>.98)reason="Return thermal guard";
        else if(v.LandedOrSplashed || (v.altitude-c1ReturnAlt<15 && v.verticalSpeed < -5) || v.parts.SelectMany(p=>p.Resources.Cast<PartResource>()).Where(r=>r.resourceName=="LqdMethane" || r.resourceName=="Oxidizer").Sum(r=>r.amount*r.info.density)<4)reason="Return contact/descent/four-tonne reserve guard";
        if(reason!=null)C1ReturnStop(reason,true);
    }
    void C1ReturnInput(FlightCtrlState input){
        if(!c1ReturnActive || Time.timeScale<=0)return;var v=c1ReturnVessel;double ut=Planetarium.GetUniversalTime(),vertical=v.verticalSpeed;Vector3d up=v.upAxis;
        if(!c1ReturnIgnited){v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy").FindModuleImplementing<DenizStarshipControls>().Engines(false);c1ReturnIgnited=true;}
        var target=v.mainBody.GetWorldSurfacePosition(c1ReturnLat,c1ReturnLon,c1ReturnAlt);Vector3d error=target-v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy").transform.position;
        double height=-Vector3d.Dot(error,up);var horizontalError=error-up*Vector3d.Dot(error,up);var hVelocity=v.srf_velocity-up*vertical;double distance=horizontalError.magnitude;
        var engines=v.FindPartModulesImplementing<ModuleEngines>().Where(e=>e.EngineIgnited);double available=engines.Sum(e=>e.maxThrust*e.realIsp/e.atmosphereCurve.Evaluate(0));double mass=v.GetTotalMass(),maxA=available/mass;
        double engineVertical=v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust)/mass*Vector3d.Dot(v.GetTransform().up,up);
        double dt=ut-c1ReturnPreviousUT;if(c1ReturnPreviousUT>0 && dt>0 && dt<.2){double measured=(vertical-c1ReturnPreviousV)/dt-.5*(engineVertical+c1ReturnPreviousEngine);c1ReturnExternal=.98*c1ReturnExternal+.02*Math.Max(-45,Math.Min(45,measured));}
        c1ReturnPreviousUT=ut;c1ReturnPreviousV=vertical;c1ReturnPreviousEngine=engineVertical;
        if(height<Math.Max(0,-vertical)*Math.Max(0,-vertical)/(2*Math.Max(5,.85*maxA-9.81))+1500)c1ReturnBrake=true;
        if(c1ReturnBrake && c1ReturnPrecisionBrake && !c1ReturnPrecisionApplied){
            // Coast uses wheels only. Ordinary native precision-RCS rotation
            // supplements attitude tracking during the actual powered descent.
            v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy").FindModuleImplementing<DenizStarshipControls>().RcsPrecision();
            foreach(var r in c1ReturnRcs.Keys)r.enablePitch=r.enableYaw=r.enableRoll=true;
            c1ReturnPrecisionApplied=true;
        }
        if(height<300)c1ReturnNear=true;
        double profile=height>3500?25:4;double wanted=-Math.Min(height>3500?950:80,Math.Sqrt(2*profile*Math.Max(0,height-120)));
        if(c1ReturnNear){profile=0;double hold=(distance<3 && hVelocity.magnitude<.5)?c1ReturnHandoffHeight:120;wanted=Math.Max(-5,Math.Min(3,(hold-height)*.2));}
        double speed=Math.Min(height>800?80:20,Math.Min(Math.Sqrt(4*distance),distance*(height>800?.12:.15)));var desiredH=distance>.01?horizontalError/distance*speed:Vector3d.zero;
        var horizontalA=height>800?(desiredH-hVelocity)/4:horizontalError*.10-hVelocity*.9;double limit=height>800?8:2;if(horizontalA.magnitude>limit)horizontalA=horizontalA.normalized*limit;
        double verticalA=Math.Max(.2,Math.Min(.9*maxA,profile+(wanted-vertical)*(height<400?.9:.5)-c1ReturnExternal));
        var direction=c1ReturnBrake?(up*verticalA+horizontalA).normalized:up;var roll=(Vector3d)v.mainBody.transform.TransformDirection(c1ReturnRollLocal);roll-=direction*Vector3d.Dot(roll,direction);roll.Normalize();
        var attitude=v.GetMasterMechJeb().attitude;
        // Select an explicit native attitude test mode; RCS rotation UI
        // checkboxes can be isolated from translation and are restored on stop.
        if(c1ReturnQuaternion)attitude.attitudeTo(Quaternion.LookRotation((Vector3)direction,(Vector3)roll),AttitudeReference.INERTIAL,this,true,true,true);
        else attitude.attitudeTo(direction,AttitudeReference.INERTIAL,this);
        var booster=v.parts.Single(p=>p.partInfo.name=="DenizSuperHeavy");var rollAxis=(Vector3d)booster.transform.up;var actualRoll=-(Vector3d)booster.transform.forward;var wantedRoll=roll-rollAxis*Vector3d.Dot(roll,rollAxis);wantedRoll.Normalize();
        double signedRoll=Math.Atan2(Vector3d.Dot(rollAxis,Vector3d.Cross(actualRoll,wantedRoll)),Vector3d.Dot(actualRoll,wantedRoll));double rollRate=Vector3d.Dot((Vector3d)booster.rb.angularVelocity,rollAxis);
        double wantedRate=Vector3d.Angle(rollAxis,direction)<15?Math.Max(-.025,Math.Min(.025,signedRoll*.05)):0;
        if(!c1ReturnQuaternion)input.roll=(float)-Math.Max(-.15,Math.Min(.15,(wantedRate-rollRate)*10));
        input.X=input.Y=input.Z=0;
        if(!c1ReturnBrake && c1ReturnCoastTrim){
            // Ordinary early RCS translation trims the native ballistic miss,
            // avoiding a long powered lateral hover near the tower.
            double gravity=Math.Max(1,v.graviticAcceleration.magnitude);double remaining=Math.Max(30,(vertical+Math.Sqrt(vertical*vertical+2*gravity*Math.Max(0,height)))/gravity+10);
            var trim=(horizontalError/remaining-hVelocity)/50;var local=v.ReferenceTransform.InverseTransformDirection(-Vector3.ClampMagnitude((Vector3)trim,.6f));input.X=local.x;input.Z=local.y;input.Y=local.z;
        }
        double tilt=Vector3d.Dot(v.GetTransform().up,up);double throttle=c1ReturnBrake?Math.Max(0,Math.Min(1,verticalA/(maxA*Math.Max(.1,tilt)))):0;
        input.mainThrottle=(float)throttle;FlightInputHandler.state.mainThrottle=input.mainThrottle;c1ReturnHeight=height;c1ReturnDistance=distance;c1ReturnWantedV=wanted;c1ReturnThrottle=throttle;
        if(ut>=c1ReturnNextLog){var n=new ConfigNode("C1_RETURN_FRAME");n.AddValue("UT",ut);n.AddValue("MET",v.missionTime);n.AddValue("alt",v.altitude);n.AddValue("heightAboveMeasuredCatch",height);n.AddValue("horizontalError",distance);n.AddValue("vertical",vertical);n.AddValue("horizontalSpeed",hVelocity.magnitude);n.AddValue("wantedVertical",wanted);n.AddValue("throttle",throttle);n.AddValue("thrust",v.FindPartModulesImplementing<ModuleEngines>().Sum(e=>e.finalThrust));n.AddValue("externalVerticalAcceleration",c1ReturnExternal);n.AddValue("tiltUp",tilt);n.AddValue("braking",c1ReturnBrake);n.AddValue("precisionRcsApplied",c1ReturnPrecisionApplied);n.AddValue("rollError",Vector3d.Angle(-v.GetTransform().forward,roll));n.AddValue("signedRollError",signedRoll*180/Math.PI);n.AddValue("rollRate",rollRate);n.AddValue("wantedRollRate",wantedRate);n.AddValue("ordinaryRollInput",input.roll);n.AddValue("ordinaryTranslationInput",new Vector3(input.X,input.Y,input.Z));n.AddValue("ids",string.Join(",",c1ReturnIDs));File.AppendAllText(Path.Combine(Dir,c1ReturnFile),n.ToString()+"\n");c1ReturnNextLog=ut+.1;}
        if(c1ReturnNear && Math.Abs(height-c1ReturnHandoffHeight)<1 && distance<2 && hVelocity.magnitude<.3 && Math.Abs(vertical)<.3 && Vector3d.Angle(v.GetTransform().up,up)<.5 && Vector3d.Angle(-v.GetTransform().forward,roll)<.5 && v.angularVelocity.magnitude<.005)C1ReturnStop("Ready for actual tower hover handoff",true);
    }
    CraftBrowserDialog editorBrowser;
    bool EditorCommand(ConfigNode c)
    {
        if(C1Inspect(c))return true;
        string op=c.GetValue("op");
        if(op=="editor"){EditorDriver.StartEditor(EditorFacility.VAB);return true;}
        if(op=="fullscreen"){GameSettings.FULLSCREEN=true;Screen.SetResolution(GameSettings.SCREEN_RESOLUTION_WIDTH,GameSettings.SCREEN_RESOLUTION_HEIGHT,true);return true;}
        if(op=="editorview")
        {
            if(!HighLogic.LoadedSceneIsEditor || EditorDriver.fetch==null)throw new Exception("VAB required");
            var camera=EditorDriver.fetch.vabCamera;camera.maxDistance=600;camera.maxHeight=220;camera.sharpness=20;
            EditorBounds.Instance.cameraMaxDistance=600;EditorBounds.Instance.cameraOffsetBounds=new Bounds(new Vector3(0,100,0),new Vector3(600,600,600));
            camera.PlaceCamera(new Vector3(0,float.Parse(c.GetValue("height")),0),float.Parse(c.GetValue("distance")));
            camera.camPitch=float.Parse(c.GetValue("pitch"))*Mathf.Deg2Rad;camera.camHdg=float.Parse(c.GetValue("heading"))*Mathf.Deg2Rad;return true;
        }
        if(op=="editorbrowserstock")
        {
            if(!HighLogic.LoadedSceneIsEditor || editorBrowser==null)throw new Exception("Open the VAB browser first");
            var directory=editorBrowser.GetComponentsInChildren<DirectoryController>(true).First();
            var stock=directory.GetComponentsInChildren<DirectoryActionGroup>(true).First(g=>g.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(t=>t.text.StartsWith("Stock")));
            directory.SelectDirectory(stock);
            editorBrowser.BuildStockCraftList();
            CraftSearch.Instance.searchField.text=c.HasValue("search")?c.GetValue("search"):"D Starship";
            CraftSearch.Instance.SearchStart();
            return true;
        }
        if(op=="editorbrowserscroll")
        {
            if(!HighLogic.LoadedSceneIsEditor || editorBrowser==null)throw new Exception("Open the VAB browser first");
            CraftBrowserDialog.ScrollView.verticalNormalizedPosition=float.Parse(c.GetValue("value"));return true;
        }

        if(op=="screenshot"){ScreenCapture.CaptureScreenshot(Path.Combine(Dir,Path.GetFileName(c.GetValue("name"))));return true;}
        if(op=="editorload")
        {
            if(!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch==null)throw new Exception("VAB required");
            string craft=Path.GetFullPath(c.GetValue("craft"));
            string folder=Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath,"Ships/VAB"));
            string name=Path.GetFileNameWithoutExtension(craft);
            if(Path.GetDirectoryName(craft)!=folder || !name.StartsWith("Deniz Starship ") || !name.EndsWith(" - Tower Launch") || Path.GetExtension(craft)!=".craft" || !File.Exists(craft))throw new Exception("Installed Tower Launch craft required");
            if(editorBrowser!=null)editorBrowser.Dismiss();
            EditorLogic.LoadShipFromFile(craft);return true;
        }
        if(op=="editorbrowser")
        {
            if(!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch==null)throw new Exception("VAB required");
            if(editorBrowser!=null)editorBrowser.Dismiss();
            editorBrowser=CraftBrowserDialog.Spawn(EditorFacility.VAB,HighLogic.SaveFolder,(string file,CraftBrowserDialog.LoadType kind)=>EditorLogic.LoadShipFromFile(file),()=>{},true);
            return true;
        }
        return false;
    }
    void EditorSnapshot(ConfigNode n)
    {
        if(!TestSave || !HighLogic.LoadedSceneIsEditor || EditorLogic.fetch==null || EditorLogic.fetch.ship==null)return;
        var ship=EditorLogic.fetch.ship;
        n.AddValue("editorOutwardActuationEnabled",ship.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<StarshipExpansionProject.Modules.ModuleSEPRaptor>().Count(m=>m.enableActuateOut));n.AddValue("vessel",ship.shipName);n.AddValue("parts",ship.parts.Count);n.AddValue("crew",ship.parts.Sum(p=>p.protoModuleCrew.Count));
        n.AddValue("editorCameraDistance",EditorDriver.fetch.vabCamera.Distance);n.AddValue("stageCount",StageManager.StageCount);n.AddValue("lastStage",StageManager.LastStage);
        foreach(var p in ship.parts)
        {
            var pn=n.AddNode("EDITOR_PART");pn.AddValue("name",p.partInfo.name);pn.AddValue("inverseStage",p.inverseStage);pn.AddValue("crewCapacity",p.CrewCapacity);
            foreach(PartResource resource in p.Resources){var rn=pn.AddNode("RESOURCE");rn.AddValue("name",resource.resourceName);rn.AddValue("amount",resource.amount);rn.AddValue("maxAmount",resource.maxAmount);}
            foreach(PartModule module in p.Modules){if(!(module is DenizStarshipControls || module is ModuleEngines || module is ModuleDecouple || module is DenizStarbaseClamp || module is ModuleAnimateGeneric || module is ModuleParachute || module is ModuleRoboticServoPiston || module is ModuleRCSFX || module is ModuleWheelDeployment))continue;var mn=pn.AddNode("MODULE");module.Save(mn);}
        }
    }
    IEnumerator Hop(){var v=FlightGlobals.ActiveVessel;v.ActionGroups.SetGroup(KSPActionGroup.SAS,true);var m=v.FindPartModulesImplementing<DenizStarshipControls>().First();m.ExtendLegs();yield return new WaitForSeconds(6);double ground=v.radarAltitude;m.Engines(true);FlightInputHandler.state.mainThrottle=1;while(v.radarAltitude<ground+150)yield return null;FlightInputHandler.state.mainThrottle=0;while(v.verticalSpeed>0)yield return null;while(v.situation!=Vessel.Situations.LANDED){double height=v.radarAltitude-ground;double a=v.GetTotalMass()*v.mainBody.gravParameter/Math.Pow(v.mainBody.Radius+v.altitude,2);double thrust=v.FindPartModulesImplementing<ModuleEngines>().Where(e=>e.EngineIgnited).Sum(e=>e.maxThrust*e.realIsp/e.atmosphereCurve.Evaluate(0));double desired=-Math.Min(8,Math.Max(.5,height*.15));FlightInputHandler.state.mainThrottle=(float)Math.Max(0,Math.Min(1,a*(1+(desired-v.verticalSpeed)*.3)/thrust));yield return null;}FlightInputHandler.state.mainThrottle=0;File.AppendAllText(Path.Combine(Dir,"hop-result.txt"),v.vesselName+" landed with "+v.parts.Count+" parts, vertical speed "+v.verticalSpeed+"\n");}
    void RecordValidationOptions(ConfigNode n){n.AddValue("instantaneousFps",1f/Time.unscaledDeltaTime);n.AddValue("physicsTimeScale",Time.timeScale);var type=typeof(Vessel).Assembly.GetType("CheatOptions");if(type!=null){var options=n.AddNode("CHEATS");foreach(var field in type.GetFields(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).Where(f=>f.FieldType==typeof(bool)))options.AddValue(field.Name,field.GetValue(null));}}
    // A7 optional observer only: native wheel/support reads, no physics setters.
    object DiagnosticMember(object source,string name){
        if(source==null)return null;
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
        var field=source.GetType().GetField(name,flags);if(field!=null)return field.GetValue(source);
        var property=source.GetType().GetProperty(name,flags);return property==null || property.GetIndexParameters().Length!=0?null:property.GetValue(source,null);
    }
    void DiagnosticValue(ConfigNode n,string name,object value){
        if(value==null)return;
        if(value is float)n.AddValue(name,((float)value).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        else if(value is double)n.AddValue(name,((double)value).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        else if(value.GetType().IsPrimitive || value.GetType().IsEnum || value is string || value is Vector3 || value is Vector3d)n.AddValue(name,value);
    }
    void WheelDiagnosticSnapshot(ConfigNode n,Vessel active){
        foreach(var v in FlightGlobals.Vessels.Where(x=>x.loaded && !x.packed && x.mainBody!=null && (x.mainBody.bodyName=="Mun" || ExactA8DunaBase(x) || (x==active && x.mainBody.bodyName=="Kerbin" && x.vesselName=="D Starship 3 Rover" && x.parts.Count==25)) && x.parts.Count>0)){
            var vn=n.AddNode("MUN_MISSION_VESSEL");vn.AddValue("id",v.id);vn.AddValue("name",v.vesselName);vn.AddValue("parts",v.parts.Count);vn.AddValue("crew",v.GetCrewCount());vn.AddValue("situation",v.situation);vn.AddValue("pitch",90-Vector3d.Angle(v.GetTransform().up,v.upAxis));vn.AddValue("angularVelocity",v.angularVelocity.magnitude);vn.AddValue("SAS",v.ActionGroups[KSPActionGroup.SAS]);vn.AddValue("RCS",v.ActionGroups[KSPActionGroup.RCS]);vn.AddValue("mainThrottle",v.ctrlState.mainThrottle);vn.AddValue("latitude",v.latitude);vn.AddValue("longitude",v.longitude);vn.AddValue("radarAlt",v.radarAltitude);vn.AddValue("mass",v.GetTotalMass());vn.AddValue("COMRootLocal",v.rootPart.transform.InverseTransformPoint(v.CoM));vn.AddValue("upAxisRootLocal",v.rootPart.transform.InverseTransformDirection((Vector3)v.upAxis));vn.AddValue("surfaceVelocityRootLocal",v.rootPart.transform.InverseTransformDirection((Vector3)v.srf_velocity));vn.AddValue("totalThrust",v.FindPartModulesImplementing<ModuleEngines>().Sum(x=>x.finalThrust));
            foreach(var p in v.parts){vn.AddValue("partID",p.flightID);
                var body=vn.AddNode("PART_BODY");body.AddValue("id",p.flightID);body.AddValue("name",p.partInfo.name);body.AddValue("rootLocalPosition",v.rootPart.transform.InverseTransformPoint(p.transform.position));if(p.rb!=null){body.AddValue("angularVelocity",p.rb.angularVelocity);body.AddValue("angularSpeed",p.rb.angularVelocity.magnitude);body.AddValue("velocity",p.rb.velocity);body.AddValue("nativeRigidbodyMass",p.rb.mass);body.AddValue("nativeBodyCOMRootLocal",v.rootPart.transform.InverseTransformPoint(p.rb.worldCenterOfMass));if(v.rootPart.rb!=null)body.AddValue("velocityRelativeToRoot",p.rb.velocity-v.rootPart.rb.velocity);}
                foreach(var engine in p.FindModulesImplementing<ModuleEngines>()){
                    var en=body.AddNode("ENGINE_DIAGNOSTIC");en.AddValue("ignited",engine.EngineIgnited);en.AddValue("finalThrust",engine.finalThrust);
                    foreach(var tr in engine.thrustTransforms){var tn=en.AddNode("THRUST_TRANSFORM");tn.AddValue("name",tr.name);tn.AddValue("positionRootLocal",v.rootPart.transform.InverseTransformPoint(tr.position));tn.AddValue("forceAxisRootLocal",v.rootPart.transform.InverseTransformDirection(-tr.forward));}
                }
                foreach(var gimbal in p.FindModulesImplementing<ModuleGimbal>()){
                    var gn=body.AddNode("GIMBAL_DIAGNOSTIC");foreach(var key in new[]{"gimbalRange","gimbalRangeXP","gimbalRangeXN","gimbalRangeYP","gimbalRangeYN","gimbalLimiter","gimbalLock","gimbalActive","actuation","actuationLocal","enablePitch","enableYaw","enableRoll"})DiagnosticValue(gn,key,DiagnosticMember(gimbal,key));
                    foreach(var tr in gimbal.gimbalTransforms){var pn=gn.AddNode("GIMBAL_PIVOT");pn.AddValue("name",tr.name);pn.AddValue("positionRootLocal",v.rootPart.transform.InverseTransformPoint(tr.position));pn.AddValue("axisRootLocal",v.rootPart.transform.InverseTransformDirection(-tr.forward));}
                }
                foreach(var decouple in p.FindModulesImplementing<ModuleDecouple>()){var dn=body.AddNode("NATIVE_DECOUPLER");dn.AddValue("ejectionForce",decouple.ejectionForce);dn.AddValue("ejectionForcePercent",decouple.ejectionForcePercent);dn.AddValue("isDecoupled",decouple.isDecoupled);}

                foreach(var wheel in p.FindModulesImplementing<ModuleWheelBase>()){
                    var wn=vn.AddNode("WHEEL_DIAGNOSTIC");wn.AddValue("partID",p.flightID);wn.AddValue("partName",p.partInfo.name);wn.AddValue("partRootLocal",v.rootPart.transform.InverseTransformPoint(p.transform.position));
                    try{
                        foreach(var key in new[]{"wheelType","isGrounded","radius","center","mass","suspensionEnabled"})DiagnosticValue(wn,key,DiagnosticMember(wheel,key));
                        var controller=DiagnosticMember(wheel,"Wheel")??DiagnosticMember(wheel,"wheel");
                        foreach(var key in new[]{"IsGrounded","LegacyWheelLoad","WheelRadius","WheelDown","SuspensionAxis"})DiagnosticValue(wn,"controller"+key,DiagnosticMember(controller,key));
                        var center=DiagnosticMember(controller,"WheelCenter");if(center is Vector3){wn.AddValue("wheelCenterRootLocal",v.rootPart.transform.InverseTransformPoint((Vector3)center));wn.AddValue("wheelCenterWorld",center);}
                        // Read-only physics queries, no wheel/collider changes.
                        if(center is Vector3 && v.radarAltitude<100){
                            var origin=(Vector3)center+(Vector3)v.upAxis*200;
                            foreach(var hit in Physics.RaycastAll(origin,(Vector3)(-v.upAxis),400).OrderBy(x=>x.distance)){
                                if(hit.collider==null || hit.collider.gameObject.layer!=15 || hit.collider.GetComponentInParent<Part>()!=null || Vector3d.Dot(hit.normal,v.upAxis)<=0)continue;
                                var ground=wn.AddNode("NATIVE_GROUND_RAY");ground.AddValue("collider",hit.collider.name);ground.AddValue("layer",hit.collider.gameObject.layer);ground.AddValue("distance",hit.distance);ground.AddValue("normalWorld",hit.normal);ground.AddValue("normalRootLocal",v.rootPart.transform.InverseTransformDirection(hit.normal));ground.AddValue("pointRootLocal",v.rootPart.transform.InverseTransformPoint(hit.point));ground.AddValue("slopeDegrees",Vector3d.Angle(hit.normal,v.upAxis));break;
                            }
                        }
                        var state=DiagnosticMember(controller,"currentState");
                        if(state!=null){var sn=wn.AddNode("NATIVE_WHEEL_STATE");foreach(var field in state.GetType().GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic))DiagnosticValue(sn,field.Name,field.GetValue(state));}
                        var collider=DiagnosticMember(controller,"wheelCollider");
                        var cn=wn.AddNode("NATIVE_WHEEL_COLLIDER");foreach(var key in new[]{"radius","center","suspensionDistance","suspensionAnchor","mass","enabled"})DiagnosticValue(cn,key,DiagnosticMember(collider,key));
                        var down=DiagnosticMember(controller,"WheelDown");if(center is Vector3 && down is Vector3 && collider!=null){var endpoint=(Vector3)center+((Vector3)down).normalized*(float)(Convert.ToDouble(DiagnosticMember(collider,"suspensionDistance"))+Convert.ToDouble(DiagnosticMember(collider,"radius")));wn.AddValue("fullTravelFootEstimateRootLocal",v.rootPart.transform.InverseTransformPoint(endpoint));wn.AddValue("footEstimateMethod","Native hub + wheelDown*(full suspension travel+radius); static reach estimate, not exact current contact point");}
                        var susp=p.Modules.Cast<PartModule>().FirstOrDefault(x=>x.moduleName=="ModuleWheelSuspension");
                        var su=wn.AddNode("SUSPENSION_DIAGNOSTIC");foreach(var key in new[]{"spring","damper","targetPosition","springRatio","springTweakable","damperRatio","damperTweakable","autoSpringDamper","maximumLoad","suspCompression","suspMass","sprungMassGravity","vesselMass","boost","autoBoost","useAutoBoost","suspensionOffset","suspensionDistance"})DiagnosticValue(su,key,DiagnosticMember(susp,key));
                    }catch(Exception error){wn.AddValue("diagnosticError",error.GetBaseException().Message);}
                }
            }
        }
    }
    // Native readback only, including installed cryogenic state and ephemerides.
    void A8PlanningSnapshot(ConfigNode n,Vessel v) {
        foreach(var part in v.parts)foreach(var module in part.Modules.Cast<PartModule>().Where(m=>m.GetType().Name=="ModuleCryoTank")) {
            var tank=n.AddNode("NATIVE_CRYO_TANK");tank.AddValue("partID",part.flightID);tank.AddValue("partName",part.partInfo.name);
            foreach(string key in new[]{"CoolingAllowed","CoolingEnabled","BoiloffOccuring","LastUpdateTime","fuelAmount","maxFuelAmount","finalCoolingCost","currentCoolingCost","fluxScale","BoiloffStatus","CoolingStatus"})DiagnosticValue(tank,key,DiagnosticMember(module,key));
            foreach(var e in module.Events) {var eventNode=tank.AddNode("NATIVE_EVENT");eventNode.AddValue("name",e.name);eventNode.AddValue("active",e.active);}
        }
        foreach(var part in v.parts)foreach(var module in part.Modules.Cast<PartModule>().Where(m=>m is ModuleDeployableSolarPanel)) {
            var panel=n.AddNode("NATIVE_SOLAR_PANEL");panel.AddValue("partID",part.flightID);panel.AddValue("partName",part.partInfo.name);
            foreach(string key in new[]{"deployState","stateString","flowRate","chargeRate","sunAOA","isTracking"})DiagnosticValue(panel,key,DiagnosticMember(module,key));
            foreach(var e in module.Events) {var eventNode=panel.AddNode("NATIVE_EVENT");eventNode.AddValue("name",e.name);eventNode.AddValue("active",e.active);}
        }
        foreach(var body in FlightGlobals.Bodies.Where(b=>b.name=="Kerbin" || b.name=="Duna")) {
            var ep=n.AddNode("NATIVE_PLANET_EPHEMERIS");ep.AddValue("name",body.name);ep.AddValue("parent",body.referenceBody.name);ep.AddValue("radius",body.Radius);ep.AddValue("mu",body.gravParameter);ep.AddValue("atmosphereDepth",body.atmosphereDepth);
            var o=body.orbit;ep.AddValue("semiMajorAxis",o.semiMajorAxis);ep.AddValue("eccentricity",o.eccentricity);ep.AddValue("inclination",o.inclination);ep.AddValue("LAN",o.LAN);ep.AddValue("argumentOfPeriapsis",o.argumentOfPeriapsis);ep.AddValue("meanAnomalyAtEpoch",o.meanAnomalyAtEpoch);ep.AddValue("epoch",o.epoch);ep.AddValue("period",o.period);
        }
    }
    void Snapshot(){var n=new ConfigNode();n.AddValue("bridgeBuild","C1-origin-constraints-82");n.AddValue("lastCommandID",lastCommandID);n.AddValue("lastCommandOp",lastCommandOp);n.AddValue("lastCommandResult",lastCommandResult);RecordValidationOptions(n);C1Snapshot(n);n.AddValue("scene",HighLogic.LoadedScene);n.AddValue("save",HighLogic.SaveFolder);n.AddValue("time",DateTime.UtcNow.ToString("o"));n.AddValue("benchmarkFps",benchmarkFps);n.AddValue("benchmarkWindowSeconds",benchmarkWindow);n.AddValue("benchmarkSimWallRatio",benchmarkSimRatio);n.AddValue("fullscreen",Screen.fullScreen);n.AddValue("ambientLightBoostFactor",GameSettings.AMBIENTLIGHT_BOOSTFACTOR);n.AddValue("screenWidth",Screen.width);n.AddValue("screenHeight",Screen.height);EditorSnapshot(n);n.AddValue("activeVesselDestroyed",TestSave && HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel!=null && FlightGlobals.ActiveVessel.parts.Count==0);if(TestSave && HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel!=null && FlightGlobals.ActiveVessel.parts.Count>0){var v=FlightGlobals.ActiveVessel;B2Snapshot(n,v);try{B1Snapshot(n,v);}catch(Exception ex){n.AddValue("b1SnapshotError",ex.GetBaseException().Message);}A9Snapshot(n,v);A8PlanningSnapshot(n,v);OrbitalSnapshot(n,v);BargeSnapshot(n,v);A6Snapshot(n,v);try{MissionSnapshot(n,v);}catch(Exception error){n.AddValue("missionSnapshotError",error.Message);}try{WheelDiagnosticSnapshot(n,v);}catch(Exception error){n.AddValue("wheelDiagnosticError",error.GetBaseException().Message);}n.AddValue("vessel",v.vesselName);n.AddValue("parts",v.parts.Count);n.AddValue("mass",v.GetTotalMass());n.AddValue("sasEnabled",v.ActionGroups[KSPActionGroup.SAS]);n.AddValue("sasMode",v.Autopilot.Mode);n.AddValue("totalEngineThrust",v.FindPartModulesImplementing<ModuleEngines>().Sum(x=>x.finalThrust));n.AddValue("flightOutwardActuationEnabled",v.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<StarshipExpansionProject.Modules.ModuleSEPRaptor>().Count(x=>x.enableActuateOut));n.AddValue("UT",Planetarium.GetUniversalTime());n.AddValue("MET",v.missionTime);n.AddValue("Ap",v.orbit.ApA);n.AddValue("Pe",v.orbit.PeA);n.AddValue("speed",v.srfSpeed);n.AddValue("dynamicPressureKPa",v.dynamicPressurekPa);n.AddValue("latitude",v.latitude);n.AddValue("longitude",v.longitude);n.AddValue("maxTemperatureRatio",v.parts.Max(p=>Math.Max(p.temperature/p.maxTemp,p.skinTemperature/p.skinMaxTemp)));var sun=FlightGlobals.Bodies.First(x=>x.isStar);n.AddValue("sunElevation",Math.Asin(Vector3d.Dot((sun.position-v.transform.position).normalized,v.upAxis))*180/Math.PI);n.AddValue("alt",v.altitude);n.AddValue("radarAlt",v.radarAltitude);n.AddValue("verticalSpeed",v.verticalSpeed);n.AddValue("horizontalSpeed",v.horizontalSrfSpeed);n.AddValue("situation",v.situation);n.AddValue("stage",v.currentStage);n.AddValue("crew",v.GetCrewCount());n.AddValue("vessels",FlightGlobals.Vessels.Count);n.AddValue("throttle",v.ctrlState.mainThrottle);if(FlightCamera.fetch!=null){n.AddValue("cameraPitch",FlightCamera.CamPitch);n.AddValue("cameraHeading",FlightCamera.CamHdg);n.AddValue("cameraDistance",FlightCamera.fetch.Distance);}foreach(var other in FlightGlobals.Vessels){var vn=n.AddNode("VESSEL");vn.AddValue("name",other.vesselName);vn.AddValue("loaded",other.loaded);vn.AddValue("packed",other.packed);vn.AddValue("parts",other.loaded?other.parts.Count:(other.protoVessel==null?0:other.protoVessel.protoPartSnapshots.Count));foreach(var pp in other.parts)vn.AddValue("part",pp.partInfo.name);}foreach(var p in v.parts){var pn=n.AddNode("PART");pn.AddValue("name",p.partInfo.name);pn.AddValue("id",p.flightID);pn.AddValue("breakingForce",p.breakingForce);pn.AddValue("breakingTorque",p.breakingTorque);pn.AddValue("inverseStage",p.inverseStage);pn.AddValue("shielded",p.ShieldedFromAirstream);pn.AddValue("fuelCrossFeed",p.fuelCrossFeed);pn.AddValue("pos",p.transform.position-v.rootPart.transform.position);pn.AddValue("rotation",p.transform.rotation);if(p.partInfo.name=="DenizStarshipClamshell"){var h=p.FindModelTransforms("Rotate").FirstOrDefault();if(h!=null)pn.AddValue("LidRotation",h.localRotation);}if(p.partInfo.name=="DenizStarshipCargo"){foreach(var tr in p.GetComponentsInChildren<Transform>(true).Where(tr=>tr.name=="DoorL" || tr.name=="DoorR")){pn.AddValue(tr.name+"Rotation",tr.localRotation);pn.AddValue(tr.name+"Position",tr.position-v.rootPart.transform.position);}}foreach(PartResource r in p.Resources)pn.AddValue(r.resourceName,r.amount+"/"+r.maxAmount);foreach(PartModule x in p.Modules){if(x is DenizStarshipControls || x is ModuleAnimateGeneric || x is ModuleWheelDeployment || x is ModuleRoboticServoPiston || x is ModuleEngines || x is ModuleSLEAnimate || x is ModuleSLEController || x is DenizStarbaseClamp || x is ModuleRCSFX || x is ModuleParachute){var mn=pn.AddNode("MODULE");x.Save(mn);if(x is DenizStarshipControls){mn.AddValue("releaseContactCount",((DenizStarshipControls)x).PendingReleaseContacts);mn.AddValue("releaseContactTotal",((DenizStarshipControls)x).LastReleaseContactTotal);DiagnosticValue(mn,"releaseCollisionPairsRestored",DiagnosticMember(x,"ReleaseCollisionPairsRestored"));foreach(string member in new[]{"status","ReturnClearanceRadius","LastPayloadEjectionForce","ReturnWaitingForPayload","PayloadClearanceGap"})DiagnosticValue(mn,member,DiagnosticMember(x,member));}if(x is ModuleRoboticServoPiston)mn.AddValue("currentExtension",((ModuleRoboticServoPiston)x).currentExtension);if(x is ModuleAnimateGeneric)mn.AddValue("progress",((ModuleAnimateGeneric)x).Progress);if(x is ModuleRCSFX){var r=(ModuleRCSFX)x;mn.AddValue("power",r.thrusterPower);mn.AddValue("maxFuelFlow",r.maxFuelFlow);mn.AddValue("exhaustVelocity",typeof(ModuleRCS).GetField("exhaustVel",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).GetValue(r));mn.AddValue("useZaxis",r.useZaxis);mn.AddValue("thrustForces",string.Join(",",(r.thrustForces??new float[0]).Select(z=>z.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));foreach(var tr in (r.thrusterTransforms??new System.Collections.Generic.List<Transform>())){var tn=mn.AddNode("RCS_THRUSTER");tn.AddValue("name",tr.name);tn.AddValue("rootLocalPosition",v.rootPart.transform.InverseTransformPoint(tr.position));tn.AddValue("rootLocalForceAxis",v.rootPart.transform.InverseTransformDirection(-(r.useZaxis?tr.forward:tr.up)));}}if(x is ModuleSLEController)mn.AddValue("angle",((ModuleSLEController)x).ExtendedCurrent);if(x is ModuleSLEAnimate)mn.AddValue("extension",((ModuleSLEAnimate)x).ExtendedCurrent);if(x is DenizStarbaseClamp)mn.AddValue("status",((DenizStarbaseClamp)x).status);if(x is ModuleEngines){var e=(ModuleEngines)x;mn.AddValue("maxThrust",e.maxThrust);mn.AddValue("thrust",e.finalThrust);}}}}}n.Save(Path.Combine(Dir,"state.cfg"));if(TestSave && KSPLog.Instance!=null){var writer=DiagnosticMember(KSPLog.Instance,"fileStream") as StreamWriter;if(writer!=null)writer.Flush();}}
}

// Read-only collision evidence from the actual rigid-body collision callbacks.
public class DenizDeckContactRecorder : MonoBehaviour
{
    public static int Count;
    void OnCollisionEnter(Collision collision)
    {
        var other=collision.collider.GetComponentInParent<Part>();
        if(other==null || other.vessel==null || other.vessel.vesselName!="Drone Ship - Landing Practice" || HighLogic.SaveFolder!="Deniz Starship Missions Test")return;
        var own=GetComponent<Part>();if(own==null || own.vessel==null || !(own.vessel.vesselName=="Deniz Super Heavy" || own.vessel.vesselName=="D Starship 1 Empty"))return;
        Count++;
        var n=new ConfigNode("DECK_CONTACT");n.AddValue("utc",DateTime.UtcNow.ToString("o"));n.AddValue("UT",Planetarium.GetUniversalTime());n.AddValue("part",own.partInfo.name);n.AddValue("other",other.partInfo.name);n.AddValue("relativeVelocity",collision.relativeVelocity);n.AddValue("boosterParts",own.vessel.parts.Count);n.AddValue("contactVessel",own.vessel.vesselName);n.AddValue("contactVesselCrew",own.vessel.GetCrewCount());n.AddValue("bargeParts",other.vessel.parts.Count);
        foreach(var point in collision.contacts){var c=n.AddNode("CONTACT");c.AddValue("point",point.point);c.AddValue("normal",point.normal);}
        File.AppendAllText(Path.Combine(KSPUtil.ApplicationRootPath,"DenizStarshipValidation","deck-contacts.log"),n.ToString()+"\n");
    }
}

// Observation only: no IgnoreCollision, forces, transforms or actor placement.
public class DenizB2ContactRecorder : MonoBehaviour {
    public string file;
    void OnCollisionEnter(Collision collision){
        if(HighLogic.SaveFolder!="Deniz Starship Missions Test" || string.IsNullOrEmpty(file))return;
        var own=GetComponent<Part>();var other=collision.collider.GetComponentInParent<Part>();if(own==null || own.vessel==null)return;
        var n=new ConfigNode("B2_CONTACT");n.AddValue("utc",DateTime.UtcNow.ToString("o"));n.AddValue("UT",Planetarium.GetUniversalTime());n.AddValue("part",own.partInfo.name);n.AddValue("id",own.flightID);n.AddValue("other",other==null?collision.collider.name:other.partInfo.name);n.AddValue("otherID",other==null?0:other.flightID);n.AddValue("collider",collision.collider.name);n.AddValue("relativeVelocity",collision.relativeVelocity);n.AddValue("sameVessel",other!=null && own.vessel==other.vessel);n.AddValue("ownVessel",own.vessel.vesselName);
        var controls=own.vessel.FindPartModulesImplementing<DenizStarshipControls>().FirstOrDefault();if(controls!=null){n.AddValue("doorProgress",controls.DoorOpenProgress);n.AddValue("cargoLocked",controls.cargoLocked);}
        foreach(var point in collision.contacts){var q=n.AddNode("CONTACT");q.AddValue("point",point.point);q.AddValue("normal",point.normal);}
        File.AppendAllText(Path.Combine(KSPUtil.ApplicationRootPath,"DenizStarshipValidation",file),n.ToString()+"\n");
    }
}

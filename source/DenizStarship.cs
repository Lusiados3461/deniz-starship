using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ModuleWheels;
using Expansions.Serenity;
using StarshipExpansionProject;
using StarshipExpansionProject.Modules;
using StarshipLaunchExpansion.Modules;

public class DenizStarshipControls : PartModule
{
    [KSPField(isPersistant=true,guiActive=true,guiName="Cargo locked")] public bool cargoLocked=true;
    [KSPField(isPersistant=true,guiActive=true,guiName="Legs locked")] public bool legsLocked=false;
    [KSPField(isPersistant=true)] public bool cargoOpen=false;
    [KSPField(isPersistant=true)] public bool legsExtended=false;
    [KSPField(isPersistant=true)] public bool armExtended=false;
    [KSPField(isPersistant=true)] public bool reentryMode=false;
    [KSPField(isPersistant=true,guiActive=true,guiName="RCS mode")] public string rcsMode="High power";
    [KSPField] public float rcsScale=4.096f;
    [KSPField(guiActive=true,guiName="Mission controls")] public string status="Ready";
    bool Leader {get{return vessel!=null && vessel.FindPartModulesImplementing<DenizStarshipControls>().FirstOrDefault()==this;}}
    IEnumerable<Part> OwnParts {get{return vessel==null?(IEnumerable<Part>)new Part[0]:vessel.parts;}}
    ModuleAnimateGeneric Door {get{return OwnParts.Where(p=>p.partInfo.name=="DenizStarshipCargo" || p.partInfo.name=="DenizStarshipClamshell").SelectMany(p=>p.FindModulesImplementing<ModuleAnimateGeneric>()).FirstOrDefault();}}
    public float DoorOpenProgress {get{return Door==null?0:(Door.part.partInfo.name=="DenizStarshipClamshell"?1-Door.Progress:Door.Progress);}}
    bool returning;
    // The attached nose sweeps around its native hinge. A sphere enclosing all
    // its physical collider bounds also encloses their rigid closing rotation.
    // Keep actual bay contacts enabled; clearance is a prerequisite to closing.
    readonly List<Part> releasedPayload=new List<Part>();
    [KSPField(isPersistant=true)] public string releasedPayloadIDs="";
    [KSPField(isPersistant=true)] public float savedReturnClearanceRadius;
    Transform returnClearancePivot;
    public float ReturnClearanceRadius {get{return savedReturnClearanceRadius;}}
    public float LastPayloadEjectionForce {get;private set;}
    public bool ReturnWaitingForPayload {get;private set;}
    public float PayloadClearanceGap {
        get {
            if(releasedPayloadIDs=="")return float.PositiveInfinity;
            if(releasedPayload.Count==0){
                var ids=releasedPayloadIDs.Split(',');
                var loaded=FlightGlobals.Vessels.Where(v=>v.loaded).SelectMany(v=>v.parts).ToArray();
                var resolved=ids.Select(id=>loaded.FirstOrDefault(x=>x.flightID.ToString()==id)).ToArray();
                if(resolved.Any(p=>p==null))return float.NegativeInfinity;
                releasedPayload.AddRange(resolved);
            }
            if(returnClearancePivot==null && Door!=null)returnClearancePivot=Door.part.FindModelTransforms("Rotate").FirstOrDefault();
            if(returnClearancePivot==null || ReturnClearanceRadius<=0 || releasedPayload.Any(p=>p==null || p.vessel==null))return float.NegativeInfinity;
            float gap=float.PositiveInfinity;bool measured=false;
            foreach(var p in releasedPayload)foreach(var c in PhysicalColliders(p).Where(c=>c.enabled && c.gameObject.activeInHierarchy)){
                measured=true;
                gap=Mathf.Min(gap,Vector3.Distance(returnClearancePivot.position,c.bounds.ClosestPoint(returnClearancePivot.position))-ReturnClearanceRadius);
            }
            return measured?gap:float.NegativeInfinity;
        }
    }
    bool PayloadClearForClosing {get{return releasedPayloadIDs=="" || PayloadClearanceGap>.5f;}}
    void RememberReleasedPayload(ModuleDecouple[] collars){
        releasedPayload.Clear();
        releasedPayload.AddRange(collars.SelectMany(d=>d.part.children.ToArray()).SelectMany(Branch).Distinct());
        releasedPayloadIDs=string.Join(",",releasedPayload.Select(p=>p.flightID.ToString()).ToArray());
        var door=Door;
        returnClearancePivot=door.part.FindModelTransforms("Rotate").FirstOrDefault();
        savedReturnClearanceRadius=0;
        if(returnClearancePivot==null)return;
        foreach(var c in PhysicalColliders(door.part)){
            var bounds=c.bounds;
            for(int corner=0;corner<8;corner++){
                var point=bounds.center+Vector3.Scale(bounds.extents,new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1));
                savedReturnClearanceRadius=Mathf.Max(ReturnClearanceRadius,Vector3.Distance(returnClearancePivot.position,point));
            }
        }
    }
    float NativeReleaseForce(ModuleDecouple collar){
        var payload=collar.part.children.ToArray().SelectMany(Branch).Distinct().ToArray();
        double payloadMass=payload.Sum(p=>(double)p.mass+p.GetResourceMass());
        double carrierMass=vessel.GetTotalMass()-payloadMass;
        if(payloadMass<=0 || carrierMass<=0 || TimeWarp.fixedDeltaTime<=0)return collar.ejectionForce;
        // Native ModuleDecouple applies opposite half-forces for one physics
        // step. At 100% the reduced-mass calibration targets 0.6 m/s relative
        // ejection. Native direction/percentage and actual physics remain in use.
        double reducedMass=1/(1/payloadMass+1/carrierMass);
        return (float)(2*.6*reducedMass/TimeWarp.fixedDeltaTime);
    }
    // Decoupling enables contacts between the support and folded cargo. Keep only
    // those pairs ignored until the support withdraws; bay-wall contacts stay active.
    sealed class ReleaseContact {public Collider support,payload;}
    readonly List<ReleaseContact> releaseContacts=new List<ReleaseContact>();
    public int PendingReleaseContacts {get{return releaseContacts.Count;}}
    public int LastReleaseContactTotal {get;private set;}
    float releaseContactGraceUntil;
    public long ReleaseCollisionPairsRestored {get;private set;}
    // Stock CollisionManager rewrites cross-vessel ignores on its next physics
    // tick. Reapply only the registered arm/collar pairs after that native reset.
    public void RestoreReleaseContactsAfterNativeReset(){
        foreach(var pair in releaseContacts)if(pair.support!=null && pair.payload!=null && !Physics.GetIgnoreCollision(pair.support,pair.payload))ReleaseCollisionPairsRestored++;
        MaintainReleaseContacts();
    }
    static IEnumerable<Part> Branch(Part root){yield return root;foreach(var child in root.children.ToArray())foreach(var p in Branch(child))yield return p;}
    static IEnumerable<Collider> PhysicalColliders(Part p){return p.GetComponentsInChildren<Collider>(true).Where(c=>!c.isTrigger && c.GetComponentInParent<Part>()==p);}
    void GuardReleaseContacts(ModuleDecouple[] collars){var supports=OwnParts.Where(p=>p.partInfo.name=="DenizCargoArm" || p.partInfo.name=="DenizPayloadRelease").SelectMany(PhysicalColliders).ToArray();var cargo=collars.SelectMany(d=>d.part.children.ToArray()).SelectMany(Branch).Distinct().SelectMany(PhysicalColliders).ToArray();foreach(var a in supports)foreach(var b in cargo){Physics.IgnoreCollision(a,b,true);releaseContacts.Add(new ReleaseContact{support=a,payload=b});}LastReleaseContactTotal=supports.Length*cargo.Length;releaseContactGraceUntil=Time.time+3f;}
    void MaintainReleaseContacts(){bool withdrawn=Time.time>releaseContactGraceUntil && Arms.All(a=>a.currentExtension<.03f);for(int i=releaseContacts.Count-1;i>=0;i--){var pair=releaseContacts[i];if(pair.support==null || pair.payload==null){releaseContacts.RemoveAt(i);continue;}if(withdrawn && !pair.support.bounds.Intersects(pair.payload.bounds)){Physics.IgnoreCollision(pair.support,pair.payload,false);releaseContacts.RemoveAt(i);}else Physics.IgnoreCollision(pair.support,pair.payload,true);}}
    // Stock RCS caches fuel flow during OnLoad. Our mode is a kN baseline;
    // installed MechJeb Smart Translation writes dimensionless 0..1 module
    // fractions into thrusterPower. Keep that fraction at the selected mode's
    // authority instead of mistaking it for a <=1 kN mode. MechJeb is optional:
    // inspect its public controller state without changing its code/settings.
    IEnumerable<ModuleRCSFX> MissionRcs {get{return OwnParts.Where(p=>p.partInfo.name=="DenizStarshipCore" || p.partInfo.name=="DenizStarshipCargo" || p.partInfo.name=="DenizStarshipClamshell" || p.partInfo.name=="DenizSuperHeavy").SelectMany(p=>p.FindModulesImplementing<ModuleRCSFX>());}}
    PartModule rcsMechJebCore;
    System.Reflection.PropertyInfo rcsMasterProperty,rcsBalancerEnabledProperty;
    System.Reflection.FieldInfo rcsBalancerField,rcsSmartTranslationField;
    bool NativeRcsBalancerActive() {
        if(rcsMechJebCore==null || rcsMechJebCore.vessel!=vessel) {
            rcsMechJebCore=OwnParts.SelectMany(p=>p.Modules.Cast<PartModule>()).FirstOrDefault(m=>m.GetType().FullName=="MuMech.MechJebCore");
            if(rcsMechJebCore==null)return false;
            rcsMasterProperty=rcsMechJebCore.GetType().GetProperty("MasterMechJeb");
            rcsBalancerField=rcsMechJebCore.GetType().GetField("rcsbal");
        }
        if(rcsMasterProperty==null || rcsBalancerField==null)return false;
        var master=rcsMasterProperty.GetValue(rcsMechJebCore,null);
        if(master==null)return false;
        var balancer=rcsBalancerField.GetValue(master);
        if(balancer==null)return false;
        if(rcsBalancerEnabledProperty==null)rcsBalancerEnabledProperty=balancer.GetType().GetProperty("enabled");
        if(rcsSmartTranslationField==null)rcsSmartTranslationField=balancer.GetType().GetField("smartTranslation");
        return rcsBalancerEnabledProperty!=null && rcsSmartTranslationField!=null && (bool)rcsBalancerEnabledProperty.GetValue(balancer,null) && (bool)rcsSmartTranslationField.GetValue(balancer);
    }
    float NominalRcsPower(Part p){return (rcsMode=="Docking precision"?10:(p.partInfo.name=="DenizSuperHeavy"?150:100))*rcsScale;}
    void RefreshRcsFuelFlow(){if(!Leader)return;bool balancing=NativeRcsBalancerActive();foreach(var r in MissionRcs)if(r.atmosphereCurve!=null){double vacuumExhaust=(double)r.atmosphereCurve.Evaluate(0f)*9.80665;double power=Math.Max(0,r.thrusterPower);if(balancing && power<=1.0001)power=Math.Min(1,power)*NominalRcsPower(r.part);if(vacuumExhaust>0)r.maxFuelFlow=power/vacuumExhaust;}}
    public void FixedUpdate(){if(HighLogic.LoadedSceneIsFlight){MaintainReleaseContacts();RefreshRcsFuelFlow();}}
    IEnumerable<ModuleWheelDeployment> Legs {get{return OwnParts.Where(p=>p.partInfo.name.StartsWith("DenizStarshipLeg")).SelectMany(p=>p.FindModulesImplementing<ModuleWheelDeployment>());}}
    IEnumerable<ModuleRoboticServoPiston> Arms {get{return OwnParts.Where(p=>p.partInfo.name=="DenizCargoArm").SelectMany(p=>p.FindModulesImplementing<ModuleRoboticServoPiston>());}}
    public override void OnStart(StartState state){base.OnStart(state);if(HighLogic.LoadedSceneIsFlight){var keeper=gameObject.GetComponent<DenizReleaseContactKeeper>();if(keeper==null)keeper=gameObject.AddComponent<DenizReleaseContactKeeper>();keeper.owner=this;StartCoroutine(Ready());}}
    public override void OnActive(){if(part.partInfo.name!="DenizSuperHeavy")return;foreach(var p in part.children){foreach(var e in p.FindModulesImplementing<ModuleEngines>())e.Shutdown();var sw=p.FindModuleImplementing<ModuleSEPEngineSwitch>();if(sw!=null)sw.ShutdownActiveEngine();}StartCoroutine(NameBooster());}
    IEnumerator NameBooster(){yield return new WaitForFixedUpdate();if(vessel!=null && !OwnParts.Any(p=>p.partInfo.name=="DenizStarshipCore")){vessel.vesselName="Deniz Super Heavy";vessel.vesselType=VesselType.Probe;}}
    IEnumerator Ready(){yield return new WaitForSeconds(1);foreach(var l in Legs){foreach(BaseAction a in l.Actions)a.actionGroup=KSPActionGroup.None;} foreach(var l in OwnParts.Where(p=>DenizPayloadControls.InCargo(p)).SelectMany(p=>p.FindModulesImplementing<ModuleWheelDeployment>())){foreach(BaseAction a in l.Actions)a.actionGroup=KSPActionGroup.None;foreach(BaseEvent e in l.Events)e.active=false;} foreach(var a in Arms)foreach(var body in a.part.GetComponentsInChildren<Rigidbody>()){body.solverIterations=24;body.solverVelocityIterations=8;} RefreshLocks();Rcs(rcsMode=="Docking precision");}
    void Tell(string s){status=s;ScreenMessages.PostScreenMessage(s,3,ScreenMessageStyle.UPPER_CENTER);}
    void RefreshLocks(){if(!Leader)return;var d=Door;if(d!=null){foreach(BaseEvent e in d.Events)e.active=false;foreach(BaseAction a in d.Actions){a.active=false;a.actionGroup=KSPActionGroup.None;}}foreach(var l in Legs){foreach(BaseEvent e in l.Events)e.active=false;foreach(BaseAction a in l.Actions){a.active=false;a.actionGroup=KSPActionGroup.None;}}}
    public void LateUpdate(){if(HighLogic.LoadedSceneIsFlight && Leader){MaintainReleaseContacts();RefreshRcsFuelFlow();RefreshLocks();foreach(var a in Arms)if(!a.servoIsLocked && Mathf.Abs(a.currentExtension-a.targetExtension)<.02f)a.EngageServoLock();}}
    [KSPEvent(guiActive=true,guiName="Open cargo bay")] public void OpenCargo(){SetCargo(true);}
    [KSPEvent(guiActive=true,guiName="Close cargo bay")] public void CloseCargo(){SetCargo(false);}
    void SetCargo(bool open){if(!Leader)return;if(cargoLocked){Tell("Unlock cargo first (2)");return;}if(!open && Arms.Any(a=>a.currentExtension>.05f)){Tell("Retract payload arm first (8)");return;}var d=Door;if(d==null){Tell("This vessel has no cargo bay");return;}if(!open && d.part.partInfo.name=="DenizStarshipClamshell" && !PayloadClearForClosing){Tell("Wait for released payload to clear the nose");return;}if(cargoOpen!=open){d.Toggle();cargoOpen=open;}Tell(open?"Cargo opening":"Cargo closing");}
    [KSPAction("Toggle cargo bay")] public void ToggleCargo(KSPActionParam p){SetCargo(!cargoOpen);}
    [KSPEvent(guiActive=true,guiName="Toggle cargo lock")] public void CargoLock(){if(!Leader)return;if(Door!=null && Door.IsMoving()){Tell("Wait for the cargo door to stop");return;}cargoLocked=!cargoLocked;Tell(cargoLocked?"Cargo locked":"Cargo unlocked");}
    [KSPAction("Lock / unlock cargo")] public void CargoLockAction(KSPActionParam p){CargoLock();}
    [KSPEvent(guiActive=true,guiName="Extend landing legs")] public void ExtendLegs(){SetLegs(true);}
    [KSPEvent(guiActive=true,guiName="Retract landing legs")] public void RetractLegs(){SetLegs(false);}
    void SetLegs(bool extended){if(!Leader)return;if(legsLocked){Tell("Unlock legs first (4)");return;}foreach(var l in Legs)l.ActionToggle(extended?KSPActionType.Activate:KSPActionType.Deactivate);legsExtended=extended;Tell(extended?"Legs extending":"Legs retracting");}
    [KSPAction("Toggle landing legs")] public void ToggleLegs(KSPActionParam p){SetLegs(!legsExtended);}
    [KSPEvent(guiActive=true,guiName="Toggle leg lock")] public void LegLock(){if(!Leader)return;if(Legs.Any(l=>l.position>.01f && l.position<.99f)){Tell("Wait for the legs to stop");return;}legsLocked=!legsLocked;Tell(legsLocked?"Legs locked":"Legs unlocked");}
    [KSPAction("Lock / unlock legs")] public void LegLockAction(KSPActionParam p){LegLock();}
    [KSPAction("Toggle entry flap preset")] public void Reentry(KSPActionParam p){if(!Leader)return;reentryMode=!reentryMode;foreach(var f in OwnParts.SelectMany(x=>x.FindModulesImplementing<ModuleSEPControlSurface>()).Where(f=>f.part.partInfo.name.Contains("SHIP"))){if(reentryMode)f.ActivateFlipAngles(p);else f.ActivateLandingAngles(p);}Tell(reentryMode?"Entry flap preset":"Landing flap preset");}
    public void Engines(bool landing){if(!Leader)return;foreach(var p in OwnParts){if(p.partInfo.name.Replace('.','_').Contains("SEP_26_R3")){foreach(var e in p.FindModulesImplementing<ModuleEngines>()){if(landing&&p.partInfo.name.Contains("VAC"))e.Shutdown();else e.Activate();}}var sw=p.FindModuleImplementing<ModuleSEPEngineSwitch>();if(sw!=null){sw.SetActiveEngine(landing?3:0,true);sw.ActivateActiveEngine();}}Tell(landing?"Landing engine selected - throttle is yours":"Flight engines selected - throttle is yours");}
    [KSPAction("Landing engine mode")] public void LandingEngines(KSPActionParam p){Engines(true);}
    [KSPAction("Flight engine mode")] public void FlightEngines(KSPActionParam p){Engines(false);}
    [KSPEvent(guiActive=true,guiName="Toggle payload arm")] public void Arm(){if(!Leader || returning)return;if(!cargoOpen || Door==null || DoorOpenProgress<.99f || Door.IsMoving()){Tell("Open the cargo bay and wait first");return;}armExtended=!armExtended;foreach(var a in Arms){a.DisengageServoLock();if(armExtended)a.ExtendPiston();else a.RetractPiston();}Tell(armExtended?"Payload arm extending":"Payload arm retracting");}
    [KSPAction("Extend / retract payload arm")] public void ArmAction(KSPActionParam p){Arm();}
    [KSPEvent(guiActive=true,guiName="Release payload")] public void Release(){
        if(!Leader || returning)return;
        if(!cargoOpen || Door==null || DoorOpenProgress<.99f || Door.IsMoving() || !armExtended || Arms.Count()!=1 || Arms.Any(a=>a.currentExtension<9.1f)){Tell("Open bay and fully extend arm before release");return;}
        if(vessel.ctrlState.mainThrottle>.0001f || OwnParts.SelectMany(p=>p.FindModulesImplementing<ModuleEngines>()).Any(e=>e.finalThrust>.01f)){Tell("Cut main thrust before payload release");return;}
        bool stow=Door.part.partInfo.name=="DenizStarshipClamshell";
        var collars=OwnParts.Where(p=>p.partInfo.name=="DenizPayloadRelease").SelectMany(p=>p.FindModulesImplementing<ModuleDecouple>()).Where(d=>!d.isDecoupled && d.part.children.Count>0).ToArray();
        if(collars.Length==0){Tell("No attached payload to release");return;}
        if(stow)RememberReleasedPayload(collars);
        GuardReleaseContacts(collars);
        foreach(var d in collars){
            float configuredForce=d.ejectionForce;
            if(stow)d.ejectionForce=NativeReleaseForce(d);
            LastPayloadEjectionForce=d.ejectionForce;
            try{d.Decouple();}finally{d.ejectionForce=configuredForce;}
        }
        MaintainReleaseContacts();Tell("Payload released - coast until cargo is stowed");
        if(stow)PrepareReturn();
    }
    [KSPEvent(guiActive=true,guiName="Prepare for return: stow and close")] public void PrepareReturn(){if(!Leader || returning || Door==null)return;StartCoroutine(StowForReturn());}
    IEnumerator StowForReturn(){
        returning=true;yield return new WaitForSeconds(2);armExtended=false;
        foreach(var a in Arms){a.DisengageServoLock();a.RetractPiston();}
        Tell("Preparing return: retracting cargo arm");float start=Time.time;
        while(Arms.Any(a=>a.currentExtension>.03f)){
            if(Time.time-start>70){Tell("Arm did not retract: cargo stays open");returning=false;yield break;}
            yield return null;
        }
        ReturnWaitingForPayload=!PayloadClearForClosing;
        if(ReturnWaitingForPayload)Tell("Waiting for payload to clear the nose");
        start=Time.time;
        while(!PayloadClearForClosing){
            if(Time.time-start>90){Tell("Payload clearance unconfirmed: cargo stays open");ReturnWaitingForPayload=false;returning=false;yield break;}
            yield return null;
        }
        ReturnWaitingForPayload=false;cargoLocked=false;SetCargo(false);
        if(cargoOpen){returning=false;yield break;}
        while(Door.IsMoving())yield return null;
        cargoLocked=true;releasedPayload.Clear();releasedPayloadIDs="";Tell("Cargo nose closed and locked for return");returning=false;
    }
    [KSPAction("Release mission payload")] public void ReleaseAction(KSPActionParam p){Release();}
    [KSPAction("Deploy payload equipment")] public void Equipment(KSPActionParam p){if(!Leader)return;DenizPayloadControls.Deploy(OwnParts.Where(piece=>!DenizPayloadControls.InCargo(piece)));Tell("Payload equipment deployed");}
    void Rcs(bool precise){if(!Leader)return;rcsMode=precise?"Docking precision":"High power";foreach(var r in MissionRcs)r.thrusterPower=NominalRcsPower(r.part);RefreshRcsFuelFlow();Tell("RCS: "+rcsMode);}
    [KSPEvent(guiActive=true,guiName="RCS: docking precision")] public void RcsPrecision(){Rcs(true);}
    [KSPEvent(guiActive=true,guiName="RCS: high power")] public void RcsHigh(){Rcs(false);}
    [KSPAction("Emergency engine cutoff")] public void Cutoff(KSPActionParam p){if(!Leader)return;vessel.ctrlState.mainThrottle=0;FlightInputHandler.state.mainThrottle=0;foreach(var e in OwnParts.SelectMany(x=>x.FindModulesImplementing<ModuleEngines>()))e.Shutdown();foreach(var s in OwnParts.SelectMany(x=>x.FindModulesImplementing<ModuleSEPEngineSwitch>()))s.ShutdownActiveEngine();Tell("Emergency engine cutoff");}
}

// Run after stock collision resets, before the next physics solve. Keeping this
// separate leaves engine, RCS and arm-control update order unchanged.
[DefaultExecutionOrder(10000)]
public class DenizReleaseContactKeeper : MonoBehaviour
{
    public DenizStarshipControls owner;
    public void FixedUpdate(){if(owner!=null && HighLogic.LoadedSceneIsFlight)owner.RestoreReleaseContactsAfterNativeReset();}
}

public class DenizCargoPanel : PartModule
{
    [KSPField(guiActive=true,guiName="Cargo bay")] public string doorState="Closed";
    [KSPField(guiActive=true,guiName="Cargo lock")] public string lockState="Locked - press 2";
    DenizStarshipControls Control {get{return vessel==null?null:vessel.FindPartModulesImplementing<DenizStarshipControls>().FirstOrDefault();}}
    public void LateUpdate(){if(!HighLogic.LoadedSceneIsFlight)return;var c=Control;if(c==null)return;var d=part.FindModuleImplementing<ModuleAnimateGeneric>();doorState=d.IsMoving()?(c.cargoOpen?"Opening":"Closing"):(c.DoorOpenProgress>.99f?"Open":"Closed");lockState=c.cargoLocked?"Locked - press 2":"Unlocked";Events["Open"].active=!c.cargoOpen;Events["Close"].active=c.cargoOpen;}
    [KSPEvent(guiActive=true,guiName="Open cargo bay (1)")] public void Open(){if(Control!=null)Control.OpenCargo();}
    [KSPEvent(guiActive=true,guiName="Close cargo bay (1)")] public void Close(){if(Control!=null)Control.CloseCargo();}
    [KSPEvent(guiActive=true,guiName="Lock / unlock cargo (2)")] public void Lock(){if(Control!=null)Control.CargoLock();}
    [KSPEvent(guiActive=true,guiName="Extend / retract payload arm (8)")] public void Arm(){if(Control!=null)Control.Arm();}
    [KSPEvent(guiActive=true,guiName="Release payload (9)")] public void Release(){if(Control!=null)Control.Release();}
    [KSPEvent(guiActive=true,guiName="Prepare for return: stow and close")] public void Return(){if(Control!=null)Control.PrepareReturn();}
}

public class DenizTelescopicSleeve : PartModule
{
    Transform sleeve;
    ModuleRoboticServoPiston piston;
    public override void OnStart(StartState state){base.OnStart(state);piston=part.FindModuleImplementing<ModuleRoboticServoPiston>();var go=GameObject.CreatePrimitive(PrimitiveType.Cylinder);var collider=go.GetComponent<Collider>();collider.enabled=false;UnityEngine.Object.Destroy(collider);go.name="Deniz extended actuator sleeve";go.layer=part.gameObject.layer;go.transform.SetParent(part.transform,false);sleeve=go.transform;var source=part.FindModelComponents<MeshRenderer>().FirstOrDefault();if(source!=null)go.GetComponent<MeshRenderer>().sharedMaterial=source.sharedMaterial;}
    public void LateUpdate(){if(sleeve==null || piston==null)return;float extension=Mathf.Max(0,piston.currentExtension);sleeve.gameObject.SetActive(extension>.01f);float bottom=.35f,top=1.32439f+extension;sleeve.localPosition=new Vector3(0,(bottom+top)/2,0);sleeve.localScale=new Vector3(.26f,(top-bottom)/2,.26f);}
}

[DefaultExecutionOrder(10000)]
public class DenizNoseHinge : PartModule
{
    [KSPField] public float openAngle=110;
    Transform hinge;
    public override void OnStart(StartState state){base.OnStart(state);hinge=part.FindModelTransforms("Rotate").FirstOrDefault();}
    public void LateUpdate(){if(hinge==null)return;var d=part.FindModuleImplementing<ModuleAnimateGeneric>();if(d!=null)hinge.localRotation=Quaternion.Euler(-openAngle*(1-d.Progress),0,0);}
}

public class DenizStarbaseClamp : LaunchClamp
{
    [KSPField(guiActive=true,guiName="Launch mount")] public string status="Holding stack - Space to launch";
    bool launching;
    public override void OnStart(StartState state){
        base.OnStart(state);
        if(HighLogic.LoadedSceneIsFlight){GameEvents.onFloatingOriginShift.Remove(RebaseGroundAnchor);GameEvents.onFloatingOriginShift.Add(RebaseGroundAnchor);}
    }
    public void OnDestroy(){GameEvents.onFloatingOriginShift.Remove(RebaseGroundAnchor);}
    void RebaseGroundAnchor(Vector3d offset,Vector3d nonFrame){
        if(!HighLogic.LoadedSceneIsFlight || part==null || vessel==null || !vessel.loaded || vessel.packed || !part.PermanentGroundContact)return;
        var worldJoints=part.GetComponents<ConfigurableJoint>().Where(j=>j.connectedBody==null).ToArray();
        if(worldJoints.Length!=1)return;
        // Native origin shifts already translated this grounded vessel. Keep
        // its existing stock clamp anchor in that same coordinate frame.
        // No desired position, velocity, strength or extra force is supplied.
        var shift=(Vector3)(vessel.LandedOrSplashed?FloatingOrigin.OffsetNonKrakensbane:offset);
        worldJoints[0].connectedAnchor-=shift;
    }
    bool HasCaughtBooster {get{return vessel!=null && vessel.FindPartModulesImplementing<DenizTowerCatch>().Any(c=>c.capturedRootID!=0);}}
    public override void OnActive(){if(HasCaughtBooster){status="Caught booster - use safe catch release";return;}if(launching)return;launching=true;StartCoroutine(LaunchSequence());}
    [KSPEvent(guiActive=true,guiName="Open tower arms")] public void OpenArms(){if(HasCaughtBooster){status="Caught booster - use safe catch release";return;}foreach(var a in vessel.FindPartModulesImplementing<ModuleSLEController>()){a.ExtensionSpeed=a.moduleID=="SLEArmController"?10:1;a.OpenCloseLimit=a.moduleID=="SLEArmController"?113.5f:20;a.OpenClose=true;a.ExtensionLimit=0;}status="Tower arms opening";}
    IEnumerator LaunchSequence(){if(HasCaughtBooster){launching=false;yield break;}OpenArms();ScreenMessages.PostScreenMessage("Opening support arms - mount holds until clear",5,ScreenMessageStyle.UPPER_CENTER);yield return new WaitForSeconds(1);float start=Time.time;while(vessel.FindPartModulesImplementing<ModuleSLEAnimate>().Where(a=>a.moduleID=="LeftArmAnim" || a.moduleID=="RightArmAnim").Any(a=>a.ExtendedCurrent>2)){if(HasCaughtBooster){status="Caught booster - use safe catch release";launching=false;yield break;}if(Time.time-start>30){status="Arms did not clear - mount remains holding";launching=false;yield break;}yield return null;}if(HasCaughtBooster){status="Caught booster - use safe catch release";launching=false;yield break;}status="Released";base.OnActive();}
}

public class DenizPayloadControls : PartModule
{
    public override void OnStart(StartState state){base.OnStart(state);if(HighLogic.LoadedSceneIsFlight)StartCoroutine(RestoreAfterRelease());}
    IEnumerator RestoreAfterRelease(){while(vessel!=null && vessel.FindPartModulesImplementing<DenizStarshipControls>().Any())yield return new WaitForSeconds(1);if(vessel==null)yield break;foreach(var l in vessel.FindPartModulesImplementing<ModuleWheelDeployment>()){foreach(BaseEvent e in l.Events)e.active=true;foreach(BaseAction a in l.Actions){a.active=true;if(a.name.Contains("Toggle"))a.actionGroup=KSPActionGroup.Gear;}}}
    public static bool InCargo(Part p){while(p!=null){if(p.partInfo.name=="DenizPayloadRelease")return true;p=p.parent;}return false;}
    public static void Deploy(IEnumerable<Part> parts){foreach(var p in parts){foreach(var a in p.FindModulesImplementing<ModuleDeployableSolarPanel>())a.Extend();foreach(var a in p.FindModulesImplementing<ModuleDeployableAntenna>())a.Extend();foreach(var a in p.FindModulesImplementing<ModuleDeployableRadiator>())a.Extend();}}
    [KSPAction("Deploy equipment")] public void Equipment(KSPActionParam p){if(vessel.FindPartModulesImplementing<DenizStarshipControls>().Any())return;Deploy(vessel.parts);}
    [KSPAction("Deploy payload parachutes")] public void Parachutes(KSPActionParam p){if(vessel.FindPartModulesImplementing<DenizStarshipControls>().Any())return;foreach(var chute in vessel.FindPartModulesImplementing<ModuleParachute>())chute.Deploy();}
    [KSPAction("Start payload engines")] public void Engines(KSPActionParam p){if(vessel.FindPartModulesImplementing<DenizStarshipControls>().Any())return;foreach(var e in vessel.FindPartModulesImplementing<ModuleEngines>())e.Activate();}
    [KSPAction("Stop payload engines")] public void Stop(KSPActionParam p){if(vessel.FindPartModulesImplementing<DenizStarshipControls>().Any())return;foreach(var e in vessel.FindPartModulesImplementing<ModuleEngines>())e.Shutdown();}
}


// SEP ModuleSEPRaptor.Start reads a flight-only gimbal reference in the VAB.
// PartModule load callbacks run before MonoBehaviour.Start; guard only editor loads.
// Flight keeps SEP's original outward actuation and ordinary gimbal controls.
public class DenizRaptorEditorGuard : PartModule
{
    void GuardEditor()
    {
        if(!HighLogic.LoadedSceneIsEditor || part==null)return;
        foreach(var module in part.Modules.Cast<PartModule>().OfType<StarshipExpansionProject.Modules.ModuleSEPRaptor>())
            module.enableActuateOut=false;
    }
    public override void OnAwake(){base.OnAwake();GuardEditor();}
    public override void OnLoad(ConfigNode node){base.OnLoad(node);GuardEditor();}
}

// Original simple support brackets. Existing blueprint and part identities are
// unchanged; the pilot explicitly enables this prototype hardware before flight.
public class DenizCatchContact : MonoBehaviour
{
    public DenizBoosterCatchPins hardware;
    readonly Dictionary<Collider,Dictionary<Collider,float>> touches=new Dictionary<Collider,Dictionary<Collider,float>>();
    void OnCollisionEnter(Collision c){Observe(c);}
    void OnCollisionStay(Collision c){Observe(c);}
    void Observe(Collision c){
        if(hardware==null || hardware.Pins==null)return;
        foreach(var contact in c.contacts)if(hardware.Pins.Contains(contact.thisCollider) && contact.otherCollider!=null && Vector3.Dot(contact.normal,hardware.part.transform.up)>.7f){
            Dictionary<Collider,float> rails;if(!touches.TryGetValue(contact.thisCollider,out rails)){rails=new Dictionary<Collider,float>();touches[contact.thisCollider]=rails;}rails[contact.otherCollider]=Time.fixedTime;
        }
    }
    public bool Touched(Collider pin,Collider rail){Dictionary<Collider,float> rails;float stamp;return touches.TryGetValue(pin,out rails) && rails.TryGetValue(rail,out stamp) && Time.fixedTime-stamp<.1f;}
}
public class DenizBoosterCatchPins : PartModule, IPartMassModifier
{
    [KSPField(isPersistant=true,guiActive=true,guiActiveEditor=true,guiName="Catch brackets")] public bool pinsEnabled=false;
    [KSPField(isPersistant=true,guiActive=true,guiActiveEditor=true,guiName="Bracket angle",guiUnits="°")]
    [UI_FloatRange(minValue=0,maxValue=180,stepIncrement=5)] public float pinYaw;
    [KSPField] public float pinHeight=25;
    [KSPField] public float supportRadius=6.8f;
    public Collider[] Pins {get;private set;}
    public DenizCatchContact Contact {get;private set;}
    public Vector3[] PinCenters {get{return Pins==null?new Vector3[0]:Pins.Select(c=>c.bounds.center).ToArray();}}
    public override void OnStart(StartState s){base.OnStart(s);Build();}
    void Build(){
        Pins=new Collider[2];var material=new Material(Shader.Find("KSP/Diffuse"));material.color=new Color(.8f,.48f,.12f);
        for(int i=0;i<2;i++){
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name="DenizCatchPin"+i;go.layer=part.gameObject.layer;go.transform.SetParent(part.transform,false);
            go.transform.localScale=new Vector3(4.8f,.5f,1.4f);go.GetComponent<Renderer>().sharedMaterial=material;Pins[i]=go.GetComponent<Collider>();
        }
        Contact=part.gameObject.AddComponent<DenizCatchContact>();Contact.hardware=this;Refresh();
    }
    public void LateUpdate(){if(Pins!=null){Fields["pinYaw"].guiActive=HighLogic.LoadedSceneIsFlight && vessel.situation==Vessel.Situations.PRELAUNCH;}}
    void Refresh(){for(int i=0;i<2;i++){var tr=Pins[i].transform;var q=Quaternion.AngleAxis(pinYaw,Vector3.up);tr.localPosition=q*new Vector3((i==0?-1:1)*supportRadius,pinHeight,0);tr.localRotation=q;tr.gameObject.SetActive(pinsEnabled);}}
    [KSPEvent(guiActive=true,guiActiveEditor=true,guiName="Enable catch brackets")]
    public void EnablePins(){if(HighLogic.LoadedSceneIsFlight && vessel.situation!=Vessel.Situations.PRELAUNCH){ScreenMessages.PostScreenMessage("Configure brackets before launch",4,ScreenMessageStyle.UPPER_CENTER);return;}pinsEnabled=true;Refresh();}
    public float GetModuleMass(float mass,ModifierStagingSituation situation){return pinsEnabled?.8f:0;}
    public ModifierChangeWhen GetModuleMassChangeWhen(){return ModifierChangeWhen.CONSTANTLY;}
}

// Reversible native attachment after both measured physical contacts. Match the
// stock grapple lifecycle: contact attach node and preservation of current part
// poses around coupling. This does not place a booster at the catch target.
public class DenizTowerCatch : PartModule, IActiveJointHost
{
    [KSPField(guiActive=true,guiName="Tower catch")] public string catchStatus="Disarmed";
    [KSPField(isPersistant=true)] public string capturedIDs="";
    [KSPField(isPersistant=true)] public uint capturedRootID;
    [KSPField(isPersistant=true)] public string capturedName="";
    [KSPField(isPersistant=true)] public bool calibrated;
    [KSPField(isPersistant=true)] public Vector3 targetLocal;
    [KSPField(isPersistant=true)] public bool contactNodeSaved;
    [KSPField(isPersistant=true)] public bool contactNodeOnBooster;
    [KSPField(isPersistant=true)] public Vector3 contactNodePosition;
    [KSPField(isPersistant=true)] public Vector3 contactNodeOrientation;
    [KSPField(isPersistant=true)] public Vector3 contactNodeSecondaryAxis;
    [KSPField(guiActive=true,guiName="Target error",guiUnits="m",guiFormat="F2")] public float targetError;
    [KSPField(guiActive=true,guiName="Relative speed",guiUnits="m/s",guiFormat="F2")] public float relativeSpeed;
    [KSPField(guiActive=true,guiName="Support contacts")] public string contacts="0 / 2";
    bool armed,closing;float dwell,closeStart;Part candidate;
    AttachNode contactNode;
    ActiveJointPivot captureJoint;
    public Part GetHostPart(){return part;}
    public Transform GetLocalTransform(){return part.transform;}
    public void OnJointInit(ActiveJoint joint){}
    public void OnDriveModeChanged(ActiveJoint.DriveMode mode){}
    public void OnDestroy(){if(captureJoint!=null)captureJoint.Terminate();}
    public event Action<Part> CaptureStarting;
    public event Action<Part> CaptureCompleted;
    Transform trolley;
    public bool Armed {get{return armed;}}
    public Vector3 TargetWorld {get{return trolley==null?part.transform.position:trolley.TransformPoint(targetLocal);}}
    public override void OnStart(StartState s){base.OnStart(s);armed=false;closing=false;trolley=part.FindModelTransform("Trolleys");catchStatus=capturedRootID==0?"Disarmed":"Checking saved support";if(capturedRootID!=0){MoveArms(8);var root=vessel.parts.FirstOrDefault(p=>p.flightID==capturedRootID);if(contactNodeSaved && root!=null){InstallContactNode(root);if(contactNodeOnBooster)StartCaptureJoint();}}}
    void InstallContactNode(Part root){
        part.attachNodes.RemoveAll(n=>n.id=="denizCatchContact");
        root.attachNodes.RemoveAll(n=>n.id=="denizCatchContact");
        contactNode=new AttachNode();contactNode.id="denizCatchContact";contactNode.size=1;
        contactNode.owner=contactNodeOnBooster?root:part;contactNode.attachedPart=contactNodeOnBooster?part:root;contactNode.attachedPartId=contactNode.attachedPart.flightID;
        contactNode.position=contactNode.originalPosition=contactNodePosition;
        contactNode.orientation=contactNode.originalOrientation=contactNodeOrientation;
        contactNode.secondaryAxis=contactNode.originalSecondaryAxis=contactNodeSecondaryAxis;
        contactNode.ResourceXFeed=false;contactNode.owner.attachNodes.Add(contactNode);
    }
    void StartCaptureJoint(){if(captureJoint!=null)captureJoint.Terminate();captureJoint=ActiveJointPivot.Create(this,contactNode);captureJoint.SetPivotAngleLimit(5);}
    void CoupleAtCurrentContacts(Part root){
        var incoming=root.vessel;var anchored=vessel;
        var physical=incoming.parts.Concat(anchored.parts).Where(p=>p.physicalSignificance==Part.PhysicalSignificance.FULL).ToArray();
        var positions=physical.ToDictionary(p=>p,p=>p.transform.position);
        var rotations=physical.ToDictionary(p=>p,p=>p.transform.rotation);
        var pins=root.FindModuleImplementing<DenizBoosterCatchPins>();
        var midpoint=pins.PinCenters.Aggregate(Vector3.zero,(a,p)=>a+p)/pins.PinCenters.Length-root.transform.up*.25f;
        // Stock grapple puts the node on the incoming child when that child
        // couples to the grabber. Store the measured hit in that same space.
        var measuredRootLocal=root.transform.InverseTransformPoint(midpoint);
        if(CaptureStarting!=null)CaptureStarting(root);
        GameEvents.onActiveJointNeedUpdate.Fire(incoming);GameEvents.onActiveJointNeedUpdate.Fire(anchored);
        incoming.SetRotation(incoming.transform.rotation);anchored.SetRotation(anchored.transform.rotation);
        // Stock's pristine-coordinate synchronization precedes joint creation.
        // Use this vessel's current position: no desired flight position.
        anchored.SetPosition((Vector3d)anchored.transform.position,true);
        contactNodeOnBooster=true;contactNodePosition=measuredRootLocal;
        contactNodeOrientation=root.transform.InverseTransformDirection(part.transform.up);
        contactNodeSecondaryAxis=root.transform.InverseTransformDirection(-part.transform.forward);
        contactNodeSaved=true;InstallContactNode(root);
        if(FlightGlobals.ActiveVessel==incoming){FlightGlobals.ForceSetActiveVessel(anchored);FlightInputHandler.SetNeutralControls();}
        root.Couple(part);
        StartCaptureJoint();
        // Stock grapple preserves these same physical-part world poses after
        // native hierarchy changes. No velocities, orbit, fuel or clock edited.
        foreach(var p in physical){p.transform.position=positions[p];p.transform.rotation=rotations[p];}
        // Stock grapple notifies native joint/autostrut subscribers again after
        // the temporary pristine hierarchy has its actual poses restored.
        GameEvents.onVesselWasModified.Fire(vessel);
        if(CaptureCompleted!=null)CaptureCompleted(root);
    }
    ModuleSLEController Arms {get{return part.FindModulesImplementing<ModuleSLEController>().FirstOrDefault(a=>a.moduleID=="SLEArmController");}}
    float ArmGap {get{return 113.5f-part.FindModulesImplementing<ModuleSLEAnimate>().Where(a=>a.moduleID=="LeftArmAnim" || a.moduleID=="RightArmAnim").Sum(a=>a.ExtendedCurrent);}}
    IEnumerable<Collider> Rails(int i){return part.GetComponentsInChildren<Collider>(true).Where(c=>c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy && Path(c.transform).Contains(i==0?"ChopstickRight/Colliders/":"ChopstickLeft/Colliders.002/") && c.name.StartsWith("Collider1"));}
    string Path(Transform t){string s=t.name;while(t.parent!=null && t.parent!=part.transform){t=t.parent;s=t.name+"/"+s;}return s;}
    void MoveArms(float angle){var a=Arms;if(a==null)return;a.OpenClose=true;a.OpenCloseLimit=angle;a.ExtensionLimit=0;a.ExtensionSpeed=10;}
    void Tell(string s){catchStatus=s;ScreenMessages.PostScreenMessage(s,5,ScreenMessageStyle.UPPER_CENTER);}
    [KSPEvent(guiActive=true,guiName="Calibrate catch on pad")]
    public void Calibrate(){
        if(vessel==null || vessel.situation!=Vessel.Situations.PRELAUNCH || trolley==null || Arms==null || Mathf.Abs(Arms.OpenCloseLimit-8)>1){Tell("Calibrate with original closed launch arms on pad");return;}
        var b=vessel.parts.SingleOrDefault(p=>p.partInfo.name=="DenizSuperHeavy");var pins=b==null?null:b.FindModuleImplementing<DenizBoosterCatchPins>();
        if(pins==null || !pins.pinsEnabled){Tell("Enable booster brackets on pad first");return;}
        var up=b.transform.up;var centers=pins.PinCenters;float[] heights=new float[2];
        var right=Rails(0).ToArray();var left=Rails(1).ToArray();
        if(right.Length!=1 || left.Length!=1){Tell("Exactly two known physical support rails required");return;}
        // The existing twin-tower geometry is asymmetric around the pad axis.
        // Measure the support-box center from the two actual rail colliders,
        // rather than assuming the original launch axis is the capture center.
        var railMid=(right[0].bounds.center+left[0].bounds.center)*.5f;
        var offset=Vector3.ProjectOnPlane(railMid-b.transform.position,up);
        for(int i=0;i<2;i++){
            bool found=false;float highest=float.NegativeInfinity;
            foreach(var c in Rails(i))foreach(var pin in centers){
                RaycastHit h;if(c.Raycast(new Ray(pin+offset+up*100,-up),out h,200)){highest=Mathf.Max(highest,Vector3.Dot(h.point-b.transform.position,up));found=true;}
            }
            if(!found){Tell("Bracket misses a physical arm rail: adjust bracket angle");return;}heights[i]=highest;
        }
        if(Mathf.Abs(heights[0]-heights[1])>.25f){Tell("Arm rail heights do not match");return;}
        // 0.27m puts each 0.5m bracket bottom 2cm above the measured rail.
        var target=b.transform.position+offset+up*((heights[0]+heights[1])*.5f+.27f-pins.pinHeight);
        targetLocal=trolley.InverseTransformPoint(target);calibrated=true;Tell("Catch target measured from both physical rails");
    }
    [KSPEvent(guiActive=true,guiActiveUnfocused=true,externalToEVAOnly=false,unfocusedRange=500,guiName="Arm / abort tower catch")]
    public void ToggleCatch(){
        if(capturedRootID!=0){Tell("Supported: use safe release");return;}
        if(armed){armed=false;closing=false;dwell=0;candidate=null;MoveArms(113.5f);Tell("Catch aborted; arms opening");return;}
        if(!calibrated || trolley==null || Arms==null || !vessel.parts.Any(p=>p is Part && p.FindModuleImplementing<DenizStarbaseClamp>()!=null)){Tell("Calibrated anchored tower required");return;}
        if(FlightGlobals.Vessels.Where(v=>v.loaded).SelectMany(v=>v.FindPartModulesImplementing<DenizTowerCatch>()).Any(c=>c!=this && (c.Armed || c.capturedRootID!=0))){Tell("Another tower already owns catch");return;}
        armed=true;closing=false;dwell=0;MoveArms(113.5f);Tell("Catch armed; pilot flies approach");
    }
    bool MotionSafe(Part b,float positionLimit,float speedLimit,float spinLimit,float angleLimit){
        targetError=Vector3.Distance(b.transform.position,TargetWorld);
        relativeSpeed=(float)(b.vessel.srf_velocity-vessel.srf_velocity).magnitude;
        return targetError<=positionLimit && relativeSpeed<=speedLimit && b.vessel.angularVelocity.magnitude<=spinLimit && Vector3.Angle(b.transform.up,b.vessel.upAxis)<=angleLimit;
    }
    int SupportCount(Part b){
        var pins=b.FindModuleImplementing<DenizBoosterCatchPins>();if(pins==null || !pins.pinsEnabled)return 0;
        int total=0;var used=new HashSet<int>();
        for(int i=0;i<2;i++){
            bool hit=false;
            for(int k=0;k<pins.Pins.Length && !hit;k++)if(!used.Contains(k))foreach(var rail in Rails(i)){
                Vector3 separation;float distance;
                var pin=pins.Pins[k];
                // Require actual native collider overlap/touch, not model bounds.
                if(Physics.ComputePenetration(pin,pin.transform.position,pin.transform.rotation,rail,rail.transform.position,rail.transform.rotation,out separation,out distance)){
                    if(distance>.08f)continue;
                    if(pins.Contact!=null && pins.Contact.Touched(pin,rail)){hit=true;used.Add(k);break;}
                }
                var point=pin.ClosestPoint(rail.bounds.center);var gap=Vector3.Distance(point,rail.ClosestPoint(point));
                if(pins.Contact!=null && pins.Contact.Touched(pin,rail) && gap<.03f){hit=true;used.Add(k);break;}
            }
            if(hit)total++;
        }
        contacts=total+" / 2";return total;
    }
    public string SupportDetail {
        get {
            var b=candidate;
            if(b==null && capturedRootID!=0)b=vessel.parts.FirstOrDefault(p=>p.partInfo.name=="DenizSuperHeavy");
            var pins=b==null?null:b.FindModuleImplementing<DenizBoosterCatchPins>();if(pins==null || pins.Pins==null)return "No candidate";
            var rows=new List<string>();
            for(int i=0;i<2;i++)foreach(var rail in Rails(i))for(int k=0;k<pins.Pins.Length;k++){
                var pin=pins.Pins[k];Vector3 direction;float depth;
                bool overlap=Physics.ComputePenetration(pin,pin.transform.position,pin.transform.rotation,rail,rail.transform.position,rail.transform.rotation,out direction,out depth);
                var point=pin.ClosestPoint(rail.bounds.center);float gap=Vector3.Distance(point,rail.ClosestPoint(point));
                rows.Add("rail"+i+"/pin"+k+" touch="+(pins.Contact!=null && pins.Contact.Touched(pin,rail))+" overlap="+overlap+" depth="+depth.ToString("F4")+" gap="+gap.ToString("F4"));
            }
            return string.Join("; ",rows.ToArray());
        }
    }
    public float CurrentArmGap {get{return ArmGap;}}
    public int SupportGeometryCount {
        get {
            var b=candidate;if(b==null && capturedRootID!=0)b=vessel.parts.FirstOrDefault(p=>p.partInfo.name=="DenizSuperHeavy");
            var pins=b==null?null:b.FindModuleImplementing<DenizBoosterCatchPins>();if(pins==null || pins.Pins==null)return 0;
            int total=0;var used=new HashSet<int>();
            for(int i=0;i<2;i++){
                bool near=false;
                for(int k=0;k<pins.Pins.Length && !near;k++)if(!used.Contains(k))foreach(var rail in Rails(i)){
                    var pin=pins.Pins[k];Vector3 direction;float depth;
                    if(Physics.ComputePenetration(pin,pin.transform.position,pin.transform.rotation,rail,rail.transform.position,rail.transform.rotation,out direction,out depth)){
                        if(depth>.08f)continue;near=true;used.Add(k);break;
                    }
                    var point=pin.ClosestPoint(rail.bounds.center);if(Vector3.Distance(point,rail.ClosestPoint(point))<.03f){near=true;used.Add(k);break;}
                }
                if(near)total++;
            }
            return total;
        }
    }
    public void FixedUpdate(){
        if(!HighLogic.LoadedSceneIsFlight || vessel==null || vessel.packed || Time.timeScale<=0)return;
        if(capturedRootID!=0){
            var savedRoot=vessel.parts.FirstOrDefault(p=>p.flightID==capturedRootID);
            var ids=capturedIDs.Split(',');
            bool intact=savedRoot!=null && savedRoot.parent==part && savedRoot.attachJoint!=null && ids.All(id=>vessel.parts.Any(p=>p.flightID.ToString()==id));
            catchStatus=!intact?"FAULT: saved catch parts/joint missing":(Mathf.Abs(ArmGap-8)>1 || SupportGeometryCount!=2?"FAULT: catch arm/support geometry changed":"Supported by native attachment");armed=false;return;
        }
        if(!armed)return;
        if(candidate==null){
            var candidates=FlightGlobals.Vessels.Where(v=>v!=vessel && v.loaded && !v.packed && v.GetCrewCount()==0 && v.parts.Count==11).SelectMany(v=>v.parts).Where(p=>p.partInfo.name=="DenizSuperHeavy" && p.FindModuleImplementing<DenizBoosterCatchPins>()!=null && p.FindModuleImplementing<DenizBoosterCatchPins>().pinsEnabled).ToArray();
            if(candidates.Length!=1){catchStatus="Waiting for one uncrewed 11-part booster";return;}candidate=candidates[0];
        }
        if(candidate.vessel==vessel || candidate.vessel.parts.Count!=11){armed=false;Tell("Catch inhibited: booster identity changed");return;}
        if(!closing){
            if(!MotionSafe(candidate,.5f,.2f,.01f,3)){dwell=0;catchStatus="Armed: outside capture limits";return;}
            dwell+=Time.fixedDeltaTime;if(dwell<1){catchStatus="Verifying one-second approach dwell";return;}
            closing=true;closeStart=Time.time;MoveArms(8);Tell("Closing native arms");
        }
        if(!MotionSafe(candidate,1,.5f,.03f,5)){armed=false;closing=false;MoveArms(113.5f);Tell("Unsafe drift: catch aborted");return;}
        if(Time.time-closeStart>15){armed=false;closing=false;MoveArms(113.5f);Tell("No two-arm engagement: catch aborted");return;}
        if(Arms==null || Mathf.Abs(ArmGap-8)>1 || SupportCount(candidate)!=2)return;
        var root=candidate.vessel.rootPart;capturedIDs=string.Join(",",candidate.vessel.parts.Select(p=>p.flightID.ToString()).ToArray());capturedRootID=root.flightID;capturedName=candidate.vessel.vesselName;
        CoupleAtCurrentContacts(root);armed=false;closing=false;Tell("Caught: native reversible attachment");
    }
    [KSPEvent(guiActive=true,guiName="Safe release caught booster")]
    public void SafeRelease(){
        var root=vessel.parts.FirstOrDefault(p=>p.flightID==capturedRootID);if(root==null || root.parent!=part){Tell("No intact caught booster to release");return;}
        var ids=capturedIDs.Split(',');var booster=vessel.parts.Where(p=>ids.Contains(p.flightID.ToString())).ToArray();
        if(booster.Length!=11){Tell("Original booster identities required");return;}
        var mass=booster.Sum(p=>p.mass+p.GetResourceMass());var thrust=booster.SelectMany(p=>p.FindModulesImplementing<ModuleEngines>()).Sum(e=>e.finalThrust);
        if(thrust<1.05*mass*vessel.graviticAcceleration.magnitude){Tell("Pilot must establish upward thrust before release");return;}
        string name=capturedName;bool follow=FlightGlobals.ActiveVessel==vessel;if(captureJoint!=null){captureJoint.Terminate();captureJoint=null;}root.decouple(0);part.attachNodes.RemoveAll(n=>n.id=="denizCatchContact");root.attachNodes.RemoveAll(n=>n.id=="denizCatchContact");contactNode=null;contactNodeSaved=false;contactNodeOnBooster=false;root.vessel.vesselName=name;if(follow)StartCoroutine(FollowReleasedBooster(root));capturedRootID=0;capturedIDs="";capturedName="";MoveArms(113.5f);Tell("Released; catch disarmed");
    }
    IEnumerator FollowReleasedBooster(Part root){
        // Match stock grapple release: let native decouple finish the vessel's
        // orbit/body lifecycle before focus subscribers inspect the new vessel.
        yield return null;
        if(!HighLogic.LoadedSceneIsFlight || root==null || root.vessel==null)yield break;
        FlightGlobals.ForceSetActiveVessel(root.vessel);
        FlightInputHandler.SetNeutralControls();
        root.vessel.ResumeStaging();
        FlightCamera.MinDistanceBackaway();
    }
}

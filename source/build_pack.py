from pathlib import Path
import argparse,copy,json,math,shutil
from config import *
from geometry import *
HERE=Path(__file__).resolve().parent
OUT=HERE.parent/'build/Starship Mission Pack'
MOD=KSP/'GameData/LusiadosStarshipMissions'
# Retired Full Stack/Ship Only on 2026-10-05 at the owner's request.
# Keep the original persistent-ID formula and generator functions for temporary tests.
VARIANTS=[(True,True)]
WRITE_FILES=__name__=='__main__'
DRY_RUN=not WRITE_FILES
if WRITE_FILES:
 parser=argparse.ArgumentParser(description='Generate the 16 Tower Launch craft and Booster Practice. Back up before installing.')
 parser.add_argument('--dry-run',action='store_true',help='Write only to --out; never modify Steam.')
 parser.add_argument('--out',type=Path,help='Output folder, required for --dry-run.')
 args=parser.parse_args()
 if args.dry_run and args.out is None:parser.error('--dry-run requires --out to preserve the current pack.')
 if args.out is not None and not args.dry_run:parser.error('--out requires --dry-run; normal generation installs to Steam.')
 DRY_RUN=args.dry_run
 if args.out is not None:OUT=args.out.resolve()
 if DRY_RUN and (OUT==KSP.resolve() or KSP.resolve() in OUT.parents):parser.error('A dry-run output folder must be outside Steam.')
 OUT.mkdir(parents=True,exist_ok=True)
 if not DRY_RUN:(MOD/'Plugins').mkdir(parents=True,exist_ok=True)
SCALE=1.6
MASS_SCALE=SCALE**3
OWN=set()
SCALEMAP={}
SCALED=set()

def clone(src,name,title):
 c=copy.deepcopy(CACHE[src]);c.set('name',name);c.set('title',title);c.set('manufacturer','Lusiados Mission Systems');c.set('TechRequired','veryHeavyRocketry')
 if not c.nodes('MODEL'):c.children+=copy.deepcopy(model_defs(src));c.values=[(k,v) for k,v in c.values if k!='mesh']
 c.children=[m for m in c.children if not(m.name=='MODULE' and m.get('name') in ['BoosterGuidanceCore','ModuleB9PartInfo'])]
 CACHE[name]=c;OWN.add(name);return c
def mod(c,name,**fields):
 n=Node('MODULE');n.set('name',name)
 for k,v in fields.items():n.set(k,v)
 c.children.append(n);return n
def modify(c,module,**fields):
 for m in c.nodes('MODULE'):
  if m.get('name')==module:
   for k,v in fields.items():m.set(k,v)
def resource(c,name,amount):
 c.children=[m for m in c.children if not(m.name=='RESOURCE' and m.get('name')==name)]
 n=Node('RESOURCE');n.set('name',name);n.set('amount',amount);n.set('maxAmount',amount);c.children.append(n)

core=clone('SEP_26_SHIP_CORE','DenizStarshipCore','Starship mission tank and avionics')
core.set('breakingForce',50000);core.set('breakingTorque',50000)
mod(core,'DenizStarshipControls');mod(core,'kOSProcessor',diskSpace=200000,ECPerBytePerSecond=0,ECPerInstruction=.000004)
modify(core,'ModuleReactionWheel',PitchTorque=60,YawTorque=60,RollTorque=30)
modify(core,'ModuleRCSFX',thrusterPower=100)
cargo=clone('SEP_26_SHIP_CARGO','DenizStarshipCargo','Starship lockable mission cargo bay')
modify(cargo,'ModuleRCSFX',thrusterPower=100)
modify(cargo,'ModuleAnimateGeneric',revClampDirection='False',allowDeployLimit='False')
modify(cargo,'ModuleCargoBay',closedPosition=0)
mod(cargo,'DenizCargoPanel')
clamshell=clone('DenizStarshipCargo','DenizStarshipClamshell','Starship reusable hinged-nose cargo bay')
clamshell.nodes('MODEL')[0].set('model','TundraExploration/Parts/GOJIRAIII/TE2_19_SS_CARGO')
clamshell.children=[n for n in clamshell.children if not((n.name=='NODE' and n.get('name')=='node_cargo') or (n.name=='MODULE' and (n.get('moduleID')=='SEP_Ablative' or n.get('name')=='ModuleB9DisableTransform')))]
for m in clamshell.nodes('MODULE'):
 if m.get('name')=='ModuleCargoBay':m.values=[(k,v) for k,v in m.values if k!='nodeInnerAft']
for n in clamshell.nodes('NODE'):
 n.set('transform',{'node_fin_left':'node_FF_L','node_fin_right':'node_FF_R'}.get(n.get('transform'),n.get('transform')))
modify(clamshell,'ModuleAnimateGeneric',animationName='SS__CARGO',revClampDirection='False',allowDeployLimit='False',animSpeed=1)
modify(clamshell,'ModuleCargoBay',closedPosition=1,lookupCenter='0,3.3,0',DeployModuleIndex=0)
mod(clamshell,'DenizNoseHinge',openAngle=110)
cabin=clone('mk2LanderCabin_v2','DenizStarshipCabin','Starship two-seat flight cabin')
cabin.set('rescaleFactor',.75);cabin.set('mass',1.5)
modify(cabin,'ModuleCommand',minimumCrew=0)
booster=clone('SEP_26_BOOSTER_CORE','DenizSuperHeavy','Super Heavy mission booster')
booster.set('breakingForce',50000);booster.set('breakingTorque',50000)
mod(booster,'DenizStarshipControls');modify(booster,'ModuleDecouple',ejectionForce=30)
mod(booster,'DenizBoosterCatchPins',pinsEnabled='False',pinHeight=25)
modify(booster,'ModuleRCSFX',thrusterPower=150)
for name,rf,load in [('DenizStarshipLeg',1.8,120),('DenizStarshipLegBooster',2.5,200)]:
 c=clone('landingLeg1-2',name,'Starship landing leg' if load==120 else 'Super Heavy landing leg')
 c.set('rescaleFactor',rf);c.set('mass',.25 if load==120 else .4);c.set('cost',1800);c.set('breakingForce',500);c.set('breakingTorque',500)
 modify(c,'ModuleWheelSuspension',maximumLoad=load,springRatio=35,damperRatio=3)
arm=clone('piston_03','DenizCargoArm','Starship powered payload deployment arm')
arm.set('rescaleFactor',1);arm.set('mass',6);arm.set('cost',2000);arm.set('breakingForce',20000);arm.set('breakingTorque',20000)
modify(arm,'ModuleRoboticServoPiston',maxMotorOutput=20000,positionSpringMutliplier=2000,positionDampingMutliplier=120,traverseVelocityLimits='0.02,1.6',hardMinMaxLimits='0,9.6',softMinMaxExtension='0,9.6',servoMass=.5)
mod(arm,'DenizTelescopicSleeve')
release=clone('Decoupler_1','DenizPayloadRelease','Cargo arm payload release collar')
modify(release,'ModuleDecouple',ejectionForce=.1,stagingEnabled='False');release.set('stagingIcon','');release.set('stageOffset',-1);release.set('fuelCrossFeed','False');modify(release,'ModuleToggleCrossfeed',crossfeedStatus='false')
for src in ['probeCoreHex_v2','roverBody','landerCabinSmall','sspx-habitation-1875-1']:
 c=clone(src,'DenizPayload_'+src,'Mission '+CACHE[src].get('title',src));mod(c,'DenizPayloadControls')
 modify(c,'ModuleCommand',minimumCrew=0)
 c.set('node_stack_cargo','0,-0.2,0.4,0,0,1,1')
 refuel=clone('Size1p5_Tank_04','DenizMethaloxPod','Methalox orbital refuelling pod') if False else None
refuel=clone('fuelTank','DenizMethaloxPod','Methalox orbital refuelling pod')
refuel.children=[n for n in refuel.children if n.name!='RESOURCE' and not(n.name=='MODULE' and (n.get('moduleID')=='fuelSwitch' or n.get('name')=='ModuleCryoTank'))]
resource(refuel,'LqdMethane',2400);resource(refuel,'Oxidizer',800)
for src,name in [('DenizPayload_probeCoreHex_v2','DenizStationPayload'),('DenizPayload_landerCabinSmall','DenizOutpostPayload')]:
 c=clone(src,name,'Station habitat deployment core' if name=='DenizStationPayload' else 'Duna outpost command core')
 c.set('node_stack_cargo','0,'+('0.55' if name=='DenizOutpostPayload' else '0.8')+',0.4,0,0,1,1')
for src,name,rf in [('spacedust-telescope-1','DenizLargeTelescope',1.65),('mk2LanderCabin_v2','DenizLargeVehicle',1.35),('sspx-habitation-375-1','DenizLargeHabitat',1),('Size3SmallTank','DenizLargeDepot',1),('mk2LanderCabin_v2','DenizCrewShuttle',1),('probeStackLarge','DenizHeavyTug',1)]:
 c=clone(src,name,{'DenizLargeTelescope':'Hubble-style orbital observatory','DenizLargeVehicle':'Expedition vehicle cabin','DenizLargeHabitat':'Large station and surface habitat','DenizLargeDepot':'Orbital methalox depot','DenizCrewShuttle':'Independent crew shuttle','DenizHeavyTug':'Heavy asteroid tug control core'}[name]);c.set('rescaleFactor',float(c.get('rescaleFactor','1'))*rf);c.set('mass',float(c.get('mass','1'))*rf**3)
 c.set('node_stack_cargo','0,'+('-0.242424,0,0,-1,0' if name=='DenizLargeTelescope' else '-0.2,0.4,0,0,1')+',1')
 if not any(m.get('name')=='ModuleCommand' for m in c.nodes('MODULE')):mod(c,'ModuleCommand',minimumCrew=0)
 modify(c,'ModuleCommand',minimumCrew=0);mod(c,'DenizPayloadControls')
 if name=='DenizLargeTelescope':
  modify(c,'ModuleSpaceDustTelescope',PowerCost=3,Target='Mun',Enabled='False')
  for mm in c.nodes('MODULE'):
   if mm.get('name')=='ModuleSpaceDustTelescope':
    for slot,inst in zip(mm.nodes('SLOT'),['XenonSpectrometer','OxidizerSpectrometer']):slot.set('Instrument',inst)
 if name=='DenizLargeDepot':
  c.children=[n for n in c.children if n.name!='RESOURCE' and not(n.name=='MODULE' and n.get('name') in ['ModuleB9PartSwitch','ModuleCryoTank'])];resource(c,'LqdMethane',9000);resource(c,'Oxidizer',3000)
CARGO_MAP={}
for src,name,title in [('liquidEngineMini_v2','DenizCargoEngineMini','Payload Spark engine'),('liquidEngine2-2_v2','DenizCargoEngineMedium','Payload Poodle vacuum engine'),('parachuteRadial','DenizCargoParachute','Payload radial parachute')]:
 c=clone(src,name,title);c.set('stagingIcon','');c.set('stageOffset',-1)
 for m in c.nodes('MODULE'):
  if m.get('name') in ['ModuleEngines','ModuleEnginesFX','ModuleParachute']:
   for field,value in [('stagingEnabled','False'),('overrideStagingIconIfBlank','False'),('stagingToggleEnabledEditor','False'),('stagingToggleEnabledFlight','False')]:m.set(field,value)
 # Recorded Mun rover COM requires about3.76deg around the Spark pivot.
 # Existing payload engine gets6deg TVC; no extra part/model/thrust change.
 if name=='DenizCargoEngineMini':modify(c,'ModuleGimbal',gimbalRange=6)
 CARGO_MAP[src]=name
for f in (KSP/'GameData/StarshipLaunchExpansion/Parts/SSPads').glob('*.cfg'):
 for p in parse(f.read_text()).nodes('PART'):CACHE[p.get('name')]=p
pad=clone('SLE_SS_OLP','DenizStarbaseMount','Starship twin-tower launch mount')
pad.nodes('MODEL')[0].set('scale','1.65,1,1.65')
pad.set('breakingForce',50000);pad.set('breakingTorque',50000)
mod(pad,'ModuleCommand',minimumCrew=0);resource(pad,'ElectricCharge',10000)
mod(pad,'ModuleDataTransmitter',antennaType='INTERNAL',packetInterval=1,packetSize=2,packetResourceCost=.1,requiredResource='ElectricCharge',antennaPower=5000)
pad.children=[m for m in pad.children if not(m.name=='MODULE' and (m.get('name') in ['ModuleDockingNode'] or m.get('moduleID')=='ClampSwitch'))]
modify(pad,'LaunchClamp',name='DenizStarbaseClamp')
pad.set('node_stack_connect03','-20.8444,-5.43387365,-1.05908179,0,1,0,5')
for src in ['SEP_26_SHIP_AFT_LEFT_FLAP','SEP_26_SHIP_AFT_RIGHT_FLAP','SEP_26_SHIP_FWD_LEFT_FLAP','SEP_26_SHIP_FWD_RIGHT_FLAP','SEP_26_R3_SL_C','SEP_26_R3_VAC','SEP_26_BOOSTER_CLUSTER','SEP_26_BOOSTER_GRIDFIN','SLE_SS_OLIT_Base','SLE_SS_OLIT_Core','SLE_SS_OLIT_Top','SLE_SS_OLIT_MZ']:
 name='DenizScaled_'+src;clone(src,name,'Full-scale '+CACHE[src].get('title',src));SCALEMAP[src]=name
 # Guard SEP's editor-only initialization defect; flight actuation stays original.
 if src=='SEP_26_R3_SL_C':mod(CACHE[name],'DenizRaptorEditorGuard')
 if src=='SEP_26_BOOSTER_CLUSTER':CACHE[name].set('breakingForce',50000);CACHE[name].set('breakingTorque',50000)
 if src=='SLE_SS_OLIT_MZ':mod(CACHE[name],'DenizTowerCatch')
SCALED=set(SCALEMAP.values())|{'DenizStarshipCore','DenizStarshipCargo','DenizStarshipClamshell','DenizSuperHeavy','DenizStarshipLeg','DenizStarshipLegBooster','DenizStarbaseMount'}
def rescale_part(c):
 c.set('rescaleFactor',float(c.get('rescaleFactor','1'))*SCALE)
 for field in ['mass','breakingForce','breakingTorque']:
  if c.get(field):c.set(field,float(c.get(field))*MASS_SCALE)
 for r in c.nodes('RESOURCE'):
  for field in ['amount','maxAmount']:r.set(field,float(r.get(field))*MASS_SCALE)
 for m in c.nodes('MODULE'):
  if m.get('name')=='ModuleSEPProceduralEngineGUI':
   for engine_set in m.nodes('ENGINESET'):
    for field in ['singleEngineThrust','mass']:
     if engine_set.get(field):engine_set.set(field,float(engine_set.get(field))*MASS_SCALE)
  for field in ['maxThrust','thrusterPower','baseVolume','maximumLoad']:
   if m.get(field):m.set(field,float(m.get(field))*MASS_SCALE)
  if m.get('name')=='ModuleReactionWheel':
   for field in ['PitchTorque','YawTorque','RollTorque']:
    if m.get(field):m.set(field,float(m.get(field))*SCALE**5)
  if m.get('name')=='ModuleCargoBay':m.set('lookupRadius',float(m.get('lookupRadius','5'))*SCALE)
  for sub in m.nodes('SUBTYPE'):
   if sub.get('addedMass'):sub.set('addedMass',float(sub.get('addedMass'))*MASS_SCALE)
 for m in c.nodes('MODULE'):
  if m.get('name')=='DenizStarshipControls':m.set('rcsScale',MASS_SCALE)
for name in SCALED:rescale_part(CACHE[name])
# The original flap configs omit joint strengths. Give the much larger custom
# flaps finite hinge strengths instead of inheriting Part's small defaults.
for src in ['SEP_26_SHIP_AFT_LEFT_FLAP','SEP_26_SHIP_AFT_RIGHT_FLAP','SEP_26_SHIP_FWD_LEFT_FLAP','SEP_26_SHIP_FWD_RIGHT_FLAP']:
 CACHE[SCALEMAP[src]].set('breakingForce',20000)
 CACHE[SCALEMAP[src]].set('breakingTorque',50000)
SCALEMAP.update(CARGO_MAP)
for m in CACHE[SCALEMAP['SLE_SS_OLIT_MZ']].nodes('MODULE'):
 if m.get('moduleID')=='TrolleyAnim':m.set('MaxExtension',104)
 if m.get('moduleID') in ['Pushers1Anim','Pushers2Anim']:m.set('MaxExtension',20)
 if m.get('moduleID')=='SLEPushersController':m.set('OpenCloseRange','0.32,20,20')
# Global FMRS/kOS patches are applied by ModuleManager to these clones once.
for name in OWN:CACHE[name].children=[n for n in CACHE[name].children if not(n.name=='MODULE' and n.get('name') in ['FMRS_PM','KOSNameTag','ModuleB9PartInfo'])]
localization=dict(re.findall(r'^\s*(#LOC_\w+)\s*=\s*(.*?)\s*$',(KSP/'GameData/StarshipLaunchExpansion/Localization/en-us.cfg').read_text(),re.M))
def localized(v):return localization.get(v,v)
configs=Node();configs.children=[CACHE[n] for n in sorted(OWN)]
if WRITE_FILES:
 if DRY_RUN:(OUT/'MissionParts.cfg').write_text(configs.dump()+'\n')
 else:
  (MOD/'MissionParts.cfg').write_text(configs.dump()+'\n');shutil.copy2(HERE/'DenizStarship.dll',MOD/'Plugins/DenizStarship.dll')
for name in OWN:
 mod(CACHE[name],'FMRS_PM',parent_vessel=0);mod(CACHE[name],'KOSNameTag')
 if any(m.get('name')=='ModuleB9PartSwitch' for m in CACHE[name].nodes('MODULE')):mod(CACHE[name],'ModuleB9PartInfo')

memo={}
def geom(name):
 if name not in memo:memo[name]=model(name)[0]
 return copy.deepcopy(memo[name])
def display_name(title):
 # Owner requested short visible names on 2026-10-05. Keep file identifiers stable.
 if title == 'Lusiados Super Heavy - Booster Practice':return 'D Booster Practice'
 if not title.startswith('Lusiados Starship ') or not title.endswith(' - Tower Launch'):return title
 number,mission=title.removeprefix('Lusiados Starship ').removesuffix(' - Tower Launch').split(' - ',1)
 clam=mission.endswith(' - Clamshell');mission=mission.removesuffix(' - Clamshell')
 labels={'Empty':'Empty','Empty Large Bay':'Empty','Satellite':'Sat','Hubble Telescope':'Hubble','Mun Rover':'Rover','Expedition Rover':'Rover','Station Builder':'Station','Station Hub':'Hub','Refuelling':'Refuel','Orbital Fuel Depot':'Depot','Duna Outpost':'Outpost','Surface Base':'Base','Crew Rescue':'Rescue','Crew Shuttle':'Crew','Asteroid Tug':'Tug','Heavy Asteroid Tug':'Tug'}
 return 'D Starship '+str(int(number))+('C' if clam else '')+' '+labels[mission]
class Craft:
 def __init__(self,title,base):self.title=title;self.parts=[];self.base=base;self.root=None
 def part(self,name,pos=(0,0,0),rot=(1,0,0,0),tag=''):
  name=SCALEMAP.get(name,name)
  p=dict(name=name,id=self.base+len(self.parts),pos=tuple(pos),rot=rot,nodes=geom(name),links=[],connections={},modules=[],stage=-1,tag=tag,parent=None,surface=False)
  self.parts.append(p)
  if self.root is None:self.root=p
  return p
 def attach(self,parent,pnode,name,cnode,rot=(1,0,0,0),tag=''):
  p=self.part(name,rot=rot,tag=tag);pd=rotate(parent['rot'],parent['nodes'][pnode][1]);cd=rotate(rot,p['nodes'][cnode][1]);p['rot']=qmul(fromto(cd,scale(pd,-1)),rot)
  p['pos']=sub(add(parent['pos'],rotate(parent['rot'],parent['nodes'][pnode][0])),rotate(p['rot'],p['nodes'][cnode][0]))
  parent['links'].append(p);parent['connections'][pnode]=p;p['connections'][cnode]=parent;p['parent']=parent;return p
 def mount(self,parent,name,local,rot=(1,0,0,0),tag=''):
  if parent['name'] in SCALED:local=scale(local,SCALE)
  p=self.part(name,add(parent['pos'],rotate(parent['rot'],local)),qmul(parent['rot'],rot),tag);parent['links'].append(p);p['parent']=parent;p['surface']=True;return p
 def module(self,p,name,**fields):
  n=Node('MODULE');n.set('name',name)
  for k,v in fields.items():n.set(k,v)
  p['modules'].append(n);return n
 def actions(self,p,module,mapping,**fields):
  n=self.module(p,module,**fields);a=Node('ACTIONS');n.children.append(a)
  for action,group in mapping.items():v=Node(action);v.set('actionGroup',group);a.children.append(v)
  return n
 def payload_control(self,p):self.actions(p,'DenizPayloadControls',{'Equipment':'Custom10','Engines':'Custom07','Stop':'Custom06','Parachutes':'Custom05'})
 def write(self,description,folder,write=None):
  root=Node();header={'ship':display_name(self.title),'version':'1.12.5','description':description,'type':'VAB','size':'85,145,32','steamPublishedFileId':0,'persistentId':self.base,'rot':'0,0,0,0','missionFlag':'Squad/Flags/default','vesselType':'Ship','OverrideDefault':'False,False,False,False','OverrideActionControl':'0,0,0,0','OverrideAxisControl':'0,0,0,0','OverrideGroupNames':',,,'}
  for k,v in header.items():root.set(k,v)
  def key(p):return p['name'].replace('_','.')+'_'+str(p['id'])
  def vec(v,sep=','):return sep.join(f'{x:.9g}' if abs(x)>1e-8 else '0' for x in v)
  errors=[]
  for p in self.parts:
   n=Node('PART');root.children.append(n);q=p['rot'];qxyzw=(q[1],q[2],q[3],q[0])
   fields={'part':key(p),'partName':'Part','persistentId':p['id'],'pos':vec(p['pos']),'attPos':'0,0,0','attPos0':vec(p['pos']),'rot':vec(qxyzw),'attRot':vec(qxyzw),'attRot0':'0,0,0,1','mir':'1,1,1','symMethod':'Radial','autostrutMode':'Off' if p['name']=='DenizCargoArm' or any(a['name']=='DenizCargoArm' for a in ancestors(p)) else ('Grandparent' if p!=self.root else 'Off'),'rigidAttachment':'False' if p['name']=='DenizCargoArm' else 'True','istg':p['stage'],'resPri':0,'dstg':p['stage'],'sidx':-1,'sqor':-1,'sepI':0,'attm':1 if p['surface'] else 0,'modCost':0,'modMass':0,'modSize':'0,0,0'}
   # The root-mounted physicless lamp/ladder need Root, not Grandparent,
   # to survive full-fuel tower physics startup (recorded 5 Oct ground retry).
   if p['name'] in ('domeLight1','telescopicLadderBay') and p['parent'] is self.root:fields['autostrutMode']='Root'
   for k,v in fields.items():n.set(k,v)
   for child in p['links']:n.values.append(('link',key(child)))
   for a,(pos,d) in p['nodes'].items():
    other=p['connections'].get(a);node='_'.join([key(other) if other else 'Null_0',vec(pos,'|'),vec(d,'|'),vec(pos,'|'),vec(d,'|')]);n.values.append(('attN',a+','+node))
    if other:
     b=next(bb for bb,cc in other['connections'].items() if cc is p);pa=add(p['pos'],rotate(p['rot'],pos));pb=add(other['pos'],rotate(other['rot'],other['nodes'][b][0]));errors.append(math.sqrt(dot(sub(pa,pb),sub(pa,pb))))
   if p['surface']:
    pos,d=p['nodes'].get('srfAttach',((0,0,0),(0,0,1)));n.set('srfN','srfAttach,'+'_'.join([key(p['parent']),vec(pos,'|'),vec(d,'|'),vec(pos,'|'),vec(d,'|')]))
   if p['tag'] and any(x.get('name')=='KOSNameTag' for x in CACHE[p['name']].nodes('MODULE')):self.module(p,'KOSNameTag',nameTag=p['tag'])
   remaining=list(p['modules'])
   for prefab in CACHE[p['name']].nodes('MODULE'):
    saved=Node('MODULE');saved.set('name',prefab.get('name'))
    if prefab.get('moduleID'):saved.set('moduleID',prefab.get('moduleID'))
    if prefab.get('name')=='ModuleB9PartSwitch' and prefab.nodes('SUBTYPE'):saved.set('currentSubtype',localized(prefab.nodes('SUBTYPE')[0].get('name')))
    found=next((x for x in remaining if x.get('name')==prefab.get('name') and (not x.get('moduleID') or x.get('moduleID')==prefab.get('moduleID'))),None)
    if found:
     for k,v in found.values:saved.set(k,localized(v) if k=='currentSubtype' else v)
     saved.children=found.children;remaining.remove(found)
    if prefab.get('name') in ['ModuleEngines','ModuleEnginesFX','ModuleParachute'] and any(a['name']=='DenizPayloadRelease' for a in ancestors(p)):saved.set('stagingEnabled','False')
    if prefab.get('name')=='ModuleWheelDeployment':saved.set('stateString','Retracted');saved.set('position',0)
    if prefab.get('name')=='ModuleSpaceDustTelescope':
     saved.set('Target','Mun');saved.set('Enabled','False');saved.set('PowerCost',3);saved.children+=copy.deepcopy(prefab.nodes('SLOT'))
    if p['name'].endswith('SEP_26_R3_SL_C') and prefab.get('name')=='ModuleB9PartSwitch' and any(x.get('name')=='StaticOff' for x in prefab.nodes('SUBTYPE')):saved.set('currentSubtype','StaticOff')
    n.children.append(saved)
   assert not remaining,(p['name'],[x.get('name') for x in remaining])
  assert not errors or max(errors)<1e-5
  path=folder/(self.title+'.craft')
  if WRITE_FILES if write is None else write:
   folder.mkdir(parents=True,exist_ok=True);path.write_text(root.dump()+'\n')
  return {'title':self.title,'displayName':display_name(self.title),'file':str(path),'parts':len(self.parts),'maxJointError':max(errors,default=0),'crewCapacity':sum(int(CACHE[p['name']].get('CrewCapacity','0')) for p in self.parts),'dryMass':sum(float(CACHE[p['name']].get('mass','0')) for p in self.parts)}
def ancestors(p):
 out=[];p=p['parent']
 while p:out.append(p);p=p['parent']
 return out

CONTROL={'ToggleCargo':'Custom01','CargoLockAction':'Custom02','ToggleLegs':'Gear, Custom03','LegLockAction':'Custom04','Reentry':'Custom05','LandingEngines':'Custom06','FlightEngines':'Custom07','ArmAction':'Custom08','ReleaseAction':'Custom09','Equipment':'Custom10','Cutoff':'Abort'}
def foundation(c,stack=True,tower=False,clamshell=False):
 core=c.part('DenizStarshipCore',(0,61*SCALE,0),tag='SHIP_TANK');c.actions(core,'DenizStarshipControls',CONTROL,cargoLocked='True',legsLocked='False',cargoOpen='False',legsExtended='False')
 c.module(core,'ModuleB9PartSwitch',moduleID='SEP_Raptor_VAC',currentSubtype='3RVAC')
 bay=c.attach(core,'node_top','DenizStarshipClamshell' if clamshell else 'DenizStarshipCargo','node_bottom',tag='CARGO_BAY');c.module(bay,'ModuleAnimateGeneric',animTime=1 if clamshell else 0,animSwitch='False' if clamshell else 'True',deployPercent=100)
 cabin=c.mount(bay,'DenizStarshipCabin',(0,1.5/SCALE,3.0/SCALE) if clamshell else (0,3.9,-.35),tag='FLIGHT_CABIN');c.module(cabin,'ModulePartVariants',selectedVariant='Bare')
 for side in ['LEFT','RIGHT']:
  c.attach(core,'node_fin_'+side.lower(),'SEP_26_SHIP_AFT_'+side+'_FLAP','node_fin_'+side.lower())
  c.attach(bay,'node_fin_'+side.lower(),'SEP_26_SHIP_FWD_'+side+'_FLAP','node_fin_'+side.lower())
 for i in range(1,4):
  c.attach(core,'node_raptor_'+str(i),'SEP_26_R3_SL_C','node_top_R3',axisrot((0,1,0),(i-1)*120))['stage']=1 if stack else 0
  c.attach(core,'node_raptorv_'+str(i),'SEP_26_R3_VAC','node_top_R3',axisrot((0,1,0),(i-1)*120))['stage']=1 if stack else 0
 c.attach(core,'node_Docking','dockingPort2','bottom',rot=axisrot((1,0,0),-90),tag='SHIP_DOCKING')
 for i in range(4):
  t=math.radians(45+i*90);r=2.77
  c.mount(core,'DenizStarshipLeg',(r*math.sin(t),-11.4,r*math.cos(t)),axisrot((0,1,0),45+i*90),tag='SHIP_LEG')
 for s in [-1,1]:c.mount(core,'solarPanels4',(s*2.8,1.5,.4),axisrot((0,1,0),s*90),tag='SHIP_SOLAR')
 c.mount(core,'batteryBank',(0,2,2.78));c.mount(core,'domeLight1',(0,2,-3.2),axisrot((0,1,0),180));c.mount(core,'telescopicLadderBay',(.4,2,3.2),axisrot((0,1,0),180))
 if stack:
  b=c.attach(core,'node_bottom','DenizSuperHeavy','node_top',tag='BOOSTER_TANK');b['stage']=1;c.actions(b,'DenizStarshipControls',CONTROL)
  cluster=c.attach(b,'node_bottom','SEP_26_BOOSTER_CLUSTER','node_top_Cluster',tag='BOOSTER_ENGINES');cluster['stage']=2;c.module(cluster,'ModuleSEPEngineSwitch',selectedIndex=0)
  for i in range(1,4):c.attach(b,'node_gridfin_'+str(i),'SEP_26_BOOSTER_GRIDFIN','node_gridfin')
  for i in range(6):
   t=math.radians(i*60);c.mount(b,'DenizStarshipLegBooster',(2.75*math.sin(t),-21,2.75*math.cos(t)),axisrot((0,1,0),i*60),tag='BOOSTER_LEG')
  if tower:
   stand=c.attach(cluster,'node_bottom_Cluster','DenizStarbaseMount','node_stack_top',tag='LAUNCH_MOUNT');stand['stage']=2
   c.module(stand,'ModuleB9PartSwitch',moduleID='structureSwitch',currentSubtype='#LOC_SLE_SS_OLP_Complete')
   for side,node,rot in [(1,'connect01',(1,0,0,0)),(-1,'connect03',axisrot((0,1,0),180))]:
    base=c.attach(stand,node,'SLE_SS_OLIT_Base','bottom',rot=rot,tag='LAUNCH_TOWER')
    mast=c.attach(base,'top','SLE_SS_OLIT_Core','bottom',rot=base['rot'],tag='LAUNCH_TOWER')
    mast['nodes']['top']=((0,73.4789*SCALE,0),(0,1,0))
    c.module(mast,'ModuleB9PartSwitch',moduleID='OLITSelector',currentSubtype='#LOC_SLE_SS_OLIT_Core_OLIT7Name')
    c.attach(mast,'top','SLE_SS_OLIT_Top','bottom',rot=mast['rot'],tag='LAUNCH_TOWER')
    arm=c.attach(mast,'node_stack_connect01','SLE_SS_OLIT_MZ','node_stack_connect01',rot=mast['rot'],tag='TOWER_ARMS')
    c.module(arm,'ModuleSLEAnimate',moduleID='TrolleyAnim',AnimatePosition=30/65*10,ExtensionLimit=48,ExtensionSpeed=.5)
    for aid in ['LeftArmAnim','RightArmAnim']:c.module(arm,'ModuleSLEAnimate',moduleID=aid,AnimatePosition=52.75/113.5*10,ExtensionLimit=52.75,ExtensionSpeed=10)
    for aid in ['Pushers1Anim','Pushers2Anim']:c.module(arm,'ModuleSLEAnimate',moduleID=aid,AnimatePosition=10,ExtensionLimit=20,ExtensionSpeed=1)
    c.module(arm,'ModuleSLEController',moduleID='SLEArmController',OpenClose='True',OpenCloseLimit=8,ExtensionLimit=0,ExtensionSpeed=10)
    c.module(arm,'ModuleSLEController',moduleID='SLEPushersController',OpenClose='True',OpenCloseLimit=20,ExtensionLimit=0,ExtensionSpeed=1)
  else:
   for s in [-1,1]:c.mount(b,'launchClamp1',(s*4.5,-15,0),tag='LAUNCH_CLAMP')['stage']=2
 else:
  for s in [-1,1]:c.mount(core,'launchClamp1',(s*4.5,-10,0),tag='PRACTICE_CLAMP')['stage']=0
 # The permanent cabin sits above the lower cargo working volume. Payloads exit toward -Z.
 arm_height=4.2 if any(m in c.title for m in ['Surface Base','Heavy Asteroid Tug']) else (3.2 if 'Crew Shuttle' in c.title else (2.7 if 'Hubble Telescope' in c.title else 2.4))
 # One large actuator replaces the four-joint chain; the folded payload position stays identical.
 arm_z=(2.15*SCALE-(.662193*4+.44406*3)+1.32439)/SCALE
 a=c.mount(bay,'DenizCargoArm',(0,arm_height,arm_z) if clamshell else (0,-.25,arm_z),axisrot((1,0,0),-90),tag='CARGO_ARM')
 c.module(a,'ModuleRoboticServoPiston',targetExtension=0,currentExtension=0,servoIsLocked='True',servoMotorIsEngaged='True',traverseVelocity=.48)
 collar=c.attach(a,'top','DenizPayloadRelease','bottom',rot=a['rot'],tag='PAYLOAD_RELEASE');c.module(collar,'ModuleDecouple',stagingEnabled='False',ejectionForce=.1)
 return bay,collar

def avionics(c,p,small=True):
 c.mount(p,'batteryBankMini',(0,.1,.6));c.mount(p,'rtg',(0,.1,-.6));c.mount(p,'commDish',(0,.2,.45));
 for s in [-1,1]:c.mount(p,'solarPanels4',(s*.7,.1,0),axisrot((0,1,0),s*90))
def rcs(c,p,r=.7):
 c.mount(p,'rcsTankRadialLong',(0,0,r))
 for t in [0,90,180,270]:
  a=math.radians(t);c.mount(p,'RCSblock_01_small',(r*math.sin(a),0,r*math.cos(a)),axisrot((0,1,0),t))
def payload(c,collar,mission):
 if mission=='Empty':return
 # Stack attachment is radial at the collar; payload's vertical axis stays parallel to the ship.
 name='DenizPayload_roverBody' if mission=='Mun Rover' else ('DenizOutpostPayload' if mission=='Duna Outpost' else ('DenizStationPayload' if mission=='Station Builder' else 'DenizPayload_probeCoreHex_v2'))
 p=c.attach(collar,'top',name,'cargo',tag='MISSION_PAYLOAD');c.payload_control(p)
 if mission=='Satellite':
  t=c.attach(p,'bottom','fuelTankSmall','top');c.attach(t,'bottom','liquidEngineMini_v2','top')
  c.attach(p,'top','dockingPort2','bottom');avionics(c,p);rcs(c,t)
 elif mission=='Mun Rover':
  t=c.attach(p,'bottom','fuelTankSmall','top');c.attach(t,'bottom','liquidEngineMini_v2','top')
  for x in [-.75,.75]:
   for z in [-.6,.6]:c.mount(t,'roverWheel2',(x,-1.45,z),axisrot((0,1,0),90 if x>0 else -90))
  avionics(c,p);c.mount(p,'sensorThermometer',(.45,.15,0));c.mount(p,'sensorBarometer',(-.45,.15,0));c.mount(p,'science_module',(0,.2,-.4));c.mount(p,'GooExperiment',(0,.2,.4))
 elif mission=='Station Builder':
  t=c.attach(p,'top','sspx-habitation-1875-1','bottom');c.attach(t,'top','dockingPort2','bottom');c.attach(p,'bottom','dockingPort2','bottom');avionics(c,p);rcs(c,t,.94)
 elif mission=='Refuelling':
  t=c.attach(p,'top','DenizMethaloxPod','bottom');c.attach(t,'top','dockingPort2','bottom');c.attach(p,'bottom','dockingPort2','bottom');avionics(c,p);rcs(c,t)
 elif mission=='Duna Outpost':
  t=c.attach(p,'top','sspx-habitation-1875-1','bottom');c.attach(t,'top','dockingPort2','bottom')
  tank=c.attach(p,'bottom','fuelTankSmallFlat','top');c.attach(tank,'bottom','liquidEngineMini_v2','top')
  avionics(c,p)
  for deg in [0,120,240]:
   a=math.radians(deg);c.mount(tank,'landingLeg1-2',(.62*math.sin(a),-.4,.62*math.cos(a)),axisrot((0,1,0),deg))
   c.mount(t,'parachuteRadial',(.98*math.sin(a),.8,.98*math.cos(a)),axisrot((0,1,0),deg))
  c.mount(t,'sensorThermometer',(.94,0,0));c.mount(t,'GooExperiment',(-.94,0,0))
 elif mission=='Crew Rescue':
  t=c.attach(p,'top','DenizPayload_landerCabinSmall','bottom');c.attach(t,'top','dockingPort2','bottom');tank=c.attach(p,'bottom','fuelTankSmall','top');c.attach(tank,'bottom','liquidEngineMini_v2','top');avionics(c,p);rcs(c,t)
 elif mission=='Asteroid Tug':
  t=c.attach(p,'top','fuelTank','bottom');c.attach(t,'top','GrapplingDevice','top');c.attach(p,'bottom','liquidEngineMini_v2','top');avionics(c,p);rcs(c,t)

def large_payload(c,collar,mission):
 if mission=='Empty Large Bay':return
 names={'Hubble Telescope':'DenizLargeTelescope','Expedition Rover':'DenizLargeVehicle','Station Hub':'DenizLargeHabitat','Orbital Fuel Depot':'DenizLargeDepot','Surface Base':'DenizLargeHabitat','Crew Shuttle':'DenizCrewShuttle','Heavy Asteroid Tug':'DenizHeavyTug'}
 p=c.attach(collar,'top',names[mission],'cargo',rot=axisrot((1,0,0),-90) if mission=='Hubble Telescope' else (1,0,0,0),tag='MISSION_PAYLOAD');c.payload_control(p)
 if mission=='Hubble Telescope':
  # A large optical barrel with real SpaceDust scanning, independent power and RCS.
  c.mount(p,'batteryBank',(0,-1.0,-.5));c.mount(p,'rtg',(0,1.0,-.5));c.mount(p,'commDish',(0,1.55,-1));c.mount(p,'rcsTankRadialLong',(0,-1.55,-1))
  for side in [-1,1]:
   c.mount(p,'solarPanels4',(side*1.65,0,-.5),axisrot((0,0,1),side*90))
  for deg in [0,90,180,270]:
   a=math.radians(deg);c.mount(p,'RCSblock_01_small',(1.65*math.sin(a),1.65*math.cos(a),-1),axisrot((0,0,1),deg))
 elif mission=='Expedition Rover':
  body=c.attach(p,'bottom','Rockomax16_BW','top');c.attach(body,'bottom','dockingPortLarge','top');c.module(p,'ModulePartVariants',selectedVariant='Rover')
  for x in [-1.75,1.75]:
   for y in [-1.4,0,1.4]:c.mount(body,'roverWheel1',(x,y,-1.25),axisrot((0,1,0),0 if x>0 else 180))
  for side in [-1,1]:c.mount(p,'domeLight1',(side*.8,.4,-1.65))
  avionics(c,p);c.mount(p,'GooExperiment',(.9,0,1.65));c.mount(p,'science_module',(-.9,0,1.65))
 elif mission=='Station Hub':
  c.attach(p,'top','dockingPortLarge','bottom');c.attach(p,'bottom','dockingPortLarge','bottom')
  for deg in [0,90,180,270]:
   a=math.radians(deg);c.mount(p,'dockingPort2',(1.95*math.sin(a),0,1.95*math.cos(a)),axisrot((0,1,0),deg))
  avionics(c,p);rcs(c,p,2.02)
 elif mission=='Orbital Fuel Depot':
  c.attach(p,'top','dockingPortLarge','bottom');c.attach(p,'bottom','dockingPortLarge','bottom');avionics(c,p);rcs(c,p,1.95)
 elif mission=='Surface Base':
  c.attach(p,'top','dockingPortLarge','bottom');tank=c.attach(p,'bottom','Rockomax16_BW','top');c.attach(tank,'bottom','liquidEngine2-2_v2','top')
  for deg in [0,90,180,270]:
   a=math.radians(deg);c.mount(tank,'landingLeg1-2',(1.25*math.sin(a),-.4,1.25*math.cos(a)),axisrot((0,1,0),deg));c.mount(p,'parachuteRadial',(1.96*math.sin(a),1,1.96*math.cos(a)),axisrot((0,1,0),deg))
  avionics(c,p);c.mount(p,'sensorThermometer',(1.96,.2,0));c.mount(p,'GooExperiment',(-1.96,.2,0))
 elif mission=='Crew Shuttle':
  tank=c.attach(p,'bottom','Rockomax16_BW','top');c.attach(tank,'bottom','liquidEngine2-2_v2','top');c.attach(p,'top','dockingPort2','bottom');avionics(c,p);rcs(c,p,1.4)
  for deg in [0,90,180,270]:
   a=math.radians(deg);c.mount(tank,'landingLeg1-2',(1.25*math.sin(a),-.4,1.25*math.cos(a)),axisrot((0,1,0),deg))
 elif mission=='Heavy Asteroid Tug':
  tank=c.attach(p,'bottom','Rockomax32_BW','top');c.attach(tank,'bottom','liquidEngine2-2_v2','top');c.attach(p,'top','GrapplingDevice','top');avionics(c,p);rcs(c,p,1.4)

MISSIONS=['Empty','Satellite','Mun Rover','Station Builder','Refuelling','Duna Outpost','Crew Rescue','Asteroid Tug']
LARGE_MISSIONS=['Empty Large Bay','Hubble Telescope','Expedition Rover','Station Hub','Orbital Fuel Depot','Surface Base','Crew Shuttle','Heavy Asteroid Tug']
manifest=[]
CRAFTS=[]
def make_mission(i,shell=False,stack=True,tower=True,title_override=None):
 mission=LARGE_MISSIONS[i-1] if shell else MISSIONS[i-1]
 title=f'Lusiados Starship {i:02} - {mission}'+(' - Clamshell' if shell else '')+(' - Tower Launch' if tower else (' - Full Stack' if stack else ' - Ship Only'))
 c=Craft(title_override or title,2800000000+i*10000+(100000 if shell else 0)+(2500 if tower else (0 if stack else 5000)))
 bay,collar=foundation(c,stack,tower,shell);large_payload(c,collar,mission) if shell else payload(c,collar,mission)
 desc=f'{mission} mission. Permanent two-seat flight cabin. Keys 1 cargo, 2 cargo lock, 3/G legs, 4 leg lock, 5 entry flaps, 6 landing engines, 7 flight engines, 8 cargo arm, 9 payload release, 0 equipment. Space launches booster, next Space separates and starts ship. Throttle remains manual. Open bay then extend arm fully before release. Read the Mission Pack guide for mission-specific operation.'
 return c,desc
for i,m in enumerate(MISSIONS,1):
 title=f'Lusiados Starship {i:02} - {m}'
 for shell in [False,True]:
  mission=LARGE_MISSIONS[i-1] if shell else m
  actual_title=f'Lusiados Starship {i:02} - {mission}' if shell else title
  for stack,tower in VARIANTS:
   c,desc=make_mission(i,shell,stack,tower)
   CRAFTS.append(c)
   folder=OUT/('Tower Launch' if tower else ('Full Stacks' if stack else 'Ships Only'));info=c.write(desc,folder);manifest.append(info)
   if WRITE_FILES and not DRY_RUN:shutil.copy2(info['file'],KSP/'Ships/VAB'/Path(info['file']).name)
c=Craft('Lusiados Super Heavy - Booster Practice',2800990000)
b=c.part('DenizSuperHeavy',(0,26*SCALE,0),tag='BOOSTER_TANK');c.actions(b,'DenizStarshipControls',CONTROL);c.module(b,'ModuleDecouple',stagingEnabled='False')
cl=c.attach(b,'node_bottom','SEP_26_BOOSTER_CLUSTER','node_top_Cluster',tag='BOOSTER_ENGINES');cl['stage']=0;c.module(cl,'ModuleSEPEngineSwitch',selectedIndex=0)
for j in range(1,4):c.attach(b,'node_gridfin_'+str(j),'SEP_26_BOOSTER_GRIDFIN','node_gridfin')
for j in range(6):
 t=math.radians(j*60);c.mount(b,'DenizStarshipLegBooster',(2.75*math.sin(t),-21,2.75*math.cos(t)),axisrot((0,1,0),j*60),tag='BOOSTER_LEG')
info=c.write('Booster-only landing practice. Start with 5 percent methane and oxidizer for infinite-fuel practice. 3/G legs, 4 leg locks, 6 centre engines, 7 full engine cluster. Throttle manual. See the Mission Pack guide.',OUT/'Practice')
manifest.append(info)
if WRITE_FILES:
 if not DRY_RUN:shutil.copy2(info['file'],KSP/'Ships/VAB'/Path(info['file']).name)
 (OUT/'build-manifest.json').write_text(json.dumps(manifest,indent=2))
 print(json.dumps([{k:v for k,v in m.items() if k!='file'} for m in manifest],indent=2))

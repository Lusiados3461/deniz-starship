from pathlib import Path
import sys, math
from config import KSP,parts_cache
sys.path.insert(0,str(Path(__file__).resolve().parent))
import mu
mu.Mu.read_vector=lambda self:self.read_float(3)
mu.Mu.read_quaternion=lambda self:(lambda q:(q[3],q[0],q[1],q[2]))(self.read_float(4))
def add(a,b):return tuple(x+y for x,y in zip(a,b))
def sub(a,b):return tuple(x-y for x,y in zip(a,b))
def scale(a,s):return tuple(x*s for x in a)
def dot(a,b):return sum(x*y for x,y in zip(a,b))
def cross(a,b):return(a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def unit(a):return scale(a,1/math.sqrt(dot(a,a)))
def qmul(a,b):
 w,x,y,z=a;W,X,Y,Z=b
 return(w*W-x*X-y*Y-z*Z,w*X+x*W+y*Z-z*Y,w*Y-x*Z+y*W+z*X,w*Z+x*Y-y*X+z*W)
def rotate(q,v):return qmul(qmul(q,(0,*v)),(q[0],-q[1],-q[2],-q[3]))[1:]
def fromto(a,b):
 a,b=unit(a),unit(b);d=dot(a,b)
 if d<-.999999:
  axis=unit(cross(a,(1,0,0) if abs(a[0])<.9 else(0,1,0)));return(0,*axis)
 return unit((1+d,*cross(a,b)))
def axisrot(axis,deg):
 t=math.radians(deg/2);return(math.cos(t),*scale(unit(axis),math.sin(t)))

CACHE=parts_cache()
SOURCES={}
for f in (KSP/'GameData').rglob('*.cfg'):
 try:
  import re
  names=re.findall(r'^\s*name\s*=\s*([^\s]+)',f.read_text(errors='replace'),re.M)
  if names and names[0] in CACHE:SOURCES.setdefault(names[0],f)
 except OSError:pass
def model_defs(name):
 c=CACHE[name];models=c.nodes('MODEL')
 if models:return models
 from config import Node
 f=SOURCES[name];m=Node('MODEL');m.set('model',str((f.parent/c.get('mesh','model.mu')).relative_to(KSP/'GameData')).removesuffix('.mu'));return[m]
def model(name):
 c=CACHE[name];nodes={};trans={};bounds=[]
 models=model_defs(name);rescale=float(c.get('rescaleFactor','1'))
 for mdl in models:
  ms=tuple(float(v)*rescale for v in mdl.get('scale','1,1,1').split(','));mp=tuple(float(v) for v in mdl.get('position','0,0,0').split(','))
  path=KSP/'GameData'/(mdl.get('model')+'.mu')
  if not path.exists():path=next(path.parent.glob('*.mu'))
  m=mu.Mu().read(str(path))
  def visit(o,pos=mp,q=(1,0,0,0),s=ms,root=False):
   t=o.transform
   if not root:
    pos=add(pos,rotate(q,tuple(t.localPosition[i]*s[i] for i in range(3))));q=qmul(q,t.localRotation);s=tuple(s[i]*t.localScale[i] for i in range(3))
   trans[t.name]=(pos,unit(rotate(q,(0,0,1))))
   if hasattr(o,'shared_mesh') and o.shared_mesh:
    for v in o.shared_mesh.verts:bounds.append(add(pos,rotate(q,tuple(v[i]*s[i] for i in range(3)))))
   for ch in o.children:visit(ch,pos,q,s)
  visit(m.obj,root=True)
 for n in c.nodes('NODE'):
  if n.get('transform') in trans:nodes[n.get('name')]=trans[n.get('transform')]
 for k,v in c.values:
  if k.startswith('node_stack_') or k=='node_attach':
   vs=[float(x) for x in v.split(',')];nodes[k.replace('node_stack_','').replace('node_attach','srfAttach')]=(scale(vs[:3],rescale),unit(vs[3:6]))
 return nodes,trans,bounds
if __name__=='__main__':
 for n in sys.argv[1:]:
  nodes,trans,b=model(n);print(n,nodes);print('bounds',tuple(min(v[i] for v in b) for i in range(3)),tuple(max(v[i] for v in b) for i in range(3)))
  print({k:v for k,v in trans.items() if any(s in k.lower() for s in ['hatch','door','ladder','cargo'])})

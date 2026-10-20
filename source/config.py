from pathlib import Path
import re

class Node:
    def __init__(self,name='ROOT',values=None,children=None):
        self.name=name;self.values=values or [];self.children=children or []
    def get(self,k,default=None):
        return next((v for n,v in self.values if n==k),default)
    def all(self,k):return [v for n,v in self.values if n==k]
    def nodes(self,n):return [c for c in self.children if c.name==n]
    def set(self,k,v):
        self.values=[(n,x) for n,x in self.values if n!=k]+[(k,str(v))]
    def dump(self,indent=''):
        out=[]
        if self.name!='ROOT':out=[indent+self.name,indent+'{'];indent+='\t'
        out += [indent+k+' = '+v for k,v in self.values]
        out += [c.dump(indent) for c in self.children]
        if self.name!='ROOT':out.append(indent[:-1]+'}')
        return '\n'.join(out)

def parse(text):
    root=Node();stack=[root];pending=None
    for l in text.replace('\ufeff','').splitlines():
        l=l.split('//',1)[0].strip()
        if not l:continue
        if l=='{':
            n=Node(pending);stack[-1].children.append(n);stack.append(n);pending=None
        elif l=='}':stack.pop()
        elif '=' in l:
            k,v=l.split('=',1);stack[-1].values.append((k.strip(),v.strip()))
        elif l.endswith('{}'):stack[-1].children.append(Node(l[:-2].strip()))
        else:pending=l
    assert len(stack)==1
    return root

import os
KSP=Path(os.environ.get('KSP_ROOT', Path.home() / 'Library/Application Support/Steam/steamapps/common/Kerbal Space Program'))
def parts_cache():
    cache=parse((KSP/'GameData/ModuleManager.ConfigCache').read_text())
    return {p.get('name'):p for url in cache.nodes('UrlConfig') if '/Test/' not in url.get('parentUrl','') for p in url.nodes('PART')}

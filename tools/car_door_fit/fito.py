import numpy as np, json, sys
from model import opening as close
from cmp import prof
R=prof('ref_open.wav',loudest=True)
space={'handleLead':(0.06,0.14),'handleDb':(-12,6),'skinT60':(0.05,0.35),'crack':(0.1,2),'clunkDb':(-6,4),'boomDb':(-16,2),'peelDb':(-30,-4),'detentAt':(0.3,0.4),'detentDb':(-10,8),'detentBright':(0.2,3),'skinTop':(1200,4000),'skinExp':(-0.5,0.8),'thumpHz':(60,140),'thumpDb':(-20,4),'skinBottom':(0,450)}
W=np.array([0.5,1,1,0.6])  # weight per window
def cost(P):
    B=prof(close(P=P),loudest=True); d=(B-R)
    
    return float(np.sqrt(np.mean((d*W)**2)))
rng=np.random.default_rng(1)
best=None
import json as _j
try:
  best=(cost(_j.load(open('best_open.json'))|{'skinTop':4000,'skinExp':0.5,'thumpHz':90,'thumpDb':-20,'skinBottom':0}),_j.load(open('best_open.json'))|{'skinTop':4000,'skinExp':0.5,'thumpHz':90,'thumpDb':-20,'skinBottom':0})
except Exception as ex: print(ex)
for it in range(int(sys.argv[1])):
    if best is None or rng.random()<0.3:
        P={k:float(rng.uniform(*v)) for k,v in space.items()}
    else:
        P=dict(best[1])
        for k in rng.choice(list(space),3,replace=False):
            lo,hi=space[k]; P[k]=float(np.clip(P[k]+rng.normal(0,(hi-lo)*0.12),lo,hi))
    c=cost(P)
    if best is None or c<best[0]: best=(c,P); print(it,round(c,2),flush=True)
json.dump(best[1],open('best_open.json','w'),indent=1); print(best)

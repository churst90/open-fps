import numpy as np, json, sys
from model import close
from cmp import prof
R=prof('ref_close.wav')
space={'preDb':(-20,4),'airHz':(35,90),'crack':(0.2,3),'boomAt':(0.0,0.05),'pulse':(0.03,0.12),'boomDb':(-8,6),
       'skinT60':(0.1,0.6),'skinTilt':(-0.6,0.4),'latchDb':(-14,2),'settleDb':(-6,8),'settleBright':(0.1,2),'boomT60':(0.25,0.7)}
W=np.array([0.5,1,1,0.6])  # weight per window
def cost(P):
    B=prof(close(P=P)); d=(B-R)
    d[:,0]=np.clip(d[:,0],-12,12)
    return float(np.sqrt(np.mean((d*W)**2)))
rng=np.random.default_rng(1)
best=None
for it in range(int(sys.argv[1])):
    if best is None or rng.random()<0.3:
        P={k:float(rng.uniform(*v)) for k,v in space.items()}
    else:
        P=dict(best[1])
        for k in rng.choice(list(space),3,replace=False):
            lo,hi=space[k]; P[k]=float(np.clip(P[k]+rng.normal(0,(hi-lo)*0.12),lo,hi))
    c=cost(P)
    if best is None or c<best[0]: best=(c,P); print(it,round(c,2),flush=True)
json.dump(best[1],open('best.json','w'),indent=1); print(best)

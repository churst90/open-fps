import numpy as np, json, sys
import model3
from cmp import prof
from tonal import tonality
which=sys.argv[2]
ref='ref_close.wav' if which=='close' else 'ref_open.wav'; L=which=='open'
R=prof(ref,loudest=L); RT=np.array(tonality(ref,loudest=L))
fn=model3.close if which=='close' else model3.opening
if which=='close':
    space={**{f'hs{i}':(-24,6) for i in range(9)},'h0':(-16,0),'h1':(-12,2),'h3':(-20,-2),'skinT60':(0.03,0.4),'skinExp':(0,1.2),'t60max':(0.1,0.6),
           'boomAt':(0,0.06),'boomRise':(0.01,0.08),'boomT60':(0.2,0.8),'b0':(-20,6),'b1':(-20,6),'b2':(-30,0),'settleDb':(-30,-4),'settleTilt':(0,6)}
else:
    space={**{f'us{i}':(-30,6) for i in range(9)},**{f'ks{i}':(-20,10) for i in range(9)},**{f'ds{i}':(-20,10) for i in range(9)},
           'handleLead':(0.06,0.14),'handleDb':(-30,0),'clunkT60':(0.02,0.3),'detentAt':(0.3,0.4),'detentDb':(-12,6),'detentT60':(0.01,0.15)}
W=np.array([0.5,1,1,0.6])
def cost(P):
    x=fn(P=P); B=prof(x,loudest=L); d=B-R
    if which=='close': d[:,0]=0
    t=np.array(tonality(x,loudest=L)); tp=np.maximum(0,t-RT-2)   # only penalise MORE tonal than the recording
    return float(np.sqrt(np.mean((d*W)**2))+0.5*tp.sum())
rng=np.random.default_rng(2); best=None
for it in range(int(sys.argv[1])):
    if best is None or rng.random()<0.15: P={k:float(rng.uniform(*v)) for k,v in space.items()}
    else:
        P=dict(best[1])
        for k in rng.choice(list(space),4,replace=False):
            lo,hi=space[k]; P[k]=float(np.clip(P[k]+rng.normal(0,(hi-lo)*0.1),lo,hi))
    c=cost(P)
    if best is None or c<best[0]: best=(c,P)
json.dump(best[1],open(f'm3_{which}.json','w'),indent=1); print(which,'cost',round(best[0],2))

import numpy as np, soundfile as sf, sys
from scipy.signal import butter, sosfilt, lfilter
sr=48000; C=343.0
def res(x,f,t60):
    r=np.exp(-6.9/(t60*sr)); w=2*np.pi*f/sr
    return lfilter([1-r],[1,-2*r*np.cos(w),r*r],x)
def lp(x,f,o=2): return sosfilt(butter(o,f,fs=sr,output='sos'),x)
def hp(x,f,o=2): return sosfilt(butter(o,f,'high',fs=sr,output='sos'),x)
def bp(x,a,b,o=2): return sosfilt(butter(o,[a,b],'band',fs=sr,output='sos'),x)
def db(v): return 10**(v/20)
def norm(x): return x/(np.abs(x).max()+1e-12)
def cabin_modes(L,W,H):
    return [C/2*np.sqrt((a/L)**2+(b/W)**2+(c/H)**2) for a,b,c in [(1,0,0),(0,1,0),(0,0,1),(2,0,0),(1,1,0),(1,0,1)]]
SKIN=[133,208,300,400,533,683,917,1175,1500,1860,2400,2900,3700]
def hit(n,at,rng,bright=1.0,dur=0.003):
    e=np.zeros(n); k=int(dur*sr); a=int(at*sr)
    e[a:a+k]=rng.standard_normal(k)*np.exp(-np.arange(k)/(0.0008*sr)); return e
def close(seed=3,L=2.5,W=1.4,H=1.1,P=None):
    P=P or {}; g=lambda k,d: P.get(k,d)
    rng=np.random.default_rng(seed)
    T=1.4; n=int(T*sr); t0=0.25; out=np.zeros(n)
    # 1. air ahead of the door
    # The flow rises as the gap narrows and stops when the seal first closes it off, a few tens of
    # milliseconds before the door seats.
    pre=g('pre',0.18); m=int(pre*sr); end=int(g('airEnd',0.0)*sr); ph=np.arange(m)/m
    env=np.sin(np.pi*ph**g('airSkew',1.0))**2 if end>0 else ph**2
    air=norm(lp(hp(rng.standard_normal(m),20),g('airHz',55),4))*env
    a0=int(t0*sr)-end-m
    out[a0:a0+m]+=air*db(g('preDb',-14))
    # 2. the contact cluster: first touch, secondary catch, seat + primary catch, rebound
    ex=np.zeros(n)
    for dt,lv in g('hits',[(0,-8),(0.014,-4),(0.030,0),(0.075,-10)]):
        ex+=hit(n,t0+dt,rng)*db(lv)
    skin=np.zeros(n)
    for f in SKIN:
        f*=rng.uniform(.96,1.04)
        skin+=res(ex,f,g('skinT60',0.25)*(500/f)**0.5)*(f/500)**g('skinTilt',-0.1)
    skin=norm(skin)+g('crack',0.6)*norm(bp(ex,2000,10000))
    out+=norm(skin)*db(g('skinDb',0))
    # latch claw tones on the two catches
    lat=np.zeros(n)
    for dt in (0.014,0.030):
        e=hit(n,t0+dt,rng,dur=0.0015); lat+=res(e,1250,0.06)+0.8*res(e,2600,0.04)+0.5*res(e,4200,0.03)
    out+=norm(lat)*db(g('latchDb',-6))
    # 3. the cabin: the seal's compression pushes a volume into it over tens of ms
    k=int(g('pulse',0.06)*sr); pulse=np.zeros(n); a=int((t0+g('boomAt',0.01))*sr); pulse[a:a+k]=np.sin(np.pi*np.arange(k)/k)
    boom=np.zeros(n)
    for i,f in enumerate(cabin_modes(L,W,H)):
        boom+=res(pulse,f,g('boomT60',0.45)*(1-0.1*i))*(1.0 if i==0 else 0.5)
    out+=norm(boom)*db(g('boomDb',2))
    # 4. settling: the body rocks and loose things knock
    tt=g('settleAt',0.12)
    for lv in [-12,-18,-22,-28]:
        e=hit(n,t0+tt,rng,dur=0.004)
        s=norm(res(e,rng.uniform(55,90),0.12))+0.7*norm(res(e,rng.uniform(160,300),0.07))+g('settleBright',0.3)*norm(bp(e,1500,6000))
        out+=norm(s)*db(lv+g('settleDb',0)); tt+=rng.uniform(0.04,0.12)
    return norm(out)*0.7
if __name__=='__main__':
    sf.write(sys.argv[1] if len(sys.argv)>1 else 'synth_close.wav',close(),sr)

def opening(seed=5,L=2.5,W=1.4,H=1.1,P=None):
    P=P or {}; g=lambda k,d: P.get(k,d)
    rng=np.random.default_rng(seed)
    n=int(1.2*sr); t0=0.43; out=np.zeros(n)
    # 1. the handle: its pivot, and the rod working the latch, a few small taps
    tt=t0-g('handleLead',0.12)
    for lv in [-18,-14,-10,-16]:
        e=hit(n,tt,rng,dur=0.0015); s=res(e,rng.uniform(500,700),0.04)+res(e,rng.uniform(1100,1400),0.03)+0.5*res(e,rng.uniform(2800,3600),0.02)
        out+=norm(s)*db(lv+g('handleDb',-18)); tt+=rng.uniform(0.015,0.035)
    # 2. the latch lets go: the striker leaves the claw, the compressed seal throws the door out
    ex=hit(n,t0,rng)+0.5*hit(n,t0+0.008,rng)
    skin=np.zeros(n)
    for f in SKIN:
        if f>g('skinTop',4000) or f<g('skinBottom',0): continue
        f*=rng.uniform(.96,1.04); skin+=res(ex,f,g('skinT60',0.3)*(500/f)**g('skinExp',0.5))*(f/500)**-0.1
    out+=norm(res(ex,g('thumpHz',90),0.08))*db(g('thumpDb',-60))
    out+=norm(norm(skin)+g('crack',0.8)*norm(bp(ex,1500,6000)))*db(g('clunkDb',0))
    # the cabin, answering the pressure let out (smaller than on closing)
    k=int(0.03*sr); pulse=np.zeros(n); a=int(t0*sr); pulse[a:a+k]=-np.sin(np.pi*np.arange(k)/k)
    boom=sum(res(pulse,f,0.4*(1-0.1*i))*(1.0 if i==0 else 0.5) for i,f in enumerate(cabin_modes(L,W,H)))
    out+=norm(boom)*db(g('boomDb',-4))
    # the door moving and the seal peeling: a low soft tail
    m=int(0.25*sr); tail=norm(lp(hp(rng.standard_normal(m),30),160,4))*np.exp(-np.arange(m)/(0.08*sr))
    out[a+int(0.01*sr):a+int(0.01*sr)+m]+=tail*db(g('peelDb',-16))
    # 3. the check strap: the arm's roller drops into its detent as the door swings
    e=hit(n,t0+g('detentAt',0.35),rng,dur=0.002)
    d=res(e,350,0.08)+res(e,680,0.06)+res(e,820,0.05)+g('detentBright',1.2)*norm(bp(e,2000,12000))
    out+=norm(d)*db(g('detentDb',-4))
    return norm(out)*0.7

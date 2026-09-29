# Car door from band-limited noise: every part of it is noise shaped in time and frequency, nothing rings.
import numpy as np
from scipy.signal import butter, sosfilt
sr=48000
EDGES=[30,60,120,250,500,1000,2000,4000,8000,16000]
CENT=[np.sqrt(a*b) for a,b in zip(EDGES[:-1],EDGES[1:])]
def db(v): return 10**(v/20)
def norm(x): return x/(np.abs(x).max()+1e-12)
def bandnoise(n,rng):
    w=rng.standard_normal(n); out=[]
    for a,b in zip(EDGES[:-1],EDGES[1:]):
        out.append(sosfilt(butter(3,[a,min(b,23000)],'band',fs=sr,output='sos'),w))
    return out
def env_hit(n,at,t60,attack=0.0008):
    t=np.arange(n)/sr-at; e=np.zeros(n); on=t>=0
    e[on]=np.minimum(1,t[on]/attack)*np.exp(-6.9*t[on]/t60); return e
def env_swell(n,at,rise,t60):
    t=np.arange(n)/sr-at; e=np.zeros(n); on=t>=0
    r=np.clip(t[on]/rise,0,1); e[on]=np.sin(0.5*np.pi*r)**2*np.exp(-6.9*np.maximum(0,t[on]-rise)/t60); return e
def close(seed=3,P=None):
    P=P or {}; g=lambda k,d: P.get(k,d)
    rng=np.random.default_rng(seed); n=int(1.4*sr); t0=0.25
    B=bandnoise(n,rng); out=np.zeros(n)
    shape=[g(f'hs{i}',0) for i in range(9)]            # the slam's spectrum, dB per octave band
    for dt,lv in [(0,g('h0',-8)),(0.014,g('h1',-4)),(0.030,0),(0.075,g('h3',-10))]:
        for i in range(9):
            t60=min(g('t60max',0.4),g('skinT60',0.2)*(500/CENT[i])**g('skinExp',0.5))
            out+=B[i]*env_hit(n,t0+dt,t60)*db(lv+shape[i])
    # the cabin's boom: the low bands swelling as the seal pushes air in, then dying away
    for i,k in [(0,'b0'),(1,'b1'),(2,'b2')]:
        out+=B[i]*env_swell(n,t0+g('boomAt',0.02),g('boomRise',0.04),g('boomT60',0.45))*db(g(k,-6))
    # air ahead of the door
    m=int(g('pre',0.18)*sr); end=int(g('airEnd',0.03)*sr); ph=np.arange(m)/m
    a0=int(t0*sr)-end-m; env=np.zeros(n); env[a0:a0+m]=np.sin(np.pi*ph**0.5)**2
    out+=(B[0]+g('air1',0.3)*B[1])*env*db(g('preDb',-60))
    # settling: small hits, low and dull
    tt=g('settleAt',0.12)
    for lv in [0,-6,-10,-16]:
        for i in range(9):
            out+=B[i]*env_hit(n,t0+tt,min(0.15,0.08*(500/CENT[i])**0.5))*db(lv+g('settleDb',-14)+shape[i]-g('settleTilt',3)*i)
        tt+=rng.uniform(0.04,0.12)
    return norm(out)*0.7
def opening(seed=5,P=None):
    P=P or {}; g=lambda k,d: P.get(k,d)
    rng=np.random.default_rng(seed); n=int(1.2*sr); t0=0.43
    B=bandnoise(n,rng); out=np.zeros(n)
    tt=t0-g('handleLead',0.1)
    for lv in [-4,0,4,-2]:          # handle and rod: small, mid-bright ticks
        for i in range(9):
            out+=B[i]*env_hit(n,tt,0.03)*db(lv+g('handleDb',-18)+g(f'ks{i}',0))
        tt+=rng.uniform(0.015,0.035)
    shape=[g(f'us{i}',0) for i in range(9)]
    for dt,lv in [(0,0),(0.008,-6)]:  # the latch letting go
        for i in range(9):
            out+=B[i]*env_hit(n,t0+dt,min(0.3,g('clunkT60',0.1)*(500/CENT[i])**0.4))*db(lv+shape[i])
    for i in range(9):                # the check strap's detent
        out+=B[i]*env_hit(n,t0+g('detentAt',0.34),min(0.2,g('detentT60',0.06)*(500/CENT[i])**0.3))*db(g('detentDb',-2)+g(f'ds{i}',0))
    return norm(out)*0.7

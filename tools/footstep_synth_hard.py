# Hard-floor footsteps the published way (Turchet 2016; Avanzini and Rocchesso 2001):
# a Hunt-Crossley contact between heel and floor gives the force; the force radiates as the low
# thump, strikes a bank of short damped modes (redrawn every step), and drives a brief grit burst.
import numpy as np
from scipy.signal import butter, sosfilt, lfilter
SR=44100; N=int(0.35*SR)
def hc_force(m, v0, k, alpha, mu, dt=1.0/SR/4):
    """Contact force of a mass m arriving at v0 on a rigid floor: f = k x^a (1 + mu xdot), until it
    leaves. Integrated at 4x the sample rate, returned at the sample rate."""
    x, v, f = 0.0, v0, []
    for _ in range(int(0.05/dt)):
        F = k*x**alpha*(1+mu*v) if x>0 else 0.0
        F = max(F, 0.0)
        a = -F/m; v += a*dt; x += v*dt; f.append(F)
        if x<=0 and v<0 and len(f)>4: break
    f=np.array(f); n=len(f)//4
    return f[:n*4].reshape(n,4).mean(1)
def modes(force, rng, P, n=40):
    """Short damped modes struck together by the same force: centres spread over the strike's band,
    each redrawn every step, decaying fast enough to be a knock and never a note."""
    lo,hi=P['mode_lo'],P['mode_hi']
    f=np.exp(rng.uniform(np.log(lo),np.log(hi),n))
    out=np.zeros(N)
    x=np.zeros(N); x[:len(force)]=force
    for fi in f:
        tau=min(P.get('tau_max',0.01),P['tau0']*(1000/fi)**P['tau_exp']*np.exp(rng.normal(0,0.2)))
        r=np.exp(-1/(tau*SR)); w=2*np.pi*fi/SR
        g=10**((P['mode_tilt']*np.log2(fi/1000))/20)*10**(rng.normal(0,3)/20)
        out+=g*lfilter([1-r],[1,-2*r*np.cos(w),r*r],x)
    return out
def contact(rng, P, pre, v0):
    """One foot contact. A heel does not land as one blow: the edge touches, the heel flattens, the
    sole begins to roll; a few sub-contacts over a few milliseconds, each its own Hunt-Crossley pulse."""
    y=np.zeros(N)
    subs=1+rng.poisson(P.get(pre+'subs',1.5)); t=0.0
    for i in range(subs):
        k=P[pre+'k']*np.exp(rng.normal(0,0.3))
        f=hc_force(P['mass'], v0*(1 if i==0 else rng.uniform(0.3,0.9)), k, P['alpha'], P['mu'])
        a=int(t*SR); n=min(len(f),N-a)
        if n>0: y[a:a+n]+=f[:n]
        t+=rng.uniform(0.5,1.5)*P.get(pre+'spread',0.004)/max(1,subs-1)
    y/= (y.max()+1e-12)
    f=y[:int(0.06*SR)]
    out=np.zeros(N)
    # the force itself: the weight arriving (the band under ~500 Hz)
    out+=P[pre+'thump']*y
    # the leg's mass settling onto the heel pad: one low resonance, damped hard, so it is weight
    w=2*np.pi*P.get('leg_hz',120)*np.exp(rng.normal(0,0.08)); z=P.get('leg_zeta',0.4); r=np.exp(-z*w/SR)
    out+=P.get(pre+'leg',0.0)*lfilter([1-r],[1,-2*r*np.cos(w*np.sqrt(max(1e-3,1-z*z))/1),r*r],y)*8
    out+=P[pre+'body']*modes(f,rng,P)
    # grit at the contact, in proportion to the force, high band only
    g=sosfilt(butter(2,P['grit_hp'],'high',fs=SR,output='sos'),rng.standard_normal(N))*y
    out+=P[pre+'grit']*g/ (np.abs(g).max()+1e-12)
    return out
def step(rng, P, delay_ms=(60,85,120)):
    heel=contact(rng,P,'heel_',P['v0']*np.exp(rng.normal(0,0.12)))
    out=np.zeros(N); a=int(0.01*SR); out[a:]+=heel[:N-a]
    d=np.clip(rng.normal(delay_ms[1],(delay_ms[2]-delay_ms[0])/2.56),30,150)/1000
    b=int((0.01+d)*SR); toe=contact(rng,P,'toe_',P['v0']*0.6*np.exp(rng.normal(0,0.15)))
    out[b:]+=10**(P['toe_db']/20)*toe[:N-b]
    out[-int(0.01*SR):]*=np.linspace(1,0,int(0.01*SR))
    return out
DEFAULT=dict(mass=0.4,v0=0.8,alpha=2.0,mu=0.3,heel_k=4e7,toe_k=5e6,heel_thump=1.0,toe_thump=1.0,heel_body=1.0,toe_body=0.5,
             heel_grit=0.3,toe_grit=0.1,grit_hp=2500,mode_lo=250,mode_hi=6000,tau0=0.006,tau_exp=0.5,mode_tilt=-2.0,toe_db=-9)

import numpy as np
from scipy.signal import butter, sosfilt
SR=44100; N=int(0.45*SR); HOP=int(0.002*SR); F=N//HOP
EDGES=[40*2**(k/3) for k in range(0,27)]            # 40 Hz .. ~16 kHz
BANDS=[(a,min(b,0.45*SR)) for a,b in zip(EDGES[:-1],EDGES[1:])]
SOS=[butter(2,[a,b],'band',fs=SR,output='sos') for a,b in BANDS]
FINE_FROM=500.0                                     # crunch texture in bands above this
def envelopes(x):
    L=np.empty((len(BANDS),F))
    for i,sos in enumerate(SOS):
        y=sosfilt(sos,x)[:F*HOP].reshape(F,HOP)
        L[i]=0.5*np.log(np.mean(y*y,1)+1e-14)
    return np.maximum(L, L.max()-5.76)      # nothing below 50 dB under the step's own peak
def smooth(L,w=5):
    k=np.ones(w)/w; return np.array([np.convolve(r,k,'same') for r in L])
def fit(steps,K=8):
    Ls=np.array([envelopes(s) for s in steps]); C=np.array([smooth(L) for L in Ls]); R=Ls-C
    X=C.reshape(len(C),-1); mu=X.mean(0); U,sv,Vt=np.linalg.svd(X-mu,full_matrices=False)
    comps=Vt[:K]; scale=sv[:K]/np.sqrt(len(X))
    # fine texture per band: its spread and how fast it changes, where the step is loud
    act=C>(C.max(axis=2,keepdims=True)-4.0)
    sig=np.array([R[:,b][act[:,b]].std() if act[:,b].any() else 0 for b in range(len(BANDS))])
    rho=np.array([np.corrcoef(R[:,b,1:][act[:,b,1:]], R[:,b,:-1][act[:,b,1:]])[0,1] if act[:,b].sum()>10 else 0 for b in range(len(BANDS))])
    return dict(mu=mu,comps=comps,scale=scale,sig=sig,rho=np.nan_to_num(rho))
def synth(m,rng):
    z=rng.standard_normal(len(m['scale']))
    C=(m['mu']+(z*m['scale'])@m['comps']).reshape(len(BANDS),F)
    fine=np.zeros_like(C)
    for b,(lo,hi) in enumerate(BANDS):
        if lo<FINE_FROM: continue
        r=m['rho'][b]; e=rng.standard_normal(F)*m['sig'][b]*np.sqrt(max(1e-3,1-r*r)); v=0.0
        for f in range(F): v=r*v+e[f]; fine[b,f]=v
    L=C+fine
    t=(np.arange(N)+0.5)/HOP-0.5
    out=np.zeros(N); w=rng.standard_normal(N)
    for b,sos in enumerate(SOS):
        y=sosfilt(sos,w); y/= (y.std()+1e-12)
        env=np.exp(np.interp(t,np.arange(F),L[b]))
        out+=y*env
    out[:int(0.002*SR)]*=np.linspace(0,1,int(0.002*SR))
    out[-int(0.02*SR):]*=np.linspace(1,0,int(0.02*SR))
    return out

# ── Second method: borrow the rough shape of real steps, make the sound new every time ──────────
def fit2(steps):
    m=fit(steps)
    Ls=np.array([envelopes(s) for s in steps]); m['shapes']=np.array([smooth(L) for L in Ls]).astype(np.float32)
    return m
def synth2(m,rng,warp=0.08,level_db=1.5,tilt_db=2.0):
    S=m['shapes']; i,j=rng.integers(len(S),size=2); w=rng.beta(0.5,0.5)
    C=w*S[i]+(1-w)*S[j]
    # a little faster or slower
    k=1+rng.uniform(-warp,warp); src=np.clip(np.arange(F)/k,0,F-1)
    C=np.array([np.interp(src,np.arange(F),r) for r in C])
    # a little louder or softer, a little brighter or darker
    C+=rng.normal(0,level_db)/8.686
    C+=(np.linspace(-1,1,len(BANDS))*rng.normal(0,tilt_db)/8.686)[:,None]
    fine=np.zeros_like(C)
    for b,(lo,hi) in enumerate(BANDS):
        if lo<FINE_FROM: continue
        r=m['rho'][b]; e=rng.standard_normal(F)*m['sig'][b]*np.sqrt(max(1e-3,1-r*r)); v=0.0
        for f in range(F): v=r*v+e[f]; fine[b,f]=v
    L=C+fine
    t=(np.arange(N)+0.5)/HOP-0.5
    out=np.zeros(N); wn=rng.standard_normal(N)
    for b,sos in enumerate(SOS):
        y=sosfilt(sos,wn); y/=(y.std()+1e-12)
        out+=y*np.exp(np.interp(t,np.arange(F),L[b]))
    out[:int(0.002*SR)]*=np.linspace(0,1,int(0.002*SR)); out[-int(0.02*SR):]*=np.linspace(1,0,int(0.02*SR))
    return out

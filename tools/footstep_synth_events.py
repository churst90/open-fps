# Footsteps from their events: coherent contacts (and, on loose ground, a shower of grains), each
# octave bands of fresh noise starting on the same sample and dying away at the measured rate.
import numpy as np, json
from scipy.signal import butter, sosfilt
SR=44100; N=int(0.5*SR)
EDGES=[44,88,177,355,710,1420,2840,5680,11360,20000]
SOS=[butter(3,[a,min(b,0.45*SR)],'band',fs=SR,output='sos') for a,b in zip(EDGES[:-1],EDGES[1:])]
C=json.load(open('contacts.json'))
def db(v): return 10**(np.asarray(v)/20)
CENT=np.sqrt(np.array(EDGES[:-1])*np.array(EDGES[1:]))
def click(shape_db, rng, n=512):
    """The strike itself: a minimum-phase impulse with the contact's spectrum, so every frequency
    arrives at once, front-loaded, as a real blow does."""
    f=np.fft.rfftfreq(2*n,1/SR); mag_db=np.interp(np.log(np.maximum(f,20)),np.log(CENT),shape_db+rng.normal(0,1.5,len(shape_db)))
    logmag=np.log(10**(mag_db/20)+1e-9)
    cep=np.fft.irfft(logmag); cep[1:n]*=2; cep[n+1:]=0          # fold to minimum phase
    h=np.fft.irfft(np.exp(np.fft.rfft(cep)))[:n]
    return h/np.abs(h).max()
def blow(shape_db, rng, pulse_ms):
    """The click struck by a real heel: the force builds and falls over pulse_ms (a half-sine),
    because a rubber heel gives before the floor does."""
    h=click(shape_db,rng); k=max(1,int(pulse_ms/1000*SR))
    if k>1: h=np.convolve(h,np.sin(np.pi*(np.arange(k)+0.5)/k))[:len(h)]
    return h/np.abs(h).max()
def contact(out, bands, at, level_db, shape_db, t60, rng, attack=0.0004, click_db=None, pulse_ms=0.8):
    a=int(at*SR)
    if a>=N: return
    if click_db is not None:
        h=blow(np.asarray(shape_db),rng,pulse_ms); k=min(len(h),N-a)
        ref=max(db(level_db+np.max(shape_db)),1e-9)
        out[a:a+k]+=h[:k]*ref*db(click_db)
    t=np.arange(N-a)/SR
    for b in range(len(SOS)):
        env=np.minimum(1,t/attack)*np.exp(-6.9*t/max(t60[b],0.003))
        out[a:]+=bands[b][a:]*env*db(level_db+shape_db[b])
def bandnoise(rng):
    w=rng.standard_normal(N+int(0.1*SR)); return [sosfilt(s,w)[int(0.1*SR):] for s in SOS]
def step(m, rng, P=None):
    P=P or {}; c=C[m]; out=np.zeros(N)
    bands=bandnoise(rng)
    heel=np.array(c['heel'])+rng.normal(0,1.0,len(SOS))            # this step's own spectrum, a little different
    t60=np.array(c['t60'])*P.get('t60_scale',1.0)*np.exp(rng.normal(0,0.1))
    t60=np.minimum(t60,P.get('t60_max',0.4))
    lvl=rng.normal(0,c['heel_sd']*0.6)
    contact(out,bands,0.01,lvl,heel,t60,rng,click_db=P.get('click_db',0.0),pulse_ms=P.get('pulse_ms',0.8))
    if rng.random()<c['sole_prob']:
        d=np.clip(rng.normal(c['sole_delay'][1],(c['sole_delay'][2]-c['sole_delay'][0])/2.56),0.03,0.25)
        bands2=bandnoise(rng)
        contact(out,bands2,0.01+d,lvl+rng.normal(0,3),heel+np.array(c['sole']),t60*0.8,rng,click_db=P.get('click_db',0.0)-3,pulse_ms=P.get('pulse_ms',0.8)*1.5)
    # the sole rolling and scraping from heel to toe-off: noise that swells after the heel and ends
    sd=P.get('scrape_db',-60)
    if sd>-50:
        dur=P.get('scrape_s',0.12)*np.exp(rng.normal(0,0.2)); a=int(0.012*SR); k=min(int(dur*SR),N-a)
        env=np.sin(np.pi*np.arange(k)/k)**P.get('scrape_skew',1.0)
        tilt=np.linspace(-1,1,len(SOS))*P.get('scrape_tilt',0.0)
        nb=bandnoise(rng)
        for b in range(len(SOS)): out[a:a+k]+=nb[b][:k]*env*db(lvl+sd+heel[b]+tilt[b])
    # small contacts after: grit, the sole settling
    for _ in range(rng.poisson(c['extra_mean']*P.get('extra_scale',1.0))):
        bands3=bandnoise(rng)
        contact(out,bands3,0.01+rng.uniform(0.02,0.2),lvl-rng.uniform(12,22),heel,t60*0.4,rng)
    out[-int(0.02*SR):]*=np.linspace(1,0,int(0.02*SR))
    return out

def shower(out, at, rng, shape_db, level_db, rate, tau, grain_ms, variants=8):
    """Loose ground under a foot: grains knocked against each other, each a tiny click of its own.
    They come thick at first and thin out as the ground settles: rate grains a second, falling away
    with time constant tau. Each grain's size (and so its loudness) is random."""
    grains=[]
    for _ in range(variants):
        h=click(np.asarray(shape_db),rng,n=256)
        k=max(8,int(grain_ms/1000*SR)); h=h[:4*k]*np.exp(-np.arange(min(len(h),4*k))/k)
        grains.append(h)
    t=0.0
    while True:
        lam=rate*np.exp(-t/tau)
        if lam<1: break
        t+=rng.exponential(1/lam)
        a=int((at+t)*SR)
        if a>=N: break
        g=grains[rng.integers(variants)]*rng.choice([-1,1])*rng.exponential(1.0); k=min(len(g),N-a)
        out[a:a+k]+=g[:k]*db(level_db)
def step_loose(m, rng, P):
    c=C[m]; out=np.zeros(N)
    heel=np.array(c['heel'])+rng.normal(0,1.0,len(SOS)); lvl=rng.normal(0,c['heel_sd']*0.6)
    tilt=np.linspace(-1,1,len(SOS))*P.get('grain_tilt',0.0)
    h=blow(heel,rng,P.get('pulse_ms',3.0)); k=len(h); out[int(0.01*SR):int(0.01*SR)+k]+=h*db(lvl+P.get('click_db',0.0))
    shower(out,0.01,rng,heel+tilt,lvl+P.get('grain_db',-10),P.get('rate',400)*np.exp(rng.normal(0,0.2)),P.get('tau',0.08),P.get('grain_ms',2.0))
    d=np.clip(rng.normal(c['sole_delay'][1],(c['sole_delay'][2]-c['sole_delay'][0])/2.56),0.03,0.25)
    shower(out,0.01+d,rng,heel+tilt,lvl+P.get('grain_db',-10)+P.get('sole_db',-3),P.get('rate',400)*np.exp(rng.normal(0,0.2)),P.get('tau',0.08),P.get('grain_ms',2.0))
    out[-int(0.02*SR):]*=np.linspace(1,0,int(0.02*SR))
    return out

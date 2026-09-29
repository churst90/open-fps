import numpy as np
from scipy.signal import butter, sosfilt
SR=44100; B=butter(3,[250,2000],'band',fs=SR,output='sos')
def flutter(s):
    """How much the 250-2000 Hz envelope wobbles 20-150 ms after the strike, dB: its deviation from a
    20 ms smoothing of itself. A clean decay is near zero; a watery one is not."""
    y=sosfilt(B,s); n=len(y)//88; e=10*np.log10(np.mean(y[:n*88].reshape(n,88)**2,1)+1e-14)
    i0=int(np.argmax(e)); seg=e[i0+10:i0+75]
    if len(seg)<20: return np.nan
    sm=np.convolve(seg,np.ones(10)/10,'same')
    return float(np.std((seg-sm)[5:-5]))

def attack(s):
    """The strike: rise time 10-90 % of the broadband envelope at 0.1 ms, ms; and crest factor (peak
    over RMS) of the first 10 ms, dB. A struck thing rises in a millisecond with a high crest."""
    h=sosfilt(butter(2,200,'high',fs=SR,output='sos'),s)
    w=4; n=len(h)//w; e=np.sqrt(np.mean(h[:n*w].reshape(n,w)**2,1))
    ip=int(np.argmax(e[:int(0.08*SR/w)])); pk=e[ip]
    i90=ip
    i10=ip
    while i10>0 and e[i10]>0.1*pk: i10-=1
    rise=(ip-i10)*w/SR*1000
    a=i10*w; seg=h[a:a+int(0.01*SR)]
    crest=20*np.log10(np.abs(seg).max()/(np.sqrt(np.mean(seg**2))+1e-12))
    return rise, crest

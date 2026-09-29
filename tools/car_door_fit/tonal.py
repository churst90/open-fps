import numpy as np, soundfile as sf, sys
from scipy.signal import butter,sosfilt
from scipy.ndimage import median_filter
def onset(x,sr):
    h=sosfilt(butter(4,[1000,4000],'band',fs=sr,output='sos'),x); w=int(.002*sr)
    e=np.array([np.sum(h[i:i+w]**2) for i in range(0,len(h)-w,w)]); return int(np.argmax(e>e.max()*0.01))*w
def tonality(x,sr=48000,loudest=False):
    if isinstance(x,str):
        x,sr=sf.read(x); x=x if x.ndim==1 else x.mean(1)
    if loudest:
        h=sosfilt(butter(4,[1000,4000],'band',fs=sr,output='sos'),x); w=int(.002*sr)
        e=np.array([np.sum(h[i:i+w]**2) for i in range(0,len(h)-w,w)]); t=max(0,int(np.argmax(e))-2)*w
    else: t=onset(x,sr)
    out=[]
    for a,b in [(0.0,0.05),(0.05,0.14),(0.14,0.42)]:
        s=x[t+int(a*sr):t+int(b*sr)]; n=len(s)
        S=10*np.log10(np.abs(np.fft.rfft(s*np.hanning(n),8192))**2+1e-15); f=np.fft.rfftfreq(8192,1/sr)
        m=(f>40)&(f<4000); Sm=S[m]
        base=median_filter(Sm,size=max(3,int(len(Sm)/40)))   # local spectral floor
        # tonality: how far the strongest narrow peaks stand above the local floor, dB (mean of top 5)
        out.append(np.sort(Sm-base)[-5:].mean())
    return out
if __name__=='__main__':
    for f in sys.argv[1:]:
        L='open' in f
        print(f"{f[:40]:40} peaks over floor, dB (0-50 / 50-140 / 140-420 ms): "+" ".join(f"{v:5.1f}" for v in tonality(f,loudest=L)))

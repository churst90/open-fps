import numpy as np, soundfile as sf, sys
from scipy.signal import butter,sosfilt
bands=[(30,60),(60,120),(120,250),(250,500),(500,1000),(1000,2000),(2000,4000),(4000,8000),(8000,16000)]
def prof(f,sr=48000,loudest=False):
    if isinstance(f,str):
        x,sr=sf.read(f); x=x if x.ndim==1 else x.mean(1)
    else: x=f
    # align on the loudest 10 ms frame
    # align on the impact's onset: first 2 ms frame in 1-4 kHz within 20 dB of that band's peak
    h=sosfilt(butter(4,[1000,4000],'band',fs=sr,output='sos'),x); w=int(.002*sr)
    e=np.array([np.sum(h[i:i+w]**2) for i in range(0,len(h)-w,w)]); pk=(int(np.argmax(e>e.max()*0.01)) if not loudest else max(0,int(np.argmax(e))-2))*w
    rows=[]
    for (a,b) in bands:
        y=sosfilt(butter(4,[a,b],'band',fs=sr,output='sos'),x)
        def lv(t0,t1):
            s=y[max(0,pk+int(t0*sr)):pk+int(t1*sr)]; return 10*np.log10(np.mean(s**2)+1e-15)
        rows.append([lv(-0.2,-0.02),lv(-0.02,0.03),lv(0.03,0.12),lv(0.12,0.4)])
    tot=10*np.log10(sum(10**(r[1]/10) for r in rows))
    return np.array(rows)-tot
if __name__=='__main__':
  L='loudest' in sys.argv; A=prof(sys.argv[1],loudest=L); B=prof(sys.argv[2],loudest=L)
  print("band       | before impact   | impact 0-50ms   | 50-140 ms       | 140-420 ms     (ref / synth / diff dB, rel. to impact total)")
  for i,(a,b) in enumerate(bands):
    print(f"{a:5}-{b:<5}| "+" | ".join(f"{A[i,j]:6.1f} {B[i,j]:6.1f} {B[i,j]-A[i,j]:+5.1f}" for j in range(4)))

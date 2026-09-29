import json, numpy as np, soundfile as sf, sys
from model import close, opening, sr
out=sys.argv[1]
ref,_=sf.read('ref.wav'); ref=ref if ref.ndim==1 else ref.mean(1)
rc,_=sf.read('ref_close.wav'); ro,_=sf.read('ref_open.wav')
def rms(x): return np.sqrt(np.mean(x**2))
def match(x,r): return x*rms(r)/rms(x)
B=json.load(open('best.json')); A=json.load(open('air.json')); O=json.load(open('open_final.json'))
c0=close(P=B); c1=close(P=A); o=opening(P=O)
files={'01 recording, door closing':rc,'02 synthesised closing, no air before the slam':match(c0,rc),
       '03 synthesised closing, with the air push before the slam':match(c1,rc),
       '04 recording, door opening':ro,'05 synthesised opening':match(o,ro)}
# whole sequence like the recording: open, then close 2.8 s later
seq=np.zeros(int(5.0*sr)); oo=match(o,ro); seq[int(0.1*sr):int(0.1*sr)+len(oo)]+=oo
cc=match(c1,rc); a=int(3.35*sr); seq[a:a+len(cc)]+=cc[:len(seq)-a]
files['06 recording, whole thing']=ref*1.0
files['07 synthesised, whole thing']=seq
peak=max(np.abs(v).max() for v in files.values())
for k,v in files.items(): sf.write(f"{out}/{k}.wav",(v*0.89/peak).astype(np.float32),sr)
print("written",len(files))

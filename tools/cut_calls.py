#!/usr/bin/env python3
# Cuts single calls out of recordings into ASSETS/SOUNDS/<FOLDER>/ (used for the birds, 2026-09-23).
# Paths and per-species settings are at the bottom. Needs ffmpeg and sox. To be generalised for footsteps.
import subprocess, array, math, os, sys
SRC='/home/cody/external-rescue/Github/open-fps/inbox/birds'
DST='/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS/BIRDS'
SR=44100; N=220  # 5 ms frames
def load(path, t0=0, dur=None):
    cmd=['ffmpeg','-v','error']+(['-ss',str(t0)] if t0 else [])+(['-t',str(dur)] if dur else [])+['-i',os.path.join(SRC,path),'-ac','1','-ar',str(SR),'-f','s16le','-']
    a=array.array('h'); a.frombytes(subprocess.run(cmd,capture_output=True).stdout); return a
def env(a):
    return [10*math.log10(sum(v*v for v in a[i:i+N])/N+1e-9)-90.3 for i in range(0,len(a)-N,N)]
def detect(f, above, gap, minlen, maxlen):
    s=sorted(f); med=s[len(s)//2]; th=med+above; ev=[]; on=None; last=0
    for i,d in enumerate(f):
        if d>th:
            if on is None: on=i
            last=i
        elif on is not None and i-last>gap:
            if minlen<=last-on+1<=maxlen: ev.append((on,last))
            on=None
    return ev, med
def fingerprint(seg):
    fp=[]
    for i in range(0,len(seg)-N,N):
        fr=seg[i:i+N]; e=sum(v*v for v in fr)/N
        z=sum(1 for j in range(1,len(fr)) if (fr[j-1]<0)!=(fr[j]<0))
        fp.append((math.log10(e+1),z))
    return fp
def same(fa,fb):
    best=0
    for lag in range(-3,4):
        pa=[fa[i] for i in range(len(fa)) if 0<=i+lag<len(fb)]; pb=[fb[i+lag] for i in range(len(fa)) if 0<=i+lag<len(fb)]
        if len(pa)<10: continue
        for k in (0,1):
            x=[p[k] for p in pa]; y=[p[k] for p in pb]; mx=sum(x)/len(x); my=sum(y)/len(y)
            num=sum((a-mx)*(b-my) for a,b in zip(x,y)); den=math.sqrt(sum((a-mx)**2 for a in x)*sum((b-my)**2 for b in y))+1e-9
            if k==0: c0=num/den
            else: c1=num/den
        best=max(best,min(c0,c1))
    return best
def ncc(x,y):
    n=min(len(x),len(y)); x=x[:n]; y=y[:n]
    sx=sum(v*v for v in x); sy=sum(v*v for v in y)
    return sum(p*q for p,q in zip(x,y))/math.sqrt(sx*sy+1e-9)
def write(species, idx, a, s0, s1, hp):
    s0=max(0,s0-int(0.015*SR)); s1=min(len(a),s1+int(0.06*SR))
    tmp='/tmp/claude-1000/birds/tmp.raw'
    array.array('h',a[s0:s1]).tofile(open(tmp,'wb'))
    d=(s1-s0)/SR
    os.makedirs(os.path.join(DST,species),exist_ok=True)
    out=os.path.join(DST,species,f'{species.lower()}_{idx:02d}.wav')
    subprocess.run(['sox','-t','raw','-r',str(SR),'-e','signed','-b','16','-c','1',tmp,out,
                    'highpass',str(hp),'fade','t','0.005',f'{d:.3f}','0.04','norm','-1'],check=True)
    return out,d
COUNT={}
KEPT={}
def run(species, path, hp, above, gap, minlen, maxlen, limit, t0=0, dur=None, dedupe=0.95, keep=None):
    a=load(path,t0,dur); f=env(a); ev,med=detect(f,above,gap,minlen,maxlen)
    kept=KEPT.setdefault(species,[]); n=COUNT.get(species,0); start=n
    for e0,e1 in ev:
        pk=max(f[e0:e1+1])
        if pk < med+above+6: continue
        seg=a[e0*N:(e1+1)*N]
        fp=fingerprint(seg)
        if any(same(fp,k)>dedupe for k in kept): continue
        kept.append(fp); n+=1; COUNT[species]=n
        out,d=write(species,n,a,e0*N,(e1+1)*N,hp)
        print(f"  {species} {n:02d}: {t0+e0*N/SR:7.2f}s {d*1000:5.0f} ms peak {pk:6.1f} (floor {med:.0f})")
        if n-start>=limit: break
    return n-start
print(run('SPARROW','house sparow.mp3',1500,12,3,20,70,60))
print(run('CROW','crow caw.mp3',250,20,6,20,200,6))
print(run('GOOSE','goose1.mp3',200,15,6,40,200,10) + run('GOOSE','goose2.mp3',200,15,6,40,200,10))
print(run('PIGEON','pigeon.mp3',100,8,30,40,450,10))
print(run('DOVE','morning dove.mp3',120,12,40,100,500,16))

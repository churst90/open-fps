#!/usr/bin/env python3
"""Reads the queued DSP-graph command that an FMOD mixer thread was executing when it faulted,
out of a createdump ELF core, and names the DSP units involved.

    python3 tools/read_core_dsp.py <dump> [lib/libfmodL.so]

What it prints, and why it exists: see docs/THE_MIXER_THREAD_CRASH.md ("How it was found").
The fault frame is found the same way as read_core_fault.py (the rt_sigframe on the crashing
thread's stack). Above it, the dispatcher's saved rbp is the command record: a byte opcode, then
(this, target, connection, type). A DSP's name pointer is at +0xf0 and usually points into the
library's own strings, which are not in the core, so they are read from the library file; pass the
SAME build that was running (the logging build for `run-gtk-client.sh fmodlog` runs). Offsets
below are FMOD 2.03.14 x86-64 and were read from the disassembly:
  DSP +0x64 flags (0x100 = execute on caller thread)   +0x78 input list head   +0x1a8 mNumInputs
  +0x190 output list head   +0x1aa mNumOutputs           connection +0x58 source, +0x60 sink
Frame layout of DSPI::disconnectFrom (logging build): saved rbx/r12/r13/r14/r15/rbp at rsp+0x28..0x50.
"""
import struct,sys,re
path=sys.argv[1]
LIBPATH=sys.argv[2] if len(sys.argv)>2 else 'lib/libfmodL.so'
f=open(path,'rb'); e=f.read(64)
ph_off=struct.unpack_from('<Q',e,0x20)[0]; es=struct.unpack_from('<H',e,0x36)[0]; n=struct.unpack_from('<H',e,0x38)[0]
f.seek(ph_off); ph=f.read(es*n)
notes=[];loads=[]
for i in range(n):
    o=i*es; t,fl=struct.unpack_from('<II',ph,o)
    off,va,_,fsz,msz=struct.unpack_from('<QQQQQ',ph,o+8)
    if t==4: notes.append((off,fsz))
    elif t==1: loads.append((va,off,fsz,msz,fl))
prs={};ntf=None
for off,size in notes:
    f.seek(off); d=f.read(size); pos=0
    while pos+12<=len(d):
        ns,ds,tp=struct.unpack_from('<III',d,pos); pos+=12; pos+=(ns+3)&~3
        desc=d[pos:pos+ds]; pos+=(ds+3)&~3
        if tp==1 and len(desc)>=112: prs[struct.unpack_from('<i',desc,0x20)[0]]=struct.unpack_from('<27Q',desc,112)
        elif tp==0x46494c45: ntf=desc
cnt,pgsz=struct.unpack_from('<QQ',ntf,0); p=16; tri=[]
for i in range(cnt):
    a,b,c=struct.unpack_from('<QQQ',ntf,p); p+=24; tri.append((a,b,c))
rest=ntf[p:].split(b'\0')
maps=sorted((a,b,rest[i].decode('utf-8','replace')) for i,(a,b,c) in enumerate(tri))
def who(a):
    for s_,e_,nm in maps:
        if s_<=a<e_: return nm.split('/')[-1]
    return None
def read(a,ln):
    for va,off,fsz,msz,fl in loads:
        if va<=a<va+fsz:
            f.seek(off+(a-va)); return f.read(min(ln,fsz-(a-va)))
    return b''
def q(a): b=read(a,8); return struct.unpack('<Q',b)[0] if len(b)==8 else None
def strings(b,minlen=4):
    return [m.group().decode() for m in re.finditer(rb'[\x20-\x7e]{%d,}'%minlen,b)]
G=['R8','R9','R10','R11','R12','R13','R14','R15','RDI','RSI','RBP','RBX','RDX','RAX','RCX','RSP','RIP','EFL','CSGSFS','ERR','TRAPNO','OLDMASK','CR2']
gi={n:40+i*8 for i,n in enumerate(G)}
names=['r15','r14','r13','r12','rbp','rbx','r11','r10','r9','r8','rax','rcx','rdx','rsi','rdi','orig_rax','rip','cs','eflags','rsp','ss','fs_base','gs_base','ds','es','fs','gs']
def describe_dsp(a,label):
    if not a: print(f"   {label}: NULL"); return
    b=read(a,0x260)
    if len(b)<0x200: print(f"   {label}: 0x{a:x} unreadable ({who(a)})"); return
    nout=struct.unpack_from('<H',b,0x1a8)[0]; nin=struct.unpack_from('<H',b,0x1aa)[0]
    onext=struct.unpack_from('<Q',b,0x78)[0]; inext=struct.unpack_from('<Q',b,0x190)[0]
    # walk lists safely
    def walk(head,limit=64):
        out=[]; cur=q(head)
        while cur and cur!=head and len(out)<limit:
            data=q(cur+0x10); out.append(data); cur=q(cur)
        return out, (cur==head)
    outs,ok1=walk(a+0x78); ins,ok2=walk(a+0x190)
    found=[]
    for i in range(0,0x260,8):
        p=struct.unpack_from('<Q',b,i)[0]
        if p and who(p) and p&0xffff800000000000==0:
            s=strings(read(p,0x60),5)
            s=[x for x in s if not x.startswith('/')]
            if s: found.append((i,s[0][:40]))
    inl=[s for s in strings(b,6)][:3]
    print(f"   {label}: 0x{a:x} numOutputs={nout} list={len(outs)}{'' if ok1 else '(BROKEN)'}  numInputs={nin} list={len(ins)}{'' if ok2 else '(BROKEN)'}  names={found[:4]} inline={inl}")
    return outs,ins

LIB=open(LIBPATH,'rb').read()
def libbase():
    return min(a for a,b,nm in maps if nm.endswith('libfmod.so'))
def libstr(p):
    off=p-libbase()
    fo = off if off<0x88d44 else off-0x1000 if off<0x1cec30 else off-0x3000
    if 0<=fo<len(LIB):
        s=LIB[fo:fo+48].split(b'\0')[0]
        return s.decode('latin1')
    return None
def name(a):
    if not a or not read(a,0x100): return '?'
    np_=q(a+0xf0)
    if not np_: return '(no name ptr)'
    if who(np_)=='libfmod.so':
        s=libstr(np_); 
        if s: return f"lib:{s}"
    b=read(np_,48)
    if b: return "mem:"+b.split(b'\0')[0].decode('latin1',errors='replace')
    return f"unreadable name ptr 0x{np_:x} ({who(np_)})"
def walk(head,limit=80):
    out=[]; cur=q(head)
    while cur and cur!=head and len(out)<limit:
        out.append(q(cur+0x10)); cur=q(cur)
    return out
def desc(a,label,deep=True):
    b=read(a,0x1b0)
    if len(b)<0x1b0: print(f"  {label}: 0x{a:x} unreadable"); return
    c8=struct.unpack_from('<H',b,0x1a8)[0]; ca=struct.unpack_from('<H',b,0x1aa)[0]
    l78=walk(a+0x78); l190=walk(a+0x190)
    print(f"  {label}: 0x{a:x} {name(a)}  cnt78={c8} list78={len(l78)}  cnt190={ca} list190={len(l190)}")
    return l78,l190

def find_fault():
    for t in prs:
        reg=dict(zip(names,prs[t])); rsp0=reg['rsp']
        blob=read(rsp0, 512*1024)
        for i in range(0,len(blob)-8,8):
            if struct.unpack_from('<Q',blob,i)[0]!=0x7c: continue
            uc=i-gi['CR2']
            if uc<0: continue
            if struct.unpack_from('<Q',blob,uc+gi['TRAPNO'])[0]!=14: continue
            rip=struct.unpack_from('<Q',blob,uc+gi['RIP'])[0]
            rsp=struct.unpack_from('<Q',blob,uc+gi['RSP'])[0]
            return t,rip,rsp
    return None,None,None
t,rip,rsp=find_fault()
if t is None:
    print("no page-fault context found on any thread's stack (a hang dump? use read_core_threads.py)"); sys.exit(1)
sb=read(rsp,0x60)
saved={k:struct.unpack_from('<Q',sb,o)[0] for k,o in (('rbx',0x28),('r12',0x30),('r13',0x38),('r14',0x40),('r15',0x48),('rbp',0x50),('ret',0x58))}
rec=saved['rbp']; drbx=saved['rbx']; idx=saved['r14']
w=who(rip)
print(f"crashing thread {t:#x}: fault RIP {w}+0x{rip-min(a for a,b,nm in maps if nm.endswith(w)):x}" if w else f"crashing thread {t:#x}")
print(f"dispatcher frame: queue owner 0x{drbx:x}, record at +0x{idx:x} (0x{rec:x}), return 0x{saved['ret']:x}")
rb=read(rec,0x28)
op=rb[0]; this,target,conn,ty=struct.unpack_from('<QQQQ',rb,8)
print(f"record: op={op} ({'disconnect' if op==3 else 'connect' if op==0 else '?'}) this=0x{this:x} target=0x{target:x} conn=0x{conn:x} type=0x{ty&0xffffffff:x}")
def fl(a): return struct.unpack('<I',read(a+0x64,4))[0]
def show(lbl,a):
    b=read(a,0x1b0)
    if len(b)<0x1b0: print(f"  {lbl:12} 0x{a:x} unreadable"); return
    nin=struct.unpack_from('<H',b,0x1a8)[0]; nout=struct.unpack_from('<H',b,0x1aa)[0]
    lin=walk(a+0x78); lout=walk(a+0x190)
    bad=' <-- COUNT/LIST MISMATCH' if (nin!=len(lin) or nout!=len(lout)) else ''
    print(f"  {lbl:12} {name(a):20} 0x{a:x} inputs {nin}/{len(lin)} outputs {nout}/{len(lout)} flags=0x{fl(a):x}{bad}")
print("units (count/list):")
show('this',this); show('target',target)
for c in walk(this+0x78): show('  this.in',q(c+0x58))
for c in walk(this+0x190): show('  this.out',q(c+0x60))
for c in walk(target+0x78): show('  target.in',q(c+0x58))
for c in walk(target+0x190): show('  target.out',q(c+0x60))
cb=read(conn,0x80)
if len(cb)==0x80:
    print(f"connection: source=0x{struct.unpack_from('<Q',cb,0x58)[0]:x} sink=0x{struct.unpack_from('<Q',cb,0x60)[0]:x} flags=0x{struct.unpack_from('<I',cb,0x7c)[0]:x} (zeros = already freed by the executor)")
buf=drbx+0x1e8; total=struct.unpack('<i',read(drbx+0x101e8,4))[0]
recs=[]; off=0
while off<total:
    h=struct.unpack('<I',read(buf+off,4))[0]; o=h&0xff; size=h>>8
    if size<=0 or size>0x200: break
    recs.append((off,o)); off+=size
from collections import Counter
print(f"batch: {total} bytes, {len(recs)} records, ops {dict(Counter(o for _,o in recs))} (0=connect, 3=disconnect)")

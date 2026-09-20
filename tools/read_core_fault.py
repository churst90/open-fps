import struct,sys,glob
path=sys.argv[1]
tid=int(sys.argv[2],16) if len(sys.argv)>2 else None
f=open(path,'rb'); e=f.read(64)
ph_off=struct.unpack_from('<Q',e,0x20)[0]; es=struct.unpack_from('<H',e,0x36)[0]; n=struct.unpack_from('<H',e,0x38)[0]
f.seek(ph_off); ph=f.read(es*n)
notes=[];loads=[]
for i in range(n):
    o=i*es; t,_=struct.unpack_from('<II',ph,o)
    off,va,_,fsz,_=struct.unpack_from('<QQQQQ',ph,o+8)
    if t==4: notes.append((off,fsz))
    elif t==1: loads.append((va,off,fsz))
prs={};ntf=None
for off,size in notes:
    f.seek(off); d=f.read(size); pos=0
    while pos+12<=len(d):
        ns,ds,tp=struct.unpack_from('<III',d,pos); pos+=12; pos+=(ns+3)&~3
        desc=d[pos:pos+ds]; pos+=(ds+3)&~3
        if tp==1 and len(desc)>=112: prs[struct.unpack_from('<i',desc,0x20)[0]]=struct.unpack_from('<27Q',desc,112)
        elif tp==0x46494c45: ntf=desc
cnt,_=struct.unpack_from('<QQ',ntf,0); p=16; tri=[]
for i in range(cnt):
    a,b,c=struct.unpack_from('<QQQ',ntf,p); p+=24; tri.append((a,b))
rest=ntf[p:].split(b'\0')
maps=sorted((a,b,rest[i].decode('utf-8','replace')) for i,(a,b) in enumerate(tri))
def who(a):
    lo,hi=0,len(maps)-1
    while lo<=hi:
        m=(lo+hi)//2; s_,e_,nm=maps[m]
        if a<s_: hi=m-1
        elif a>=e_: lo=m+1
        else: return nm.split('/')[-1], a-s_
    return None,0
def read(a,ln):
    for va,off,fsz in loads:
        if va<=a<va+fsz:
            f.seek(off+(a-va)); return f.read(min(ln,fsz-(a-va)))
    return b''
names=['r15','r14','r13','r12','rbp','rbx','r11','r10','r9','r8','rax','rcx','rdx','rsi','rdi',
       'orig_rax','rip','cs','eflags','rsp','ss','fs_base','gs_base','ds','es','fs','gs']
tids=[tid] if tid else list(prs)
for t in tids:
    if t not in prs: continue
    reg=dict(zip(names,prs[t]))
    blob=read(reg['rsp'], 512*1024)
    hits=[]
    for i in range(0,len(blob)-32,4):
        signo,errno,code=struct.unpack_from('<iii',blob,i)
        if signo==11 and errno==0 and code in (1,2,128) :
            addr=struct.unpack_from('<Q',blob,i+16)[0]
            hits.append((code,addr,reg['rsp']+i))
    if hits:
        print(f"tid {t:#x}: siginfo_t candidates on stack")
        for code,addr,at in hits[:8]:
            nm,off=who(addr)
            print(f"   si_code={code} ({'SEGV_MAPERR' if code==1 else 'SEGV_ACCERR' if code==2 else code})  si_addr=0x{addr:x} {'in '+nm if nm else '(unmapped)'}  @stack 0x{at:x}")

# --- the ucontext the kernel handed the handler sits just before the siginfo in the
# --- rt_sigframe. Find it by its CR2 (== si_addr) and read the FAULTING RIP out of it.
G=['R8','R9','R10','R11','R12','R13','R14','R15','RDI','RSI','RBP','RBX','RDX','RAX','RCX','RSP','RIP','EFL','CSGSFS','ERR','TRAPNO','OLDMASK','CR2']
gi={n:40+i*8 for i,n in enumerate(G)}
for t in tids:
    if t not in prs: continue
    reg=dict(zip(names,prs[t])); base=reg['rsp']
    blob=read(base, 512*1024)
    for i in range(0,len(blob)-8,8):
        if struct.unpack_from('<Q',blob,i)[0]!=0x7c: continue
        uc=i-gi['CR2']
        if uc<0 or uc+gi['CR2']+8>len(blob): continue
        tn=struct.unpack_from('<Q',blob,uc+gi['TRAPNO'])[0]
        er=struct.unpack_from('<Q',blob,uc+gi['ERR'])[0]
        if tn!=14: continue
        rip=struct.unpack_from('<Q',blob,uc+gi['RIP'])[0]
        print(f"\ntid {t:#x}: PAGE FAULT context (trapno 14, err {er} = {'read' if not er&2 else 'write'} in user mode)")
        for r in ('RIP','RSP','RBP','RAX','RBX','RCX','RDX','RSI','RDI','R12','R13','R14','R15'):
            v=struct.unpack_from('<Q',blob,uc+gi[r])[0]
            nm,off=who(v)
            print(f"   {r:4} = 0x{v:016x}" + (f"   {nm}+0x{off:x}" if nm else ""))

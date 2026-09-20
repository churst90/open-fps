import struct,sys,collections
path=sys.argv[1]
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
bases={}
for a,b,nm in maps: bases.setdefault(nm.split('/')[-1], a)
def who(a):
    lo,hi=0,len(maps)-1
    while lo<=hi:
        m=(lo+hi)//2; s_,e_,nm=maps[m]
        if a<s_: hi=m-1
        elif a>=e_: lo=m+1
        else:
            b=nm.split('/')[-1]; return b, a-bases[b]
    return None,0
def read(a,ln):
    for va,off,fsz in loads:
        if va<=a<va+fsz:
            f.seek(off+(a-va)); return f.read(min(ln,fsz-(a-va)))
    return b''
names=['r15','r14','r13','r12','rbp','rbx','r11','r10','r9','r8','rax','rcx','rdx','rsi','rdi',
       'orig_rax','rip','cs','eflags','rsp','ss','fs_base','gs_base','ds','es','fs','gs']
print(f"{len(prs)} threads\n")
for tid,r in prs.items():
    reg=dict(zip(names,r))
    nm,off=who(reg['rip'])
    syscall = reg['orig_rax']
    blob=read(reg['rsp'], 64*1024)
    seen=[]
    for i in range(0,len(blob)-8,8):
        v=struct.unpack_from('<Q',blob,i)[0]
        if v<0x10000: continue
        b,o=who(v)
        if b and ('.so' in b or b.endswith('.dll')) and b not in seen: seen.append(b)
    interesting=[x for x in seen if 'fmod' in x or 'phonon' in x.lower() or 'coreclr' in x]
    print(f"tid {tid:#x} rip={nm}+0x{off:x} syscall={syscall} :: {', '.join(interesting[:5]) or '-'}")

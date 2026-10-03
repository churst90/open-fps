import math
inch=0.0254
print("== panel masses")
def glass(FW,FH):  # Pella 250 XO formulas, inches
    return (FW/2-4.1875)*inch,(FH-7.25)*inch
for FW,FH in [(72,80),(96,80),(72,96),(96,96)]:
    gw,gh=glass(FW,FH); A=gw*gh; pw=FW/2*inch; ph=(FH-3)*inch
    per=2*(pw+ph)
    row=[f"{FW}x{FH}in panel {pw:.3f}x{ph:.3f} m glass {gw:.3f}x{gh:.3f}={A:.3f} m2 perim {per:.2f} m"]
    for t in (3,4,5,6):
        mg=A*2*t*2.5
        row.append(f"{t}+{t}: glass {mg:.1f} kg")
    print("; ".join(row))
print("== test report panels")
for name,gw,gh,t1,t2,frame,maint,init in [
 ("CRL 3000 R",1.105+0.032,2.242+0.032,6.0,6.0,12,42.3,46.7),
 ("AllWeather 8100 CW",1.060+0.025,1.860+0.025,4.76,4.76,15,53,75),
 ("Fleetwood 4070-T LC",1.725+0.029,3.495+0.029,6.0,6.0,30,78,80),
 ("Eurotek lift-slide",1.80-0.15,2.31-0.15,6.35,4.76,25,40,44)]:
    mg=gw*gh*(t1+t2)*2.5; M=mg+frame; W=M*9.81
    print(f"{name}: glass {mg:.0f} kg, est total {M:.0f} kg, W {W:.0f} N, maintain {maint} N -> mu_eff {maint/W:.3f}; initiate {init} -> {init/W:.3f}")
print("== Hertz")
def Estar(E1,n1,E2,n2): return 1/((1-n1**2)/E1+(1-n2**2)/E2)
mats={"nylon/Al":Estar(2.8e9,0.40,70e9,0.33),"POM/Al":Estar(2.9e9,0.35,70e9,0.33),
      "steel/Al":Estar(205e9,0.29,70e9,0.33),"steel/stainless cap":Estar(205e9,0.29,193e9,0.29),
      "nylon wet(1.6GPa)/Al":Estar(1.6e9,0.40,70e9,0.33)}
for k,v in mats.items(): print(f"E* {k}: {v/1e9:.2f} GPa")
for D_in in (1.0,1.25,1.5):
  Rx=D_in*inch/2
  for rc in (3.0e-3,2.5e-3):
    rg=0.125*inch
    Ry=1/(1/rc-1/rg)
    Re=math.sqrt(Rx*Ry)
    for F in (100,200,400):
      out=[]
      for k in ("nylon/Al","steel/Al","steel/stainless cap"):
        E=mats[k]; a=(3*F*Re/(4*E))**(1/3); d=a*a/Re; p0=3*F/(2*math.pi*a*a); kc=2*E*a
        out.append(f"{k}: a={a*1e3:.2f}mm d={d*1e6:.1f}um p0={p0/1e6:.0f}MPa k={kc/1e6:.2f}MN/m")
      print(f"D={D_in}in crown r={rc*1e3}mm Ry={Ry*1e3:.1f}mm Re={Re*1e3:.1f}mm F={F}N | "+" | ".join(out))
print("== flat cylinder groove? also hysteresis Crr")
for alpha in (0.05,0.1,0.2):
    for aR in (0.05,0.1):
        print(f"alpha {alpha} a/R {aR}: Crr={3/16*alpha*aR:.4f}")
print("== wheel rotation rates")
for D_in in (1.0,1.25,1.5,1.75):
    C=math.pi*D_in*inch
    print(f"D {D_in}in circ {C*1e3:.1f}mm: " + ", ".join(f"v{v}:{v/C:.1f}Hz" for v in (0.2,0.5,1.0,1.5)))
print("== contact filter cutoffs v/(2a)")
for a in (0.3e-3,1.5e-3,2.5e-3):
    print(f"2a={2*a*1e3:.1f}mm: "+", ".join(f"v{v}:{v/(2*a):.0f}Hz" for v in (0.2,0.5,1.0)))
print("== grit climb")
for D_in in (1.0,1.25,1.5):
    R=D_in*inch/2
    for h in (0.05e-3,0.2e-3,0.5e-3):
        x=math.sqrt(2*R*h); print(f"D{D_in} h{h*1e3}mm: approach {x*1e3:.2f}mm, at 0.5 m/s {x/0.5*1e3:.1f} ms")
print("== flat spot")
for D_in in (1.25,):
    R=D_in*inch/2
    for d in (0.05e-3,0.1e-3,0.3e-3):
        L=2*math.sqrt(2*R*d); print(f"flat depth {d*1e3}mm: chord {L*1e3:.2f}mm")
print("== glass plates")
E=72e9; nu=0.22
for t in (3,4,5,6):
    h=t/1000; D=E*h**3/(12*(1-nu**2)); m=2500*h
    for a,b in [(0.81,1.85),(1.11,1.85),(0.81,2.25),(1.11,2.25)]:
        c=math.pi/2*math.sqrt(D/m)
        f11=c*(1/a**2+1/b**2); f21=c*(4/a**2+1/b**2); f12=c*(1/a**2+4/b**2); f13=c*(1/a**2+9/b**2)
        print(f"t{t} {a}x{b}: f11={f11:.1f} f12={f12:.1f} f13={f13:.1f} f21={f21:.1f}  (clamped f11~x{1.8:.1f}={1.8*f11:.0f})")
    print(f"   fc=12500/t={12500/t:.0f} Hz; bending wave c at 1kHz={ (math.sqrt(2*math.pi*1000)*(D/m)**0.25):.0f} m/s")
print("== mass-air-mass")
rho=1.21;c=343
for t1,t2,d in [(3,3,16.2),(4,4,16),(4,4,12),(5,5,12.7),(6,6,19),(4.76,4.76,15.9),(3,3,12)]:
    m1=2.5*t1;m2=2.5*t2
    f=1/(2*math.pi)*math.sqrt(rho*c*c/(d/1000)*(m1+m2)/(m1*m2))
    fq=1200*math.sqrt((t1+t2)/(t1*t2*d))
    for (a,b) in [(0.81,1.85),(1.11,1.85)]:
        h=t1/1000; D=E*h**3/(12*(1-nu**2)); f11=math.pi/2*math.sqrt(D/(2500*h))*(1/a**2+1/b**2)
        fb=math.sqrt(f11**2+0.657*f**2)
        print(f"{t1}-{d}-{t2}: f_mam={f:.0f} Hz (Quirt {fq:.0f}); (1,1) breathing on {a}x{b}: {fb:.0f} Hz; in-phase f11 {f11:.1f}")
print("== impacts")
for M in (40,70,120):
  for v in (0.3,0.7,1.5):
    KE=0.5*M*v*v
    s=[]
    for k in (5e4,2e5,2e6):
        T=math.pi*math.sqrt(M/k); Fp=v*math.sqrt(M*k); s.append(f"k{k/1e3:.0f}kN/m: {T*1e3:.0f}ms peak {Fp:.0f}N")
    print(f"M{M} v{v}: KE {KE:.1f} J, p {M*v:.0f} Ns | "+" | ".join(s))
print("== decay times T60=2.2/(f eta)")
for f in (20,170,500,1000,3000):
    print(f, [round(2.2/(f*eta),3) for eta in (0.005,0.02,0.05)])

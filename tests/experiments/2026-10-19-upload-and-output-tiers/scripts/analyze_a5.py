import re, glob, os, sys
D=r"C:\PLAN\3FCompare\3fp\build\anim-perf\lane-a5"
def parse_lsp(p):
    t=open(p,encoding='utf-8',errors='replace').read()
    r={}
    m=re.search(r'\[queue\] animated bytesPerFrame=(\d+) pipeline=(-?\d+) limit=(-?\d+)',t)
    r['trace']=m.groups() if m else None
    m=re.search(r'观测队列峰=(\d+)',t)
    r['qpeak']=int(m.group(1)) if m else None
    m=re.search(r'SEAM RESULT: (\w+)（(.*?)）(?:红因: ?(.*?))?\s*$',t,re.M)
    if m: r['seam']=(m.group(1),m.group(2),m.group(3))
    m=re.search(r'回绕帧比稳态帧 ([+-][\d.]+)ms',t)
    r['seam_delta']=float(m.group(1)) if m else None
    m=re.search(r'回绕次数=(\d+)\s+长停\(>(\d+)ms\)次数=(\d+)',t)
    if m: r['wraps'],r['longstop']=(int(m.group(1)),int(m.group(3)))
    m=re.search(r'整窗均值 ([\d.]+)ms vs 期望 ([\d.]+)ms',t)
    if m: r['rhythm']=(float(m.group(1)),float(m.group(2)))
    m=re.search(r'droppedVideoFrames: \d+ → \d+（净增 (\d+)）',t)
    r['dropped']=int(m.group(1)) if m else None
    m=re.search(r'coalescedVideoFrames: \d+ → \d+（净增 (\d+)）',t)
    r['coalesced']=int(m.group(1)) if m else None
    for k in ('重复','跳号','起点漂移','倒退'):
        mm=re.search(k+r'[^，。）]*',t)
        if mm and ('[红]' in t[max(0,t.find(mm.group(0))-6):t.find(mm.group(0))]): r.setdefault('frameseq',[]).append(mm.group(0))
    return r
def parse_aud(p):
    t=open(p,encoding='utf-8',errors='replace').read()
    r={}
    rows=re.findall(r'^\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)',t,re.M)
    if rows:
        r['priv_max']=max(float(x[5]) for x in rows)
        r['cpu_mean']=sum(float(x[6]) for x in rows)/len(rows)
        r['qpeak']=max(int(x[3]) for x in rows)
    m=re.search(r'私有=(\d+(?:\.\d+)?)MB',t)
    if m: r['priv_first']=float(m.group(1))
    m=re.search(r'AUDIT RESULT',t)
    r['ok']=bool(m)
    return r
cases=['4K','640','1080','6K']
print(f"{'case':6} {'arm':3} {'rnd':3} | {'trace(limit)':22} {'qpeak(lsp/aud)':14} {'seamΔms':8} {'longstop':8} {'drop':5} {'coal':5} {'rhythm':16} {'privMB':8} {'cpu':8} verdict")
for c in cases:
    for arm in ('A','B'):
        for rnd in ('r0','r1'):
            lp=os.path.join(D,f"a5b_lsp_{c}_{arm}_{rnd}.txt")
            ap=os.path.join(D,f"a5b_aud_{c}_{arm}_{rnd}.txt")
            if not os.path.exists(lp): print(f"{c:6} {arm:3} {rnd:3} | MISSING"); continue
            L=parse_lsp(lp); U=parse_aud(ap) if os.path.exists(ap) else {}
            tr='-'.join(L['trace'][1:]) if L.get('trace') else 'no-trace'
            q=f"{L.get('qpeak')}/{U.get('qpeak','-')}"
            sd=L.get('seam_delta'); sd=f"{sd:+.1f}" if sd is not None else '-'
            ls=L.get('longstop','-'); dp=L.get('dropped','-'); co=L.get('coalesced','-')
            rh=L.get('rhythm'); rh=f"{rh[0]:.1f}/{rh[1]:.0f}" if rh else '-'
            pm=U.get('priv_max','-'); cm=U.get('cpu_mean','-')
            pm=f"{pm:.0f}" if isinstance(pm,float) else pm
            cm=f"{cm:.0f}" if isinstance(cm,float) else cm
            v=L.get('seam',('?',))[0]
            print(f"{c:6} {arm:3} {rnd:3} | {tr:22} {q:14} {sd:8} {str(ls):8} {str(dp):5} {str(co):5} {rh:16} {pm:8} {cm:8} {v}")

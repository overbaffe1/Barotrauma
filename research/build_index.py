#!/usr/bin/env python3
import os, csv
from pathlib import Path
root=Path('/home/user/Barotrauma')
out=root/'research'
rows=[]
def count(p):
    try:
        with open(p,'r',encoding='utf-8',errors='replace') as f: return sum(1 for _ in f)
    except: return -1
for dp,dn,fn in os.walk(root):
    if '.git' in dp or '/.git' in dp: continue
    for f in fn:
        if not f.endswith('.cs'): continue
        p=Path(dp)/f
        rel=p.relative_to(root).as_posix()
        rows.append({'path':rel,'lines':count(p),'status':'new','explored':0})
rows.sort(key=lambda r:r['path'])
with open(out/'paths.csv','w',newline='',encoding='utf-8') as f:
    w=csv.DictWriter(f,fieldnames=['path','lines','status','explored']); w.writeheader(); w.writerows(rows)
out.joinpath('ALL_CS_PATHS.txt').write_text('\n'.join(r['path'] for r in rows)+'\n',encoding='utf-8')
print('files',len(rows),'total_lines',sum(r['lines'] for r in rows if r['lines']>0))
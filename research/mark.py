#!/usr/bin/env python3
"""Track research ranges per file. Usage:
python research/mark.py <relpath> <a-b,a-b,...>
"""
import json, csv, sys
from pathlib import Path

ROOT = Path('/home/user/Barotrauma')
CSV = ROOT/'research/paths.csv'
RANGES = ROOT/'research/ranges.json'
LOG = ROOT/'research/RESEARCH_LOG.txt'

def count_lines(p):
    with open(p, 'r', encoding='utf-8', errors='replace') as f:
        return sum(1 for _ in f)

def parse(ranges_data):
    out=[]
    for r in ranges_data:
        if '-' in r:
            a,b=r.split('-',1); a=int(a); b=int(b)
            if a>b: a,b=b,a
            out.append((a,b))
        else:
            x=int(r); out.append((x,x))
    return sorted(out)

def merge(rs):
    rs=sorted(rs)
    out=[]
    for a,b in rs:
        if out and a<=out[-1][1]+1:
            out[-1]=(out[-1][0], max(out[-1][1],b))
        else:
            out.append((a,b))
    return out

def fmt(rs):
    return ','.join(f'{a}-{b}' if a!=b else f'{a}' for a,b in rs)

def covered(rs):
    return sum(b-a+1 for a,b in rs)

def load_data():
    if not RANGES.exists():
        return {}
    return json.loads(RANGES.read_text(encoding='utf-8'))

def read_csv():
    with open(CSV,'r',newline='',encoding='utf-8') as f:
        return list(csv.DictReader(f))

def write_csv(rows):
    with open(CSV,'w',newline='',encoding='utf-8') as f:
        w=csv.DictWriter(f, fieldnames=['path','lines','status','explored'])
        w.writeheader()
        w.writerows(rows)

def write_log(rows, data):
    total=sum(int(x['lines']) for x in rows if int(x['lines'])>0)
    done=[x for x in rows if x['status']=='done']
    part=[x for x in rows if x['status']=='partial']
    new=[x for x in rows if x['status']=='new']
    lines=[ "BAROTRAUMA RESEARCH LOG (per-path line coverage)",
            "Updated: 2026-08-26",
            "",
            f"Total .cs files: {len(rows)}",
            f"Total lines: {total}",
            f"Done: {len(done)} files / {sum(int(x['lines']) for x in done)} lines",
            f"Partial: {len(part)} files / {sum(int(x['lines']) for x in part)} lines",
            f"Not started: {len(new)} files / {sum(int(x['lines']) for x in new)} lines",
            "",
            "=== RESEARCHED ===" ]
    for x in done:
        lines.append(f"[DONE   ] {x['path']}  {x['lines']}/{x['lines']}")
    for x in part:
        rs=data.get(x['path'],[])
        cr=covered(rs)
        lines.append(f"[PARTIAL] {x['path']}  {cr}/{x['lines']}  ranges={fmt(rs)}")
    lines += ["", "=== NOT STARTED ==="]
    for x in new:
        lines.append(f"[NEW    ] {x['path']}  0/{x['lines']}")
    LOG.write_text('\n'.join(lines), encoding='utf-8')

if __name__=='__main__':
    rel=sys.argv[1].lstrip('./')
    ranges_data=sys.argv[2].split(',') if len(sys.argv)>2 else []
    data=load_data()
    existing=data.get(rel, [])
    merged=merge([tuple(x) for x in existing]+parse(ranges_data))
    data[rel]=[list(x) for x in merged]
    RANGES.write_text(json.dumps(data, indent=2), encoding='utf-8')
    cr=covered(merged)
    rows=read_csv()
    for x in rows:
        if x['path']==rel:
            x['explored']=str(cr)
            x['status']='done' if cr>=int(x['lines']) else 'partial'
    write_csv(rows)
    write_log(rows, data)
    print(f"marked {rel}: {cr}/{next(int(x['lines']) for x in rows if x['path']==rel)}")
import json,csv,sys
R=json.load(open('research/ranges.json'))
rows=list(csv.DictReader(open('research/paths.csv')))
def cov(rs): return sum(b-a+1 for a,b in rs)
out=[]
new=partial=done=0
for r in rows:
    path=r['path']; total=int(r['lines'])
    if path in R:
        rr=R[path]; c=cov(rr)
        if c>=total:
            done+=1
            out.append(('done', path, total, total))
        else:
            partial+=1
            out.append(('partial', path, total, c))
    else:
        new+=1
        out.append(('new', path, total, 0))
out.sort(key=lambda x:(x[0],x[1]))
lines=["BAROTRAUMA RESEARCH COVERAGE (per-file checked lines)",
       "Updated: 2026-08-26","",
       f"TOTAL .cs: {len(rows)}   DONE: {done}   PARTIAL: {partial}   NOT STARTED: {new}","",
       "=== FULLY EXPLORED (checked lines = total) ==="]
for s,p,t,c in out:
    if s=='done': lines.append(f"CHECKED {c}/{t}  {p}")
lines+=["","=== PARTIAL (checked X of Y) ==="]
for s,p,t,c in out:
    if s=='partial': lines.append(f"CHECKED {c}/{t}  {p}")
lines+=["","=== NOT STARTED (0 of Y) ==="]
for s,p,t,c in out:
    if s=='new': lines.append(f"CHECKED 0/{t}  {p}")
open('research/COVERAGE_PROGRESS.txt','w',encoding='utf-8').write('\n'.join(lines))
print(f"done={done} partial={partial} new={new}")
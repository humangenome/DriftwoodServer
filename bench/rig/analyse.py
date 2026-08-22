#!/usr/bin/env python3
import csv, statistics, sys, os, glob
d = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'csv')
rows_out = []
for path in sorted(glob.glob(os.path.join(d, '*.csv'))):
    tag = os.path.basename(path)[:-4]
    rows = list(csv.DictReader(open(path)))
    if not rows:
        print(f"{tag}: EMPTY"); continue
    by = {}
    for r in rows: by.setdefault(r['pid'], []).append(r)
    per = []
    for pid, rs in by.items():
        cpu = [float(r['cpuPct']) for r in rs]
        priv = [float(r['privMB']) for r in rs]
        per.append((pid, statistics.mean(cpu), sorted(cpu)[int(len(cpu)*0.95)], statistics.mean(priv), priv[0], priv[-1], len(rs)))
    n = len(per)
    mean_cpu = statistics.mean(p[1] for p in per)
    p95 = statistics.mean(p[2] for p in per)
    mean_priv = statistics.mean(p[3] for p in per)
    drift = statistics.mean(p[5]-p[4] for p in per)
    total = mean_cpu * n
    t = [float(r['t']) for r in rows]
    print(f"{tag:18s} instances={n} window={max(t)-min(t):.0f}s  perInstanceCPU mean={mean_cpu:6.2f}% p95={p95:6.2f}%  totalCPU={total:7.2f}%  privMB mean={mean_priv:7.1f} drift={drift:+.1f}")
    rows_out.append((tag, n, mean_cpu, p95, total, mean_priv))
print()
print("| run | instances | per-instance CPU (% of one core) | p95 | total CPU | private MB |")
print("|---|---|---|---|---|---|")
for tag, n, m, p, tot, pr in rows_out:
    print(f"| {tag} | {n} | {m:.1f}% | {p:.1f}% | {tot:.0f}% | {pr:.0f} |")

# Per-player slope, if the load runs are present. This is the number that constrains a physics game.
lookup = {t: (n, m, p, tot, pr) for t, n, m, p, tot, pr in rows_out}
def cpu(tag):
    for t, n, m, p, tot, pr in rows_out:
        if t.startswith(tag): return m
    return None
a, e1, e2, d = cpu('A-1x'), cpu('E1-1player'), cpu('E2-2players'), cpu('D-1x-ghost')
if a and (e1 or e2):
    print()
    print("PER-PLAYER SLOPE (% of one i3-7100 core)")
    print(f"  idle (0 players):        {a:.2f}%")
    if e1: print(f"  1 player:                {e1:.2f}%   delta from idle = {e1-a:+.2f}%")
    if e2: print(f"  2 players:               {e2:.2f}%   delta from idle = {e2-a:+.2f}%")
    if e1 and e2: print(f"  marginal 2nd player:     {e2-e1:+.2f}%")
    if e1: print(f"  => cost per player, first: {e1-a:.2f}% of one core")
if a and d:
    print()
    print("GHOST-HOST A/B (% of one i3-7100 core)")
    print(f"  suppressed (A): {a:.2f}%   not suppressed (D): {d:.2f}%   suppression saves {d-a:+.2f}%")

# Lever table: everything measured against the uncapped idle baseline, so each lever's worth is
# stated as points of one core rather than as a percentage of a percentage.
base = cpu('B-1x-uncap') or cpu('A-1x')
if base:
    levers = [
        ('F-physics30',      'physics step 0.01 -> 0.0333 (3.3x fewer steps)'),
        ('G-tick20',         'netcode tick 50 -> 20 Hz'),
        ('H-both',           'physics + tick together'),
        ('I-pause-empty',    'world frozen while empty (timeScale 0)'),
        ('L-idle5-cap30',    '5 fps while empty, 30 fps with players'),
        ('N-idle5-and-pause','5 fps while empty AND the world frozen'),
        ('M-both-then-join', '...both stood down, and a client joins'),
        ('P-uncapped',       'wired limiter, uncapped (re-baseline)'),
        ('P-cap30',          'real frame cap 30 fps'),
        ('P-cap15',          'real frame cap 15 fps'),
        ('P-cap5',           'real frame cap 5 fps'),
    ]
    printed = False
    for tag, what in levers:
        v = cpu(tag)
        if v is None: continue
        if not printed:
            print(); print(f"LEVERS (baseline = uncapped idle {base:.2f}% of one i3-7100 core)"); printed = True
        print(f"  {tag:20s} {v:6.2f}%   {v-base:+6.2f} pts  ({(v-base)/base*100:+5.1f}%)   {what}")

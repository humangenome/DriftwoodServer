using System;
using System.Diagnostics;

// Core-normalisation benchmark. One question only: how much faster is a fleet core than the
// measurement rig's 2017 i3, for the KIND of work a headless game server does?
//
// A single latency-bound FP chain is not enough - two CPUs can be close on instruction latency and
// far apart on real work - so three workloads are run and reported separately:
//
//   fpchain : serial Math.Sqrt/Math.Sin dependency chain. Latency bound. Closest to a per-object
//             physics integration step's arithmetic.
//   branchy : integer work with data-dependent branches. Stresses the branch predictor, which is
//             what an entity update loop full of state checks actually leans on.
//   memory  : pseudo-random reads over 32 MB, larger than L2 and hostile to prefetch. This is the
//             component a game server's object graph traversal is closest to, and the one where an
//             old core and a new one differ most.
//
// Report all three. A ratio taken from one of them alone is how a normalisation goes wrong.
static double FpChain(long iterations)
{
    double acc = 0.0;
    long ints = 1;
    for (long i = 1; i <= iterations; i++)
    {
        ints = (ints * 1103515245 + 12345) & 0x7FFFFFFF;
        double x = (ints & 0xFFFF) / 65536.0;
        acc += Math.Sqrt(x * x + 1.0) - Math.Sin(x) * 0.5;
        if ((i & 1023) == 0) acc *= 0.9999999;
    }
    return acc;
}

static long Branchy(long iterations)
{
    long acc = 0;
    uint state = 2463534242u;
    for (long i = 0; i < iterations; i++)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        uint v = state & 0xFF;
        if (v < 64) acc += v;
        else if (v < 128) acc -= v >> 1;
        else if (v < 192) acc ^= v;
        else acc += (v & 7) == 0 ? 3 : 1;
        if ((acc & 0xFFFF) == 0) acc += 7;
    }
    return acc;
}

static long MemoryChase(int[] table, long iterations)
{
    long acc = 0;
    int index = 0;
    int mask = table.Length - 1;
    for (long i = 0; i < iterations; i++)
    {
        index = table[index] & mask;   // dependent load: no prefetch, no overlap
        acc += index;
    }
    return acc;
}

static double Best(Func<double> body, int runs)
{
    double best = double.MaxValue;
    for (int i = 0; i < runs; i++) best = Math.Min(best, body());
    return best;
}

int size = 8 * 1024 * 1024;            // 8M ints = 32 MB, well past any L2 and most L3 slices
int[] table = new int[size];
uint seed = 123456789u;
for (int i = 0; i < size; i++)
{
    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
    table[i] = (int)(seed & 0x7FFFFFFF);
}

FpChain(2_000_000); Branchy(2_000_000); MemoryChase(table, 1_000_000);   // warm up JIT and predictors

long fpN = 60_000_000, brN = 200_000_000, memN = 30_000_000;
double fp = Best(() => { Stopwatch sw = Stopwatch.StartNew(); FpChain(fpN); sw.Stop(); return sw.Elapsed.TotalSeconds; }, 3);
double br = Best(() => { Stopwatch sw = Stopwatch.StartNew(); Branchy(brN); sw.Stop(); return sw.Elapsed.TotalSeconds; }, 3);
double mem = Best(() => { Stopwatch sw = Stopwatch.StartNew(); MemoryChase(table, memN); sw.Stop(); return sw.Elapsed.TotalSeconds; }, 3);

Console.WriteLine($"corebench fpchain={fpN / fp / 1e6:F2} branchy={brN / br / 1e6:F2} memory={memN / mem / 1e6:F2} Miter/s");

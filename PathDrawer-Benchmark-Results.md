# PathDrawer Benchmark Results

Unity 6000.6.3f1 | 13th Gen Intel(R) Core(TM) i3-13100F | 30 measured iterations after 3 warmups.

| Input points | RDP points | Output points | RDP median (µs) | Spline + LUT median (µs) | Ribbon median (µs) | Total median (µs) | Total P95 (µs) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 10 | 110 | 115.3 | 115.7 | 29.0 | 256.8 | 276.5 |
| 500 | 42 | 552 | 1022.7 | 628.1 | 119.1 | 1854.0 | 2186.9 |
| 2000 | 172 | 2211 | 5203.6 | 2402.1 | 251.9 | 7802.9 | 9430.1 |

Timed scope: iterative RDP, Burst centripetal spline LUT, uniform-distance resampling, writable MeshData allocation, Burst ribbon generation, and mesh application. Scene rendering and GPU time are excluded.

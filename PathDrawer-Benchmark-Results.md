# PathDrawer Benchmark Results

Unity 6000.6.3f1 | 13th Gen Intel(R) Core(TM) i3-13100F | 30 measured iterations after 3 warmups.

| Input points | RDP points | Output points | RDP median (µs) | Spline + LUT median (µs) | Ribbon median (µs) | Total median (µs) | Total P95 (µs) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 10 | 110 | 125.6 | 124.4 | 32.8 | 288.1 | 341.8 |
| 500 | 42 | 552 | 962.1 | 620.1 | 85.7 | 1681.4 | 1843.5 |
| 2000 | 172 | 2211 | 5397.8 | 2479.9 | 239.5 | 8082.8 | 8502.0 |

Timed scope: iterative RDP, Burst centripetal spline LUT, uniform-distance resampling, writable MeshData allocation, Burst ribbon generation, and mesh application. Scene rendering and GPU time are excluded.

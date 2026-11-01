# Audyt implementacji PathDrawer

**Data aktualizacji:** 1 listopada 2026  
**Unity Editor:** 6000.6.3f1 (Unity 6.6)  
**Wynik:** repozytorium jest samodzielnym projektem Unity `PathDrawer`; przykładowa scena, narzędzia edytora, testy i pomiary zostały sprawdzone w GUI edytora. Interakcyjne rysowanie ścieżki myszą w Game View nie zostało potwierdzone w tym przebiegu.

## Zakres implementacji

### 01 — Path Input

`PathInputDecimator` przechowuje próbki w buforze `NativeList<float3>` o ustalonej pojemności. Odrzuca próbki na podstawie dystansu i kierunku, a iteracyjny RDP obsługuje `ReadOnlySpan<float3>` oraz `NativeArray<float3>` bez rekurencji. Bufory robocze są przydzielane z wyprzedzeniem.

`PathDrawerDemoController` pobiera pozycję wskaźnika przez Input System, z fallbackiem do starego wejścia myszy, rzutuje ją na powierzchnię i łączy etapy pipeline’u podczas przeciągania.

### 02 — Path Geometry

`CentripetalCatmullRomResampler` udostępnia spline Catmull–Rom z α = 0,5, tablicę LUT długości łuku i resampling po stałym dystansie. Tryb balistyczny z prędkością początkową, grawitacją i oporem wykorzystuje ten sam format próbek i API ewaluacji dystansu.

Walidacja granic odrzuca nadmiarowe `LookupSegments` przed planowaniem jobów i sprawdza liczbę próbek względem długości wszystkich buforów natywnych przed ewaluacją.

### 03 — Line Renderer i Ribbon

`PathRenderAdapter` aktualizuje podłączone renderery z tablic natywnych. `BurstRibbonMeshBuilder` zapisuje siatkę do `MeshData` w zadaniu Burst; tworzy indeksy, tangenty i UV ciągłe po długości łuku. Łączenia miter/bevel ograniczają zwężanie na ostrych zakrętach.

### 04 — Trajectory View i batched physics

`TrajectoryPhysicsValidator` używa `RaycastCommand.ScheduleBatch` do projekcji punktów na teren oraz `SpherecastCommand.ScheduleBatch` do sprawdzania przeszkód. Wynik identyfikuje pierwszy punkt blokady, aby adapter renderowania mógł rozdzielić odcinek osiągalny i zablokowany. Scena przykładowa zawiera nierówny teren i przeszkody na jawnie skonfigurowanej warstwie.

## Testy i pomiary

Przed odłączeniem nazw assembly i namespace od wcześniejszego kontenera w Unity Test Runner uruchomiono wszystkie **7 testów: 7 passed, 0 failed, 0 ignored**. Wśród nich są testy redukcji RDP z zachowaniem końców, interpolacji spline i ostrych zakrętów, równomiernego dystansu, walidacji zakresów oraz test alokacji. Po zmianie nazw assembly należy powtórzyć suite po otwarciu checkoutu z nowej lokalizacji.

`FullPipelineFrameLoop_AllocatesZeroManagedBytesAfterWarmup` wykonał 500 rozgrzanych iteracji połączonych etapów wejścia, geometrii i generowania ribbonu z asercją `GC.GetAllocatedBytesForCurrentThread()`. Wynik testu: **0 B zarządzanej alokacji** w mierzonym zakresie testu. Nie jest to pomiar wszystkich ścieżek Unity, zapytań fizyki, renderowania GPU ani dowolnego kodu integrującego.

Benchmark uruchomiono z menu edytora, po trzech rozgrzewkach Burst i 30 mierzonych próbach na każdą wielkość wejścia. Sprzęt: **13th Gen Intel(R) Core(TM) i3-13100F**.

| Wejście | Po RDP | Wynikowe punkty | RDP mediana (µs) | Spline + LUT mediana (µs) | Ribbon mediana (µs) | Całość mediana (µs) | Całość P95 (µs) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 10 | 110 | 125.6 | 124.4 | 32.8 | 288.1 | 341.8 |
| 500 | 42 | 552 | 962.1 | 620.1 | 85.7 | 1681.4 | 1843.5 |
| 2000 | 172 | 2211 | 5397.8 | 2479.9 | 239.5 | 8082.8 | 8502.0 |

Zakres pomiaru obejmuje RDP, spline/LUT, resampling, alokację writable `MeshData`, job ribbonu i zastosowanie siatki. Nie obejmuje czasu GPU ani renderowania sceny. Narzędzie edytora aktualizuje `README.md` oraz [`PathDrawer-Benchmark-Results.md`](PathDrawer-Benchmark-Results.md).

## Reprodukcja w Unity Editor

1. Otwórz projekt w Unity `6000.6.3f1` i poczekaj na import pakietów oraz kompilację.
2. Uruchom `Tools > PathDrawer > 1. Build & Save Ready-to-Play SampleScene`, aby odtworzyć zapisaną scenę demonstracyjną.
3. Otwórz `Window > General > Test Runner`, wybierz EditMode i uruchom wszystkie testy.
4. Uruchom `Tools > PathDrawer > 2. Run Benchmark Sweep & Export Markdown`, aby wykonać serię benchmarków i odświeżyć tabelę.

GUI potwierdziło, że scena i narzędzie benchmarku działają, a konsola nie zgłaszała błędów po tych operacjach. Po zmianie namespace/assembly GUI testów nie powtórzono. Wygenerowane projekty C# zbudowały się po tych zmianach bez błędów; dwa ostrzeżenia dotyczyły konfliktu wersji `System.Numerics.Vectors` w referencjach edytora. Próba przeciągnięcia wskaźnika nie pokazała narysowanej ścieżki, dlatego interakcję runtime należy potwierdzić ręcznie po otwarciu projektu z docelowego katalogu.

## Stan repozytorium

- Repozytorium jest osobnym checkoutem w `unity-trajectory-pathdrawer`; przestrzenie nazw i nazwy assembly definitions używają teraz wyłącznie `PathDrawer`.
- `ProjectSettings` ustawia produkt na `PathDrawer`, a nazwę wydawcy na `Mr444-dev`.
- Root zawiera licencję MIT, benchmark i workflow GitHub Actions; prerelease `com.unity.ai.assistant` nie znajduje się w `Packages/manifest.json`.
- `Assets/Editor/HubForceResolve.cs` nie jest obecny.
- Wyniki bezpieczeństwa z wcześniejszego audytu miały zakres częściowy; ten dokument nie stanowi pełnego audytu zależności ani zachowania edytora Unity.

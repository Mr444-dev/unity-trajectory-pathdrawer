# Audyt implementacji PathDrawer

**Data weryfikacji:** 1 listopada 2026
**Unity Editor:** 6000.6.3f1 (Unity 6.6)  
**Checkout:** samodzielne repozytorium `unity-trajectory-pathdrawer`, poza projektem OmniTwin.

## Wynik

Scena demonstracyjna została zbudowana i zapisana przez menu Unity Editor. Test Runner wykonał **7/7 testów EditMode** bez niepowodzeń; w tym test 500 iteracji całego pipeline’u potwierdził **0 B zarządzanej alokacji GC po rozgrzaniu**. Wykonano benchmark 30 próbek dla każdej z trzech wielkości oraz zapisano obraz Game View 1920×1080 do `docs/screenshots/pathdrawer-game-view.png`.

Tryb Live Showcase działał w Play Mode i wyświetlał spline, blokowany odcinek, łuk balistyczny oraz HUD. Nie potwierdzono jeszcze długiego, ciągłego gestu rysowania myszą w ręcznym teście: dostępny sterownik GUI wykonał kliknięcie, które wyłączyło `Auto Showcase`, ale nie utrzymał przycisku przez wiele klatek. Kod wejścia jest podłączony, lecz pełne rysowanie przez użytkownika pozostaje do sprawdzenia w edytorze lub buildzie.

## Cztery podsystemy Engineering X-Ray

### 01 — Path Input

`PathInputDecimator` przechowuje próbki w buforze `NativeList<float3>` o ustalonej pojemności. Odrzuca ruch poniżej progów dystansu i skrętu, a iteracyjny RDP obsługuje `ReadOnlySpan<float3>` oraz `NativeArray<float3>` bez rekurencji. Bufor stosu i maska punktów są przydzielane z wyprzedzeniem.

`PathDrawerDemoController` czyta `Pointer.current` z fallbackiem do legacy `Input.mousePosition`, rzutuje pozycję na powierzchnię i przekazuje próbki do pipeline’u. W próbce GUI wejście klawiatury potwierdzono: `B`/`S` zmienia tryb, `R` przywraca showcase; kliknięcie wyłączyło `Auto Showcase`.

### 02 — Path Geometry

`CentripetalCatmullRomResampler` generuje spline Catmull–Rom z α = 0,5, tablicę LUT długości łuku oraz punkty oddalone o stały dystans. Tryb balistyczny korzysta z tego samego formatu i ewaluacji według dystansu. Testy sprawdzają interpolację punktów kontrolnych, zachowanie na ostrym skręcie oraz odstęp `0,20 m` z tolerancją poniżej `1e-3 m`.

### 03 — Line Renderer i ribbon

`PathRenderAdapter` przekazuje dane z tablic natywnych do `LineRenderer`. `BurstRibbonMeshBuilder` zapisuje geometrię w writable `MeshData` z zadania Burst; generuje indeksy, tangenty, ciągłe UV długości łuku oraz łączenia miter/bevel. Test alokacji całego pipeline’u obejmuje generowanie ribbonu.

### 04 — Trajectory View i batched physics

`TrajectoryPhysicsValidator` używa `RaycastCommand.ScheduleBatch` do projekcji punktów na teren oraz `SpherecastCommand.ScheduleBatch` do wykrywania blokad. Wynik zachowuje pierwsze trafienie, punkt wpływu i długość osiągalnego odcinka. W świeżo skompilowanym Play Mode nie powtórzyły się wcześniejsze wyjątki safety jobów; poprawka jawnie wywołuje `Complete()` przed odczytem wyników batcha.

## Testy i benchmark

Test Runner EditMode: **7 passed, 0 failed, 0 ignored**. Test `FullPipelineFrameLoop_AllocatesZeroManagedBytesAfterWarmup` mierzy 500 rozgrzanych iteracji Path Input + geometrii + writable-MeshData ribbonu przy użyciu `GC.GetAllocatedBytesForCurrentThread()` i przeszedł asercję `0 B`. Osobny test mierzy 1 000 przebudów geometrii.

Benchmark uruchomiono z menu Unity Editor. Metoda wykonuje 3 rozgrzewki i 30 mierzonych próbek dla każdej wielkości. Sprzęt: **13th Gen Intel(R) Core(TM) i3-13100F**; Unity **6000.6.3f1**.

| Wejście | Po RDP | Punkty wyjściowe | RDP mediana (µs) | Spline + LUT mediana (µs) | Ribbon mediana (µs) | Całość mediana (µs) | Całość P95 (µs) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 10 | 110 | 115.3 | 115.7 | 29.0 | 256.8 | 276.5 |
| 500 | 42 | 552 | 1022.7 | 628.1 | 119.1 | 1854.0 | 2186.9 |
| 2000 | 172 | 2211 | 5203.6 | 2402.1 | 251.9 | 7802.9 | 9430.1 |

Zakres obejmuje iteracyjny RDP, Burst spline/LUT, uniform-distance resampling, alokację writable `MeshData`, generowanie ribbonu i zastosowanie siatki. Nie obejmuje GPU, renderowania sceny ani zapytań fizycznych. Pełna tabela i opis środowiska są w [`PathDrawer-Benchmark-Results.md`](PathDrawer-Benchmark-Results.md).

## Scena i zrzut ekranu

Menu `Tools > PathDrawer > 1. Build & Save Ready-to-Play SampleScene` wygenerowało `Assets/Scenes/SampleScene.unity` z terenem, plateau i rampami, przeszkodami na warstwie `PathDrawerObstacles`, kamerą perspektywiczną, materiałami URP, rendererami linii/siatki, znacznikami wpływu, HUD-em i podłączonym kontrolerem.

Menu `Tools > PathDrawer > 3. Capture 1080p Portfolio Screenshot` uruchomiło scenę, odczekało 60 klatek i zapisało `docs/screenshots/pathdrawer-game-view.png`. Obraz został obejrzany: pokazuje HUD, siatkę terenu, trzy przeszkody, osiągalną turkusową ścieżkę, czerwony zablokowany odcinek i żółty łuk balistyczny.

## Odtworzenie w GUI Unity

1. Otwórz samodzielny katalog repozytorium w Unity `6000.6.3f1` i poczekaj na import oraz kompilację.
2. Jeśli pliki `.cs` zmieniono poza edytorem, użyj `Assets > Refresh`; w tym przebiegu ten krok był potrzebny do przeładowania źródeł.
3. Uruchom `Tools > PathDrawer > 1. Build & Save Ready-to-Play SampleScene`.
4. Otwórz `Window > General > Test Runner`, wybierz EditMode i kliknij `Run All`.
5. Uruchom `Tools > PathDrawer > 2. Run Benchmark Sweep & Export Markdown`.
6. Uruchom `Tools > PathDrawer > 3. Capture 1080p Portfolio Screenshot`.
7. Wejdź w Play Mode, wybierz tryb `S`/`B`, rysuj lewym przyciskiem myszy i wciśnij `R`, aby przywrócić showcase.

## Znane ograniczenia weryfikacji

- Po `Assets > Reimport All` Unity odbudowało cache i ponownie otworzyło właściwy projekt. Podczas odtwarzania cache wystąpiły przejściowe błędy importu edytora Input System oraz błąd kompilacji `CS1503` w edytorowym hooku zrzutu ekranu. Hook zmieniono na wyszukanie obiektu sceny o ustalonej nazwie; po `Assets > Refresh` skrypty skompilowały się, błędy importu Input System zniknęły po zakończeniu importu, a testy EditMode ponownie przeszły 7/7.
- Podczas importu pakietu URP pojawił się komunikat edytora: `Host type is not matching any asset type` dla `Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/RenderingLayerMask/TraceRenderingLayerMask.urtshader`. To zasób pakietu URP, a renderowanie sceny, kompilacja skryptów i testy działały. Nie znaleziono potwierdzonej poprawki w repozytorium; dlatego nie deklaruję czystego logu Unity na każdej maszynie.
- Testy i benchmark wykonano z GUI Unity. Push `7fd32ec` uruchomił GitHub Actions, ale GameCI nie mógł aktywować Unity, ponieważ sekret `UNITY_LICENSE` nie jest skonfigurowany. Workflow zaktualizowano, by zawsze sprawdzał metadane projektu i jawnie pomijał testy EditMode bez licencji; nowy wynik CI należy oceniać po kolejnym pushu. Nie uruchomiono builda standalone.
- Automatyzacja potwierdziła wejście klawiatury i reakcję na kliknięcie, ale nie sprawdziła ciągłego gestu myszy przez wiele klatek.
- `0 B` dotyczy zarządzanej alokacji w zakresie testu po rozgrzaniu. Writable `MeshData`, pamięć natywna, synchronizacja jobów i upload GPU są osobnymi kosztami.

## Repozytorium i zależności

- Projekt jest osobnym repozytorium `unity-trajectory-pathdrawer`, a nie podprojektem OmniTwin.
- `Packages/manifest.json` nie zawiera prerelease `com.unity.ai.assistant`.
- Główny katalog zawiera licencję MIT i workflow GitHub Actions dla EditMode.
- `Assets/Editor/HubForceResolve.cs` nie znajduje się w checkoutcie.

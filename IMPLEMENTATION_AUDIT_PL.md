# Audyt implementacji PathDrawer

**Data:** 31 października 2026
**Zakres:** przegląd przed publikacją repozytorium unity-trajectory-pathdrawer
**Wynik:** cztery subsystemy są zaimplementowane jako biblioteka C# dla Unity, z testami EditMode i narzędziem benchmarkowym. Nie są jeszcze potwierdzone końcową kompilacją ani pomiarami na docelowym urządzeniu.

## Co zostało zbudowane

### 01 — Path Input

PathInputDecimator przechowuje próbki w buforze NativeList<float3> o pojemności ustalonej przy inicjalizacji. Odrzuca próbki poniżej progu odległości, z histerezą kierunku ograniczającą jitter i nadmiar punktów. Uproszczenie RDP działa iteracyjnie, bez rekurencji i zarządzanych kolekcji; stos oraz maska punktów są przekazywane jako wcześniej utworzone bufory. Dostępna jest ścieżka oparta o ReadOnlySpan<float3> i o NativeArray<float3>.

API przyjmuje pozycje świata od kodu integrującego. Repozytorium nie zawiera komponentu MonoBehaviour pobierającego pointer/touch, konfiguracji wejścia PathDrawer w scenie ani gotowego przepływu UI.

### 02 — Path Geometry

CentripetalCatmullRomResampler udostępnia ewaluację przedziału centripetalnego Catmull–Rom z α = 0.5 oraz zadania Burst budujące gęstą tablicę LUT. Drugi job kumuluje długość i wyszukuje przedziały binarnie, aby generować punkty co zadany dystans. Tryb balistyczny z prędkością początkową, grawitacją, oporem i czasem trwania korzysta z tej samej ścieżki resamplingu. Wynik zawiera pozycje, styczne, dystanse i długość; EvaluateAtDistance umożliwia odczyt pozycji po przebytej drodze.

W tym audycie naprawiłem dwie walidacje granic:

- liczba próbek balistycznych jest teraz liczona w long i porównywana z pojemnością bufora przed rzutowaniem i zaplanowaniem jobów;
- publiczny evaluator odrzuca dodatni count, jeśli przekracza długość któregokolwiek z trzech buforów natywnych; job resamplera sprawdza count i bufory przed indeksowaniem.

Przed poprawkami przegląd bezpieczeństwa wykrył możliwość przepełnienia LookupSegments + 1 i odczytu poza NativeArray, a także odczytów poza buforami w EvaluateAtDistance. Dodałem testy regresyjne dla obu przypadków. To błędy w publicznym API biblioteki; w projekcie nie ma ścieżki sieciowej ani wejścia użytkownika, które same przekazywałyby te wartości.

### 03 — Line Renderer i Ribbon

PathRenderAdapter przekazuje pozycje natywne do LineRenderer bez tworzenia zarządzanej tablicy. Rezerwuje dodatkowy slot na punkt kontaktu przeszkody i przycina odcinek osiągalny w miejscu trafienia; odcinek zablokowany jest opcjonalny. Ostatnia poprawka zapewnia prawidłowe przycięcie także wtedy, gdy drugi renderer nie został podłączony.

BurstRibbonMeshBuilder tworzy MeshData przez writable MeshData API i wypełnia bufor w zadaniu Burst. Mesh zawiera wierzchołki krawędzi, normalne, tangenty, UV oparte o skumulowaną długość oraz indeksy. Miter jest ograniczony, a tryb bevel rozdziela krawędzie i wypełnia narożnik, by uniknąć zwężenia przy ostrych zakrętach.

### 04 — Trajectory View i batched physics

TrajectoryPhysicsValidator korzysta z RaycastCommand.ScheduleBatch do projekcji ścieżki na teren i z SpherecastCommand.ScheduleBatch do wykrywania przeszkód. Wyniki zawierają odcinek osiągalny, pierwszą blokadę i dystans trafienia do przekazania warstwie renderowania.

Zapytania są zbatchowane, ale przepływ nie jest w pełni pozbawiony synchronizacji: przed planowaniem rzutów na przeszkody kod kończy zależny job projekcji. Integrator musi wybrać maski warstw i zarządzać cyklem życia natywnych buforów oraz komponentów Unity.

## Pamięć i wydajność

Bufory pracy są przydzielane podczas inicjalizacji i mają stałą pojemność; ścieżki runtime nie powinny powiększać ich w trakcie pracy. Jedyny test alokacji mierzy 1000 rozgrzanych przebudów geometrii spline. Nie dowodzi on wyniku 0 B dla wejścia, walidacji fizyki, aktualizacji LineRenderer i mesha jako jednego pełnego frame-loopa.

Benchmark w PathDrawerBenchmarkRunner przygotowuje pomiar RDP, spline/LUT, ribbonu i całości dla wejść 100, 500 i 2000 punktów. Rozgrzewa Burst, zbiera 30 prób i raportuje medianę oraz P95 do PathDrawer-Benchmark-Results.md. **Próba uruchomienia batch nie wygenerowała wyników**: Unity Licensing zgłosił brak dostępnego tokenu, a metoda benchmarku nie została wykonana. Tabela w README zawiera Not recorded; nie ma podstaw do podawania liczb ani porównywania urządzeń.

## Testy i weryfikacja

Kod testów EditMode obejmuje redukcję RDP i zachowanie końców, interpolację kontroli spline oraz próbki ostrych zakrętów, równomierny dystans punktów na nierówno rozłożonych kontrolach, pomiar alokacji po rozgrzaniu oraz nowe regresje count/bounds. Testów nie uruchomiono w tej sesji.

Projekt miał wcześniejszą kompilację w Unity 6000.6.3f1, lecz nastąpiła ona przed końcową zmianą adaptera i przed dodanymi poprawkami walidacji. Końcowa wersja wymaga ponownej kompilacji oraz uruchomienia EditMode Test Runnera. W repozytorium nie skonfigurowano CI ani automatycznego workflow budowania.

Połączenie Unity MCP w tej sesji wskazywało na sąsiedni projekt OmniTwin.Unity, a nie na ten checkout. Nie użyto go jako dowodu kompilacji PathDrawer. Próba otwarcia tego checkoutu w trybie batch zakończyła się błędem Unity Licensing „Access token is unavailable”; nie uzyskano końcowej kompilacji ani benchmarku.

## Przegląd publikacyjny i bezpieczeństwo

- Usunięto Assets/Editor/HubForceResolve.cs, skrypt InitializeOnLoad, który automatycznie wywoływał resolve pakietów i usuwał własny plik. Ta zmiana musi być częścią opublikowanego commita.
- W przejrzanych polach Unity PlayerSettings nie znaleziono niepustego hasła ani klucza. Projektowe flagi usług Unity są wyłączone lub niekonfigurowane. To przegląd źródła i ustawień, nie audyt zachowania samego Unity Editor.
- ProjectSettings/Packages/com.unity.ai.assistant/Settings.json pozostaje plikiem lokalnym ignorowanym przez .gitignore; nie jest częścią repozytorium. Manifest nadal zawiera prerelease Unity AI Assistant, a zawartości pakietu, jego praktyk transmisji danych i aktualnych advisories nie badano.
- Audyt bezpieczeństwa repozytorium zakończył się z pokryciem **częściowym**. Raport nie twierdzi, że wszystkie 96 plików ani kod zewnętrznych pakietów zostały sprawdzone.
- Przegląd repozytorium nie znalazł implementacji pobierania pointer/touch ani skonfigurowanych masek fizyki w scenie. Integrator dostarcza te wartości.
- Nie dodano LICENSE, przykładowej integracji runtime ani benchmarków liczbowych. Publiczne repozytorium nie będzie więc określało licencji ponownego użycia kodu.

## Lista przed oznaczeniem gotowości produkcyjnej

1. Skompilować projekt w Unity 6000.6.3f1 po finalnych zmianach.
2. Uruchomić wszystkie testy EditMode, w tym regresje przepełnienia i długości buforów.
3. Uruchomić benchmark na udokumentowanym sprzęcie i wkleić wygenerowane metryki.
4. Zdecydować, czy Unity AI Assistant prerelease ma pozostać zależnością projektu.
5. Dodać licencję i CI, jeśli repozytorium ma być dystrybuowane lub utrzymywane jako pakiet.

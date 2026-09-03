# BorderlessMouse

<p align="center"><img src="assets/logo/logo-256.png" width="128" alt="BorderlessMouse"></p>

[![CI](https://github.com/pkozubski/BorderlessMouse/actions/workflows/ci.yml/badge.svg)](https://github.com/pkozubski/BorderlessMouse/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/pkozubski/BorderlessMouse)](https://github.com/pkozubski/BorderlessMouse/releases/latest)

Współdzielenie **klawiatury i myszy**, **schowka** oraz **dźwięku** między Windows a macOS
po sieci lokalnej – bez sterowników, konta i chmury, z uwierzytelnionym szyfrowaniem.

Scenariusz, na który jest zbudowana ta wersja:

* **Windows → Mac**: fizyczna klawiatura i mysz podpięte do Windowsa sterują Makiem
  (kursor przechodzi przez krawędź ekranu jak w Synergy/Barrier).
* **Mac → Windows**: cały dźwięk systemowy Maca gra na słuchawkach/głośnikach Windowsa.
* **Schowek w obie strony**: kopiuj i wklejaj tekst, zdjęcia i zrzuty ekranu
  między komputerami (bez przełączania kursora). Zmiany wykrywamy co ok. 0,5 s;
  czas przesłania obrazu zależy od jego wielkości i sieci.
* **Autostart**: obie aplikacje mogą uruchamiać się przy logowaniu i czekać w tle
  (pasek menu / zasobnik).

```
┌──────────────── Windows ────────────────┐        ┌──────────────── macOS ─────────────────┐
│ hooki WH_MOUSE_LL / WH_KEYBOARD_LL      │  TCP   │ NWListener :47800                      │
│ → AES-256-GCM → TcpClient (NODELAY)     │ ─────▶ │ → CGEvent (mysz, klawiatura)           │
│                                         │        │                                        │
│ UDP :47802 → AES-GCM → jitter → WASAPI  │ ◀───── │ Core Audio process tap (14.2+) → UDP   │
│ (NAudio, shared/exclusive)              │  UDP   │ szyfrowany PCM 16-bit, ~5 ms/pakiet     │
│                                         │        │                                        │
│ broadcast "BLM2?" → lista Maców         │ ◀───── │ odpowiedź discovery :47801              │
│ schowek (GetClipboardSequenceNumber)    │ ◀────▶ │ schowek (NSPasteboard.changeCount)      │
└─────────────────────────────────────────┘        └────────────────────────────────────────┘
```

Połączenie wymaga kodu z Maca. Wzajemne uwierzytelnienie HMAC-SHA-256 tworzy dla każdej
sesji osobne klucze sterowania i audio; licznik pakietów blokuje ich ponowne odtworzenie.
Szczegóły: [PROTOCOL.md](PROTOCOL.md), [polityka bezpieczeństwa](SECURITY.md) i
[informacja o prywatności](PRIVACY.md).

## Wymagania

| | |
|---|---|
| macOS | 14.2 lub nowszy (Core Audio taps). Uprawnienia: **Dostępność** i **Nagrywanie dźwięku systemowego**. |
| Windows | Windows 10/11 x64. Plik `BorderlessMouse-Windows-x64.exe` z [Releases](https://github.com/pkozubski/BorderlessMouse/releases/latest) nie wymaga instalowania .NET. |
| Sieć | Obie maszyny w tej samej sieci LAN (najlepiej kabel; Wi‑Fi też działa). |

## Pobieranie

Gotowe pliki są w [GitHub Releases](https://github.com/pkozubski/BorderlessMouse/releases/latest):

* `BorderlessMouse-macOS.zip` – uniwersalna aplikacja (Apple Silicon + Intel). Rozpakuj i przenieś
  do `~/Applications` (auto-updater potrzebuje prawa zapisu do katalogu aplikacji).
* `BorderlessMouse-Windows-x64.exe` – pojedynczy plik, bez instalatora.
* `SHA256SUMS.txt` – sumy kontrolne używane też przez updater.

Publiczne wydania komercyjne muszą być podpisane certyfikatem Apple Developer ID,
notaryzowane i staplowane, a plik Windows podpisany Authenticode z zaufanym znacznikiem
czasu. Workflow wydania zatrzymuje publikację, jeśli któregokolwiek poświadczenia brakuje.

## Autostart

Przełącznik **Uruchamiaj przy logowaniu** jest w karcie „Ustawienia” (macOS) i w grupie
„Uruchamianie” (Windows). Stan jest czytany z systemu, więc zgadza się z tym, co widać
w ustawieniach systemowych, nawet po ręcznej zmianie.

| | Mechanizm | Gdzie to widać |
|---|---|---|
| macOS | `SMAppService.mainApp` (element logowania). Gdy system go odrzuci, aplikacja instaluje LaunchAgent w `~/Library/LaunchAgents/com.borderlessmouse.mac.login.plist`. | Ustawienia systemowe → Ogólne → Elementy logowania |
| Windows | Wpis `BorderlessMouse` w `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (bez uprawnień administratora). | Menedżer zadań → Aplikacje autostartu |

Przy starcie z logowania aplikacja domyślnie nie otwiera okna: macOS pokazuje tylko ikonę
w pasku menu, Windows tylko ikonę w zasobniku (można to wyłączyć obok przełącznika
autostartu). Wpis autostartu jest automatycznie poprawiany, jeśli plik aplikacji zmieni
ścieżkę, np. po przeniesieniu folderu.

Diagnostyka: `BorderlessMouse.app/Contents/MacOS/BorderlessMouse --login-item-test` włącza
autostart, wypisuje użyty mechanizm i przywraca poprzedni stan.

## Interfejs

Przy pierwszym uruchomieniu obie aplikacje prowadzą przez parowanie i wymagane zgody.
Interfejs automatycznie używa polskiego dla polskiego języka systemu, a angielskiego dla
pozostałych. Obie aplikacje mają natywny dla swojego systemu pasek boczny z sekcjami
(Połączenie, Uprawnienia – tylko Mac, Sterowanie, Ustawienia, Dziennik). Każda sekcja to
jedna przewijana strona z grupami (np. Sterowanie: klawiatura i mysz, dźwięk, schowek;
Ustawienia: ogólne, uruchamianie, aktualizacje). Wiersze to „tytuł + opis + kontrolka”.

* macOS: SwiftUI `NavigationSplitView` (sidebar jak w Ustawieniach systemowych) i
  `Form(.grouped)`; wskaźnik stanu jest elementem paska narzędzi.
* Windows: FluentAvalonia `NavigationView` (WinUI 3, Mica, systemowy kolor akcentu) z
  kartami na stronach; komunikaty (aktualizacja, brak uprawnienia na Macu) są nad treścią
  każdej strony.

Wygląd kontrolek na Macu zależy od SDK, z którym zlinkowano aplikację: build z SDK macOS 26
dostaje na Tahoe nowy wygląd systemu (szklane przełączniki, pigułkowe przyciski), starszy SDK
daje tryb kompatybilności sprzed Tahoe. `build.sh` wybiera najnowszy SDK z Command Line Tools
i Xcode, a CI buduje na `macos-26` z wymogiem `REQUIRE_SDK_MAJOR=26`.

Podgląd wyglądu bez uruchamiania sieci i uprawnień:

* macOS: `BorderlessMouse.app/Contents/MacOS/BorderlessMouse --ui-preview zrzut.png
  [--ui-section connection|permissions|control|settings|log]` – okno pojawia się na
  ekranie na około sekundę (sidebar i toolbar renderuje dopiero kompozytor).
* Windows: `BorderlessMouse.exe --screenshot zrzut.png` (zapisuje pierwszą stronę pod tą
  nazwą, a pozostałe jako `zrzut-control.png`, `zrzut-settings.png`, `zrzut-log.png`).

W tym trybie aplikacja nie łączy się z niczym i pokazuje przykładowe dane.

## Auto-updater

Obie aplikacje sprawdzają najnowsze wydanie na GitHubie (5 s po starcie i co 6 godzin, można
wyłączyć w karcie **Aktualizacje**). Aktualizacja jest pobierana, weryfikowana sumą SHA-256 i
instalowana jednym kliknięciem, po czym aplikacja uruchamia się ponownie.

* **Windows**: updater wymaga SHA-256 i prawidłowego Authenticode od przypiętego certyfikatu
  wydawcy. Podmiana zachowuje poprzedni plik do chwili poprawnego restartu i cofa zmianę
  po błędzie.
* **macOS**: updater wymaga SHA-256 i podpisu zgodnego z publicznym certyfikatem wydawcy.
  Bundle jest podmieniany dopiero po weryfikacji, z lokalną kopią do wycofania operacji.

Nowe wydanie robi się jednym tagiem:

```bash
git tag v1.1.0 && git push --tags
```

Workflow `.github/workflows/release.yml` buduje obie aplikacje, liczy sumy i publikuje release.

Konfiguracja podpisów, notaryzacji i bramek wydania jest opisana w
[docs/RELEASE.md](docs/RELEASE.md). Klucze prywatne nie trafiają do repozytorium ani
artefaktów i są dostępne tylko w chronionym środowisku `production-release`.

CI i wydanie sprawdzają zgodność podpisu dwóch różnych buildów oraz odrzucanie
uszkodzonych, niepodpisanych i podpisanych ad-hoc aktualizacji.

## Uruchomienie krok po kroku

### 1. Mac

Zbudowana lokalnie aplikacja: `macos/build/BorderlessMouse.app` (po `./build.sh`).

1. Uruchom aplikację. Kreator pokaże kod parowania i stan gotowości Maca.
2. W kreatorze lub karcie **Uprawnienia macOS** kliknij **Poproś** przy „Dostępność” i włącz
   BorderlessMouse w *Ustawienia systemowe → Prywatność i ochrona → Dostępność*.
3. Przy pierwszym streamie audio macOS zapyta o **nagrywanie dźwięku systemowego** – zgódź się
   (*Prywatność i ochrona → Nagrywanie ekranu i dźwięku systemowego*).
4. Na macOS 15+ może pojawić się pytanie o **Sieć lokalną** – również zgódź się.
5. Jeśli zapora macOS jest włączona, zezwól na połączenia przychodzące.

Aplikacja ma też ikonę w pasku menu z szybkimi przełącznikami.

### 2. Windows

1. Uruchom `BorderlessMouse.exe` (przy pierwszym starcie Zapora Windows zapyta o dostęp – zaznacz
   **Sieci prywatne**; bez tego nie dotrze dźwięk UDP).
2. Przepisz kod parowania wyświetlony przez Maca. Jest chroniony przez DPAPI dla bieżącego
   konta Windows i nie trafia do zwykłego pliku ustawień.
3. Mac pojawi się na liście **Maki w sieci lokalnej** – wybierz go (albo wpisz IP) i **Połącz**.
4. Ustaw, po której stronie ekranu stoi Mac (domyślnie *po lewej*) oraz wybierz skrót
   awaryjny: **Scroll Lock**, **Pause/Break** albo **F12**.
5. Przesuń mysz przez tę krawędź – kursor przechodzi na Maca, a kursor Windows zostaje
   (ukryty) w miejscu przekroczenia. Ruch myszy jest czytany przez Raw Input, więc nie ma
   akceleracji Windows; tempo dostroisz suwakiem „Czułość myszy na Macu”. Powrót: przesuń
   kursor przez przeciwną krawędź Maca albo naciśnij wybrany skrót (działa w obie strony).
6. Dźwięk: włączony domyślnie; wybierz urządzenie wyjściowe i ewentualnie zmniejsz bufor.
7. Schowek: włączony domyślnie po obu stronach; synchronizowany jest
   tekst do 1 MiB oraz zdjęcia i zrzuty ekranu do 32 MiB w PNG (maks. 64 × 1024² pikseli).
   Użyj „Kopiuj obraz” w przeglądarce/edytorze lub skopiuj zrzut ekranu do schowka,
   a następnie wklej go w aplikacji obsługującej obrazy na drugim komputerze.
   Obie aplikacje muszą być zaktualizowane. Kopiowanie plików w Finderze/Eksploratorze
   i formatowanie tekstu nie są obsługiwane.

Ustawienia są zapisywane w `%APPDATA%\BorderlessMouse\settings.json`; zamknięcie okna chowa
aplikację do zasobnika (wyjście przez menu ikony).

### Gry i pełny ekran

Automatyczne przejście kursora na Maca jest wstrzymane, gdy aktywna aplikacja zajmuje
pełny ekran (także okno bez ramek), ukrywa kursor lub ogranicza jego ruch. Chroni to
przed przypadkowym oddaniem sterowania podczas grania, również gdy gra nie zatrzymuje
systemowego kursora na środku ekranu. Dotyczy to także innych aplikacji pełnoekranowych.

Po **Alt+Tab** do zwykłego okna lub na pulpit przełączanie krawędzią wraca, gdy aplikacja
zwolni mysz. Wybrany skrót awaryjny nadal przełącza ręcznie w obie strony, także podczas gry.
Dźwięk i schowek działają przez cały czas.

## Opóźnienie i wydajność

* **Wejście**: TCP z `TCP_NODELAY`; ramki sterowania są chronione AES-256-GCM i trafiają
  bezpośrednio do `CGEvent.post` po weryfikacji integralności i licznika sesji.
* **Audio**: AES-256-GCM, bez kodeka (PCM 16-bit, 1,5 Mb/s przy 48 kHz stereo), pakiety co ~5 ms,
  bufor jitter domyślnie 20 ms, WASAPI shared 15 ms (exclusive: 5 ms). Realne opóźnienie
  end-to-end zwykle 30–45 ms. Na kablu można zejść z buforem do 5–10 ms.
* Mac przechwytuje dźwięk przez **Core Audio process tap** – bez BlackHole/Soundflower;
  opcja „Wycisz głośniki Maca” używa `muteBehavior = .mutedWhenTapped`, więc dźwięk słychać
  tylko na Windowsie.

## Mapowanie klawiszy

Klawisze mapowane są **po scancode** (pozycja fizyczna), więc układ klawiatury jest
niezależny od systemu. Domyślnie *Ctrl ↔ Cmd* są zamienione (Ctrl+C na PC = ⌘C na Macu),
można to wyłączyć na Macu. Win = ⌘, Alt = ⌥, PrintScreen = F13, Insert = Help,
klawisze multimedialne (głośność, play/pause, next/prev) działają.

## Budowanie

### macOS

```bash
cd macos
./build.sh                # swiftc, wystarczą Command Line Tools → build/BorderlessMouse.app
CONFIG=debug ./build.sh   # bez optymalizacji
# Xcode:
xcodegen generate         # brew install xcodegen
open BorderlessMouse.xcodeproj
```

Uwaga: build ad-hoc (`build.sh` bez `SIGN_IDENTITY`) zmienia sygnaturę przy każdej
kompilacji. macOS pokazuje wtedy aplikację jako włączoną w liście Dostępności, ale
`AXIsProcessTrusted` zwraca `false` i Mac natychmiast odsyła kursor do Windowsa.
Objaw po stronie Windows: w dzienniku „Mac natychmiast oddał sterowanie”.
Rozwiązania:

* usuń BorderlessMouse z listy *Dostępność* (minus) i dodaj ponownie po każdym buildzie, albo
* podpisuj stałym certyfikatem, wtedy uprawnienia zostają:
  * Xcode: wybierz swój Team w ustawieniach targetu, lub
  * bez konta developerskiego: w *Dostęp do pęku kluczy → Asystent certyfikatów → Utwórz
    certyfikat* (typ: *Podpisywanie kodu*, nazwa np. `BorderlessMouse Dev`), potem
    `SIGN_IDENTITY="BorderlessMouse Dev" ./build.sh`.

### Windows

```powershell
cd windows\BorderlessMouse
dotnet build -c Release                                  # wymaga .NET 8 SDK
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\publish\win-x64
```

Projekt kompiluje się także na macOS/Linux (Avalonia); logika Windows (hooki, WASAPI)
jest oznaczona `[SupportedOSPlatform("windows")]`.

## Struktura

```
PROTOCOL.md                  – opis protokołu (TCP + UDP)
macos/
  build.sh                   – build bez Xcode
  project.yml                – XcodeGen
  BorderlessMouse/Sources/
    App/        Engine (logika), AppState (UI), Settings, ClipboardSync, Updater, LoginItem, UIPreview
    Security/   kod parowania, Keychain, HMAC/HKDF, bezpieczna sesja AES-GCM
    Localization/ polski i angielski interfejs
    Network/    ControlServer (TCP), DiscoveryResponder (UDP), AudioSender (UDP)
    Input/      InputInjector (CGEvent), KeyMap (scancode → kVK)
    Audio/      SystemAudioTap (Core Audio process tap)
    Views/      ContentView (sidebar + strony), Components (SettingRow, StatusPill), pasek menu
assets/logo/                 – wspólne logo + skrypt generujący .icns/.ico/.png
.github/workflows/           – CI (build obu aplikacji) i Release (tag v* → GitHub Release)
windows/
  BorderlessMouse/
    Input/      NativeMethods, LowLevelHooks, NativeInputWindow (Raw Input + hider), InputCapture
    Models/     Settings, Autostart (klucz Run w rejestrze)
    Security/   kod parowania, DPAPI, HMAC/HKDF, AES-GCM, weryfikacja Authenticode
    Localization/ polski i angielski interfejs
    Net/        ControlClient (TCP), Discovery (UDP), AudioReceiver (UDP), ClipboardSync, Updater
    Audio/      JitterBufferProvider, AudioPlayer (NAudio/WASAPI)
    Views/      MainWindow.axaml (NavigationView w AppWindow z Mica), Pages/ (strony), SettingRow
    ViewModels/ MainViewModel
```

## Użyte projekty open source

* [Avalonia UI](https://avaloniaui.net) (MIT) – interfejs Windows
* [FluentAvalonia](https://github.com/amwx/FluentAvalonia) (MIT) – style i kontrolki WinUI 3 (Fluent Design v2, Mica, kolor akcentu systemu)
* [NAudio](https://github.com/naudio/NAudio) (MIT) – WASAPI
* [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT)
* [Inter](https://github.com/rsms/inter) (SIL OFL 1.1) – krój pisma interfejsu Windows
* ANGLE (licencja BSD) oraz SkiaSharp/HarfBuzzSharp (MIT) – natywne renderowanie Avalonia
* Mechanika przełączania krawędzią i hooków wzorowana na Synergy/Barrier/Deskflow (GPL – kod nie jest kopiowany).

## Znane ograniczenia / co dalej

* Schowek synchronizuje tekst i obrazy; nie przesyła plików ani formatowania tekstu.
  Animowane obrazy są przesyłane jako pojedyncza klatka PNG.
* Kierunek tylko Windows → Mac (wejście) i Mac → Windows (dźwięk); protokół jest gotowy na
  rozszerzenie o kierunek odwrotny.
* Caps Lock jest przekazywany jako zwykły klawisz – macOS może go ignorować.
* Audio jest szyfrowane, ale nieskompresowane; przy 48 kHz stereo zużywa około 1,5 Mb/s.
* Protokół v2 nie łączy się ze starszymi buildami korzystającymi z protokołu v1.

## Komercyjne wydanie

Kod aplikacji jest objęty zastrzeżoną licencją [LICENSE](LICENSE). Przed płatną betą należy
uzupełnić dane sprzedawcy i jurysdykcję w [szkicu EULA](docs/EULA-DRAFT.md), skonfigurować
podpisy według [runbooka wydania](docs/RELEASE.md) i wykonać scenariusze z
[planu płatnej bety](docs/BETA-PLAN.md). Licencje zależności są zebrane w
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

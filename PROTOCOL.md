# BorderlessMouse – bezpieczny protokół LAN v2

Wszystkie liczby wielobajtowe są little-endian. Napisy są kodowane jako UTF-8.
Protokół v1 nie jest akceptowany, ponieważ nie uwierzytelniał sterowania ani schowka.

## Porty i wykrywanie

| Kanał | Transport | Port | Kierunek |
|---|---|---:|---|
| Discovery | UDP | 47801 | Windows → broadcast → Mac |
| Sterowanie | TCP | 47800 | Windows → Mac |
| Audio | UDP | 47802 (konfigurowalny) | Mac → Windows |
| Wideo ekranu wirtualnego | TCP | losowy, jednorazowy (z `DISPLAY_READY`) | Windows łączy się z Makiem, obraz płynie Mac → Windows |

Windows wysyła `BLM2?`. Mac odpowiada `BLM2!` + `u16 tcpPort` + nazwa.
Discovery nie zawiera kodu, klucza ani danych użytkownika.

## Parowanie i handshake

Mac generuje losowy sekret 128-bitowy, pokazuje go jako kod Base32 i zapisuje
w macOS Keychain (`AfterFirstUnlockThisDeviceOnly`). Windows po wpisaniu kodu
zapisuje sekret przez DPAPI dla bieżącego konta. Kod nigdy nie trafia do zwykłych
ustawień ani logów.

Zewnętrzna ramka TCP: `u8 type`, `u8 len`, payload. Jeśli `len == 0xFF`, po
nagłówku występuje `u32 length`. Limit koperty wynosi 32 MiB + narzut AEAD.

1. `HELLO (0x01)`: `u8 version=2`, `16 B clientNonce`, nazwa Windows.
2. `CHALLENGE (0x02)`: `u8 version=2`, `16 B serverNonce`, `32 B serverProof`, nazwa Maca.
3. Windows porównuje `serverProof = HMAC-SHA256(secret, "BorderlessMouse/v2/server" || nonces)`.
4. `AUTHENTICATE (0x03)`: `clientProof = HMAC-SHA256(secret, "BorderlessMouse/v2/client" || nonces)`.
5. Obie strony wyprowadzają osobne klucze C→S, S→C i audio przez HKDF-SHA256.
6. Mac wysyła zaszyfrowane `READY (0x06)`. Dopiero wtedy połączenie jest aktywne.

Handshake ma limit pięciu sekund. Nieudane uwierzytelnienie uruchamia krótkie
ograniczenie częstotliwości. Nowe połączenie nigdy nie zastępuje aktywnej sesji.
Wygenerowanie nowego kodu na Macu natychmiast unieważnia poprzedni dostęp.

## Szyfrowany kanał sterowania

Wiadomości aplikacyjne są pełnymi ramkami umieszczonymi w `SECURE (0x04)`:

```
u64 counter
ciphertext
16 B AES-256-GCM tag
```

Nonce ma postać `"BLM2" || counter`. AAD to również `"BLM2" || counter`.
Każdy kierunek używa innego klucza. TCP zachowuje kolejność, dlatego licznik
musi rosnąć ściśle; powtórzenie, przestawienie lub modyfikacja kończy sesję.

| Typ | Nazwa | Kierunek | Payload |
|---:|---|---|---|
| 0x10 | MOUSE_MOVE | W → M | `i16 dx`, `i16 dy` |
| 0x11 | MOUSE_BUTTON | W → M | `u8 button`, `u8 down` |
| 0x12 | MOUSE_WHEEL | W → M | `i16 dx`, `i16 dy` |
| 0x13 | MOUSE_ABSOLUTE | W → M | `u16 x`, `u16 y` (0…65535 na ekranie wirtualnym, tryb okien) |
| 0x20 | KEY | W → M | `u16 scancode`, `u16 vk`, `u8 flags` |
| 0x21 | RELEASE_ALL | W → M | – |
| 0x30 | ENTER | W → M | `u8 edge`, `f32 ratio` |
| 0x31 | LEAVE | M → W | `u8 edge`, `f32 ratio`, opcjonalnie `u8 flags` (bit 0: kursor wyszedł z ekranu wirtualnego) |
| 0x32 | WINDOW_ENTER | W → M | `u16 x`, `u16 y` – kursor Windows nad oknem Maca |
| 0x33 | WINDOW_LEAVE | W → M | `u8 keepKeyboard` – kursor zszedł z okna (1 = klawiatura nadal w oknie Maca) |
| 0x34 | WINDOW_HANDOFF | M → W | `u16 x`, `u16 y` – okno upuszczone na ekranie wirtualnym, Windows przejmuje kursor |
| 0x35 | WINDOW_RAISE | W → M | `u32 windowID` – okno aktywowane na Windowsie, Mac wyciąga je na wierzch |
| 0x36 | WINDOW_CLOSE | W → M | `u32 windowID` – Alt+F4 / zamknięcie z paska zadań |
| 0x40 | AUDIO_START | W → M | `u16 udpPort`, `u8 format` |
| 0x41 | AUDIO_STOP | W → M | – |
| 0x42 | AUDIO_FORMAT | M → W | `u32 rate`, `u8 channels`, `u8 format`, `u8 status`, komunikat |
| 0x50 | PING | obie | `u64 timestamp` |
| 0x51 | PONG | obie | echo PING |
| 0x60 | STATUS | M → W | flagi stanu |
| 0x70 | CLIPBOARD | obie | `u8 format`, dane |
| 0x80 | DISPLAY_START | W → M | `u16 width`, `u16 height`, `u16 scalePercent`, `u8 edge`, `u8 codec=0`, `u32 maxBitrateKbps` (0 = auto), opcjonalnie `u8 mode` (0 = cały pulpit, 1 = okna) |
| 0x81 | DISPLAY_STOP | W → M | – |
| 0x82 | DISPLAY_READY | M → W | `u8 status`, `u16 port`, `32 B key`, `16 B token`, `u16 width`, `u16 height`, komunikat |
| 0x83 | DISPLAY_KEYFRAME | W → M | opcjonalnie `u32 streamID` (brak = wszystkie strumienie) |
| 0x84 | DISPLAY_FOCUS | M → W | `u8 active` |
| 0x85 | DISPLAY_MODE | W → M | `u8 mode` |
| 0x86 | DISPLAY_WINDOWS | M → W | `u16 displayWidth`, `u16 displayHeight`, `u8 count`, `count ×` (`u32 id`, `i32 pid`, `i32 x`, `i32 y`, `u16 w`, `u16 h`, `u8 flags`, `u8 titleLength`, tytuł UTF-8); piksele ekranu wirtualnego, od najwyższego; flagi: bit 0 pasek menu, bit 1 menu/podpowiedź |
| 0x87 | WINDOW_ICON | M → W | `i32 pid`, PNG 64×64 – ikona aplikacji na pasek zadań |

Flagi `STATUS`: bit 0 Dostępność, bit 1 przechwytywanie audio, bit 2 kursor na Macu,
bit 3 Mac obsługuje ekran wirtualny, bit 4 ekran wirtualny włączony na Macu,
bit 5 strumień ekranu działa, bit 6 Mac obsługuje tryb okien. Windows wysyła `DISPLAY_*` dopiero, gdy widzi bit 3 –
starsza wersja Maca nie zna tych typów i zerwałaby sesję.

Schowek przyjmuje tekst do 1 MiB i PNG do 32 MiB / 64 megapikseli. Nieznane,
niepoprawne i zbyt duże ramki są odrzucane przed przetwarzaniem.

## Szyfrowane audio UDP

Nagłówek ma 32 bajty i jest uwierzytelniany jako AAD:

```
u16 magic = 0x4D42
u8  version = 2
u8  flags = 0
u64 sessionId
u64 counter
u16 sequence
u16 frames
u8  channels
u8  format
u16 reserved
u32 frameIndex
ciphertext PCM
16 B AES-256-GCM tag
```

Nonce audio to pierwsze cztery bajty `sessionId` + `counter`. Odbiornik sprawdza
adres IPv4 aktywnego Maca, identyfikator sesji, rozmiary, tag i 64-pakietowe okno
anty-replay. Pakiet nie wpływa na licznik replay przed poprawną autoryzacją.

## Ekran wirtualny (wideo)

1. Windows wysyła `DISPLAY_START` z rozdzielczością monitora po stronie Maca (piksele
   fizyczne), skalowaniem Windows i krawędzią Maca zwróconą w stronę Windowsa.
2. Mac tworzy wirtualny monitor (`CGVirtualDisplay`) przy tej krawędzi, nagrywa go przez
   ScreenCaptureKit i koduje H.264 (VideoToolbox, bez klatek B, SPS/PPS przed każdą
   klatką kluczową, BT.709, zakres 16–235).
3. Mac otwiera jednorazowy port TCP i odpowiada `DISPLAY_READY` z portem, losowym
   kluczem AES-256 i losowym tokenem. Oba przechodzą wyłącznie przez zaszyfrowany kanał
   sterowania i obowiązują dla jednego strumienia. `status != 0` oznacza błąd lub
   zatrzymanie; opis jest w komunikacie (ta sama wiadomość zgłasza późniejsze awarie).
4. Windows łączy się z portem i wysyła 16-bajtowy token. Mac porównuje go w stałym czasie,
   przyjmuje pierwszego poprawnego klienta i zamyka nasłuch. Błędny token nie zajmuje portu.
5. Mac wysyła rekordy:

```
u32 length          // licznik + szyfrogram + tag
u64 counter         // rośnie o 1; powtórzenie lub przestawienie kończy strumień
ciphertext
16 B AES-256-GCM tag
```

Nonce i AAD to `"BLMV" || counter`. Po odszyfrowaniu:

```
u8  kind            // 1 = cały ekran wirtualny, 2 = jedno okno (tryb okien)
u8  flags           // bit 0: klatka kluczowa
u16 width
u16 height
u64 captureMicros   // tylko diagnostyka
[u32 streamID]      // tylko kind 2: CGWindowID okna albo 0xFFFFFFFE (pasek menu)
[u16 cornerRadius]  // tylko kind 2: promień narożnika okna w pikselach
H.264 Annex B
```

Strumień okna ma rozmiar okna w pikselach, zaokrąglony w górę do parzystych wymiarów
(co najmniej 16×16).

Rekord ma najwyżej 16 MiB. ScreenCaptureKit nie dostarcza klatek, gdy ekran się nie
zmienia, więc nieruchomy obraz nie generuje ruchu. Gdy sieć nie nadąża, Mac odrzuca klatki
aż do najbliższej klatki kluczowej. Windows prosi o nią przez `DISPLAY_KEYFRAME` po błędzie
dekodera i przy każdym pokazaniu okna.

`DISPLAY_FOCUS` mówi, że kursor sterowany z Windowsa jest na ekranie wirtualnym: Windows
pokazuje wtedy obraz Maca na całym monitorze. Kursor z Windowsa wchodzi zawsze na fizyczny
ekran Maca; na ekran wirtualny przechodzi dopiero z niego i tylko podczas przeciągania
(wciśnięty przycisk myszy). Zwykły ruch przez tę krawędź oddaje sterowanie Windowsowi
(`LEAVE` bez bitu 0), jak bez ekranu wirtualnego. Wyjście przez dalszą krawędź
ekranu wirtualnego oddaje sterowanie Windowsowi (`LEAVE` z bitem 0), a kursor Windows
pojawia się przy tej samej krawędzi monitora.

### Tryb okien

W trybie okien (`mode = 1`) każde okno Maca leżące na ekranie wirtualnym ma na Windowsie
własne okno systemowe (pasek zadań, Alt+Tab, minimalizacja, kolejność okien). Mac nagrywa
każde okno osobno (ScreenCaptureKit, niezależnie od położenia i okien nad nim) i wysyła je
jako osobne strumienie `kind = 2`; listę okien (`DISPLAY_WINDOWS`) do ~30 razy na sekundę.

* Kursor Windows nad oknem Maca zostaje lokalny. Windows wysyła `WINDOW_ENTER`, potem
  `MOUSE_ABSOLUTE` przy każdym ruchu oraz kopie `MOUSE_BUTTON` / `MOUSE_WHEEL`; kliknięcia
  docierają też do okna Windows (aktywacja, kolejność). Przed kliknięciem w inne okno
  Windows wysyła `WINDOW_RAISE`, żeby na Macu leżało ono na wierzchu.
* Klawiatura (`KEY`) trafia do Maca, gdy aktywne okno Windows reprezentuje okno Maca.
  Skróty Windows (klawisz Win, Alt+Tab, Alt+Esc, Alt+F4) zostają w Windowsie; przy utracie
  aktywności Windows wysyła `RELEASE_ALL`.
* Przeciągnięcie okna z MacBooka na ekran wirtualny i puszczenie przycisku kończy się
  `WINDOW_HANDOFF`: Windows kontynuuje od tego punktu.
* Przeciągnięcie okna Maca przez krawędź Windows po stronie Maca wysyła zwykłe `ENTER`;
  Mac zachowuje wciśnięty przycisk, więc okno przechodzi na ekran MacBooka.
* Okna przywrócone przez macOS z poprzedniej sesji Mac odsyła na fizyczny ekran, więc
  każda sesja zaczyna się bez okien.

## Granice zaufania

Protokół chroni poufność i integralność ruchu w LAN. Nie chroni komputera już
przejętego przez złośliwe oprogramowanie działające na koncie użytkownika.
Kod parowania należy traktować jak hasło do klawiatury, schowka, dźwięku i obrazu ekranu
wirtualnego.

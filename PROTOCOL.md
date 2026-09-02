# BorderlessMouse – protokół LAN (v1)

Wszystkie liczby są little-endian. Napisy są UTF-8 bez terminatora,
a ich długość wynika z długości ramki.

## Porty

| Kanał            | Transport | Port                 | Kierunek                     |
|------------------|-----------|----------------------|------------------------------|
| Discovery        | UDP       | 47801 (Mac nasłuchuje) | Windows → broadcast → Mac  |
| Sterowanie/wejście | TCP     | 47800 (Mac nasłuchuje) | Windows łączy się z Makiem |
| Audio            | UDP       | 47802 (Windows nasłuchuje, konfigurowalny) | Mac → Windows |

## Discovery (UDP)

Windows wysyła broadcast `BLM1?` (5 bajtów ASCII) na port 47801.
Mac odpowiada unicastem do nadawcy: `BLM1!` + `u16 tcpPort` + `nazwa`.

## Kanał sterowania (TCP, `TCP_NODELAY`)

Ramka: `u8 type`, `u8 len`, `len` bajtów payloadu. Jeśli `len == 0xFF`, to jest
to **długa ramka**: po nagłówku następuje `u32 length`, a potem `length` bajtów
payloadu (limit 32 MiB + 1 bajt; payloady o długości ≥ 255 bajtów zawsze idą jako długie ramki).

| Typ  | Nazwa        | Kierunek | Payload |
|------|--------------|----------|---------|
| 0x01 | HELLO        | W → M    | `u8 version=1`, `nazwa` |
| 0x02 | WELCOME      | M → W    | `u8 version=1`, `nazwa` |
| 0x10 | MOUSE_MOVE   | W → M    | `i16 dx`, `i16 dy` (piksele, względne) |
| 0x11 | MOUSE_BUTTON | W → M    | `u8 button` (0 L, 1 R, 2 M, 3 X1, 4 X2), `u8 down` |
| 0x12 | MOUSE_WHEEL  | W → M    | `i16 dx`, `i16 dy` (jednostki Windows, 120 = 1 ząbek) |
| 0x20 | KEY          | W → M    | `u16 scancode`, `u16 vk`, `u8 flags` (bit0 extended, bit1 down, bit2 repeat) |
| 0x21 | RELEASE_ALL  | W → M    | – (zwolnij wszystkie klawisze i przyciski) |
| 0x30 | ENTER        | W → M    | `u8 edge` (krawędź Maca, przez którą kursor wchodzi: 0 L, 1 R, 2 T, 3 B), `f32 ratio` (0–1 wzdłuż krawędzi) |
| 0x31 | LEAVE        | M → W    | `u8 edge` (krawędź Maca, przez którą kursor wyszedł), `f32 ratio` |
| 0x40 | AUDIO_START  | W → M    | `u16 udpPort` (port na Windowsie), `u8 format` (0 = s16le) |
| 0x41 | AUDIO_STOP   | W → M    | – |
| 0x42 | AUDIO_FORMAT | M → W    | `u32 sampleRate`, `u8 channels`, `u8 format` (0 s16le, 1 f32le), `u8 status` (0 ok, 1 błąd), `komunikat` |
| 0x50 | PING         | obie     | `u64 ts` (dowolny znacznik nadawcy) |
| 0x51 | PONG         | obie     | echo payloadu PING |
| 0x60 | STATUS       | M → W    | `u8 flags` (bit0 Accessibility OK, bit1 audio przechwytywane, bit2 kursor na Macu) |
| 0x70 | CLIPBOARD    | obie     | `u8 format` (0 = tekst UTF-8, 1 = obraz PNG), dane (limit 1 MiB tekstu lub 32 MiB PNG, maks. 64 × 1024² pikseli) |

Ustalenia:

* Windows jest "serwerem wejścia" (ma fizyczną klawiaturę i mysz), Mac
  jest "klientem wejścia" (wstrzykuje zdarzenia przez CGEvent).
* Po ENTER Mac przejmuje kursor; ruchy są względne (Mac sam pilnuje
  granic ekranów). Gdy kursor uderzy w krawędź, przez którą wszedł, Mac
  wysyła LEAVE i przestaje wstrzykiwać zdarzenia; Windows odblokowuje
  lokalne wejście i stawia kursor przy swojej krawędzi.
* Schowek: każda strona obserwuje własny schowek (Mac: `changeCount`, Windows:
  `GetClipboardSequenceNumber`) i po zmianie wysyła tekst lub obraz CLIPBOARD. Odbiorca
  ustawia schowek i zapamiętuje własną zmianę, żeby nie odsyłać jej z powrotem.
  Obraz ma pierwszeństwo przed tekstem/URL-em udostępnionym razem z nim.
  macOS konwertuje TIFF do PNG przy wysyłaniu i udostępnia PNG oraz TIFF po odbiorze;
  Windows udostępnia PNG i natywną bitmapę. Zachowujemy rozdzielczość i przezroczystość.
  Puste, nieznane, niepoprawne i zbyt duże payloady są ignorowane.
  Obsługa zdjęć wymaga aktualizacji obu aplikacji; starsze wersje obsługują tylko format 0
  i mogą rozłączyć połączenie przy ramce większej niż ich limit 4 MiB.
* Scancode ma pierwszeństwo przy mapowaniu klawiszy (mapowanie fizyczne,
  niezależne od układu klawiatury). `vk` jest zapasowe.

## Audio (UDP, Mac → Windows)

Nagłówek 12 bajtów, potem PCM interleaved:

```
u16 magic = 0x4D42 ("BM")
u16 seq          (rośnie o 1 na pakiet, zawija się)
u16 frames       (liczba ramek w pakiecie)
u8  channels
u8  format       (0 = s16le, 1 = f32le)
u32 frameIndex   (indeks pierwszej ramki w strumieniu)
```

Domyślnie: 48 kHz / 2 kanały / s16 → 256 ramek na pakiet (~5,3 ms,
1036 bajtów). Windows utrzymuje bufor jitter o zadanym rozmiarze
(domyślnie 20 ms) i tnie nadmiar, gdy opóźnienie narasta.

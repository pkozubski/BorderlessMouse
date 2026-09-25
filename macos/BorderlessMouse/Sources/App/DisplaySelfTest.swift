import AppKit
import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import Network
import ScreenCaptureKit
import VideoToolbox

/// Samotest ekranu wirtualnego bez Windowsa (`--display-selftest`):
/// 1. tworzy wirtualny monitor i ustawia go obok ekranu Maca,
/// 2. koduje syntetyczne klatki, wysyła je przez VideoServer na localhost,
///    odszyfrowuje i dekoduje VideoToolboxem, porównując kolory,
/// 3. jeśli jest zgoda na nagrywanie ekranu – nagrywa monitor przez 2 s.
/// `--fixture <plik.json>` zapisuje zaszyfrowane rekordy dla testu Windows.
enum DisplaySelfTest {
    struct Color: Equatable {
        let r: UInt8, g: UInt8, b: UInt8
        func near(_ other: Color, tolerance: Int = 24) -> Bool {
            abs(Int(r) - Int(other.r)) <= tolerance && abs(Int(g) - Int(other.g)) <= tolerance
                && abs(Int(b) - Int(other.b)) <= tolerance
        }
    }

    static func run() -> Never {
        let args = CommandLine.arguments
        // Kursory systemowe (i inne zasoby AppKit) ładują się dopiero po połączeniu z serwerem okien.
        _ = NSApplication.shared
        var failures = 0
        func check(_ ok: Bool, _ message: String) {
            print(ok ? "✓ \(message)" : "✗ \(message)")
            if !ok { failures += 1 }
        }

        if let index = args.firstIndex(of: "--fixture"), index + 1 < args.count {
            do {
                try writeFixture(to: URL(fileURLWithPath: args[index + 1]))
                print("✓ zapisano \(args[index + 1])")
                exit(0)
            } catch {
                print("✗ fixture: \(error.localizedDescription)")
                exit(1)
            }
        }

        // 1. Wirtualny monitor
        check(VirtualDisplay.isSupported, "CGVirtualDisplay dostępne")
        var displayID: CGDirectDisplayID = 0
        do {
            let before = VirtualDisplay.activeDisplays()
            let display = try VirtualDisplay(name: "BorderlessMouse selftest", pixelWidth: 1920, pixelHeight: 1080, scalePercent: 100)
            displayID = display.displayID
            display.attach(to: .right)
            usleep(300_000)
            let main = CGDisplayBounds(CGMainDisplayID())
            check(VirtualDisplay.activeDisplays().contains(display.displayID), "monitor \(display.displayID) aktywny (wcześniej \(before.count) ekranów)")
            check(display.pixelSize == (1920, 1080), "tryb \(display.pixelSize.width)×\(display.pixelSize.height) px")
            check(display.bounds.minX == main.maxX, "ustawiony po prawej stronie ekranu głównego (\(display.bounds))")

            if DisplayCapture.hasPermission {
                let frames = captureFrames(displayID: display.displayID, seconds: 2)
                check(frames > 0, "ScreenCaptureKit dostarczył \(frames) klatek")
            } else {
                print("– pominięto nagrywanie ekranu: brak zgody „Nagrywanie ekranu” dla tego procesu")
            }
        } catch {
            check(false, "tworzenie monitora: \(error.localizedDescription)")
        }
        usleep(300_000)
        check(displayID == 0 || !VirtualDisplay.activeDisplays().contains(displayID), "monitor usunięty po zwolnieniu")

        // Nagrywanie pojedynczego okna (tryb okien) na przykładzie okna z ekranu głównego.
        if DisplayCapture.hasPermission {
            let windows = WindowTracker.snapshot(displayID: CGMainDisplayID()).filter { !$0.isMenuBar && !$0.isPopup }
            if let window = windows.max(by: { $0.frame.width * $0.frame.height < $1.frame.width * $1.frame.height }) {
                let scale = WindowTracker.pixelScale(of: CGMainDisplayID())
                let size = WindowTracker.pixelSize(of: window, scale: scale)
                let result = captureWindow(window, width: size.width, height: size.height)
                check(result.frames > 0, "okno „\(window.title)” \(size.width)×\(size.height): \(result.frames) klatek, zakodowano \(result.encoded)")
                check(result.encoded > 0, "koder okna zwrócił klatki")
                let corner = (red: Int(result.radius >> 8), green: Int(result.radius & 0xFF))
                check(abs(corner.red - 41) < 12 && abs(corner.green - 41) < 12,
                      "narożnik okna wypełniony kolorem paska (R \(corner.red), G \(corner.green))")
                // Cały potok trybu okien: WindowStreams → ramki z identyfikatorem okna i promieniem.
                let lock = NSLock()
                var frames: [VideoStream.Frame] = []
                let streams = WindowStreams(send: { frame in lock.lock(); frames.append(frame); lock.unlock(); return true },
                                            hasClient: { true })
                streams.start(displayID: CGMainDisplayID())
                streams.update([window])
                usleep(1_500_000)
                streams.requestKeyframe(window.id)
                usleep(300_000)
                streams.stop()
                lock.lock()
                let own = frames.filter { $0.streamID == window.id }
                lock.unlock()
                check(!own.isEmpty && own.first?.isKeyframe == true, "strumień okna: \(own.count) klatek, pierwsza kluczowa")
                check(own.contains { $0.cornerRadius > 0 }, "promień narożnika w nagłówku klatki: \(own.last?.cornerRadius ?? 0) px")
                check(own.filter(\.isKeyframe).count >= 2, "klatka kluczowa na żądanie (DISPLAY_KEYFRAME z id okna)")
                // Okno znika, zanim nagrywanie ruszy (np. podpowiedź): nagrywanie nie może zostać włączone.
                let transient = WindowCapture()
                var lateFrames = 0
                transient.onFrame = { _, _ in lock.lock(); lateFrames += 1; lock.unlock() }
                Task { try? await transient.start(windowID: window.id, displayID: CGMainDisplayID(), menuBarRect: .zero, width: 640, height: 480) }
                transient.stop()
                usleep(1_500_000)
                lock.lock()
                let leaked = lateFrames
                lock.unlock()
                check(leaked == 0, "zatrzymanie w trakcie startu nie zostawia działającego nagrywania (\(leaked) klatek)")
            } else {
                print("– brak okna do testu nagrywania pojedynczego okna")
            }
        }

        // Kształt kursora: każdy wzorzec rozpoznany jako on sam.
        let tracker = CursorTracker()
        let samples: [(CursorTracker.Shape, NSCursor)] = [(.arrow, .arrow), (.iBeam, .iBeam), (.pointingHand, .pointingHand),
                                                          (.resizeLeftRight, .resizeLeftRight), (.resizeUpDown, .resizeUpDown),
                                                          (.notAllowed, .operationNotAllowed)]
        let recognized = samples.filter { tracker.identify($0.1) == $0.0 }.count
        check(recognized == samples.count, "rozpoznawanie kursora: \(recognized)/\(samples.count)")
        if let current = NSCursor.currentSystem { print("  obecny kursor systemu: \(tracker.identify(current))") }

        // 2. Koder → szyfrowany TCP → dekoder
        do {
            let result = try loopback(width: 1280, height: 720, colors: [
                Color(r: 220, g: 30, b: 30), Color(r: 30, g: 200, b: 60), Color(r: 40, g: 60, b: 230),
            ])
            check(result.decoded.count == 3, "odebrano i zdekodowano \(result.decoded.count)/3 klatek")
            for (index, pair) in zip(result.expected, result.decoded).enumerated() {
                check(pair.0.near(pair.1), "klatka \(index + 1): oczekiwano \(pair.0), jest \(pair.1)")
            }
            check(result.firstIsKeyframe, "pierwsza klatka jest kluczowa (z SPS/PPS)")
            let perf = try measureEncoder(width: 2560, height: 1440, frames: 60)
            print("  koder 2560×1440: średnio \(String(format: "%.1f", perf.averageMs)) ms/klatkę, klatka kluczowa \(perf.keyframeBytes / 1024) KiB")
            check(perf.averageMs < 16, "koder mieści się w budżecie 60 kl./s")
        } catch {
            check(false, "pętla wideo: \(error.localizedDescription)")
        }

        // 3. Okna Windows na Macu: koder → szyfrowany TCP → WinViewSession → okno macOS
        do {
            try winViewLoopback(check: check)
        } catch {
            check(false, "okna Windows na Macu: \(error.localizedDescription)")
        }

        print(failures == 0 ? "WYNIK: OK" : "WYNIK: \(failures) błędów")
        exit(failures == 0 ? 0 : 1)
    }

    // MARK: - Nagrywanie

    private static func captureFrames(displayID: CGDirectDisplayID, seconds: Double) -> Int {
        let capture = DisplayCapture()
        let lock = NSLock()
        var count = 0
        capture.onFrame = { _, _ in lock.lock(); count += 1; lock.unlock() }
        let done = DispatchSemaphore(value: 0)
        Task {
            do { try await capture.start(displayID: displayID, width: 1920, height: 1080) } catch {
                print("  błąd ScreenCaptureKit: \(error.localizedDescription)")
            }
            done.signal()
        }
        done.wait()
        // Statyczny ekran daje pojedyncze klatki – ruszamy kursorem, żeby wymusić zmiany.
        let bounds = CGDisplayBounds(displayID)
        let steps = Int(seconds * 20)
        for step in 0..<steps {
            let x = bounds.minX + CGFloat(step % 40) * bounds.width / 40
            CGWarpMouseCursorPosition(CGPoint(x: x, y: bounds.midY))
            usleep(50_000)
        }
        capture.stop()
        lock.lock()
        defer { lock.unlock() }
        return count
    }

    private static func captureWindow(_ window: TrackedWindow, width: Int, height: Int) -> (frames: Int, encoded: Int, radius: UInt16) {
        let capture = WindowCapture()
        let lock = NSLock()
        var frames = 0, encoded = 0
        var radius: UInt16 = 0
        let w = max(64, (width + 1) & ~1), h = max(64, (height + 1) & ~1)
        guard let encoder = try? VideoEncoder(width: w, height: h) else { return (0, 0, 0) }
        encoder.onFrame = { _ in lock.lock(); encoded += 1; lock.unlock() }
        capture.onFrame = { buffer, time in
            lock.lock()
            frames += 1
            let first = frames == 1
            lock.unlock()
            if first {
                // Narożnik ma być wypełniony kolorem paska (41, 41, 43), a nie czarny.
                CVPixelBufferLockBaseAddress(buffer, .readOnly)
                if let base = CVPixelBufferGetBaseAddress(buffer)?.assumingMemoryBound(to: UInt8.self) {
                    lock.lock(); radius = UInt16(base[2]) << 8 | UInt16(base[1]); lock.unlock() // R, G lewego górnego piksela
                }
                CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
                encoder.requestKeyframe()
            }
            encoder.encode(buffer, presentationTime: time)
        }
        let started = DispatchSemaphore(value: 0)
        Task {
            do {
                try await capture.start(windowID: window.id, displayID: CGMainDisplayID(), menuBarRect: .zero, width: w, height: h)
            } catch {
                print("  błąd nagrywania okna: \(error.localizedDescription)")
            }
            started.signal()
        }
        started.wait()
        usleep(1_500_000)
        capture.stop()
        encoder.invalidate()
        lock.lock()
        defer { lock.unlock() }
        return (frames, encoded, radius)
    }

    // MARK: - Pętla koder → sieć → dekoder

    struct LoopbackResult {
        let expected: [Color]
        let decoded: [Color]
        let firstIsKeyframe: Bool
    }

    static func loopback(width: Int, height: Int, colors: [Color]) throws -> LoopbackResult {
        let key = VideoStream.randomBytes(VideoStream.keyBytes)
        let token = VideoStream.randomBytes(VideoStream.tokenBytes)
        let server = VideoServer(key: key, token: token)
        let portSemaphore = DispatchSemaphore(value: 0)
        var portResult: Result<UInt16, Error>?
        server.start { portResult = $0; portSemaphore.signal() }
        portSemaphore.wait()
        let port = try portResult!.get()

        let connected = DispatchSemaphore(value: 0)
        server.onClientConnected = { connected.signal() }
        let client = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
        let clientQueue = DispatchQueue(label: "selftest.client")
        client.start(queue: clientQueue)
        client.send(content: token, completion: .contentProcessed { _ in })
        guard connected.wait(timeout: .now() + 3) == .success else { throw SelfTestError("klient nie został uwierzytelniony") }

        let encoder = try VideoEncoder(width: width, height: height)
        var frames: [VideoStream.Frame] = []
        let received = DispatchSemaphore(value: 0)
        var inbox = Data()
        var opener = VideoStream.Opener(key: key)
        func readMore() {
            client.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { data, _, complete, error in
                if let data { inbox.append(data) }
                while inbox.count >= 4 {
                    let length = Int(inbox[inbox.startIndex]) | Int(inbox[inbox.startIndex + 1]) << 8
                        | Int(inbox[inbox.startIndex + 2]) << 16 | Int(inbox[inbox.startIndex + 3]) << 24
                    guard inbox.count >= 4 + length else { break }
                    let body = inbox.subdata(in: (inbox.startIndex + 4)..<(inbox.startIndex + 4 + length))
                    inbox.removeSubrange(inbox.startIndex..<(inbox.startIndex + 4 + length))
                    if let clear = opener.open(body), let frame = VideoStream.Frame(decoding: clear) {
                        frames.append(frame)
                        received.signal()
                    }
                }
                if !complete && error == nil { readMore() }
            }
        }
        clientQueue.async { readMore() }

        encoder.onFrame = { encoded in
            let frame = VideoStream.Frame(kind: .h264AccessUnit, flags: encoded.isKeyframe ? [.keyframe] : [],
                                          width: encoded.width, height: encoded.height, captureMicros: 0,
                                          payload: encoded.annexB)
            _ = server.send(frame)
        }
        for (index, color) in colors.enumerated() {
            let buffer = try makeFrame(width: width, height: height, color: color)
            encoder.encode(buffer, presentationTime: CMTime(value: CMTimeValue(index), timescale: 60))
            guard received.wait(timeout: .now() + 3) == .success else { throw SelfTestError("brak klatki \(index + 1)") }
        }
        encoder.invalidate()
        client.cancel()
        server.stop()

        let decoder = try H264TestDecoder()
        var decoded: [Color] = []
        for frame in clientQueue.sync(execute: { frames }) {
            if let color = try decoder.decode(annexB: frame.payload) { decoded.append(color) }
        }
        return LoopbackResult(expected: colors, decoded: decoded, firstIsKeyframe: frames.first?.isKeyframe == true)
    }

    private static func measureEncoder(width: Int, height: Int, frames: Int) throws -> (averageMs: Double, keyframeBytes: Int) {
        let encoder = try VideoEncoder(width: width, height: height)
        let lock = NSLock()
        var starts: [Int: CFAbsoluteTime] = [:]
        var total = 0.0
        var count = 0
        var keyframeBytes = 0
        let done = DispatchSemaphore(value: 0)
        encoder.onFrame = { encoded in
            lock.lock()
            let index = Int(encoded.presentationTime.value)
            if let start = starts[index] { total += CFAbsoluteTimeGetCurrent() - start; count += 1 }
            if encoded.isKeyframe { keyframeBytes = max(keyframeBytes, encoded.annexB.count) }
            lock.unlock()
            done.signal()
        }
        var buffers: [CVPixelBuffer] = []
        for index in 0..<4 {
            buffers.append(try makeFrame(width: width, height: height,
                                         color: Color(r: UInt8(40 * index), g: 120, b: 200), stripe: index))
        }
        for index in 0..<frames {
            lock.lock()
            starts[index] = CFAbsoluteTimeGetCurrent()
            lock.unlock()
            encoder.encode(buffers[index % buffers.count], presentationTime: CMTime(value: CMTimeValue(index), timescale: 60))
            _ = done.wait(timeout: .now() + 1)
        }
        encoder.invalidate()
        return (count > 0 ? total / Double(count) * 1000 : .infinity, keyframeBytes)
    }

    /// Strumień „monitora Windows” 640×400 (górna połowa czerwona, dolna niebieska) trafia do
    /// WinViewSession jak z Windowsa; okno Windows na środku monitora musi pokazać się na
    /// ekranie Maca we właściwym miejscu, z obrazem we właściwej orientacji.
    private static func winViewLoopback(check: (Bool, String) -> Void) throws {
        let width = 640, height = 400
        let key = VideoStream.randomBytes(VideoStream.keyBytes)
        let token = VideoStream.randomBytes(VideoStream.tokenBytes)
        let screen = CGDisplayBounds(CGMainDisplayID())
        let session = WinViewSession(screen: screen, key: key, token: token)
        var keyframeRequests = 0
        session.onKeyframeNeeded = { keyframeRequests += 1 }
        var port: UInt16?
        var failure: Error?
        session.start { result in
            switch result {
            case let .success(value): port = value
            case let .failure(error): failure = error
            }
        }
        func spin(_ seconds: Double, until done: () -> Bool = { false }) {
            let end = Date().addingTimeInterval(seconds)
            while Date() < end, !done() { RunLoop.main.run(until: Date().addingTimeInterval(0.02)) }
        }
        spin(3) { port != nil || failure != nil }
        if let failure { throw failure }
        guard let port else { throw SelfTestError("odbiornik nie wystartował") }

        let client = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
        client.start(queue: DispatchQueue(label: "selftest.winview"))
        client.send(content: token, completion: .contentProcessed { _ in })
        spin(1) { keyframeRequests > 0 }
        check(keyframeRequests > 0, "odbiornik przyjął token i poprosił o klatkę kluczową")

        let window = WinWindowDescriptor(id: 0x1234, x: 160, y: 100, width: 320, height: 200, flags: WinWindowDescriptor.foreground,
                                         title: "Notatnik (test)")
        let list = WinWindowList(displayWidth: width, displayHeight: height, windows: [window])
        session.update(list)
        let encoder = try VideoEncoder(width: width, height: height)
        var sealer = VideoStream.Sealer(key: key)
        let sent = DispatchSemaphore(value: 0)
        encoder.onFrame = { encoded in
            // Klatka z listą okien z tej samej chwili (kind 3), tak jak wysyła Windows.
            let frame = VideoStream.Frame(kind: .winViewAccessUnit, flags: encoded.isKeyframe ? [.keyframe] : [],
                                          width: width, height: height, captureMicros: 0, payload: encoded.annexB,
                                          windows: list.payload)
            if let record = sealer.seal(frame.encoded()) {
                client.send(content: record, completion: .contentProcessed { _ in sent.signal() })
            }
        }
        let top = Color(r: 220, g: 30, b: 30), bottom = Color(r: 40, g: 60, b: 230)
        let image = try makeFrame(width: width, height: height, color: top, bottomColor: bottom)
        for index in 0..<3 {
            encoder.encode(image, presentationTime: CMTime(value: CMTimeValue(index), timescale: 60))
            _ = sent.wait(timeout: .now() + 2)
        }
        spin(1.5)
        encoder.invalidate()

        let inside = CGPoint(x: screen.minX + screen.width / 2, y: screen.minY + screen.height / 2)
        let hit = session.hitTest(inside)
        check(hit.map { abs(Int($0.x) - 320) <= 1 && abs(Int($0.y) - 200) <= 1 } == true,
              "środek ekranu Maca trafia w widoczne okno Windows (piksel \(hit.map { "\($0.x),\($0.y)" } ?? "brak"))")
        check(session.hitTest(CGPoint(x: screen.minX + 5, y: screen.minY + 5)) == nil, "róg ekranu poza oknem Windows")
        let back = session.macPoint(x: 320, y: 200)
        check(abs(back.x - inside.x) < 2 && abs(back.y - inside.y) < 2, "piksel monitora Windows → punkt Maca (\(back))")
        var released: (UInt16, UInt16)?
        session.onPointerRelease = { x, y in released = (x, y) }
        session.cursor(WinCursorUpdate(x: 5, y: 5, shape: 0, visible: true))
        check(released != nil, "kursor Windows poza widocznym oknem – Mac prosi o oddanie sterowania")
        released = nil
        session.cursor(WinCursorUpdate(x: 20, y: 20, shape: 0, visible: true, buttons: true))
        check(released == nil, "podczas przeciągania Mac nie zabiera kursora")
        spin(0.1)

        let stats = session.stats
        check(stats.frames >= 3, "okna Windows: odebrano \(stats.frames) klatek (\(stats.bytes) B)")
        guard let number = session.proxyWindowNumber(window.id) else {
            check(false, "okno macOS dla okna Windows nie powstało")
            session.stop()
            return
        }
        if DisplayCapture.hasPermission {
            var shot: CGImage?
            var done = false
            Task {
                defer { done = true }
                guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true),
                      let scWindow = content.windows.first(where: { $0.windowID == CGWindowID(number) }) else { return }
                let config = SCStreamConfiguration()
                config.width = 320
                config.height = 200
                config.showsCursor = false
                if #available(macOS 14.0, *) { config.ignoreShadowsSingleWindow = true }
                shot = try? await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(desktopIndependentWindow: scWindow),
                                                                   configuration: config)
            }
            spin(5) { done }
            if let shot, let upper = pixel(shot, x: 160, y: 30), let lower = pixel(shot, x: 160, y: 170) {
                check(upper.near(top, tolerance: 40) && lower.near(bottom, tolerance: 40),
                      "obraz w oknie macOS: góra \(upper), dół \(lower) (oczekiwano \(top) / \(bottom))")
            } else {
                check(false, "zrzut okna macOS z oknem Windows")
            }
        } else {
            print("– pominięto zrzut okna: brak zgody „Nagrywanie ekranu” dla tego procesu")
        }
        client.cancel()
        session.stop()
        spin(0.2)
    }

    private static func pixel(_ image: CGImage, x: Int, y: Int) -> Color? {
        guard let context = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                      bytesPerRow: image.width * 4, space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let data = context.data?.assumingMemoryBound(to: UInt8.self) else { return nil }
        let px = x * image.width / 320, py = y * image.height / 200
        let p = data + py * image.width * 4 + px * 4
        return Color(r: p[0], g: p[1], b: p[2])
    }

    // MARK: - Fixture dla Windows

    /// Mały strumień 160×96 (czerwony, zielony, niebieski) zaszyfrowany znanym
    /// kluczem – Windows sprawdza nim parser, deszyfrowanie i dekoder.
    static func writeFixture(to url: URL) throws {
        let key = Data((0..<32).map { UInt8($0 * 7 & 0xFF) })
        let colors = [Color(r: 220, g: 30, b: 30), Color(r: 30, g: 200, b: 60), Color(r: 40, g: 60, b: 230)]
        let encoder = try VideoEncoder(width: 160, height: 96, bitrate: 1_000_000)
        var encoded: [VideoEncoder.EncodedFrame] = []
        let done = DispatchSemaphore(value: 0)
        encoder.onFrame = { encoded.append($0); done.signal() }
        for (index, color) in colors.enumerated() {
            encoder.encode(try makeFrame(width: 160, height: 96, color: color),
                           presentationTime: CMTime(value: CMTimeValue(index), timescale: 60))
            guard done.wait(timeout: .now() + 3) == .success else { throw SelfTestError("koder nie oddał klatki") }
        }
        encoder.invalidate()
        var sealer = VideoStream.Sealer(key: key)
        var records: [String] = []
        for (index, frame) in encoded.enumerated() {
            let wire = VideoStream.Frame(kind: .h264AccessUnit, flags: frame.isKeyframe ? [.keyframe] : [],
                                         width: 160, height: 96, captureMicros: UInt64(index) * 16_667,
                                         payload: frame.annexB)
            guard let record = sealer.seal(wire.encoded()) else { throw SelfTestError("szyfrowanie") }
            records.append(record.map { String(format: "%02x", $0) }.joined())
        }
        let json: [String: Any] = [
            "keyHex": key.map { String(format: "%02x", $0) }.joined(),
            "width": 160,
            "height": 96,
            "records": records,
            "keyframes": encoded.map(\.isKeyframe),
            "colors": colors.map { [$0.r, $0.g, $0.b] },
        ]
        let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: url)
    }

    // MARK: - Pomocnicze

    struct SelfTestError: LocalizedError {
        let errorDescription: String?
        init(_ message: String) { errorDescription = message }
    }

    /// Klatka NV12 (zakres wideo, BT.709) w jednolitym kolorze, opcjonalnie z pasem.
    static func makeFrame(width: Int, height: Int, color: Color, stripe: Int? = nil, bottomColor: Color? = nil) throws -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        let attributes = [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary
        guard CVPixelBufferCreate(nil, width, height, kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
                                  attributes, &buffer) == kCVReturnSuccess, let buffer else {
            throw SelfTestError("CVPixelBufferCreate")
        }
        func ycbcr(_ color: Color) -> (y: UInt8, cb: UInt8, cr: UInt8) {
            let r = Double(color.r) / 255, g = Double(color.g) / 255, b = Double(color.b) / 255
            let yf = 0.2126 * r + 0.7152 * g + 0.0722 * b
            return (UInt8(clamping: Int((16 + 219 * yf).rounded())),
                    UInt8(clamping: Int((128 + 224 * (b - yf) / 1.8556).rounded())),
                    UInt8(clamping: Int((128 + 224 * (r - yf) / 1.5748).rounded())))
        }
        let upper = ycbcr(color), lower = ycbcr(bottomColor ?? color)
        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }
        let yPlane = CVPixelBufferGetBaseAddressOfPlane(buffer, 0)!.assumingMemoryBound(to: UInt8.self)
        let yStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 0)
        for row in 0..<height {
            memset(yPlane + row * yStride, Int32(row < height / 2 ? upper.y : lower.y), width)
            if let stripe, (row / 32) % 4 == stripe { memset(yPlane + row * yStride, 235, width / 3) }
        }
        let uvPlane = CVPixelBufferGetBaseAddressOfPlane(buffer, 1)!.assumingMemoryBound(to: UInt8.self)
        let uvStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 1)
        for row in 0..<(height / 2) {
            let line = uvPlane + row * uvStride
            let value = row < height / 4 ? upper : lower
            for column in 0..<(width / 2) {
                line[column * 2] = value.cb
                line[column * 2 + 1] = value.cr
            }
        }
        CVBufferSetAttachment(buffer, kCVImageBufferColorPrimariesKey, kCVImageBufferColorPrimaries_ITU_R_709_2, .shouldPropagate)
        CVBufferSetAttachment(buffer, kCVImageBufferTransferFunctionKey, kCVImageBufferTransferFunction_ITU_R_709_2, .shouldPropagate)
        CVBufferSetAttachment(buffer, kCVImageBufferYCbCrMatrixKey, kCVImageBufferYCbCrMatrix_ITU_R_709_2, .shouldPropagate)
        return buffer
    }
}

/// Minimalny dekoder Annex B → BGRA do weryfikacji (środkowy piksel).
private final class H264TestDecoder {
    private var session: VTDecompressionSession?
    private var format: CMVideoFormatDescription?

    init() throws {}

    deinit {
        if let session { VTDecompressionSessionInvalidate(session) }
    }

    func decode(annexB: Data) throws -> DisplaySelfTest.Color? {
        let nals = Self.split(annexB)
        let sps = nals.first { ($0.first ?? 0) & 0x1F == 7 }
        let pps = nals.first { ($0.first ?? 0) & 0x1F == 8 }
        if let sps, let pps { try makeSession(sps: sps, pps: pps) }
        guard let session, let format else { return nil }
        var avcc = Data()
        for nal in nals where ![7, 8].contains((nal.first ?? 0) & 0x1F) {
            var length = UInt32(nal.count).bigEndian
            avcc.append(Data(bytes: &length, count: 4))
            avcc.append(nal)
        }
        var block: CMBlockBuffer?
        let count = avcc.count
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: count,
                                                 blockAllocator: nil, customBlockSource: nil, offsetToData: 0,
                                                 dataLength: count, flags: 0, blockBufferOut: &block) == noErr,
              let block else { throw DisplaySelfTest.SelfTestError("CMBlockBuffer") }
        _ = avcc.withUnsafeBytes { CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: count) }
        var sample: CMSampleBuffer?
        var sizes = [count]
        guard CMSampleBufferCreateReady(allocator: nil, dataBuffer: block, formatDescription: format, sampleCount: 1,
                                        sampleTimingEntryCount: 0, sampleTimingArray: nil, sampleSizeEntryCount: 1,
                                        sampleSizeArray: &sizes, sampleBufferOut: &sample) == noErr,
              let sample else { throw DisplaySelfTest.SelfTestError("CMSampleBuffer") }
        var color: DisplaySelfTest.Color?
        let status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [], infoFlagsOut: nil) { status, _, image, _, _ in
            guard status == noErr, let image else { return }
            CVPixelBufferLockBaseAddress(image, .readOnly)
            defer { CVPixelBufferUnlockBaseAddress(image, .readOnly) }
            let base = CVPixelBufferGetBaseAddress(image)!.assumingMemoryBound(to: UInt8.self)
            let stride = CVPixelBufferGetBytesPerRow(image)
            let pixel = base + (CVPixelBufferGetHeight(image) / 2) * stride + (CVPixelBufferGetWidth(image) / 2) * 4
            color = DisplaySelfTest.Color(r: pixel[2], g: pixel[1], b: pixel[0])
        }
        guard status == noErr else { throw DisplaySelfTest.SelfTestError("VTDecompressionSessionDecodeFrame \(status)") }
        VTDecompressionSessionWaitForAsynchronousFrames(session)
        return color
    }

    private func makeSession(sps: Data, pps: Data) throws {
        if let session { VTDecompressionSessionInvalidate(session) }
        var description: CMFormatDescription?
        let status = sps.withUnsafeBytes { spsBytes in
            pps.withUnsafeBytes { ppsBytes in
                let pointers = [spsBytes.bindMemory(to: UInt8.self).baseAddress!, ppsBytes.bindMemory(to: UInt8.self).baseAddress!]
                let sizes = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: nil, parameterSetCount: 2,
                                                                           parameterSetPointers: pointers,
                                                                           parameterSetSizes: sizes,
                                                                           nalUnitHeaderLength: 4,
                                                                           formatDescriptionOut: &description)
            }
        }
        guard status == noErr, let description else { throw DisplaySelfTest.SelfTestError("format H.264 \(status)") }
        let attributes = [kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA] as CFDictionary
        var session: VTDecompressionSession?
        let created = VTDecompressionSessionCreate(allocator: nil, formatDescription: description, decoderSpecification: nil,
                                                   imageBufferAttributes: attributes, outputCallback: nil,
                                                   decompressionSessionOut: &session)
        guard created == noErr, let session else { throw DisplaySelfTest.SelfTestError("VTDecompressionSessionCreate \(created)") }
        self.session = session
        format = description
    }

    static func split(_ data: Data) -> [Data] {
        let bytes = [UInt8](data)
        var starts: [(Int, Int)] = []
        var i = 0
        while i + 3 <= bytes.count {
            if bytes[i] == 0, bytes[i + 1] == 0, bytes[i + 2] == 1 {
                starts.append((i, i + 3)); i += 3
            } else if i + 4 <= bytes.count, bytes[i] == 0, bytes[i + 1] == 0, bytes[i + 2] == 0, bytes[i + 3] == 1 {
                starts.append((i, i + 4)); i += 4
            } else {
                i += 1
            }
        }
        return starts.enumerated().map { index, entry in
            let end = index + 1 < starts.count ? starts[index + 1].0 : bytes.count
            return Data(bytes[entry.1..<end])
        }
    }
}

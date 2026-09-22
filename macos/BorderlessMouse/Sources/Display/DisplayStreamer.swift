import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation

/// Ekran wirtualny dla Windowsa: wirtualny monitor → ScreenCaptureKit →
/// H.264 → szyfrowany TCP. Wszystkie metody publiczne są bezpieczne wątkowo.
final class DisplayStreamer {
    struct Ready {
        let port: UInt16
        let key: Data
        let token: Data
        let width: Int
        let height: Int
        let displayID: CGDirectDisplayID
        let bitrate: Int
    }

    struct Stats {
        let framesPerSecond: Int
        let kilobitsPerSecond: Int
        let dropped: UInt64
        let width: Int
        let height: Int
    }

    /// Strumień przestał działać po starcie (np. cofnięta zgoda, rozłączenie).
    var onStopped: ((String) -> Void)?
    var onStats: ((Stats) -> Void)?
    /// Zmiana trybu monitora (np. inna rozdzielczość wybrana w macOS).
    var onDisplayChanged: (() -> Void)?

    private let queue = DispatchQueue(label: "blm.display", qos: .userInitiated)
    private let frameLock = NSLock()
    private var virtualDisplay: VirtualDisplay?
    private var capture: DisplayCapture?
    private var encoder: VideoEncoder?
    private var server: VideoServer?
    private var statsTimer: DispatchSourceTimer?
    private var edge: ScreenEdge = .right
    private var bitrate = 0
    private var active = false
    /// Rośnie przy każdym starcie i zatrzymaniu – spóźniony start nie nadpisze nowszego stanu.
    private var generation: UInt64 = 0
    private var lastStats: (frames: UInt64, bytes: UInt64) = (0, 0)
    private var showsCursor = true

    // chronione frameLock (kolejka przechwytywania i wątek VideoToolbox)
    private var awaitingKeyframe = true
    private var lastPixelBuffer: CVPixelBuffer?
    private var droppedFrames: UInt64 = 0

    private var displayIDStorage: CGDirectDisplayID?
    var displayID: CGDirectDisplayID? {
        frameLock.lock()
        defer { frameLock.unlock() }
        return displayIDStorage
    }

    func start(_ request: DisplayStartRequest, name: String, completion: @escaping (Result<Ready, Error>) -> Void) {
        queue.async {
            self.stopLocked()
            self.showsCursor = request.mode == .fullscreen
            let generation = self.generation
            Task { await self.startAsync(request, name: name, generation: generation, completion: completion) }
        }
    }

    func stop() {
        queue.sync { stopLocked() }
    }

    /// Tryb okien ukrywa kursor Maca w obrazie (Windows pokazuje własny).
    func setShowsCursor(_ shows: Bool) {
        queue.async {
            guard self.showsCursor != shows else { return }
            self.showsCursor = shows
            guard let capture = self.capture, let encoder = self.encoder else { return }
            let (width, height) = (encoder.width, encoder.height)
            let queue = self.queue
            Task { [weak self] in
                try? await capture.update(width: width, height: height, showsCursor: shows)
                queue.async { self?.forceKeyframe() }
            }
        }
    }

    /// Windows prosi o klatkę kluczową (np. po błędzie dekodera).
    func requestKeyframe() {
        queue.async { self.forceKeyframe() }
    }

    // MARK: - Start

    private enum StartError: Error { case superseded }

    private func startAsync(_ request: DisplayStartRequest, name: String, generation: UInt64,
                            completion: @escaping (Result<Ready, Error>) -> Void) async {
        do {
            let display = try onQueue { () throws -> VirtualDisplay in
                guard generation == self.generation else { throw StartError.superseded }
                return try VirtualDisplay(name: name, pixelWidth: request.pixelWidth,
                                          pixelHeight: request.pixelHeight, scalePercent: request.scalePercent)
            }
            try onQueue {
                guard generation == self.generation else { throw StartError.superseded }
                self.virtualDisplay = display
                self.edge = request.edge
                display.attach(to: request.edge)
                self.frameLock.lock()
                self.displayIDStorage = display.displayID
                self.frameLock.unlock()
            }
            let (width, height) = display.pixelSize
            let requestedBitrate = Int(request.maxBitrateKbps) * 1000
            let encoder = try VideoEncoder(width: width, height: height, bitrate: requestedBitrate)
            let key = VideoStream.randomBytes(VideoStream.keyBytes)
            let token = VideoStream.randomBytes(VideoStream.tokenBytes)
            let server = VideoServer(key: key, token: token)
            let port = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<UInt16, Error>) in
                server.start { continuation.resume(with: $0) }
            }
            let capture = DisplayCapture()
            try onQueue {
                guard generation == self.generation else {
                    server.stop()
                    throw StartError.superseded
                }
                self.encoder = encoder
                self.server = server
                self.capture = capture
                self.bitrate = encoder.bitrate
                self.wire(capture: capture, encoder: encoder, server: server)
            }
            let showsCursor = onQueue { self.showsCursor }
            try await capture.start(displayID: display.displayID, width: width, height: height, showsCursor: showsCursor)
            try onQueue {
                guard generation == self.generation else {
                    capture.stop()
                    throw StartError.superseded
                }
                self.active = true
                self.startStatsTimer()
            }
            completion(.success(Ready(port: port, key: key, token: token, width: width, height: height,
                                      displayID: display.displayID, bitrate: encoder.bitrate)))
        } catch StartError.superseded {
            // Nowszy start albo stop już przejął stan – nic nie zgłaszamy.
        } catch {
            onQueue {
                if generation == self.generation { self.stopLocked() }
            }
            completion(.failure(error))
        }
    }

    private func wire(capture: DisplayCapture, encoder: VideoEncoder, server: VideoServer) {
        capture.onFrame = { [weak self, weak encoder, weak server] pixelBuffer, time in
            guard let self, let encoder, let server else { return }
            self.frameLock.lock()
            self.lastPixelBuffer = pixelBuffer
            self.frameLock.unlock()
            // Bez odbiorcy nie ma sensu kodować – klatka kluczowa pójdzie po podłączeniu.
            guard server.hasClient else { return }
            encoder.encode(pixelBuffer, presentationTime: time)
        }
        capture.onStopped = { [weak self] error in
            self?.queue.async {
                guard let self, self.active else { return }
                let message = error.localizedDescription
                self.stopLocked()
                self.onStopped?(message)
            }
        }
        encoder.onFrame = { [weak self, weak server, weak encoder] frame in
            guard let self, let server, let encoder else { return }
            self.deliver(frame, to: server, encoder: encoder)
        }
        encoder.onError = { [weak self] status in
            self?.frameLock.lock()
            self?.awaitingKeyframe = true
            self?.frameLock.unlock()
            NSLog("BorderlessMouse: błąd kodera wideo \(status)")
        }
        server.onClientConnected = { [weak self] in
            self?.queue.async { self?.forceKeyframe() }
        }
        server.onClientDisconnected = { [weak self] in
            self?.queue.async {
                guard let self, self.active else { return }
                self.stopLocked()
                self.onStopped?(L10n.text("Windows zamknął strumień ekranu.", "Windows closed the display stream."))
            }
        }
    }

    /// Wątek VideoToolbox. Po odrzuconej klatce kolejne klatki P nie mają
    /// odniesienia, więc czekamy na klatkę kluczową.
    private func deliver(_ encoded: VideoEncoder.EncodedFrame, to server: VideoServer, encoder: VideoEncoder) {
        frameLock.lock()
        if awaitingKeyframe && !encoded.isKeyframe {
            droppedFrames &+= 1
            frameLock.unlock()
            return
        }
        frameLock.unlock()
        let micros = UInt64(max(0, CMTimeGetSeconds(encoded.presentationTime)) * 1_000_000)
        let frame = VideoStream.Frame(kind: .h264AccessUnit,
                                      flags: encoded.isKeyframe ? [.keyframe] : [],
                                      width: encoded.width, height: encoded.height,
                                      captureMicros: micros, payload: encoded.annexB)
        let sent = server.send(frame)
        frameLock.lock()
        if sent {
            if encoded.isKeyframe { awaitingKeyframe = false }
        } else {
            droppedFrames &+= 1
            if !awaitingKeyframe {
                awaitingKeyframe = true
                encoder.requestKeyframe()
            }
        }
        frameLock.unlock()
    }

    /// Na `queue`. Statyczny ekran nie generuje klatek, więc ostatnią klatkę
    /// kodujemy ponownie – odbiorca od razu dostaje pełny obraz.
    private func forceKeyframe() {
        guard let encoder, let server, server.hasClient else { return }
        frameLock.lock()
        awaitingKeyframe = true
        let last = lastPixelBuffer
        frameLock.unlock()
        encoder.requestKeyframe()
        if let last {
            encoder.encode(last, presentationTime: CMClockGetTime(CMClockGetHostTimeClock()))
        }
    }

    // MARK: - Statystyki i zmiana trybu (na `queue`)

    private func startStatsTimer() {
        lastStats = (0, 0)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 1, repeating: 1)
        timer.setEventHandler { [weak self] in self?.tick() }
        timer.resume()
        statsTimer = timer
    }

    private func tick() {
        guard active, let server, let display = virtualDisplay, let encoder else { return }
        let frames = server.framesSent
        let bytes = server.bytesSent
        frameLock.lock()
        let dropped = droppedFrames
        frameLock.unlock()
        onStats?(Stats(framesPerSecond: Int(frames &- lastStats.frames),
                       kilobitsPerSecond: Int((bytes &- lastStats.bytes) * 8 / 1000),
                       dropped: dropped, width: encoder.width, height: encoder.height))
        lastStats = (frames, bytes)

        let (width, height) = display.pixelSize
        guard width != encoder.width || height != encoder.height else { return }
        reconfigure(width: width, height: height)
    }

    private func reconfigure(width: Int, height: Int) {
        guard let capture, let server, let display = virtualDisplay else { return }
        do {
            let replacement = try VideoEncoder(width: width, height: height, bitrate: bitrate)
            encoder?.onFrame = nil
            encoder?.invalidate()
            encoder = replacement
            wire(capture: capture, encoder: replacement, server: server)
            display.attach(to: edge)
            let queue = self.queue
            Task { [weak self] in
                try? await capture.update(width: width, height: height)
                queue.async {
                    self?.forceKeyframe()
                    self?.onDisplayChanged?()
                }
            }
        } catch {
            let message = error.localizedDescription
            stopLocked()
            onStopped?(message)
        }
    }

    // MARK: - Pomocnicze

    private func stopLocked() {
        generation &+= 1
        active = false
        statsTimer?.cancel()
        statsTimer = nil
        capture?.onFrame = nil
        capture?.onStopped = nil
        capture?.stop()
        capture = nil
        encoder?.onFrame = nil
        encoder?.invalidate()
        encoder = nil
        server?.onClientConnected = nil
        server?.onClientDisconnected = nil
        server?.stop()
        server = nil
        virtualDisplay = nil // zwolnienie obiektu usuwa monitor z systemu
        frameLock.lock()
        displayIDStorage = nil
        lastPixelBuffer = nil
        awaitingKeyframe = true
        droppedFrames = 0
        frameLock.unlock()
    }

    private func onQueue<T>(_ work: () throws -> T) rethrows -> T {
        try queue.sync(execute: work)
    }
}

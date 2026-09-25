import Foundation
import Network

/// Jednorazowy serwer TCP strumienia ekranu. Nasłuchuje na losowym porcie,
/// przyjmuje dokładnie jednego klienta, który pierwszy prześle poprawny token
/// (znany tylko z zaszyfrowanego kanału sterowania), i od tej chwili wysyła
/// mu szyfrowane rekordy wideo. Wideo nie dzieli połączenia z wejściem, więc
/// duża klatka kluczowa nie opóźnia ruchów myszy.
final class VideoServer {
    enum ServerError: LocalizedError {
        case listener(String)

        var errorDescription: String? {
            switch self {
            case let .listener(message):
                return L10n.text("Serwer wideo: \(message)", "Video server: \(message)")
            }
        }
    }

    /// Klient uwierzytelniony – należy wysłać klatkę kluczową.
    var onClientConnected: (() -> Void)?
    var onClientDisconnected: (() -> Void)?

    private let queue = DispatchQueue(label: "blm.video.server", qos: .userInteractive)
    private let token: Data
    private var sealer: VideoStream.Sealer
    private var listener: NWListener?
    private var pending: NWConnection?
    private var client: NWConnection?
    private var inFlightFrames = 0
    private var inFlightBytes = 0
    private var stopped = false

    /// Nie czekamy na zaległe klatki: gdy sieć nie nadąża, klatkę odrzucamy
    /// i prosimy koder o klatkę kluczową (patrz VideoPipeline).
    /// Tryb okien wysyła kilka strumieni naraz, stąd zapas większy niż 2–3 klatki.
    private let maxInFlightFrames = 12
    private let maxInFlightBytes = 8 * 1024 * 1024

    private(set) var framesSent: UInt64 = 0
    private(set) var bytesSent: UInt64 = 0

    init(key: Data, token: Data) {
        precondition(token.count == VideoStream.tokenBytes)
        self.token = token
        sealer = VideoStream.Sealer(key: key)
    }

    /// Uruchamia nasłuch i zwraca przydzielony port (na kolejce serwera).
    func start(completion: @escaping (Result<UInt16, Error>) -> Void) {
        let params = NWParameters.tcp
        if let tcp = params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
            tcp.noDelay = true
            tcp.enableKeepalive = true
            tcp.keepaliveIdle = 5
            tcp.keepaliveInterval = 2
            tcp.keepaliveCount = 3
        }
        do {
            let listener = try NWListener(using: params, on: .any)
            self.listener = listener
            var reported = false
            listener.stateUpdateHandler = { [weak self, weak listener] state in
                guard let self, let listener else { return }
                switch state {
                case .ready:
                    guard !reported, let port = listener.port?.rawValue else { return }
                    reported = true
                    completion(.success(port))
                case .failed(let error):
                    if !reported {
                        reported = true
                        completion(.failure(ServerError.listener(error.localizedDescription)))
                    }
                    self.stopLocked()
                default:
                    break
                }
            }
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            listener.start(queue: queue)
        } catch {
            queue.async { completion(.failure(ServerError.listener(error.localizedDescription))) }
        }
    }

    func stop() {
        queue.sync { stopLocked() }
    }

    var hasClient: Bool { queue.sync { client != nil } }

    /// Szyfruje i wysyła rekord. Zwraca false, gdy nie ma klienta albo sieć
    /// nie nadąża (wtedy rekord nie został wysłany).
    func send(_ frame: VideoStream.Frame) -> Bool {
        queue.sync {
            guard let client, !stopped else { return false }
            guard inFlightFrames < maxInFlightFrames, inFlightBytes < maxInFlightBytes else { return false }
            guard let record = sealer.seal(frame.encoded()) else {
                closeClient()
                return false
            }
            let size = record.count
            inFlightFrames += 1
            inFlightBytes += size
            client.send(content: record, completion: .contentProcessed { [weak self, weak client] error in
                guard let self else { return }
                self.inFlightFrames -= 1
                self.inFlightBytes -= size
                if error != nil, let client, client === self.client { self.closeClient() }
            })
            framesSent &+= 1
            bytesSent &+= UInt64(size)
            return true
        }
    }

    // MARK: - Private (na `queue`)

    private func accept(_ connection: NWConnection) {
        guard client == nil, pending == nil, !stopped else {
            connection.cancel()
            return
        }
        pending = connection
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection else { return }
            switch state {
            case .ready:
                if connection === self.pending { self.readToken(connection) }
            case .failed, .cancelled:
                if connection === self.pending { self.pending = nil }
                if connection === self.client { self.closeClient() }
            default:
                break
            }
        }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 3) { [weak self, weak connection] in
            guard let self, let connection, connection === self.pending else { return }
            self.pending = nil
            connection.cancel()
        }
    }

    private func readToken(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: VideoStream.tokenBytes,
                           maximumLength: VideoStream.tokenBytes) { [weak self, weak connection] data, _, _, error in
            guard let self, let connection, connection === self.pending else { return }
            self.pending = nil
            guard error == nil, let data, ControlCrypto.constantTimeEqual(data, self.token) else {
                connection.cancel()
                return
            }
            self.client = connection
            // Jednorazowy port: po uwierzytelnieniu nikt więcej się nie podłączy.
            self.listener?.cancel()
            self.listener = nil
            self.watchForClose(connection)
            self.onClientConnected?()
        }
    }

    /// Windows niczego nie wysyła po tokenie; odczyt służy tylko do wykrycia rozłączenia.
    private func watchForClose(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64) { [weak self, weak connection] _, _, complete, error in
            guard let self, let connection, connection === self.client else { return }
            if complete || error != nil {
                self.closeClient()
            } else {
                self.watchForClose(connection)
            }
        }
    }

    private func closeClient() {
        guard let client else { return }
        self.client = nil
        client.stateUpdateHandler = nil
        client.cancel()
        inFlightFrames = 0
        inFlightBytes = 0
        onClientDisconnected?()
    }

    private func stopLocked() {
        stopped = true
        listener?.cancel()
        listener = nil
        pending?.cancel()
        pending = nil
        if let client {
            self.client = nil
            client.stateUpdateHandler = nil
            client.cancel()
        }
    }
}

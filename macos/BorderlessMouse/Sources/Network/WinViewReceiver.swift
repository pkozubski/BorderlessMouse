import Foundation
import Network

/// Jednorazowy odbiornik strumienia okien Windows (kierunek Windows → Mac). Mac nasłuchuje
/// (tak jak przy ekranie wirtualnym Maca), więc Windows nie musi otwierać portu w zaporze.
/// Pierwszy klient z poprawnym tokenem zostaje, nasłuch się zamyka, a dalej płyną
/// zaszyfrowane rekordy w formacie z `VideoStream` (kind 1 = cały wirtualny monitor Windows).
final class WinViewReceiver {
    enum ReceiverError: LocalizedError {
        case listener(String)

        var errorDescription: String? {
            switch self {
            case let .listener(message):
                return L10n.text("Odbiornik obrazu Windows: \(message)", "Windows view receiver: \(message)")
            }
        }
    }

    /// Klatka H.264 (na kolejce odbiornika).
    var onFrame: ((VideoStream.Frame) -> Void)?
    var onConnected: (() -> Void)?
    /// Strumień się urwał albo był niepoprawny; nil = zatrzymany lokalnie.
    var onClosed: ((String?) -> Void)?

    private let queue = DispatchQueue(label: "blm.winview.receiver", qos: .userInteractive)
    private let token: Data
    private var opener: VideoStream.Opener
    private var listener: NWListener?
    private var pending: NWConnection?
    private var client: NWConnection?
    private var stopped = false

    private(set) var framesReceived: UInt64 = 0
    private(set) var bytesReceived: UInt64 = 0

    init(key: Data, token: Data) {
        precondition(token.count == VideoStream.tokenBytes)
        self.token = token
        opener = VideoStream.Opener(key: key)
    }

    /// Uruchamia nasłuch i zwraca przydzielony port.
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
                        completion(.failure(ReceiverError.listener(error.localizedDescription)))
                    }
                    self.stopLocked()
                default:
                    break
                }
            }
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            listener.start(queue: queue)
        } catch {
            queue.async { completion(.failure(ReceiverError.listener(error.localizedDescription))) }
        }
    }

    func stop() {
        queue.sync { stopLocked() }
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
            case .failed(let error):
                if connection === self.pending { self.pending = nil }
                if connection === self.client { self.close(L10n.text("Błąd połączenia: \(error.localizedDescription)",
                                                                     "Connection error: \(error.localizedDescription)")) }
            case .cancelled:
                if connection === self.pending { self.pending = nil }
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
            self.listener?.cancel()
            self.listener = nil
            self.onConnected?()
            self.readLength(connection)
        }
    }

    private func readLength(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: VideoStream.lengthBytes,
                           maximumLength: VideoStream.lengthBytes) { [weak self, weak connection] data, _, complete, error in
            guard let self, let connection, connection === self.client else { return }
            guard error == nil, let data, data.count == VideoStream.lengthBytes else {
                self.close(complete ? L10n.text("Windows zamknął strumień okien.", "Windows closed the window stream.")
                                    : L10n.text("Przerwane połączenie strumienia okien.", "The window stream connection was interrupted."))
                return
            }
            let length = data.withUnsafeBytes { Int(UInt32(littleEndian: $0.loadUnaligned(as: UInt32.self))) }
            guard length >= VideoStream.counterBytes + VideoStream.tagBytes, length <= VideoStream.maxRecordBytes else {
                self.close(L10n.text("Nieprawidłowy rekord strumienia okien.", "Invalid window stream record."))
                return
            }
            self.readBody(connection, length: length)
        }
    }

    private func readBody(_ connection: NWConnection, length: Int) {
        connection.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self, weak connection] data, _, _, error in
            guard let self, let connection, connection === self.client else { return }
            guard error == nil, let data, data.count == length else {
                self.close(L10n.text("Przerwane połączenie strumienia okien.", "The window stream connection was interrupted."))
                return
            }
            guard let clear = self.opener.open(data), let frame = VideoStream.Frame(decoding: clear),
                  frame.kind == .h264AccessUnit else {
                self.close(L10n.text("Błąd integralności strumienia okien.", "Window stream integrity check failed."))
                return
            }
            self.framesReceived &+= 1
            self.bytesReceived &+= UInt64(VideoStream.lengthBytes + length)
            self.onFrame?(frame)
            self.readLength(connection)
        }
    }

    private func close(_ reason: String?) {
        guard !stopped else { return }
        stopLocked()
        onClosed?(reason)
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

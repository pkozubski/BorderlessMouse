import Foundation
import Network

/// Uwierzytelniony serwer kanału sterowania. Połączenie nie staje się aktywne,
/// dopóki klient nie udowodni znajomości kodu parowania. Wszystkie komunikaty
/// aplikacyjne są następnie przenoszone w kopertach AES-256-GCM.
final class ControlServer {
    struct PeerInfo: Equatable {
        let name: String
        let address: String
    }

    struct SessionKeys {
        let audioKey: Data
        let audioSessionID: UInt64
    }

    var onPeerConnected: ((PeerInfo) -> Void)?
    var onPeerDisconnected: (() -> Void)?
    var onMessage: ((MessageType, [UInt8], SessionKeys) -> Void)?
    var onListeningChanged: ((Bool, String?) -> Void)?

    private enum HandshakeState {
        case waitingForHello
        case waitingForAuthentication(clientNonce: Data, serverNonce: Data,
                                      peer: PeerInfo, session: SecureSession)
    }

    private let queue = DispatchQueue(label: "blm.control", qos: .userInteractive)
    private let callbackQueue: DispatchQueue
    private var listener: NWListener?
    private var connection: NWConnection?
    private var inbox: [UInt8] = []
    private var peer: PeerInfo?
    private var secureSession: SecureSession?
    private var handshakeState: HandshakeState = .waitingForHello
    private var handshakeTimeout: DispatchWorkItem?
    private var blockedUntil: DispatchTime = .now()
    private let localName: () -> String
    private let pairingKey: () -> Data

    init(callbackQueue: DispatchQueue = .main,
         localName: @escaping () -> String,
         pairingKey: @escaping () -> Data) {
        self.callbackQueue = callbackQueue
        self.localName = localName
        self.pairingKey = pairingKey
    }

    var isConnected: Bool { peer != nil }

    func start(port: UInt16) {
        stop()
        let params = NWParameters.tcp
        if let tcp = params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
            tcp.noDelay = true
            tcp.enableKeepalive = true
            tcp.keepaliveIdle = 5
            tcp.keepaliveInterval = 2
            tcp.keepaliveCount = 3
        }
        params.allowLocalEndpointReuse = true
        do {
            let listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
            self.listener = listener
            listener.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    self.callbackQueue.async { self.onListeningChanged?(true, nil) }
                case .failed(let error):
                    self.callbackQueue.async { self.onListeningChanged?(false, error.localizedDescription) }
                case .cancelled:
                    self.callbackQueue.async { self.onListeningChanged?(false, nil) }
                default:
                    break
                }
            }
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            listener.start(queue: queue)
        } catch {
            callbackQueue.async { self.onListeningChanged?(false, error.localizedDescription) }
        }
    }

    func stop() {
        queue.sync {
            self.closeConnection(notify: true)
            self.listener?.cancel()
            self.listener = nil
        }
    }

    /// Przyjmuje dokładnie jedną kompletną ramkę aplikacyjną i szyfruje ją.
    func send(_ data: Data) {
        queue.async {
            guard let connection = self.connection,
                  self.peer != nil,
                  let session = self.secureSession,
                  let envelope = session.seal(data) else { return }
            connection.send(content: Frame.secure(envelope), completion: .contentProcessed { error in
                if error != nil { self.closeConnection(notify: true) }
            })
        }
    }

    func disconnectPeer() {
        queue.async { self.closeConnection(notify: true) }
    }

    // MARK: - Private (na `queue`)

    private func accept(_ newConnection: NWConnection) {
        guard connection == nil, DispatchTime.now() >= blockedUntil else {
            newConnection.start(queue: queue)
            newConnection.send(content: Frame.reject("Serwer jest zajęty lub chwilowo zablokowany"),
                               completion: .contentProcessed { _ in newConnection.cancel() })
            return
        }
        connection = newConnection
        peer = nil
        secureSession = nil
        handshakeState = .waitingForHello
        inbox.removeAll(keepingCapacity: true)

        let timeout = DispatchWorkItem { [weak self, weak newConnection] in
            guard let self, let newConnection, newConnection === self.connection, self.peer == nil else { return }
            self.closeConnection(notify: false)
        }
        handshakeTimeout = timeout
        queue.asyncAfter(deadline: .now() + 5, execute: timeout)

        newConnection.stateUpdateHandler = { [weak self, weak newConnection] state in
            guard let self, let newConnection, newConnection === self.connection else { return }
            switch state {
            case .ready:
                self.receive(on: newConnection)
            case .failed, .cancelled:
                self.closeConnection(notify: true)
            default:
                break
            }
        }
        newConnection.start(queue: queue)
    }

    private func closeConnection(notify: Bool) {
        handshakeTimeout?.cancel()
        handshakeTimeout = nil
        guard let current = connection else { return }
        let hadPeer = peer != nil
        connection = nil
        peer = nil
        secureSession = nil
        handshakeState = .waitingForHello
        inbox.removeAll(keepingCapacity: true)
        current.stateUpdateHandler = nil
        current.cancel()
        if hadPeer, notify { callbackQueue.async { self.onPeerDisconnected?() } }
    }

    private func receive(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { [weak self, weak connection] data, _, complete, error in
            guard let self, let connection, connection === self.connection else { return }
            if let data, !data.isEmpty {
                self.inbox.append(contentsOf: data)
                if self.inbox.count > ProtocolConstants.maxWireFrame + 6 {
                    self.failAuthentication(on: connection, reason: "Przekroczono limit ramki")
                    return
                }
                self.drainInbox(connection)
            }
            if complete || error != nil {
                self.closeConnection(notify: true)
                return
            }
            self.receive(on: connection)
        }
    }

    private func drainInbox(_ connection: NWConnection) {
        var cursor = 0
        while inbox.count - cursor >= 2 {
            let typeByte = inbox[cursor]
            var length = Int(inbox[cursor + 1])
            var header = 2
            if length == 0xFF {
                guard inbox.count - cursor >= 6 else { break }
                let rawLength = UInt32(inbox[cursor + 2])
                    | (UInt32(inbox[cursor + 3]) << 8)
                    | (UInt32(inbox[cursor + 4]) << 16)
                    | (UInt32(inbox[cursor + 5]) << 24)
                guard rawLength <= UInt32(ProtocolConstants.maxWireFrame) else {
                    failAuthentication(on: connection, reason: "Nieprawidłowa długość ramki")
                    return
                }
                length = Int(rawLength)
                header = 6
            }
            guard inbox.count - cursor >= header + length else { break }
            let payload = Array(inbox[(cursor + header)..<(cursor + header + length)])
            cursor += header + length
            handle(typeByte: typeByte, payload: payload, connection: connection)
            guard connection === self.connection else { return }
        }
        if cursor > 0 { inbox.removeFirst(cursor) }
    }

    private func handle(typeByte: UInt8, payload: [UInt8], connection: NWConnection) {
        guard let type = MessageType(rawValue: typeByte) else {
            failAuthentication(on: connection, reason: "Nieznany typ wiadomości")
            return
        }
        if let session = secureSession, peer != nil {
            guard type == .secure,
                  let clear = session.open(Data(payload)),
                  let (innerType, innerPayload) = Frame.parseSingle(clear),
                  !Self.isHandshake(innerType) else {
                failAuthentication(on: connection, reason: "Błąd integralności sesji")
                return
            }
            handleAuthenticated(innerType, payload: innerPayload, connection: connection, session: session)
            return
        }
        handleHandshake(type, payload: payload, connection: connection)
    }

    private func handleHandshake(_ type: MessageType, payload: [UInt8], connection: NWConnection) {
        switch handshakeState {
        case .waitingForHello:
            guard type == .hello,
                  payload.count >= 1 + ControlCrypto.nonceBytes,
                  payload[0] == ProtocolConstants.version else {
                failAuthentication(on: connection, reason: "Wymagany jest bezpieczny protokół v2")
                return
            }
            let clientNonce = Data(payload[1..<(1 + ControlCrypto.nonceBytes)])
            let nameBytes = payload.dropFirst(1 + ControlCrypto.nonceBytes)
            guard nameBytes.count <= 200 else {
                failAuthentication(on: connection, reason: "Nieprawidłowa nazwa urządzenia")
                return
            }
            let address = peerAddress(connection)
            let name = String(decoding: nameBytes, as: UTF8.self)
            let pendingPeer = PeerInfo(name: name.isEmpty ? address : name, address: address)
            let serverNonce = ControlCrypto.randomNonce()
            let secret = pairingKey()
            guard secret.count == 16 else {
                failAuthentication(on: connection, reason: "Brak kodu parowania na Macu")
                return
            }
            let proof = ControlCrypto.proof(secret: secret, role: "server", clientNonce: clientNonce, serverNonce: serverNonce)
            let session = SecureSession(secret: secret, clientNonce: clientNonce, serverNonce: serverNonce, role: .server)
            handshakeState = .waitingForAuthentication(clientNonce: clientNonce, serverNonce: serverNonce,
                                                        peer: pendingPeer, session: session)
            connection.send(content: Frame.challenge(name: localName(), nonce: serverNonce, proof: proof),
                            completion: .contentProcessed { error in
                if error != nil { self.closeConnection(notify: false) }
            })

        case let .waitingForAuthentication(clientNonce, serverNonce, pendingPeer, session):
            let expected = ControlCrypto.proof(secret: pairingKey(), role: "client",
                                               clientNonce: clientNonce, serverNonce: serverNonce)
            guard type == .authenticate,
                  payload.count == ControlCrypto.proofBytes,
                  ControlCrypto.constantTimeEqual(Data(payload), expected) else {
                failAuthentication(on: connection, reason: "Kod parowania jest nieprawidłowy")
                return
            }
            handshakeTimeout?.cancel()
            handshakeTimeout = nil
            secureSession = session
            peer = pendingPeer
            guard let envelope = session.seal(Frame.ready(name: localName())) else {
                closeConnection(notify: false)
                return
            }
            connection.send(content: Frame.secure(envelope), completion: .contentProcessed { error in
                if error != nil { self.closeConnection(notify: true) }
            })
            callbackQueue.async { self.onPeerConnected?(pendingPeer) }
        }
    }

    private func handleAuthenticated(_ type: MessageType, payload: [UInt8],
                                     connection: NWConnection, session: SecureSession) {
        if type == .ping {
            guard let envelope = session.seal(Frame.pong(payload)) else {
                closeConnection(notify: true)
                return
            }
            connection.send(content: Frame.secure(envelope), completion: .contentProcessed { error in
                if error != nil { self.closeConnection(notify: true) }
            })
            return
        }
        let keys = SessionKeys(audioKey: session.audioKey, audioSessionID: session.audioSessionID)
        let callback = onMessage
        callbackQueue.async { callback?(type, payload, keys) }
    }

    private func failAuthentication(on connection: NWConnection, reason: String) {
        if peer == nil { blockedUntil = .now() + .milliseconds(750) }
        connection.send(content: Frame.reject(reason), completion: .contentProcessed { _ in
            self.closeConnection(notify: true)
        })
    }

    private func peerAddress(_ connection: NWConnection) -> String {
        if case let .hostPort(host, _) = connection.endpoint {
            return "\(host)".components(separatedBy: "%").first ?? "\(host)"
        }
        return "?"
    }

    private static func isHandshake(_ type: MessageType) -> Bool {
        switch type {
        case .hello, .challenge, .authenticate, .secure, .reject, .ready:
            return true
        default:
            return false
        }
    }
}

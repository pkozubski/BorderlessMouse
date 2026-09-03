import CryptoKit
import Foundation
import Security

/// Kod parowania jest tylko czytelną reprezentacją 128-bitowego sekretu.
/// Myślniki i wielkość liter nie mają znaczenia przy wpisywaniu kodu.
enum PairingCodeCodec {
    private static let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".utf8)

    static func encode(_ data: Data) -> String {
        var output = [UInt8]()
        var accumulator: UInt32 = 0
        var bits = 0
        for byte in data {
            accumulator = (accumulator << 8) | UInt32(byte)
            bits += 8
            while bits >= 5 {
                bits -= 5
                output.append(alphabet[Int((accumulator >> UInt32(bits)) & 0x1F)])
            }
        }
        if bits > 0 {
            output.append(alphabet[Int((accumulator << UInt32(5 - bits)) & 0x1F)])
        }
        let raw = String(decoding: output, as: UTF8.self)
        return stride(from: 0, to: raw.count, by: 5).map { start in
            let a = raw.index(raw.startIndex, offsetBy: start)
            let b = raw.index(a, offsetBy: min(5, raw.count - start))
            return String(raw[a..<b])
        }.joined(separator: "-")
    }

    static func decode(_ code: String) -> Data? {
        let normalized = code.uppercased().filter { $0.isLetter || $0.isNumber }
        // 128 bitów daje dokładnie 26 znaków Base32. Dwa najmłodsze bity
        // ostatniego znaku są dopełnieniem i muszą pozostać zerowe.
        guard normalized.utf8.count == 26,
              let last = normalized.utf8.last,
              let lastIndex = alphabet.firstIndex(of: last),
              lastIndex & 0x03 == 0 else { return nil }
        var lookup = [UInt8: UInt8]()
        for (index, byte) in alphabet.enumerated() { lookup[byte] = UInt8(index) }
        var output = [UInt8]()
        var accumulator: UInt32 = 0
        var bits = 0
        for byte in normalized.utf8 {
            guard let value = lookup[byte] else { return nil }
            accumulator = (accumulator << 5) | UInt32(value)
            bits += 5
            if bits >= 8 {
                bits -= 8
                output.append(UInt8((accumulator >> UInt32(bits)) & 0xFF))
            }
        }
        guard output.count == 16 else { return nil }
        return Data(output)
    }
}

/// Sekret parowania nigdy nie trafia do UserDefaults ani logów.
final class PairingKeyStore {
    static let shared = PairingKeyStore()

    private let service = "com.borderlessmouse.pairing"
    private let account = "primary"
    private(set) var storageError: String?
    private var storedKey: Data

    private init() {
        if let existing = Self.read(service: service, account: account), existing.count == 16 {
            storedKey = existing
        } else {
            storedKey = Self.randomKey()
            storageError = Self.write(storedKey, service: service, account: account)
        }
    }

    var key: Data { storedKey }
    var displayCode: String { PairingCodeCodec.encode(storedKey) }

    @discardableResult
    func regenerate() -> String? {
        let next = Self.randomKey()
        if let error = Self.write(next, service: service, account: account) {
            storageError = error
            return error
        }
        storedKey = next
        storageError = nil
        return nil
    }

    private static func randomKey() -> Data {
        let count = 16
        var data = Data(count: count)
        let status = data.withUnsafeMutableBytes { bytes in
            SecRandomCopyBytes(kSecRandomDefault, count, bytes.baseAddress!)
        }
        precondition(status == errSecSuccess, "Nie można wygenerować bezpiecznego kodu parowania")
        return data
    }

    private static func read(service: String, account: String) -> Data? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else { return nil }
        return item as? Data
    }

    private static func write(_ data: Data, service: String, account: String) -> String? {
        let identity: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
        ]
        let update: [CFString: Any] = [
            kSecValueData: data,
            kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let updateStatus = SecItemUpdate(identity as CFDictionary, update as CFDictionary)
        if updateStatus == errSecSuccess { return nil }
        guard updateStatus == errSecItemNotFound else { return "Keychain: błąd \(updateStatus)" }
        var item = identity
        item.merge(update) { _, new in new }
        let status = SecItemAdd(item as CFDictionary, nil)
        return status == errSecSuccess ? nil : "Keychain: błąd \(status)"
    }
}

enum ControlCrypto {
    static let nonceBytes = 16
    static let proofBytes = 32
    static let tagBytes = 16
    static let counterBytes = 8

    static func randomNonce() -> Data {
        let count = nonceBytes
        var data = Data(count: count)
        let status = data.withUnsafeMutableBytes { bytes in
            SecRandomCopyBytes(kSecRandomDefault, count, bytes.baseAddress!)
        }
        precondition(status == errSecSuccess, "Nie można wygenerować nonce")
        return data
    }

    static func proof(secret: Data, role: String, clientNonce: Data, serverNonce: Data) -> Data {
        var message = Data("BorderlessMouse/v2/\(role)".utf8)
        message.append(clientNonce)
        message.append(serverNonce)
        let code = HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: secret))
        return Data(code)
    }

    static func constantTimeEqual(_ lhs: Data, _ rhs: Data) -> Bool {
        guard lhs.count == rhs.count else { return false }
        var difference: UInt8 = 0
        for (a, b) in zip(lhs, rhs) { difference |= a ^ b }
        return difference == 0
    }

    static func derive(secret: Data, salt: Data, info: String, bytes: Int = 32) -> Data {
        let key = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: secret),
                                         salt: salt,
                                         info: Data(info.utf8),
                                         outputByteCount: bytes)
        return key.withUnsafeBytes { Data($0) }
    }
}

/// Szyfruje kompletne ramki protokołu. TCP zapewnia kolejność, dlatego licznik
/// musi być ściśle rosnący; powtórzona lub przestawiona ramka kończy sesję.
final class SecureSession {
    enum Role { case client, server }

    private let sendKey: SymmetricKey
    private let receiveKey: SymmetricKey
    let audioKey: Data
    let audioSessionID: UInt64
    private var sendCounter: UInt64 = 0
    private var receiveCounter: UInt64?
    private let lock = NSLock()

    init(secret: Data, clientNonce: Data, serverNonce: Data, role: Role) {
        var salt = Data()
        salt.append(clientNonce)
        salt.append(serverNonce)
        let clientToServer = ControlCrypto.derive(secret: secret, salt: salt, info: "BorderlessMouse/v2/control/client-to-server")
        let serverToClient = ControlCrypto.derive(secret: secret, salt: salt, info: "BorderlessMouse/v2/control/server-to-client")
        switch role {
        case .client:
            sendKey = SymmetricKey(data: clientToServer)
            receiveKey = SymmetricKey(data: serverToClient)
        case .server:
            sendKey = SymmetricKey(data: serverToClient)
            receiveKey = SymmetricKey(data: clientToServer)
        }
        audioKey = ControlCrypto.derive(secret: secret, salt: salt, info: "BorderlessMouse/v2/audio/server-to-client")
        let idData = ControlCrypto.derive(secret: secret, salt: salt, info: "BorderlessMouse/v2/audio/session-id", bytes: 8)
        audioSessionID = idData.withUnsafeBytes { raw in
            raw.loadUnaligned(as: UInt64.self).littleEndian
        }
    }

    func seal(_ plaintext: Data) -> Data? {
        lock.lock()
        defer { lock.unlock() }
        guard sendCounter != UInt64.max else { return nil }
        let counter = sendCounter
        sendCounter &+= 1
        let counterData = Self.littleEndian(counter)
        do {
            let sealed = try AES.GCM.seal(plaintext,
                                          using: sendKey,
                                          nonce: try AES.GCM.Nonce(data: Self.nonce(counter)),
                                          authenticating: Self.aad(counterData))
            var output = counterData
            output.append(sealed.ciphertext)
            output.append(sealed.tag)
            return output
        } catch {
            return nil
        }
    }

    func open(_ envelope: Data) -> Data? {
        lock.lock()
        defer { lock.unlock() }
        guard envelope.count >= ControlCrypto.counterBytes + ControlCrypto.tagBytes else { return nil }
        let counterData = envelope.prefix(ControlCrypto.counterBytes)
        let counter = counterData.withUnsafeBytes { $0.loadUnaligned(as: UInt64.self).littleEndian }
        if let previous = receiveCounter, counter <= previous { return nil }
        let ciphertext = envelope.dropFirst(ControlCrypto.counterBytes).dropLast(ControlCrypto.tagBytes)
        let tag = envelope.suffix(ControlCrypto.tagBytes)
        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: Self.nonce(counter)),
                                            ciphertext: ciphertext,
                                            tag: tag)
            let clear = try AES.GCM.open(box, using: receiveKey, authenticating: Self.aad(Data(counterData)))
            receiveCounter = counter
            return clear
        } catch {
            return nil
        }
    }

    private static func nonce(_ counter: UInt64) -> Data {
        Data([0x42, 0x4C, 0x4D, 0x32]) + littleEndian(counter)
    }

    private static func aad(_ counter: Data) -> Data {
        Data("BLM2".utf8) + counter
    }

    private static func littleEndian(_ value: UInt64) -> Data {
        var v = value.littleEndian
        return withUnsafeBytes(of: &v) { Data($0) }
    }
}

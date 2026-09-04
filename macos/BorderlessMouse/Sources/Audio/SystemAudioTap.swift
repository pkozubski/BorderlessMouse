import AudioToolbox
import CoreAudio
import Foundation

/// Przechwytuje dźwięk systemowy przez Core Audio process tap (macOS 14.2+),
/// bez instalowania sterowników. Dostarcza próbki Int16 interleaved.
final class SystemAudioTap {
    struct Format: Equatable {
        let sampleRate: Double
        let channels: Int
    }

    enum TapError: LocalizedError {
        case osStatus(OSStatus, String)
        case unsupportedFormat(String)

        var errorDescription: String? {
            switch self {
            case let .osStatus(status, what):
                return L10n.text("\(what): błąd Core Audio \(status) (\(Self.describe(status)))",
                                 "\(what): Core Audio error \(status) (\(Self.describe(status)))")
            case let .unsupportedFormat(desc):
                return L10n.text("Nieobsługiwany format tapu: \(desc)", "Unsupported audio-tap format: \(desc)")
            }
        }

        private static func describe(_ s: OSStatus) -> String {
            switch s {
            case kAudioHardwareIllegalOperationError:
                return L10n.text("illegal operation – sprawdź zgodę na nagrywanie dźwięku systemowego",
                                 "illegal operation — check System Audio Recording permission")
            case kAudioHardwareNotRunningError: return "not running"
            case kAudioHardwareUnknownPropertyError: return "unknown property"
            case kAudioHardwareBadDeviceError: return "bad device"
            case kAudioHardwareUnsupportedOperationError: return "unsupported operation"
            case kAudioHardwareBadObjectError: return "bad object"
            default:
                let c = UInt32(bitPattern: s)
                let chars = [c >> 24, c >> 16, c >> 8, c].map { Character(UnicodeScalar(UInt8($0 & 0xFF))) }
                return String(chars)
            }
        }
    }

    /// Stan zgody TCC „nagrywanie dźwięku systemowego”. macOS nie ma API do
    /// odpytania jej wprost – jedyny wiarygodny test to spróbować założyć tap.
    enum Permission: Equatable {
        case unknown
        case checking
        case granted
        case denied(String)
        case failed(String)

        var isGranted: Bool { if case .granted = self { return true }; return false }

        var message: String? {
            switch self {
            case let .denied(m), let .failed(m): return m
            case .unknown, .checking, .granted: return nil
            }
        }
    }

    /// Wołane na wątku audio. `frames` ramek interleaved Int16.
    var onSamples: ((UnsafePointer<Int16>, Int) -> Void)?
    /// Poziom szczytowy 0–1 (wątek audio).
    var onLevel: ((Float) -> Void)?
    /// Zmieniło się domyślne urządzenie wyjściowe – warto zrestartować tap.
    var onDefaultDeviceChanged: (() -> Void)?

    private(set) var format: Format?
    private(set) var isRunning = false

    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "blm.audio.tap", qos: .userInteractive)
    private var streamDescription = AudioStreamBasicDescription()
    private var conversion: UnsafeMutablePointer<Int16>?
    private var conversionCapacity = 0
    private var listenerInstalled = false
    private var levelCounter = 0

    deinit { stop() }

    // MARK: - Start / stop

    func start(muteLocal: Bool, bufferFrames: UInt32 = 256) throws {
        stop()
        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        description.uuid = UUID()
        description.name = "BorderlessMouse System Audio"
        description.isPrivate = true
        description.muteBehavior = muteLocal ? .mutedWhenTapped : .unmuted

        var tap = AudioObjectID(kAudioObjectUnknown)
        var err = AudioHardwareCreateProcessTap(description, &tap)
        guard err == noErr else { throw TapError.osStatus(err, L10n.text("Tworzenie tapu", "Creating audio tap")) }
        tapID = tap

        do {
            streamDescription = try Self.readTapFormat(tapID)
            let outputUID = try Self.readDefaultOutputUID()
            let aggregateDescription: [String: Any] = [
                kAudioAggregateDeviceNameKey: "BorderlessMouse Tap Device",
                kAudioAggregateDeviceUIDKey: "blm-tap-\(UUID().uuidString)",
                kAudioAggregateDeviceMainSubDeviceKey: outputUID,
                kAudioAggregateDeviceIsPrivateKey: true,
                kAudioAggregateDeviceIsStackedKey: false,
                kAudioAggregateDeviceTapAutoStartKey: true,
                kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
                kAudioAggregateDeviceTapListKey: [[
                    kAudioSubTapDriftCompensationKey: true,
                    kAudioSubTapUIDKey: description.uuid.uuidString,
                ]],
            ]
            var agg = AudioObjectID(kAudioObjectUnknown)
            err = AudioHardwareCreateAggregateDevice(aggregateDescription as CFDictionary, &agg)
            guard err == noErr else { throw TapError.osStatus(err, L10n.text("Tworzenie urządzenia zbiorczego", "Creating aggregate audio device")) }
            aggregateID = agg

            // mniejszy bufor = mniejsze opóźnienie
            var frames = bufferFrames
            var sizeAddr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyBufferFrameSize,
                                                      mScope: kAudioObjectPropertyScopeGlobal,
                                                      mElement: kAudioObjectPropertyElementMain)
            _ = AudioObjectSetPropertyData(aggregateID, &sizeAddr, 0, nil, UInt32(MemoryLayout<UInt32>.size), &frames)

            let isFloat = streamDescription.mFormatFlags & kAudioFormatFlagIsFloat != 0
            let isInt16 = !isFloat && streamDescription.mBitsPerChannel == 16
            guard streamDescription.mFormatID == kAudioFormatLinearPCM, isFloat || isInt16 else {
                throw TapError.unsupportedFormat("formatID \(streamDescription.mFormatID), bits \(streamDescription.mBitsPerChannel)")
            }
            format = Format(sampleRate: streamDescription.mSampleRate, channels: Int(streamDescription.mChannelsPerFrame))

            var proc: AudioDeviceIOProcID?
            err = AudioDeviceCreateIOProcIDWithBlock(&proc, aggregateID, queue) { [weak self] _, inputData, _, _, _ in
                self?.process(inputData)
            }
            guard err == noErr, let proc else { throw TapError.osStatus(err, L10n.text("Tworzenie IOProc", "Creating IOProc")) }
            procID = proc
            err = AudioDeviceStart(aggregateID, proc)
            guard err == noErr else { throw TapError.osStatus(err, L10n.text("Start urządzenia", "Starting audio device")) }
            isRunning = true
            installDefaultDeviceListener()
        } catch {
            teardown()
            throw error
        }
    }

    func stop() {
        teardown()
    }

    private func teardown() {
        isRunning = false
        if aggregateID != kAudioObjectUnknown {
            if let proc = procID {
                AudioDeviceStop(aggregateID, proc)
                AudioDeviceDestroyIOProcID(aggregateID, proc)
                procID = nil
            }
            AudioHardwareDestroyAggregateDevice(aggregateID)
            aggregateID = AudioObjectID(kAudioObjectUnknown)
        }
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
        format = nil
    }

    // MARK: - Przetwarzanie (wątek audio)

    private func process(_ inputData: UnsafePointer<AudioBufferList>) {
        let abl = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inputData))
        guard abl.count > 0, let first = abl[0].mData else { return }
        let isFloat = streamDescription.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let nonInterleaved = streamDescription.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        let channels = Int(streamDescription.mChannelsPerFrame)
        let bytesPerSample = Int(streamDescription.mBitsPerChannel / 8)
        let frames: Int
        if nonInterleaved {
            frames = Int(abl[0].mDataByteSize) / bytesPerSample
        } else {
            frames = Int(abl[0].mDataByteSize) / (bytesPerSample * max(channels, 1))
        }
        guard frames > 0 else { return }
        let total = frames * channels
        ensureCapacity(total)
        guard let out = conversion else { return }

        var peak: Float = 0
        if isFloat {
            if nonInterleaved {
                for c in 0..<min(channels, abl.count) {
                    guard let data = abl[c].mData else { continue }
                    let src = data.assumingMemoryBound(to: Float.self)
                    for i in 0..<frames {
                        let v = src[i]
                        peak = max(peak, abs(v))
                        out[i * channels + c] = Self.toInt16(v)
                    }
                }
            } else {
                let src = first.assumingMemoryBound(to: Float.self)
                for i in 0..<total {
                    let v = src[i]
                    peak = max(peak, abs(v))
                    out[i] = Self.toInt16(v)
                }
            }
        } else {
            if nonInterleaved {
                for c in 0..<min(channels, abl.count) {
                    guard let data = abl[c].mData else { continue }
                    let src = data.assumingMemoryBound(to: Int16.self)
                    for i in 0..<frames {
                        out[i * channels + c] = src[i]
                        peak = max(peak, abs(Float(src[i]) / 32768))
                    }
                }
            } else {
                let src = first.assumingMemoryBound(to: Int16.self)
                for i in 0..<total {
                    out[i] = src[i]
                    peak = max(peak, abs(Float(src[i]) / 32768))
                }
            }
        }
        onSamples?(UnsafePointer(out), frames)
        levelCounter += 1
        if levelCounter >= 8 {
            levelCounter = 0
            onLevel?(min(peak, 1))
        }
    }

    @inline(__always) private static func toInt16(_ v: Float) -> Int16 {
        let clamped = min(max(v, -1), 1)
        return Int16(clamped * 32767)
    }

    private func ensureCapacity(_ samples: Int) {
        if conversionCapacity < samples {
            conversion?.deallocate()
            conversion = UnsafeMutablePointer<Int16>.allocate(capacity: samples)
            conversionCapacity = samples
        }
    }

    // MARK: - Właściwości Core Audio

    private static func readTapFormat(_ tap: AudioObjectID) throws -> AudioStreamBasicDescription {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        let err = AudioObjectGetPropertyData(tap, &addr, 0, nil, &size, &asbd)
        guard err == noErr else { throw TapError.osStatus(err, L10n.text("Odczyt formatu tapu", "Reading audio-tap format")) }
        return asbd
    }

    private static func readDefaultOutputUID() throws -> String {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var device = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var err = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &device)
        guard err == noErr, device != kAudioObjectUnknown else { throw TapError.osStatus(err, L10n.text("Domyślne wyjście audio", "Default audio output")) }

        var uidAddr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceUID,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var uid: Unmanaged<CFString>?
        var uidSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        err = AudioObjectGetPropertyData(device, &uidAddr, 0, nil, &uidSize, &uid)
        guard err == noErr, let value = uid?.takeRetainedValue() else { throw TapError.osStatus(err, L10n.text("UID urządzenia", "Audio device UID")) }
        return value as String
    }

    private func installDefaultDeviceListener() {
        guard !listenerInstalled else { return }
        listenerInstalled = true
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, queue) { [weak self] _, _ in
            guard let self, self.isRunning else { return }
            self.onDefaultDeviceChanged?()
        }
    }
}

// MARK: - Zgoda na nagrywanie dźwięku systemowego

extension SystemAudioTap {
    /// Czy błąd Core Audio oznacza brak zgody TCC, a nie usterkę sprzętu.
    static func isPermissionDenied(_ status: OSStatus) -> Bool {
        status == kAudioHardwareIllegalOperationError || status == kAudioDevicePermissionsError
    }

    static func permission(for error: Error) -> Permission {
        if case let TapError.osStatus(status, _) = error, isPermissionDenied(status) {
            return .denied(error.localizedDescription)
        }
        return .failed(error.localizedDescription)
    }

    /// Zakłada i natychmiast niszczy tap, żeby sprawdzić zgodę – a gdy jej nie
    /// ma, wywołać systemowe okno pytania. Blokuje wątek do czasu decyzji
    /// użytkownika, więc wolno to wołać tylko z kolejki roboczej.
    static func probePermission() -> Permission {
        let tap = SystemAudioTap()
        defer { tap.stop() }
        do {
            try tap.start(muteLocal: false, bufferFrames: 512)
            return .granted
        } catch {
            return permission(for: error)
        }
    }
}

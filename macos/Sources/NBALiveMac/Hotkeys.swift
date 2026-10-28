import AppKit
import Carbon.HIToolbox

/// Carbon's system hotkeys work without the Accessibility or input-monitoring permission.
final class Hotkeys {
    private var handler: EventHandlerRef?
    private var references: [EventHotKeyRef] = []
    private let action: (UInt32) -> Void
    private(set) var failed = false
    private static let signature: OSType = 0x4e42414c
    private static let callback: EventHandlerUPP = { _, event, pointer in
        guard let event, let pointer else { return OSStatus(eventNotHandledErr) }
        var identity = EventHotKeyID()
        let status = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                       nil, UInt32(MemoryLayout<EventHotKeyID>.size), nil, &identity)
        guard status == noErr, identity.signature == Hotkeys.signature else { return OSStatus(eventNotHandledErr) }
        let object = Unmanaged<Hotkeys>.fromOpaque(pointer).takeUnretainedValue()
        object.action(identity.id)
        return noErr
    }
    init(action: @escaping (UInt32) -> Void) {
        self.action = action
        var event = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let installed = InstallEventHandler(GetApplicationEventTarget(), Self.callback, 1, &event,
                                            Unmanaged.passUnretained(self).toOpaque(), &handler)
        guard installed == noErr else { failed = true; return }
        for (key, identifier) in [(UInt32(kVK_ANSI_H), UInt32(1)), (UInt32(kVK_ANSI_L), UInt32(2))] {
            var reference: EventHotKeyRef?
            let result = RegisterEventHotKey(key, UInt32(cmdKey | optionKey), EventHotKeyID(signature: Self.signature, id: identifier),
                                             GetApplicationEventTarget(), 0, &reference)
            if result == noErr, let reference { references.append(reference) } else { failed = true }
        }
    }
    deinit {
        for reference in references { UnregisterEventHotKey(reference) }
        if let handler { RemoveEventHandler(handler) }
    }
}

import Foundation

/// The server's predecessor links and page order are authoritative; IDs are opaque strings.
public struct AnchorHistory {
    public private(set) var messages: [LiveMessage] = []
    public private(set) var cursor: String?
    public private(set) var initialized = false
    public private(set) var addedCount = 0
    private var known: Set<String> = []
    private var cursors: Set<String> = []
    private var pending: [LiveMessage] = []
    private var pendingIndex: [String: Int] = [:]
    private var direction: Bool?

    public init() {}

    /// Returns true only after a complete page chain; partial histories stay private across retries.
    public mutating func accept(_ page: [LiveMessage]) throws -> Bool {
        let forward = Self.forward(page) ?? direction ?? false
        if let previous = direction, previous != forward {
            direction = forward; cursor = nil; cursors.removeAll(); pending.removeAll(); pendingIndex.removeAll()
            return false
        }
        direction = forward
        let boundary = !forward && initialized && page.contains { known.contains($0.id) }
        if let last = page.last, !boundary, cursors.contains(last.id) || last.id == cursor {
            throw SourceError.invalidData("直播分页游标未前进")
        }
        for item in page {
            if let index = pendingIndex[item.id] { pending[index] = item }
            else { pendingIndex[item.id] = pending.count; pending.append(item) }
        }
        if let last = page.last, !boundary { cursor = last.id; cursors.insert(last.id); return false }

        let ordered = forward ? pending : Array(pending.reversed())
        var index = Dictionary(uniqueKeysWithValues: messages.enumerated().map { ($0.element.id, $0.offset) })
        var added = 0
        for item in ordered {
            if let i = index[item.id] { messages[i] = item }
            else { index[item.id] = messages.count; messages.append(item); added += 1 }
            known.insert(item.id)
        }
        addedCount = initialized ? added : 0
        initialized = true; pending.removeAll(); pendingIndex.removeAll(); cursors.removeAll()
        if !forward { cursor = nil }
        return true
    }

    static func forward(_ page: [LiveMessage]) -> Bool? {
        guard page.count > 1 else { return nil }
        for i in 1..<page.count {
            if !page[i].previousID.isEmpty && page[i].previousID == page[i - 1].id { return true }
            if !page[i - 1].previousID.isEmpty && page[i - 1].previousID == page[i].id { return false }
        }
        for i in 1..<page.count where !page[i - 1].time.isEmpty && !page[i].time.isEmpty && page[i - 1].time != page[i].time {
            return page[i - 1].time < page[i].time
        }
        return nil
    }
}

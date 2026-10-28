import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public protocol LiveSource {
    func games(date: String) async throws -> [Game]
    func match(_ game: Game) async throws -> Game
    func room(_ game: Game) async throws -> Room?
    func messages(_ room: Room, cursor: String?) async throws -> [LiveMessage]
    func statistics(_ game: Game) async throws -> BoxScore
    func structured(_ game: Game) async throws -> [LiveMessage]
}

/// All requests use one connection gate. Live requests waiting in the queue go first.
actor RequestGate {
    private var occupied = false
    private var live: [CheckedContinuation<Void, Never>] = []
    private var secondary: [CheckedContinuation<Void, Never>] = []
    func acquire(live isLive: Bool) async {
        if !occupied { occupied = true; return }
        await withCheckedContinuation { continuation in
            if isLive { live.append(continuation) } else { secondary.append(continuation) }
        }
    }
    func release() {
        if !live.isEmpty { live.removeFirst().resume() }
        else if !secondary.isEmpty { secondary.removeFirst().resume() }
        else { occupied = false }
    }
}

actor DesktopIDCache {
    private var values: [String: String] = [:]
    func get(_ game: Game) -> String? { values[game.id] }
    func put(_ game: Game, id: String) { values[game.id] = id }
}

private final class RequestCancellation {
    private let lock = NSLock()
    private var task: URLSessionDataTask?
    private var cancelled = false
    func start(_ value: URLSessionDataTask) {
        lock.lock(); task = value; let wasCancelled = cancelled; lock.unlock()
        value.resume()
        if wasCancelled { value.cancel() }
    }
    func cancel() { lock.lock(); cancelled = true; let value = task; lock.unlock(); value?.cancel() }
}

private final class RejectRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

public final class NetworkLiveSource: LiveSource {
    private let session: URLSession
    private let gate = RequestGate()
    private let desktopIDs = DesktopIDCache()
    private let requestTimeout: TimeInterval
    private let mobile = "http://games.mobileapi.hupu.com/1/7.5.59/basketballapi/"
    private let live = "https://live-api.liangle.com/1/7.5.59/live/"
    public init(session: URLSession? = nil, requestTimeout: TimeInterval = 8) {
        let config = URLSessionConfiguration.ephemeral
        config.httpShouldSetCookies = false; config.urlCache = nil
        self.session = session ?? URLSession(configuration: config, delegate: RejectRedirects(), delegateQueue: nil)
        self.requestTimeout = min(60, max(0.05, requestTimeout))
    }
    private func request(_ url: URL, live: Bool, timeout: TimeInterval? = nil) async throws -> Data {
        await gate.acquire(live: live)
        defer { Task { await gate.release() } }
        try Task.checkCancellation()
        let budget = timeout ?? requestTimeout
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: budget)
        request.setValue("NbaLive/1.0 (+personal desktop reader)", forHTTPHeaderField: "User-Agent")
        request.setValue("application/json, text/html", forHTTPHeaderField: "Accept")
        let cancellation = RequestCancellation()
        let requestCopy = request
        let (data, response): (Data, URLResponse) = try await withThrowingTaskGroup(of: (Data, URLResponse).self) { group in
            group.addTask { [session] in
                try await withTaskCancellationHandler(operation: {
                    try await withCheckedThrowingContinuation { continuation in
                        let task = session.dataTask(with: requestCopy) { data, response, error in
                            if let error { continuation.resume(throwing: error) }
                            else if let data, let response { continuation.resume(returning: (data, response)) }
                            else { continuation.resume(throwing: SourceError.invalidData("数据响应异常")) }
                        }
                        cancellation.start(task)
                    }
                }, onCancel: { cancellation.cancel() })
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(budget * 1_000_000_000))
                throw URLError(.timedOut)
            }
            defer { group.cancelAll() }
            guard let result = try await group.next() else { throw SourceError.invalidData("数据响应异常") }
            return result
        }
        try Task.checkCancellation()
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else { throw SourceError.unavailable("数据服务暂不可用") }
        guard data.count <= 8 * 1024 * 1024 else { throw SourceError.invalidData("数据响应过大") }
        return data
    }
    private func url(_ base: String, _ path: String, _ query: [(String, String)]) throws -> URL {
        guard var components = URLComponents(string: base + path) else { throw SourceError.invalidData("数据地址异常") }
        components.queryItems = query.map { URLQueryItem(name: $0.0, value: $0.1) }
        guard let result = components.url else { throw SourceError.invalidData("数据地址异常") }
        return result
    }
    public func games(date: String) async throws -> [Game] {
        let target = try url(mobile, "scheduleList", [("competitionTag", "nba"), ("time_zone", "Asia/Shanghai")])
        do { return try SourceParser.schedule(await request(target, live: false, timeout: 20), date: date) }
        catch {
            try Task.checkCancellation()
            return try await desktopGames(date: date)
        }
    }
    public func match(_ game: Game) async throws -> Game {
        if game.id.hasPrefix("web:") {
            let matches = try await desktopGames(date: game.date).filter { $0.id == game.id }
            guard matches.count == 1 else { throw SourceError.invalidData("比赛身份不匹配") }
            return matches[0]
        }
        let target = try url(mobile, "singleMatch", [("matchId", game.id)])
        return try SourceParser.singleMatch(await request(target, live: true), expected: game)
    }
    public func room(_ game: Game) async throws -> Room? {
        if game.id.hasPrefix("web:") { return nil }
        let target = try url(live, "queryLiveActivityKey", [("competitionType", "basketball"), ("matchId", game.id)])
        return try SourceParser.room(await request(target, live: true), matchID: game.id)
    }
    public func messages(_ room: Room, cursor: String?) async throws -> [LiveMessage] {
        var query = [("competitionType", "basketball"), ("matchId", room.matchID), ("liveActivityKeyStr", room.key)]
        if let cursor = cursor { query.append(("commentId", cursor)) }
        let target = try url(live, "queryLiveTextList", query)
        return try SourceParser.messages(await request(target, live: true))
    }
    public func statistics(_ game: Game) async throws -> BoxScore {
        if game.id.hasPrefix("web:") { throw SourceError.unavailable("该日期暂未提供球员统计") }
        let target = try url(mobile, "playerMatchStats", [("matchId", game.id)])
        return try SourceParser.statistics(await request(target, live: false), game: game)
    }
    public func structured(_ game: Game) async throws -> [LiveMessage] {
        let id: String
        if let known = game.desktopID { id = known }
        else if let cached = await desktopIDs.get(game) { id = cached }
        else {
            let matches = try await desktopGames(date: game.date).filter { TeamCatalog.same($0.away, game.away) && TeamCatalog.same($0.home, game.home) }
            guard matches.count == 1, let found = matches[0].desktopID else { throw SourceError.unavailable("无法唯一对应赛况记录") }
            id = found; await desktopIDs.put(game, id: id)
        }
        let html = try await desktopPage("/games/playbyplay/" + id, live: true)
        return try DesktopParser.messages(html, gameID: id, expected: game)
    }

    private func desktopGames(date: String) async throws -> [Game] {
        guard date.count == 8, date.allSatisfy({ $0.isASCII && $0.isNumber }) else { throw SourceError.invalidData("比赛日期异常") }
        let year = date.prefix(4), month = date.dropFirst(4).prefix(2), day = date.suffix(2)
        let html = try await desktopPage("/games/\(year)-\(month)-\(day)", live: false)
        return try DesktopParser.schedule(html, date: date)
    }
    private func desktopPage(_ path: String, live: Bool) async throws -> String {
        guard let secure = URL(string: "https://nba.hupu.com" + path), let fallback = URL(string: "http://nba.hupu.com" + path) else {
            throw SourceError.invalidData("数据地址异常")
        }
        let data: Data
        do { data = try await request(secure, live: live, timeout: 8) }
        catch { try Task.checkCancellation(); data = try await request(fallback, live: live, timeout: 12) }
        guard let html = String(data: data, encoding: .utf8) else { throw SourceError.invalidData("网页编码异常") }
        return html
    }
}

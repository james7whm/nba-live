#if canImport(Combine)
import Combine
#else
protocol ObservableObject: AnyObject { }
@propertyWrapper struct Published<Value> {
    var wrappedValue: Value
    init(wrappedValue: Value) { self.wrappedValue = wrappedValue }
}
#endif
import Foundation
import NBALiveCore

@MainActor final class LiveStore: ObservableObject {
    @Published var game: Game?
    @Published var games: [Game] = []
    @Published var selectedDate = Date()
    @Published var listLoading = false
    @Published var listMessage = "正在加载比赛…"
    @Published var message = "请选择比赛"
    @Published var feedLabel = ""
    @Published var history: [LiveMessage] = []
    @Published var displayed: [LiveMessage] = []
    @Published var scoreFetchedAt: Date?
    @Published var scoreWarning = ""
    @Published var hotkeyWarning = ""
    @Published var boxScore: BoxScore?
    @Published var statisticsMessage = ""
    @Published var unread = 0
    @Published var recapMessages: [LiveMessage] = []
    @Published var recapScore = ""
    @Published var recapNotice = ""
    @Published var frozenGame: Game?
    @Published var frozenMessages: [LiveMessage]?
    @Published var frozenStatistics: BoxScore?
    @Published var theme: String { didSet { defaults.set(theme, forKey: "theme") } }
    @Published var mode: String { didSet { defaults.set(mode, forKey: "mode"); updateStatisticsDemand() } }
    @Published var sourceMode: String { didSet { defaults.set(sourceMode, forKey: "sourceMode"); restartLive() } }
    @Published var menuDisplay: String { didSet { defaults.set(menuDisplay, forKey: "menuDisplay") } }
    @Published var delivery: String { didSet { defaults.set(delivery, forKey: "delivery"); replay.removeAll(); displayed = Array(history.suffix(2)) } }
    @Published var favorites: [String] { didSet { defaults.set(favorites, forKey: "favorites"); rotation = min(rotation, max(0, favorites.count - 1)) } }
    @Published var favoriteNames: [String: String] { didSet { defaults.set(favoriteNames, forKey: "favoriteNames") } }
    @Published var rotation = 0
    @Published var popoverVisible = false
    @Published var hovering = false
    @Published var statisticsVisible = false

    var openPicker: (() -> Void)?
    var openStatistics: (() -> Void)?
    var openHistory: (() -> Void)?
    var openRecap: (() -> Void)?
    var openSettings: (() -> Void)?
    private let source: LiveSource
    private let defaults: UserDefaults
    private var liveTask: Task<Void, Never>?
    private var startupTask: Task<Void, Never>?
    private var listTask: Task<Void, Never>?
    private var listTimer: Task<Void, Never>?
    private var statisticsTask: Task<Void, Never>?
    private var displayTask: Task<Void, Never>?
    private var selection = UUID()
    private var listGeneration = UUID()
    private var loadedDate = ""
    private var feeds: [String: [LiveMessage]] = [:]
    private var unreadIDs: [String: Set<String>] = [:]
    private var activeFeed = ""
    private var replay: [LiveMessage] = []
    private var recapViewed: Set<String> = []
    private var previousRecapScore: [String: String] = [:]
    private var lastReplay = Date.distantPast
    private var lastRotation = Date()

    init(source: LiveSource = NetworkLiveSource(), defaults: UserDefaults = .standard) {
        self.source = source; self.defaults = defaults
        let t = defaults.string(forKey: "theme") ?? "system"
        let m = defaults.string(forKey: "mode") ?? "text"
        let d = defaults.string(forKey: "delivery") ?? "latest"
        let s = defaults.string(forKey: "sourceMode") ?? "anchor"
        let bar = defaults.string(forKey: "menuDisplay") ?? "full"
        theme = ["system", "warm", "dark"].contains(t) ? t : "system"
        mode = ["text", "player"].contains(m) ? m : "text"
        delivery = ["latest", "paged"].contains(d) ? d : "latest"
        sourceMode = ["anchor", "structured"].contains(s) ? s : "anchor"
        menuDisplay = ["full", "score", "icon"].contains(bar) ? bar : "full"
        var unique: [String] = []
        for id in defaults.stringArray(forKey: "favorites") ?? [] where !id.isEmpty && !unique.contains(id) { unique.append(id) }
        favorites = Array(unique.prefix(3))
        favoriteNames = defaults.dictionary(forKey: "favoriteNames") as? [String: String] ?? [:]
        displayTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                guard !Task.isCancelled, let self else { return }
                self.tick()
            }
        }
    }

    var visibleGame: Game? { hovering ? frozenGame ?? game : game }
    var visibleMessages: [LiveMessage] { hovering ? frozenMessages ?? displayed : displayed }
    var menuTitle: String {
        if menuDisplay == "icon" { return "" }
        guard let game else { return "选择比赛" }
        if menuDisplay == "score" { return "\(game.awayScore.map(String.init) ?? "—") : \(game.homeScore.map(String.init) ?? "—")" }
        return "\(TeamCatalog.short(game.away)) \(game.awayScore.map(String.init) ?? "—") : \(game.homeScore.map(String.init) ?? "—") \(TeamCatalog.short(game.home))"
    }
    var selectedFavorite: Player? {
        guard !favorites.isEmpty, let box = hovering ? (frozenStatistics ?? boxScore) : boxScore else { return nil }
        let id = favorites[min(rotation, favorites.count - 1)]
        return (box.away + box.home).first { $0.id == id }
    }
    var selectedFavoriteID: String? { favorites.isEmpty ? nil : favorites[min(rotation, favorites.count - 1)] }
    func toggleFavorite(_ player: Player) -> Bool {
        if favorites.contains(player.id) { favorites.removeAll { $0 == player.id }; favoriteNames.removeValue(forKey: player.id); return true }
        guard favorites.count < 3 else { statisticsMessage = "最多关注 3 名球员"; return false }
        favorites.append(player.id); favoriteNames[player.id] = player.name; return true
    }
    func rotate(_ step: Int) {
        guard !favorites.isEmpty else { return }
        rotation = (rotation + step + favorites.count) % favorites.count; lastRotation = Date()
    }
    func setHover(_ value: Bool) {
        guard hovering != value else { return }
        if value { frozenGame = game; frozenMessages = displayed; frozenStatistics = boxScore }
        hovering = value
        if !value { frozenGame = nil; frozenMessages = nil; frozenStatistics = nil; lastRotation = Date() }
    }
    func tick() {
        guard popoverVisible, !hovering else { return }
        let now = Date()
        if mode == "player" && favorites.count > 1 && now.timeIntervalSince(lastRotation) >= 8 { rotate(1) }
        if mode == "text" && delivery == "paged" && !replay.isEmpty && now.timeIntervalSince(lastReplay) >= 4 {
            displayed = Array(replay.prefix(2)); replay.removeFirst(min(2, replay.count)); lastReplay = now
        }
    }
    func jumpLatest() { replay.removeAll(); displayed = Array(history.suffix(2)); lastReplay = Date() }

    func start() {
        guard let id = defaults.string(forKey: "lastGameID"), !id.isEmpty else { openPicker?(); return }
        let token = selection
        message = "正在恢复上次比赛…"
        startupTask = Task { [weak self] in
            guard let self else { return }
            do {
                let list = try await self.source.games(date: BeijingDate.key(Date()))
                guard !Task.isCancelled, self.selection == token else { return }
                if let last = list.first(where: { $0.id == id && $0.status == .live }) {
                    self.select(last)
                } else { self.openPicker?() }
            } catch { guard !Task.isCancelled, self.selection == token else { return }; self.openPicker?() }
        }
    }
    func loadGames() {
        let key = BeijingDate.key(selectedDate)
        if listLoading && loadedDate == key { return }
        listTask?.cancel(); listGeneration = UUID(); let generation = listGeneration
        if loadedDate != key { games = [] }; loadedDate = key; listLoading = true; listMessage = "正在加载比赛…"
        listTask = Task { [weak self] in
            guard let self else { return }
            do {
                let result = try await self.source.games(date: key)
                guard !Task.isCancelled, self.listGeneration == generation else { return }
                self.games = result; self.listLoading = false; self.listMessage = result.isEmpty ? "当天没有比赛" : "\(result.count) 场比赛 · 北京时间"
            } catch {
                guard !Task.isCancelled, self.listGeneration == generation else { return }
                self.listLoading = false; self.listMessage = self.describe(error, prefix: "比赛列表加载失败")
            }
        }
    }
    func setPickerVisible(_ visible: Bool) {
        listTimer?.cancel(); listTimer = nil
        if visible {
            startupTask?.cancel(); startupTask = nil
            loadGames()
            listTimer = Task { [weak self] in
                while !Task.isCancelled { try? await Task.sleep(nanoseconds: 60_000_000_000); if !Task.isCancelled { self?.loadGames() } }
            }
        } else { listTask?.cancel(); listTask = nil; listGeneration = UUID(); listLoading = false }
    }
    func select(_ selected: Game) {
        startupTask?.cancel(); startupTask = nil; liveTask?.cancel(); statisticsTask?.cancel(); statisticsTask = nil; selection = UUID(); let generation = selection
        game = selected; defaults.set(selected.id, forKey: "lastGameID")
        history = []; displayed = []; replay = []; boxScore = nil; feeds.removeAll(); unreadIDs.removeAll(); activeFeed = ""
        unread = 0; recapViewed.removeAll(); previousRecapScore.removeAll(); frozenGame = nil; frozenMessages = nil; frozenStatistics = nil; hovering = false
        message = "正在连接比赛…"; feedLabel = ""; scoreFetchedAt = nil; scoreWarning = ""; statisticsMessage = ""
        liveTask = Task { [weak self] in await self?.run(selected, generation: generation) }
        updateStatisticsDemand()
    }
    private func restartLive() {
        guard let selected = game else { return }
        liveTask?.cancel(); message = "正在切换文字来源…"; let generation = selection
        liveTask = Task { [weak self] in await self?.run(selected, generation: generation) }
    }
    private func valid(_ generation: UUID) -> Bool { !Task.isCancelled && selection == generation }
    private func run(_ selected: Game, generation: UUID) async {
        var current = selected; var room: Room?; var sync = AnchorHistory()
        let preferAnchor = sourceMode == "anchor"
        var nextScore = Date.distantPast, nextRoom = Date.distantPast, failures = 0, roomFailures = 0
        var anchorWarning = ""
        var finalEmpty: Date?
        while valid(generation) {
            do {
                let now = Date()
                if now >= nextScore {
                    do {
                        current = try await source.match(current); guard valid(generation) else { return }
                        game = current; scoreFetchedAt = Date(); scoreWarning = ""; nextScore = Date().addingTimeInterval(5)
                    } catch { guard valid(generation) else { return }; nextScore = Date().addingTimeInterval(10); scoreWarning = describe(error, prefix: "比分连接中断") }
                }
                if preferAnchor && now >= nextRoom {
                    do {
                        let candidate = try await source.room(current); guard valid(generation) else { return }
                        roomFailures = 0
                        anchorWarning = candidate == nil && room == nil ? (current.id.hasPrefix("web:") ? "主播暂不可用" : "暂无主播") : ""
                        if room == nil && candidate == nil { message = anchorWarning + " · 正在获取赛况实录" }
                        nextRoom = Date().addingTimeInterval(candidate == nil ? 60 : (current.status == .finished ? 5 : 60))
                        if let candidate {
                            if candidate.key != room?.key { sync = AnchorHistory(); finalEmpty = nil }
                            room = candidate
                        }
                    } catch {
                        guard valid(generation) else { return }
                        anchorWarning = "主播连接中断"
                        if room == nil { message = anchorWarning + " · 正在获取赛况实录" }
                        let delay = [5, 10, 20, 30][min(roomFailures, 3)]; roomFailures += 1
                        nextRoom = Date().addingTimeInterval(TimeInterval(delay))
                    }
                }
                var interval: TimeInterval = 5
                if let room {
                    let page = try await source.messages(room, cursor: sync.cursor); guard valid(generation) else { return }
                    let complete = try sync.accept(page)
                    if complete {
                        apply(sync.messages, feed: "anchor:" + room.key, label: room.anchor.isEmpty ? "主播" : "主播 · " + room.anchor)
                        message = current.status == .finished ? "比赛已结束" : "直播更新中"
                        if current.status == .finished && room.finished && page.isEmpty && sync.addedCount == 0 {
                            if let first = finalEmpty, Date().timeIntervalSince(first) >= 5 { message = "比赛已结束 · 直播已收尾"; return }
                            if finalEmpty == nil { finalEmpty = Date() }
                            interval = 5
                        } else { finalEmpty = nil; interval = current.status == .live ? room.liveInterval : room.idleInterval }
                    } else { message = "正在同步直播历史…"; interval = 0.05 }
                } else {
                    let records = try await source.structured(current); guard valid(generation) else { return }
                    apply(records, feed: "structured", label: "赛况实录" + (anchorWarning.isEmpty ? "" : " · " + anchorWarning))
                    message = current.status == .finished ? "比赛已结束" : "直播更新中"
                    if current.status == .finished {
                        if !preferAnchor { return }
                        interval = max(1, min(60, nextRoom.timeIntervalSinceNow))
                    }
                }
                failures = 0
                try await Task.sleep(nanoseconds: UInt64(max(0.05, interval) * 1_000_000_000))
            } catch is CancellationError { return }
            catch {
                guard valid(generation) else { return }
                message = describe(error, prefix: "直播连接中断")
                let seconds = [5, 10, 20, 30][min(failures, 3)]; failures += 1
                try? await Task.sleep(nanoseconds: UInt64(seconds) * 1_000_000_000)
            }
        }
    }
    func apply(_ items: [LiveMessage], feed: String, label: String) {
        let old = feeds[feed]; let known = Set((old ?? []).map(\.id))
        let added = old == nil ? [] : items.filter { !known.contains($0.id) }
        feeds[feed] = items
        if old != nil { unreadIDs[feed, default: []].formUnion(added.map(\.id)) }
        let changed = activeFeed != feed; activeFeed = feed; history = items; feedLabel = label
        unread = unreadIDs[feed]?.count ?? 0
        if changed || old == nil || delivery == "latest" { replay.removeAll(); displayed = Array(items.suffix(2)) }
        else if delivery == "paged" {
            replay.append(contentsOf: added); let corrected = Dictionary(uniqueKeysWithValues: items.map { ($0.id, $0) })
            replay = replay.map { corrected[$0.id] ?? $0 }; displayed = displayed.map { corrected[$0.id] ?? $0 }
        }
    }
    func prepareRecap(recent: Bool = false) {
        let ids = unreadIDs[activeFeed] ?? []
        let currentScore = game?.title ?? "比分暂不可用"
        recapMessages = recent || !recapViewed.contains(activeFeed) ? Array(history.suffix(10)) : history.filter { ids.contains($0.id) }
        recapScore = previousRecapScore[activeFeed].map { $0 + "  →  " + currentScore } ?? currentScore
        recapNotice = recapMessages.isEmpty ? "没有新增消息，可以重看最近 10 条。" : "本次共 \(recapMessages.count) 条原文"
    }
    func markRecapRead() {
        let captured = Set(recapMessages.map(\.id))
        unreadIDs[activeFeed, default: []].subtract(captured); unread = unreadIDs[activeFeed]?.count ?? 0
        recapViewed.insert(activeFeed); previousRecapScore[activeFeed] = game?.title
    }
    func setStatisticsVisible(_ visible: Bool) { statisticsVisible = visible; updateStatisticsDemand() }
    func refreshStatistics() { updateStatisticsDemand(force: true) }
    private func updateStatisticsDemand(force: Bool = false) {
        let needed = statisticsVisible || mode == "player"
        if !needed { statisticsTask?.cancel(); statisticsTask = nil; return }
        guard let current = game else { return }
        if statisticsTask != nil && !force { return }
        statisticsTask?.cancel(); let generation = selection
        statisticsTask = Task { [weak self] in
            guard let self else { return }
            repeat {
                do {
                    let result = try await self.source.statistics(current)
                    guard self.valid(generation) else { return }
                    self.boxScore = result; self.statisticsMessage = "更新于 " + Self.time(result.fetchedAt)
                    if self.game?.status == .finished || self.game?.status == .cancelled { self.statisticsTask = nil; return }
                } catch {
                    guard self.valid(generation) else { return }
                    self.statisticsMessage = self.describe(error, prefix: "统计连接中断")
                }
                let seconds = self.game?.status == .scheduled ? 30 : 15
                try? await Task.sleep(nanoseconds: UInt64(seconds) * 1_000_000_000)
            } while self.valid(generation) && (self.statisticsVisible || self.mode == "player")
        }
    }
    private func describe(_ error: Error, prefix: String) -> String {
        if let source = error as? SourceError { return source.localizedDescription }
        if let url = error as? URLError, url.code == .timedOut { return prefix + " · 请求超时" }
        return prefix + "，稍后重试"
    }
    static func time(_ date: Date) -> String {
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "zh_CN")
        formatter.timeZone = TimeZone(identifier: "Asia/Shanghai"); formatter.dateFormat = "HH:mm:ss"; return formatter.string(from: date)
    }
    func stop() { startupTask?.cancel(); liveTask?.cancel(); listTask?.cancel(); listTimer?.cancel(); statisticsTask?.cancel(); displayTask?.cancel() }
}

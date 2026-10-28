import Foundation
import XCTest
import NBALiveCore
@testable import NBALiveMacLogic

private actor FakeSource: LiveSource {
    private(set) var statisticsCalls = 0
    let failRoom: Bool
    init(failRoom: Bool = false) { self.failRoom = failRoom }
    let player = Player(id: "7", name: "球员甲", number: "7", minutes: "25", points: "16", rebounds: "5", assists: "4",
                        fieldGoals: "6-9", threePointers: "2-4", freeThrows: "2-2", starter: true, onCourt: true, didNotPlay: false)
    func games(date: String) async throws -> [Game] { [] }
    func match(_ game: Game) async throws -> Game {
        if game.id == "a" {
            // Deliberately ignore cancellation to simulate a late network response.
            await withCheckedContinuation { continuation in DispatchQueue.global().asyncAfter(deadline: .now() + 0.2) { continuation.resume() } }
        }
        return game
    }
    func room(_ game: Game) async throws -> Room? { if failRoom { throw URLError(.timedOut) }; return nil }
    func messages(_ room: Room, cursor: String?) async throws -> [LiveMessage] { [] }
    func structured(_ game: Game) async throws -> [LiveMessage] { [LiveMessage(id: game.id, text: "消息-" + game.id)] }
    func statistics(_ game: Game) async throws -> BoxScore {
        statisticsCalls += 1; return BoxScore(gameID: game.id, away: [player], home: [], fetchedAt: Date())
    }
}

final class StoreTests: XCTestCase {
    private func settings() -> UserDefaults {
        let name = "NBALiveTests-" + UUID().uuidString
        return UserDefaults(suiteName: name)!
    }
    private func game(_ id: String, score: Int = 10) -> Game {
        Game(id: id, date: "20261027", away: "勇士", home: "湖人", awayScore: score, homeScore: 9, progress: "第1节", status: .live)
    }
    private func entry(_ id: String, text: String? = nil) -> LiveMessage { LiveMessage(id: id, text: text ?? id) }

    func testFirstLaunchOpensPickerWithoutWaitingForNetwork() async {
        let store = await MainActor.run { LiveStore(source: FakeSource(), defaults: settings()) }
        await MainActor.run {
            var opened = false
            store.openPicker = { opened = true }; store.start()
            XCTAssertTrue(opened); store.stop()
        }
    }

    func testSwitchGameRejectsLateResponse() async throws {
        let fake = FakeSource(), preferences = settings()
        let store = await MainActor.run { LiveStore(source: fake, defaults: preferences) }
        await MainActor.run { store.select(game("a")) }
        try await Task.sleep(nanoseconds: 30_000_000)
        await MainActor.run { store.select(game("b")) }
        try await Task.sleep(nanoseconds: 350_000_000)
        await MainActor.run {
            XCTAssertEqual(store.game?.id, "b"); XCTAssertEqual(store.history.map(\.id), ["b"])
            store.stop()
        }
    }
    func testHoverFreezesVisibleScoreAndMessagesUntilLeave() async {
        let store = await MainActor.run { LiveStore(source: FakeSource(), defaults: settings()) }
        await MainActor.run {
            store.game = game("a", score: 10); store.displayed = [entry("one")]
            store.setHover(true); store.game = game("a", score: 12); store.displayed = [entry("two")]
            XCTAssertEqual(store.visibleGame?.awayScore, 10); XCTAssertEqual(store.visibleMessages.map(\.id), ["one"])
            store.setHover(false)
            XCTAssertEqual(store.visibleGame?.awayScore, 12); XCTAssertEqual(store.visibleMessages.map(\.id), ["two"])
            store.stop()
        }
    }
    func testReplayWaitsWhileHiddenAndCorrectionsDoNotAddUnread() async {
        let store = await MainActor.run { LiveStore(source: FakeSource(), defaults: settings()) }
        await MainActor.run {
            store.delivery = "paged"
            store.apply([entry("1"), entry("2")], feed: "structured", label: "赛况实录")
            store.apply([entry("1", text: "修正"), entry("2"), entry("3"), entry("4")], feed: "structured", label: "赛况实录")
            XCTAssertEqual(store.unread, 2); XCTAssertEqual(store.displayed.first?.text, "修正")
            store.tick(); XCTAssertEqual(store.displayed.map(\.id), ["1", "2"])
            store.popoverVisible = true; store.tick(); XCTAssertEqual(store.displayed.map(\.id), ["3", "4"])
            store.stop()
        }
    }
    func testRecapLeavesRacingArrivalsUnreadAndSeparatesFeeds() async {
        let store = await MainActor.run { LiveStore(source: FakeSource(), defaults: settings()) }
        await MainActor.run {
            store.game = game("a")
            store.apply([entry("1"), entry("2")], feed: "structured", label: "赛况实录")
            store.apply([entry("1"), entry("2"), entry("3")], feed: "structured", label: "赛况实录")
            store.prepareRecap(); store.markRecapRead(); XCTAssertEqual(store.unread, 0)
            store.apply([entry("1"), entry("2"), entry("3"), entry("4")], feed: "structured", label: "赛况实录")
            store.prepareRecap(); XCTAssertEqual(store.recapMessages.map(\.id), ["4"])
            store.apply([entry("1"), entry("2"), entry("3"), entry("4"), entry("5")], feed: "structured", label: "赛况实录")
            store.markRecapRead(); XCTAssertEqual(store.unread, 1)
            store.apply([entry("a")], feed: "anchor:room", label: "主播")
            XCTAssertEqual(store.unread, 0)
            store.apply([entry("1"), entry("2"), entry("3"), entry("4"), entry("5")], feed: "structured", label: "赛况实录")
            XCTAssertEqual(store.unread, 1); store.stop()
        }
    }
    func testFavoritesEnforceThreeAndRetainNames() async {
        let preferences = settings(), store = await MainActor.run { LiveStore(source: FakeSource(), defaults: preferences) }
        await MainActor.run {
            func player(_ id: String) -> Player { Player(id: id, name: "球员" + id, number: id, minutes: "—", points: "—", rebounds: "—", assists: "—", fieldGoals: "—", threePointers: "—", freeThrows: "—", starter: false, onCourt: false, didNotPlay: false) }
            XCTAssertTrue(store.toggleFavorite(player("1"))); XCTAssertTrue(store.toggleFavorite(player("2"))); XCTAssertTrue(store.toggleFavorite(player("3")))
            XCTAssertFalse(store.toggleFavorite(player("4"))); XCTAssertEqual(store.favorites, ["1", "2", "3"])
            let restored = LiveStore(source: FakeSource(), defaults: preferences)
            XCTAssertEqual(restored.favorites, ["1", "2", "3"]); XCTAssertEqual(restored.favoriteNames["2"], "球员2")
            store.stop(); restored.stop()
        }
    }
    func testStatisticsConsumersShareOnePollingTask() async throws {
        let fake = FakeSource(), store = await MainActor.run { LiveStore(source: fake, defaults: settings()) }
        await MainActor.run { store.select(game("b")); store.setStatisticsVisible(true) }
        try await Task.sleep(nanoseconds: 100_000_000)
        await MainActor.run { store.mode = "player"; store.setStatisticsVisible(false) }
        try await Task.sleep(nanoseconds: 100_000_000)
        let first = await fake.statisticsCalls; XCTAssertEqual(first, 1)
        await MainActor.run { store.mode = "text"; store.mode = "player" }
        try await Task.sleep(nanoseconds: 100_000_000)
        let second = await fake.statisticsCalls; XCTAssertEqual(second, 2)
        await MainActor.run { store.stop() }
    }
    func testUnavailableAnchorFallsBackToStructuredText() async throws {
        let store = await MainActor.run { LiveStore(source: FakeSource(failRoom: true), defaults: settings()) }
        await MainActor.run { store.select(game("b")) }
        try await Task.sleep(nanoseconds: 150_000_000)
        await MainActor.run {
            XCTAssertEqual(store.history.map(\.id), ["b"])
            XCTAssertTrue(store.feedLabel.contains("赛况实录")); XCTAssertTrue(store.feedLabel.contains("主播连接中断"))
            store.stop()
        }
    }
}

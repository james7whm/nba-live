import XCTest
@testable import NBALiveCore
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

private final class NeverResponds: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { }
    override func stopLoading() { }
}

final class CoreTests: XCTestCase {
    actor Order {
        private var values: [String] = []
        func append(_ value: String) { values.append(value) }
        func result() -> [String] { values }
    }
    func fixture(_ name: String) throws -> Data {
        let url = Bundle.module.url(forResource: name, withExtension: nil, subdirectory: "Fixtures")!
        return try Data(contentsOf: url)
    }
    func json(_ value: Any) throws -> Data { try JSONSerialization.data(withJSONObject: ["success": true, "result": value]) }
    func testMobileSchedulePreservesLargeIDsAndOvertime() throws {
        let item: [String: Any] = ["matchId": "1537808027304853504", "awayTeamName": "金州勇士", "homeTeamName": "丹佛掘金",
            "awayTeamId": "1901000000501331", "homeTeamId": "1901000000501301", "awayScore": 134, "homeScore": 114,
            "matchStatus": "INPROGRESS", "currentQuarter": 5, "frontEndMatchStatus": ["desc": "加时赛 04:30"]]
        let data = try json(["gameList": [["day": "20261027", "matchList": [item]]]])
        let game = try XCTUnwrap(SourceParser.schedule(data, date: "20261027").first)
        XCTAssertEqual(game.id, "1537808027304853504"); XCTAssertEqual(game.progress, "加1剩04:30")
        XCTAssertThrowsError(try SourceParser.schedule(data, date: "20261026"))
        XCTAssertEqual(try SourceParser.singleMatch(try json(item), expected: game).awayScore, 134)
        var wrong = item; wrong["matchId"] = "1537808027304853505"
        XCTAssertThrowsError(try SourceParser.singleMatch(try json(wrong), expected: game))
    }
    func testRealRosterAndIdentity() throws {
        let game = Game(id: "1537808027304853504", date: "20261027", away: "勇士", home: "掘金",
            awayTeamID: "1901000000501331", homeTeamID: "1901000000501301")
        let stats = try SourceParser.statistics(fixture("roster.json"), game: game)
        let player = try XCTUnwrap(stats.away.first { $0.id == "1519326309480660992" })
        XCTAssertEqual(player.number, "1"); XCTAssertEqual(player.points, "31"); XCTAssertEqual(player.rebounds, "12"); XCTAssertEqual(player.assists, "6")
        XCTAssertEqual(player.fieldGoals, "9-19"); XCTAssertTrue(player.starter)
        XCTAssertFalse(stats.away.contains { $0.id.contains("-team-") })
        var wrong = game; wrong.home = "湖人"
        XCTAssertThrowsError(try SourceParser.statistics(fixture("roster.json"), game: wrong))
    }
    func testRoomAndMessageIdentityValidation() throws {
        XCTAssertNil(try SourceParser.room(json(["liveActivityKey": "0:0:0"]), matchID: "1"))
        let room = try XCTUnwrap(SourceParser.room(json(["liveActivityKey": "20564829:10449:1037103", "liveStatus": "FINISH",
            "liveRoomMsgConfig": ["polling": ["open": true, "inProgressIntervalInMillSeconds": 50]]]), matchID: "1"))
        XCTAssertTrue(room.finished); XCTAssertEqual(room.liveInterval, 1)
        XCTAssertThrowsError(try SourceParser.room(json(["liveActivityKey": "bad"]), matchID: "1"))
        let messages = try SourceParser.messages(json([["commentId": "3011690597", "content": "比赛开始！！"], ["commentId": "3011612293", "content": "5-6"]]))
        XCTAssertEqual(messages.map(\.id), ["3011690597", "3011612293"])
        XCTAssertThrowsError(try SourceParser.messages(json([["commentId": "", "content": "缺少编号"]])))
    }
    func testBackwardPagesBaselineCorrectionsLateArrivalAndReadOrder() throws {
        var history = AnchorHistory()
        let a = LiveMessage(id: "900", text: "old", time: "2026-10-22 10:00:01")
        let b = LiveMessage(id: "3", text: "new", time: "2026-10-22 10:00:02", previousID: "900")
        XCTAssertFalse(try history.accept([b, a])); XCTAssertEqual(history.cursor, "900")
        XCTAssertTrue(try history.accept([])); XCTAssertEqual(history.messages.map(\.id), ["900", "3"]); XCTAssertEqual(history.addedCount, 0)
        let c = LiveMessage(id: "2", text: "latest", time: "2026-10-22 10:00:03", previousID: "3")
        let corrected = LiveMessage(id: "3", text: "corrected", time: b.time, previousID: "900")
        XCTAssertTrue(try history.accept([c, corrected])); XCTAssertEqual(history.messages.map(\.id), ["900", "3", "2"])
        XCTAssertEqual(history.messages[1].text, "corrected"); XCTAssertEqual(history.addedCount, 1)
    }
    func testForwardArchiveContinuesCursorAndRejectsCycles() throws {
        var history = AnchorHistory()
        let a = LiveMessage(id: "200", text: "a", time: "2026-10-22 10:00:00")
        let b = LiveMessage(id: "1", text: "b", time: "2026-10-22 10:00:00", previousID: "200")
        XCTAssertFalse(try history.accept([a, b])); XCTAssertEqual(history.cursor, "1")
        XCTAssertThrowsError(try history.accept([b]))
        XCTAssertTrue(try history.accept([])); XCTAssertEqual(history.cursor, "1"); XCTAssertEqual(history.messages.map(\.id), ["200", "1"])
        let c = LiveMessage(id: "40", text: "c", time: "2026-10-22 10:00:02", previousID: "1")
        XCTAssertFalse(try history.accept([c])); XCTAssertTrue(try history.accept([])); XCTAssertEqual(history.addedCount, 1)
    }
    func testRealDesktopScheduleAndPlayByPlay() throws {
        let schedule = try DesktopParser.schedule(String(decoding: fixture("games.html"), as: UTF8.self), date: "20261021")
        XCTAssertTrue(schedule.contains { $0.desktopID == "168863" }); XCTAssertTrue(schedule.allSatisfy { $0.id.hasPrefix("web:") })
        let messages = try DesktopParser.messages(String(decoding: fixture("finished.html"), as: UTF8.self), gameID: "168863")
        XCTAssertGreaterThan(messages.count, 100); XCTAssertTrue(messages.contains { $0.text.contains("第一节开始") })
        XCTAssertEqual(Set(messages.map(\.id)).count, messages.count)
        XCTAssertThrowsError(try DesktopParser.messages("<html></html>", gameID: "1"))
        let html = String(decoding: try fixture("finished.html"), as: UTF8.self)
        let game = Game(id: "web:168863", date: "20261021", away: "76人", home: "尼克斯")
        XCTAssertEqual(try DesktopParser.messages(html, gameID: "168863", expected: game).count, messages.count)
        XCTAssertThrowsError(try DesktopParser.messages(html, gameID: "168864", expected: game))
        var wrong = game; wrong.home = "湖人"
        XCTAssertThrowsError(try DesktopParser.messages(html, gameID: "168863", expected: wrong))
    }
    func testBeijingDayAndTeams() {
        XCTAssertEqual(BeijingDate.key(Date(timeIntervalSince1970: 0)), "19700101")
        XCTAssertTrue(TeamCatalog.same("金州勇士", "勇士")); XCTAssertFalse(TeamCatalog.same("勇士", "湖人"))
        XCTAssertEqual(TeamCatalog.key("丹佛掘金"), "nuggets")
    }
    func testQueuedLiveRequestGoesBeforeSecondary() async throws {
        let gate = RequestGate(), order = Order()
        await gate.acquire(live: false)
        let background = Task {
            await gate.acquire(live: false); await order.append("statistics"); await gate.release()
        }
        try await Task.sleep(nanoseconds: 50_000_000)
        let live = Task {
            await gate.acquire(live: true); await order.append("live"); await gate.release()
        }
        try await Task.sleep(nanoseconds: 50_000_000)
        await gate.release(); await live.value; await background.value
        let actual = await order.result()
        XCTAssertEqual(actual, ["live", "statistics"])
    }
    func testRequestDeadlineAndCancellationReleaseQueue() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [NeverResponds.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let source = NetworkLiveSource(session: session, requestTimeout: 0.15)
        let game = Game(id: "123", date: "20261027", away: "勇士", home: "湖人")
        let cancelled = Task { try await source.match(game) }
        try await Task.sleep(nanoseconds: 30_000_000); cancelled.cancel()
        do { _ = try await cancelled.value; XCTFail("Cancellation should end the request") } catch { }
        let start = Date()
        do { _ = try await source.match(game); XCTFail("Deadline should end the request") }
        catch let error as URLError { XCTAssertEqual(error.code, .timedOut) }
        catch { XCTFail("Unexpected error: \(error)") }
        XCTAssertLessThan(Date().timeIntervalSince(start), 2, "Cancelled requests must not hold the shared queue")
    }
    func testActualPublicSourceWhenExplicitlyRequested() async throws {
        guard ProcessInfo.processInfo.environment["NBALIVE_LIVE_PROBE"] == "1" else { throw XCTSkip("Opt-in network probe") }
        func mark(_ text: String) { FileHandle.standardError.write(Data(("Network probe: " + text + "\n").utf8)) }
        let source = NetworkLiveSource()
        mark("schedule")
        let games = try await source.games(date: "20261027")
        let selected = try XCTUnwrap(games.first { $0.id == "1537808027304853504" })
        mark("score")
        let game = try await source.match(selected)
        XCTAssertEqual(game.awayScore, 134); XCTAssertEqual(game.homeScore, 114)
        mark("room")
        let fetchedRoom = try await source.room(game)
        let room = try XCTUnwrap(fetchedRoom)
        mark("messages")
        let page = try await source.messages(room, cursor: nil)
        XCTAssertFalse(page.isEmpty)
        mark("statistics")
        let stats = try await source.statistics(game)
        let player = try XCTUnwrap(stats.away.first { $0.id == "1519326309480660992" })
        XCTAssertEqual(player.points, "31"); XCTAssertEqual(player.rebounds, "12"); XCTAssertEqual(player.assists, "6")
    }
    func testActualArchivedPagingAndStructuredFallbackWhenExplicitlyRequested() async throws {
        guard ProcessInfo.processInfo.environment["NBALIVE_LIVE_PROBE"] == "1" else { throw XCTSkip("Opt-in network probe") }
        let source = NetworkLiveSource()
        let game = Game(id: "1537445624650137600", date: "20261022", away: "勇士", home: "湖人")
        let fetchedRoom = try await source.room(game)
        let room = try XCTUnwrap(fetchedRoom)
        var history = AnchorHistory()
        for _ in 0..<100 {
            let page = try await source.messages(room, cursor: history.cursor)
            if try history.accept(page) { break }
        }
        XCTAssertTrue(history.initialized); XCTAssertGreaterThan(history.messages.count, 100)
        XCTAssertEqual(history.messages.first?.text, "比赛开始！！")
        XCTAssertEqual(Set(history.messages.map(\.id)).count, history.messages.count)
        let structured = try await source.structured(game)
        XCTAssertGreaterThan(structured.count, 100)
    }
}

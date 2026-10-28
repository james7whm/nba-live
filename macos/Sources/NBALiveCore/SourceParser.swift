import Foundation

public enum SourceParser {
    private static func document(_ data: Data) throws -> Any {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              (root["success"] as? Bool) == true, let result = root["result"], !(result is NSNull) else {
            throw SourceError.invalidData("数据源响应异常")
        }
        return result
    }
    private static func string(_ item: [String: Any], _ key: String) -> String {
        if let s = item[key] as? String { return s }
        if let n = item[key] as? NSNumber { return n.stringValue }
        return ""
    }
    private static func number(_ item: [String: Any], _ key: String) -> Int? {
        guard let value = Int(string(item, key)), value >= 0 else { return nil }; return value
    }
    private static func display(_ item: [String: Any], _ key: String) -> String {
        let value = string(item, key); return value.isEmpty || value == "-" ? "—" : value
    }
    private static func status(_ raw: String) -> GameStatus {
        switch raw.uppercased() {
        case "COMPLETED", "FINISHED": return .finished
        case "NOTSTARTED", "NOT_STARTED": return .scheduled
        case "INPROGRESS", "IN_PROGRESS", "ONGOING", "PLAYING", "LIVE": return .live
        case "CANCELLED", "POSTPONED": return .cancelled
        default: return .unknown
        }
    }
    private static func game(_ item: [String: Any], date: String) throws -> Game {
        let id = string(item, "matchId"), away = string(item, "awayTeamName"), home = string(item, "homeTeamName")
        guard !id.isEmpty, id.allSatisfy({ $0.isASCII && $0.isNumber }), !away.isEmpty, !home.isEmpty else {
            throw SourceError.invalidData("比赛编号或球队缺失")
        }
        let status = status(string(item, "matchStatus"))
        var progress = string(item["frontEndMatchStatus"] as? [String: Any] ?? [:], "desc")
        if progress.isEmpty { progress = string(item, "matchStatusChinese") }
        if status == .finished { progress = "已结束" }
        else if status == .live {
            let quarter = number(item, "currentQuarter") ?? 0
            let label = quarter > 4 ? "加\(quarter - 4)" : quarter > 0 ? "第\(quarter)节" : "进行中"
            let clock = progress.range(of: #"\d{1,2}:\d{2}(?:\.\d+)?"#, options: .regularExpression).map { String(progress[$0]) }
            progress = clock.map { "\(label)剩\($0)" } ?? (progress.isEmpty ? "\(label) · 时间未提供" : progress)
        }
        return Game(id: id, date: date, away: away, home: home, awayScore: number(item, "awayScore"),
                    homeScore: number(item, "homeScore"), progress: progress, status: status,
                    awayTeamID: string(item, "awayTeamId"), homeTeamID: string(item, "homeTeamId"))
    }

    /// An absent date is not an empty schedule: this endpoint only publishes a limited date range.
    public static func schedule(_ data: Data, date: String) throws -> [Game] {
        guard let root = try document(data) as? [String: Any], let groups = root["gameList"] as? [[String: Any]] else {
            throw SourceError.invalidData("比赛列表结构异常")
        }
        guard let group = groups.first(where: { string($0, "day") == date }) else {
            throw SourceError.unavailable("该日期超出数据源提供的赛程范围")
        }
        guard let games = group["matchList"] as? [[String: Any]] else { throw SourceError.invalidData("比赛列表结构异常") }
        var seen: Set<String> = []
        return try games.map { try game($0, date: date) }.filter { seen.insert($0.id).inserted }
    }

    public static func singleMatch(_ data: Data, expected: Game) throws -> Game {
        guard let item = try document(data) as? [String: Any] else { throw SourceError.invalidData("比赛数据异常") }
        let updated = try game(item, date: expected.date)
        guard updated.id == expected.id, TeamCatalog.same(updated.away, expected.away), TeamCatalog.same(updated.home, expected.home),
              (expected.awayTeamID.isEmpty || expected.awayTeamID == updated.awayTeamID),
              (expected.homeTeamID.isEmpty || expected.homeTeamID == updated.homeTeamID) else { throw SourceError.invalidData("比赛身份不匹配") }
        var result = updated; result.desktopID = expected.desktopID; return result
    }

    public static func room(_ data: Data, matchID: String) throws -> Room? {
        guard let item = try document(data) as? [String: Any] else { throw SourceError.invalidData("直播间数据异常") }
        let key = string(item, "liveActivityKey")
        if key.isEmpty || key == "0:0:0" { return nil }
        guard key.range(of: #"^\d+:\d+:\d+$"#, options: .regularExpression) != nil else { throw SourceError.invalidData("直播间标识异常") }
        let config = (item["liveRoomMsgConfig"] as? [String: Any])?["polling"] as? [String: Any] ?? [:]
        if (config["open"] as? Bool) == false { return nil }
        func interval(_ name: String, _ fallback: Int) -> Double { Double(min(60000, max(1000, number(config, name) ?? fallback))) / 1000 }
        return Room(matchID: matchID, key: key, anchor: string(item, "anchorName"), finished: string(item, "liveStatus") == "FINISH",
                    liveInterval: interval("inProgressIntervalInMillSeconds", 1000), idleInterval: interval("notInProgressIntervalInMillSeconds", 5000))
    }

    public static func messages(_ data: Data) throws -> [LiveMessage] {
        guard let rows = try document(data) as? [[String: Any]] else { throw SourceError.invalidData("直播消息结构异常") }
        return try rows.map { item in
            let id = string(item, "commentId"), text = string(item, "content")
            guard !id.isEmpty, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw SourceError.invalidData("直播消息缺少编号或文字") }
            return LiveMessage(id: id, text: text, author: string(item, "nickName"), time: string(item, "time"), previousID: string(item, "previousCommentId"))
        }
    }

    public static func statistics(_ data: Data, game: Game, now: Date = Date()) throws -> BoxScore {
        guard let item = try document(data) as? [String: Any], string(item, "matchId") == game.id,
              TeamCatalog.same(string(item, "awayTeamName"), game.away), TeamCatalog.same(string(item, "homeTeamName"), game.home),
              let away = item["awayPlayers"] as? [[String: Any]], let home = item["homePlayers"] as? [[String: Any]] else {
            throw SourceError.invalidData("本场球员统计身份不匹配")
        }
        func players(_ rows: [[String: Any]], teamID: String) throws -> [Player] {
            var seen: Set<String> = []
            return try rows.compactMap { row in
                let id = string(row, "playerId"), name = string(row, "name")
                if id.contains("-team-") { return nil }
                guard !id.isEmpty, !name.isEmpty, teamID.isEmpty || string(row, "teamId") == teamID else { throw SourceError.invalidData("球员统计身份不匹配") }
                guard seen.insert(id).inserted else { throw SourceError.invalidData("球员编号重复") }
                return Player(id: id, name: name, number: display(row, "number"), minutes: display(row, "mins"), points: display(row, "pts"),
                              rebounds: display(row, "reb"), assists: display(row, "asts"), fieldGoals: display(row, "twoPoints"),
                              threePointers: display(row, "threePoints"), freeThrows: display(row, "ft"), starter: number(row, "starter") == 1,
                              onCourt: number(row, "onCourt") == 1, didNotPlay: number(row, "dnp") == 1)
            }
        }
        return try BoxScore(gameID: game.id, away: players(away, teamID: game.awayTeamID), home: players(home, teamID: game.homeTeamID), fetchedAt: now)
    }
}

public enum TeamCatalog {
    // Short names are used only to normalize team identity; never to guess a player.
    private static let names: [(String, String)] = [
        ("76人", "76ers"), ("开拓者", "blazers"), ("雄鹿", "bucks"), ("公牛", "bulls"), ("骑士", "cavaliers"),
        ("凯尔特人", "celtics"), ("快船", "clippers"), ("灰熊", "grizzlies"), ("老鹰", "hawks"), ("热火", "heat"),
        ("黄蜂", "hornets"), ("爵士", "jazz"), ("国王", "kings"), ("尼克斯", "knicks"), ("湖人", "lakers"),
        ("魔术", "magic"), ("独行侠", "mavericks"), ("篮网", "nets"), ("掘金", "nuggets"), ("步行者", "pacers"),
        ("鹈鹕", "pelicans"), ("活塞", "pistons"), ("猛龙", "raptors"), ("火箭", "rockets"), ("马刺", "spurs"),
        ("太阳", "suns"), ("雷霆", "thunder"), ("森林狼", "timberwolves"), ("勇士", "warriors"), ("奇才", "wizards")
    ]
    public static func key(_ name: String) -> String? {
        if name.contains("七六人") { return "76ers" }
        if name.contains("小牛") { return "mavericks" }
        return names.first { name.contains($0.0) || name.lowercased() == $0.1 }?.1
    }
    public static func short(_ name: String) -> String { names.first { key(name) == $0.1 }?.0 ?? name }
    public static func same(_ left: String, _ right: String) -> Bool {
        guard !left.isEmpty, !right.isEmpty else { return false }
        if let a = key(left), let b = key(right) { return a == b }
        return left == right
    }
}

import Foundation

public enum GameStatus: String, Codable {
    case scheduled, live, finished, cancelled, unknown
}

public struct Game: Identifiable, Codable, Equatable {
    public let id: String
    public let date: String // yyyyMMdd, in Beijing time
    public var away: String
    public var home: String
    public var awayScore: Int?
    public var homeScore: Int?
    public var progress: String
    public var status: GameStatus
    public var awayTeamID: String
    public var homeTeamID: String
    public var desktopID: String?
    public var title: String { "\(away) \(awayScore.map(String.init) ?? "—") : \(homeScore.map(String.init) ?? "—") \(home)" }
    public init(id: String, date: String, away: String, home: String, awayScore: Int? = nil, homeScore: Int? = nil,
                progress: String = "", status: GameStatus = .unknown, awayTeamID: String = "", homeTeamID: String = "", desktopID: String? = nil) {
        self.id = id; self.date = date; self.away = away; self.home = home
        self.awayScore = awayScore; self.homeScore = homeScore; self.progress = progress; self.status = status
        self.awayTeamID = awayTeamID; self.homeTeamID = homeTeamID; self.desktopID = desktopID
    }
}

public struct LiveMessage: Identifiable, Equatable {
    public let id: String
    public let text: String
    public let author: String
    public let time: String
    public let previousID: String
    public init(id: String, text: String, author: String = "", time: String = "", previousID: String = "") {
        self.id = id; self.text = text; self.author = author; self.time = time; self.previousID = previousID
    }
}

public struct Room: Equatable {
    public let matchID: String
    public let key: String
    public let anchor: String
    public let finished: Bool
    public let liveInterval: TimeInterval
    public let idleInterval: TimeInterval
    public init(matchID: String, key: String, anchor: String, finished: Bool, liveInterval: TimeInterval, idleInterval: TimeInterval) {
        self.matchID = matchID; self.key = key; self.anchor = anchor; self.finished = finished
        self.liveInterval = liveInterval; self.idleInterval = idleInterval
    }
}

public struct Player: Identifiable, Equatable {
    public let id: String
    public let name: String
    public let number: String
    public let minutes: String
    public let points: String
    public let rebounds: String
    public let assists: String
    public let fieldGoals: String
    public let threePointers: String
    public let freeThrows: String
    public let starter: Bool
    public let onCourt: Bool
    public let didNotPlay: Bool
    public init(id: String, name: String, number: String, minutes: String, points: String, rebounds: String, assists: String,
                fieldGoals: String, threePointers: String, freeThrows: String, starter: Bool, onCourt: Bool, didNotPlay: Bool) {
        self.id = id; self.name = name; self.number = number; self.minutes = minutes; self.points = points
        self.rebounds = rebounds; self.assists = assists; self.fieldGoals = fieldGoals; self.threePointers = threePointers
        self.freeThrows = freeThrows; self.starter = starter; self.onCourt = onCourt; self.didNotPlay = didNotPlay
    }
}

public struct BoxScore: Equatable {
    public let gameID: String
    public let away: [Player]
    public let home: [Player]
    public let fetchedAt: Date
    public init(gameID: String, away: [Player], home: [Player], fetchedAt: Date) {
        self.gameID = gameID; self.away = away; self.home = home; self.fetchedAt = fetchedAt
    }
}

public enum SourceError: LocalizedError, Equatable {
    case invalidData(String)
    case unavailable(String)
    public var errorDescription: String? {
        switch self { case .invalidData(let message), .unavailable(let message): return message }
    }
}

public enum BeijingDate {
    public static func key(_ date: Date) -> String {
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "Asia/Shanghai")!; formatter.dateFormat = "yyyyMMdd"
        return formatter.string(from: date)
    }
}

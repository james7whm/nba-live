import Foundation

/// Parses the small, verified portions of the public desktop pages used for fallback.
public enum DesktopParser {
    private static func captures(_ pattern: String, in text: String) -> [[String]] {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]) else { return [] }
        let full = NSRange(text.startIndex..<text.endIndex, in: text)
        return regex.matches(in: text, range: full).map { match in
            (0..<match.numberOfRanges).map { i in Range(match.range(at: i), in: text).map { String(text[$0]) } ?? "" }
        }
    }
    private static func clean(_ html: String) -> String {
        var text = html.replacingOccurrences(of: #"<[^>]*>"#, with: " ", options: .regularExpression)
        for (key, value) in [("&nbsp;", " "), ("&#160;", " "), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""), ("&#39;", "'"), ("&amp;", "&")] {
            text = text.replacingOccurrences(of: key, with: value)
        }
        return text.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
    }
    private static func first(_ pattern: String, _ text: String) -> String { captures(pattern, in: text).first?.dropFirst().first ?? "" }

    public static func schedule(_ html: String, date: String) throws -> [Game] {
        guard html.contains("gamecenter_livestart") else { throw SourceError.invalidData("比赛列表结构异常") }
        let boxes = captures(#"<div\s+class=["']list_box["'][^>]*>(.*?)(?=<div\s+class=["']list_box["']|\z)"#, in: html)
        var games: [Game] = []; var seen: Set<String> = []
        for box in boxes {
            let body = box[1]
            let id = first(#"/games/(?:playbyplay|boxscore|preview)/(\d+)"#, body)
            let awayPart = first(#"class=["']team_vs_a_1[^"']*["'](.*?)class=["']team_vs_a_2"#, body)
            let homePart = first(#"class=["']team_vs_a_2[^"']*["'](.*?)class=["']team_vs_[bc]"#, body)
            let away = clean(first(#"class=["']txt["'].*?<a[^>]*>(.*?)</a>"#, awayPart))
            let home = clean(first(#"class=["']txt["'].*?<a[^>]*>(.*?)</a>"#, homePart))
            guard !id.isEmpty, !away.isEmpty, !home.isEmpty else { throw SourceError.invalidData("比赛列表球队或编号缺失") }
            if !seen.insert(id).inserted { continue }
            let progress = clean(first(#"class=["']team_vs_[bc]["'].*?class=["']b["'][^>]*>(.*?)</(?:span|div)>"#, body))
            let status: GameStatus = progress.contains("结束") ? .finished : progress.contains("取消") || progress.contains("延期") ? .cancelled :
                progress.contains("未开始") || progress.contains("赛前") ? .scheduled :
                progress.contains("节") || progress.contains("加时") || progress.contains("进行中") || progress.contains("中场") ? .live : .unknown
            let awayScore = Int(clean(first(#"class=["']num[^"']*["'][^>]*>(.*?)</span>"#, awayPart)))
            let homeScore = Int(clean(first(#"class=["']num[^"']*["'][^>]*>(.*?)</span>"#, homePart)))
            games.append(Game(id: "web:" + id, date: date, away: away, home: home, awayScore: awayScore, homeScore: homeScore,
                              progress: progress, status: status, desktopID: id))
        }
        if games.isEmpty && !clean(html).contains("0场比赛") { throw SourceError.invalidData("比赛列表结构异常") }
        return games
    }

    public static func messages(_ html: String, gameID: String, expected: Game? = nil) throws -> [LiveMessage] {
        let reportedID = first(#"match_id\s*:\s*["'](\d+)["']"#, html)
        if !reportedID.isEmpty && reportedID != gameID { throw SourceError.invalidData("赛况比赛编号不匹配") }
        if let expected {
            let away = clean(first(#"class=["']team_a["'][^>]*>.*?<p[^>]*>\s*<a[^>]*>(.*?)</a>"#, html))
            let home = clean(first(#"class=["']team_b["'][^>]*>.*?<p[^>]*>\s*<a[^>]*>(.*?)</a>"#, html))
            guard TeamCatalog.same(away, expected.away), TeamCatalog.same(home, expected.home) else {
                throw SourceError.invalidData("赛况球队身份不匹配")
            }
        }
        guard let marker = html.range(of: "table_overflow") else { throw SourceError.invalidData("赛况文字区域缺失") }
        let after = String(html[marker.lowerBound...])
        let table = first(#"(<table\b[^>]*>.*?</table>)"#, after)
        guard !table.isEmpty else { throw SourceError.invalidData("赛况文字区域缺失") }
        let rows = captures(#"<tr\b([^>]*)>(.*?)</tr>"#, in: table)
        var result: [LiveMessage] = []; var seen: Set<String> = []
        for (ordinal, row) in rows.enumerated() {
            let cells = captures(#"<td\b[^>]*>(.*?)</td>"#, in: row[2]).map { clean($0[1]) }
            guard !cells.isEmpty else { continue }
            let text = cells.count >= 3 ? cells[2] : cells[0]
            guard !text.isEmpty else { continue }
            let id = first(#"\bid=["']([^"']+)["']"#, row[1])
            let clock = cells.count >= 3 ? cells[0] : ""
            let key = id.isEmpty ? "fallback:\(gameID):\(ordinal):\(clock):\(text)" : "web:\(gameID):\(id)"
            if seen.insert(key).inserted { result.append(LiveMessage(id: key, text: (clock.isEmpty ? "" : clock + "  ") + text)) }
        }
        let ids = rows.compactMap { Int64(first(#"\bid=["']([^"']+)["']"#, $0[1])) }
        if ids.count > 1 && ids[0] > ids[ids.count - 1] { result.reverse() }
        return result
    }
}

import AppKit
import SwiftUI
import NBALiveCore

enum Design {
    static let accent = Color(red: 0.94, green: 0.42, blue: 0.15)
    static func background(_ dark: Bool) -> Color { dark ? Color(red: 0.09, green: 0.13, blue: 0.16) : Color(red: 0.98, green: 0.97, blue: 0.95) }
    static func card(_ dark: Bool) -> Color { dark ? Color(red: 0.14, green: 0.19, blue: 0.23) : .white }
    static func muted(_ dark: Bool) -> Color { dark ? Color(white: 0.68) : Color(white: 0.43) }
    static func theme(_ value: String) -> ColorScheme? { value == "warm" ? .light : value == "dark" ? .dark : nil }
    private static let colors: [String: UInt32] = [
        "hawks":0xE03A3E,"celtics":0x008348,"nets":0x000000,"hornets":0x00788C,"bulls":0xCD1F43,
        "cavaliers":0x860038,"mavericks":0x0064B1,"nuggets":0x0D2240,"pistons":0xC8102E,"warriors":0x1D428A,
        "rockets":0xCE0E2D,"pacers":0x0C2341,"clippers":0x12173F,"lakers":0x542C81,"grizzlies":0x5D76A9,
        "heat":0x961A31,"bucks":0x00471A,"timberwolves":0x0C2340,"pelicans":0x0A2240,"knicks":0xF58426,
        "thunder":0x057DC1,"magic":0x0050B5,"76ers":0x1D428B,"suns":0xF79E1E,"blazers":0xCF0A2C,
        "kings":0x592D81,"spurs":0x111111,"raptors":0xBC2126,"jazz":0x4E008E,"wizards":0x002B5C
    ]
    static func jersey(_ team: String) -> Color {
        let rgb = colors[TeamCatalog.key(team) ?? ""] ?? 0x334155
        return Color(red: Double((rgb >> 16) & 255) / 255, green: Double((rgb >> 8) & 255) / 255, blue: Double(rgb & 255) / 255)
    }
    static func jerseyNumber(_ team: String) -> Color {
        let rgb = colors[TeamCatalog.key(team) ?? ""] ?? 0x334155
        func linear(_ value: UInt32) -> Double {
            let channel = Double(value) / 255
            return channel <= 0.04045 ? channel / 12.92 : pow((channel + 0.055) / 1.055, 2.4)
        }
        let luminance = 0.2126 * linear((rgb >> 16) & 255) + 0.7152 * linear((rgb >> 8) & 255) + 0.0722 * linear(rgb & 255)
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? .black : .white
    }
}

struct JerseyShape: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path(); let w = rect.width, h = rect.height
        p.move(to: CGPoint(x: w * 0.26, y: 0)); p.addLine(to: CGPoint(x: w * 0.40, y: 0))
        p.addQuadCurve(to: CGPoint(x: w * 0.60, y: 0), control: CGPoint(x: w * 0.50, y: h * 0.18))
        p.addLine(to: CGPoint(x: w * 0.74, y: 0)); p.addLine(to: CGPoint(x: w, y: h * 0.27))
        p.addLine(to: CGPoint(x: w * 0.82, y: h * 0.39)); p.addLine(to: CGPoint(x: w * 0.78, y: h))
        p.addLine(to: CGPoint(x: w * 0.22, y: h)); p.addLine(to: CGPoint(x: w * 0.18, y: h * 0.39))
        p.addLine(to: CGPoint(x: 0, y: h * 0.27)); p.closeSubpath(); return p
    }
}

@MainActor struct TeamLogoView: View {
    let name: String
    var size: CGFloat = 25
    @Environment(\.colorScheme) private var scheme
    private static let cache: NSCache<NSString, NSImage> = { let cache = NSCache<NSString, NSImage>(); cache.countLimit = 60; return cache }()
    private static func image(_ key: String, _ variant: String) -> NSImage? {
        let identifier = (key + "-" + variant) as NSString
        if let image = cache.object(forKey: identifier) { return image }
        guard let url = Bundle.main.url(forResource: identifier as String, withExtension: "png", subdirectory: "Teams"), let image = NSImage(contentsOf: url) else { return nil }
        cache.setObject(image, forKey: identifier); return image
    }
    var body: some View {
        let key = TeamCatalog.key(name) ?? ""
        let variant = scheme == .dark ? "dark" : "light"
        if let image = Self.image(key, variant) {
            Image(nsImage: image).resizable().scaledToFit().frame(width: size, height: size).accessibilityLabel(name)
        } else { Image(systemName: "basketball.fill").foregroundColor(Design.muted(scheme == .dark)).frame(width: size, height: size).accessibilityLabel(name) }
    }
}

@MainActor struct ScoreLine: View {
    let game: Game
    var body: some View {
        HStack(spacing: 7) {
            TeamLogoView(name: game.away, size: 24)
            Text(TeamCatalog.short(game.away)).lineLimit(1)
            Text("\(game.awayScore.map(String.init) ?? "—") : \(game.homeScore.map(String.init) ?? "—")")
                .font(.system(size: 17, weight: .semibold, design: .rounded)).monospacedDigit().fixedSize()
            Text(TeamCatalog.short(game.home)).lineLimit(1)
            TeamLogoView(name: game.home, size: 24)
            Spacer(minLength: 4)
            Text(game.progress).font(.system(size: 11)).foregroundColor(game.status == .live ? Design.accent : .secondary).lineLimit(1)
        }.font(.system(size: 12, weight: .medium)).help(game.title + "  " + game.progress)
    }
}

@MainActor struct LivePanel: View {
    @ObservedObject var store: LiveStore
    @Environment(\.colorScheme) private var scheme
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let game = store.visibleGame {
                ScoreLine(game: game).padding(.horizontal, 14).padding(.top, 13).padding(.bottom, 10)
                Divider()
                if store.mode == "player" { playerCard }
                else { textCard }
                Divider()
                HStack(spacing: 10) {
                    Text((store.hovering ? "阅读已暂停 · " : "") + (store.feedLabel.isEmpty ? store.message : store.feedLabel + " · " + store.message) + (store.scoreWarning.isEmpty ? "" : " · " + store.scoreWarning))
                        .font(.system(size: 10)).foregroundColor(Design.muted(scheme == .dark)).lineLimit(1)
                        .help(store.scoreFetchedAt.map { "最近更新 " + LiveStore.time($0) } ?? store.message)
                    Spacer(minLength: 2)
                    if store.delivery == "paged" { Button("最新") { store.jumpLatest() }.buttonStyle(.plain).font(.system(size: 11)) }
                    Button(store.unread > 0 ? "回顾(\(min(store.unread, 99))\(store.unread > 99 ? "+" : ""))" : "回顾") { store.openRecap?() }.buttonStyle(.plain).font(.system(size: 11))
                    Menu("更多") {
                        Button("选择比赛") { store.openPicker?() }
                        Button("会话历史") { store.openHistory?() }
                        Button("球员统计") { store.openStatistics?() }
                        Divider()
                        Button(store.mode == "text" ? "切换到关注球员" : "切换到文字直播") { store.mode = store.mode == "text" ? "player" : "text" }
                        Button("设置") { store.openSettings?() }
                        Divider()
                        Button("退出") { NSApp.terminate(nil) }
                    }.menuStyle(.borderlessButton).fixedSize().font(.system(size: 11))
                }.padding(.horizontal, 14).padding(.vertical, 10)
            } else {
                VStack(alignment: .leading, spacing: 12) {
                    HStack { Image(systemName: "basketball.fill").foregroundColor(Design.accent); Text("NBA 直播条").font(.headline) }
                    Text("选择一场比赛，在菜单栏查看比分和文字直播。") .font(.system(size: 12)).foregroundColor(.secondary)
                    Button("选择比赛") { store.openPicker?() }.keyboardShortcut(.defaultAction)
                }.padding(18).frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(width: 400)
        .background(Design.background(scheme == .dark))
        .preferredColorScheme(Design.theme(store.theme))
        .onHover { store.setHover($0) }
    }
    private var textCard: some View {
        VStack(alignment: .leading, spacing: 7) {
            if store.visibleMessages.isEmpty {
                Text(store.message).font(.system(size: 12)).foregroundColor(.secondary).lineLimit(2)
            } else {
                ForEach(Array(store.visibleMessages.reversed())) { item in
                    Text(item.text).font(.system(size: 12)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading).help(item.text)
                }
            }
        }.frame(height: 56, alignment: .topLeading).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 14).padding(.vertical, 9)
    }
    private var playerCard: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 6) {
                if let player = store.selectedFavorite {
                    Text(player.name + (player.didNotPlay ? " · 未出场" : "")).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                    Text("\(player.points) 分   \(player.rebounds) 板   \(player.assists) 助  ·  \(player.minutes)   投篮 \(player.fieldGoals)")
                        .font(.system(size: 11)).monospacedDigit().lineLimit(1)
                        .help("三分 \(player.threePointers) · 罚球 \(player.freeThrows)")
                } else if let id = store.selectedFavoriteID {
                    Text(store.favoriteNames[id] ?? "已关注球员").font(.system(size: 13, weight: .semibold))
                    Text("该球员不在本场统计中，或数据尚未提供").font(.system(size: 11)).foregroundColor(.secondary)
                } else { Button("去选择球员") { store.openStatistics?() }.buttonStyle(.plain).foregroundColor(Design.accent) }
            }
            Spacer()
            if store.favorites.count > 1 {
                Button("‹") { store.rotate(-1) }.buttonStyle(.plain).help("上一位")
                Button("›") { store.rotate(1) }.buttonStyle(.plain).help("下一位")
            }
        }.frame(height: 56).padding(.horizontal, 14).padding(.vertical, 9)
    }
}

@MainActor struct GamePicker: View {
    @ObservedObject var store: LiveStore
    let select: (Game) -> Void
    @Environment(\.colorScheme) private var scheme
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                VStack(alignment: .leading, spacing: 4) { Text("选择一场比赛").font(.system(size: 23, weight: .bold)); Text("北京时间 · 每 60 秒更新").font(.caption).foregroundColor(.secondary) }
                Spacer()
                DatePicker("日期", selection: $store.selectedDate, displayedComponents: .date).labelsHidden().frame(width: 150)
                    .environment(\.timeZone, TimeZone(identifier: "Asia/Shanghai")!)
                    .onChange(of: store.selectedDate) { _ in store.loadGames() }
                Button("刷新") { store.loadGames() }.disabled(store.listLoading)
            }
            if store.listLoading && store.games.isEmpty { ProgressView("正在加载比赛…").frame(maxWidth: .infinity, maxHeight: .infinity) }
            else if store.games.isEmpty { Text(store.listMessage).foregroundColor(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity) }
            else {
                ScrollView {
                    LazyVStack(spacing: 8) {
                        ForEach(store.games) { game in
                            Button(action: { select(game) }) {
                                HStack {
                                    TeamLogoView(name: game.away, size: 26)
                                    Text(TeamCatalog.short(game.away)).frame(width: 64, alignment: .leading).lineLimit(1)
                                    Spacer(minLength: 8)
                                    Text(game.awayScore.map(String.init) ?? "—").fontWeight(game.status == .finished && (game.awayScore ?? -1) > (game.homeScore ?? -1) ? .bold : .regular)
                                    Text(":")
                                    Text(game.homeScore.map(String.init) ?? "—").fontWeight(game.status == .finished && (game.homeScore ?? -1) > (game.awayScore ?? -1) ? .bold : .regular)
                                    Spacer(minLength: 8)
                                    Text(TeamCatalog.short(game.home)).frame(width: 64, alignment: .trailing).lineLimit(1)
                                    TeamLogoView(name: game.home, size: 26)
                                    Text(game.progress).font(.caption).foregroundColor(game.status == .live ? .red : .secondary).lineLimit(1).frame(width: 110, alignment: .trailing)
                                }.foregroundColor(game.status == .live ? .red : .primary).monospacedDigit().padding(12)
                                    .background(Design.card(scheme == .dark)).cornerRadius(9)
                            }.buttonStyle(.plain).help(game.title + " · " + game.progress)
                        }
                    }.padding(.vertical, 2)
                }
            }
            Text(store.listMessage).font(.caption).foregroundColor(.secondary).lineLimit(2)
        }.padding(20).frame(minWidth: 580, minHeight: 480).background(Design.background(scheme == .dark))
            .preferredColorScheme(Design.theme(store.theme))
    }
}

@MainActor struct StatisticsView: View {
    @ObservedObject var store: LiveStore
    @State private var home = false
    @State private var onlyFavorites = false
    @State private var onCourt = false
    @Environment(\.colorScheme) private var scheme
    var players: [Player] {
        let rows = home ? store.boxScore?.home ?? [] : store.boxScore?.away ?? []
        return rows.filter { (!onlyFavorites || store.favorites.contains($0.id)) && (!onCourt || (store.game?.status == .live ? $0.onCourt : $0.starter)) }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack { if let game = store.game { ScoreLine(game: game) }; Spacer(); Button("刷新") { store.refreshStatistics() } }
            HStack {
                Picker("球队", selection: $home) {
                    Text(store.game.map { TeamCatalog.short($0.away) + "（客）" } ?? "客队").tag(false)
                    Text(store.game.map { TeamCatalog.short($0.home) + "（主）" } ?? "主队").tag(true)
                }.pickerStyle(.segmented).frame(width: 240)
                Spacer()
                Toggle("场上五人", isOn: $onCourt).help("赛前与赛后显示首发")
                Toggle("只看关注", isOn: $onlyFavorites)
            }
            if store.boxScore == nil {
                Text(store.statisticsMessage.isEmpty ? "正在加载本场球员统计…" : store.statisticsMessage).foregroundColor(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView([.horizontal, .vertical]) {
                    VStack(spacing: 0) {
                        HStack(spacing: 0) { cell("关注", 42); cell("球员", 190); cell("时间", 65); cell("得分", 55); cell("篮板", 55); cell("助攻", 55); cell("投篮", 70); cell("三分", 65); cell("罚球", 65) }
                            .font(.caption).foregroundColor(.secondary).padding(.vertical, 9).background(Design.card(scheme == .dark))
                        ForEach(players) { player in
                            HStack(spacing: 0) {
                                Button(store.favorites.contains(player.id) ? "★" : "☆") { _ = store.toggleFavorite(player) }.buttonStyle(.plain).foregroundColor(Design.accent).frame(width: 42)
                                HStack(spacing: 6) {
                                    let team = home ? (store.game?.home ?? "") : (store.game?.away ?? "")
                                    ZStack { JerseyShape().fill(player.starter ? Design.jersey(team) : Color.gray.opacity(0.45)); Text(player.number).font(.system(size: 9, weight: .bold)).foregroundColor(player.starter ? Design.jerseyNumber(team) : (scheme == .dark ? .white : .black)) }.frame(width: 24, height: 26)
                                    Text(player.name).lineLimit(1)
                                    if store.game?.status == .live && player.onCourt { Image(systemName: "basketball.fill").foregroundColor(.red) }
                                }.frame(width: 190, alignment: .leading).help(player.name)
                                cell(player.minutes, 65); cell(player.points, 55); cell(player.rebounds, 55); cell(player.assists, 55)
                                cell(player.fieldGoals, 70); cell(player.threePointers, 65); cell(player.freeThrows, 65)
                            }.font(.system(size: 12)).foregroundColor(store.game?.status == .live && player.onCourt ? .red : .primary)
                                .padding(.vertical, 8).overlay(Divider(), alignment: .bottom)
                        }
                    }
                }
            }
            Text(store.statisticsMessage).font(.caption).foregroundColor(.secondary).lineLimit(2)
        }.padding(20).frame(minWidth: 720, minHeight: 480).background(Design.background(scheme == .dark))
            .preferredColorScheme(Design.theme(store.theme))
    }
    private func cell(_ value: String, _ width: CGFloat) -> some View { Text(value).lineLimit(1).monospacedDigit().frame(width: width, alignment: .center) }
}

@MainActor struct ReaderView: View {
    @ObservedObject var store: LiveStore
    let recap: Bool
    @State private var snapshot: [LiveMessage]
    @State private var score: String
    @State private var notice: String
    @Environment(\.colorScheme) private var scheme
    init(store: LiveStore, recap: Bool) {
        self.store = store; self.recap = recap
        _snapshot = State(initialValue: recap ? store.recapMessages : store.history)
        _score = State(initialValue: recap ? store.recapScore : store.game?.title ?? "")
        _notice = State(initialValue: recap ? store.recapNotice : "本次会话原文")
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 5) { Text(recap ? "刚才发生了什么" : "会话历史").font(.title2.bold()); Text(score).font(.headline) }
                Spacer()
                if recap { Button("重看最近 10 条") { update(recent: true) } }
                Button("查看新增") { update(recent: false) }
            }
            Text(notice + (store.scoreFetchedAt.map { " · 最近更新 " + LiveStore.time($0) } ?? "")) .font(.caption).foregroundColor(.secondary)
            if recap {
                let highlights = snapshot.filter { item in ["得分", "命中", "三分", "上篮", "扣篮", "暂停", "犯规", "结束", "开始"].contains { item.text.contains($0) } }.prefix(5)
                if !highlights.isEmpty {
                    VStack(alignment: .leading, spacing: 5) { Text("关键事件").font(.headline); ForEach(Array(highlights)) { Text("• " + $0.text).font(.callout) } }
                        .frame(maxWidth: .infinity, alignment: .leading).padding(12).background(Design.card(scheme == .dark)).cornerRadius(8)
                }
            }
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(snapshot.reversed())) { item in
                        Text(item.text).frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 8).textSelection(.enabled).overlay(Divider(), alignment: .bottom)
                    }
                }
            }
        }.padding(20).frame(minWidth: 540, minHeight: 430).background(Design.background(scheme == .dark))
            .preferredColorScheme(Design.theme(store.theme))
    }
    private func update(recent: Bool) {
        if recap { store.prepareRecap(recent: recent); snapshot = store.recapMessages; score = store.recapScore; notice = store.recapNotice; store.markRecapRead() }
        else { snapshot = store.history; score = store.game?.title ?? ""; notice = "本次会话 \(snapshot.count) 条原文" }
    }
}

@MainActor struct SettingsView: View {
    @ObservedObject var store: LiveStore
    var body: some View {
        Form {
            Picker("外观", selection: $store.theme) { Text("跟随系统").tag("system"); Text("暖白").tag("warm"); Text("炭灰").tag("dark") }
            Picker("菜单栏面板", selection: $store.mode) { Text("文字直播").tag("text"); Text("关注球员").tag("player") }
            Picker("菜单栏显示", selection: $store.menuDisplay) { Text("球队和比分").tag("full"); Text("仅比分").tag("score"); Text("仅图标").tag("icon") }
            Picker("文字来源", selection: $store.sourceMode) { Text("优先主播").tag("anchor"); Text("赛况实录").tag("structured") }
            Picker("消息展示", selection: $store.delivery) { Text("实时最新").tag("latest"); Text("完整补播").tag("paged") }
            if !store.hotkeyWarning.isEmpty { Text(store.hotkeyWarning).font(.caption).foregroundColor(.red) }
            Text("菜单栏持续显示当前比分；悬停直播面板可暂停阅读，后台仍会更新。") .font(.caption).foregroundColor(.secondary)
        }.padding(22).frame(width: 470, height: 230).preferredColorScheme(Design.theme(store.theme))
    }
}

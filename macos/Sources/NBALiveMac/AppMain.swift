import AppKit
import Combine
import SwiftUI
import NBALiveCore

@main @MainActor enum AppMain {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
    }
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSPopoverDelegate, NSWindowDelegate {
    private let store = LiveStore()
    private var statusItem: NSStatusItem!
    private let popover = NSPopover()
    private var observer: AnyCancellable?
    private var hotkeys: Hotkeys?
    private var picker: NSWindow?
    private var statistics: NSWindow?
    private var history: NSWindow?
    private var recap: NSWindow?
    private var settings: NSWindow?
    private var mouseThrough = false
    private var shownGameID: String?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        guard let button = statusItem.button else { NSApp.terminate(nil); return }
        let iconURL = Bundle.main.url(forResource: "MenuBar", withExtension: "png")
        if let image = iconURL.flatMap({ NSImage(contentsOf: $0) }) ?? NSImage(systemSymbolName: "basketball", accessibilityDescription: "NBA 直播条") {
            image.size = NSSize(width: 18, height: 18)
            image.isTemplate = true; button.image = image; button.imagePosition = .imageLeading
        }
        button.target = self; button.action = #selector(statusClicked(_:)); button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        popover.contentViewController = NSHostingController(rootView: LivePanel(store: store))
        popover.behavior = .transient; popover.delegate = self
        store.openPicker = { [weak self] in self?.showPicker() }
        store.openStatistics = { [weak self] in self?.showStatistics() }
        store.openHistory = { [weak self] in self?.showHistory() }
        store.openRecap = { [weak self] in self?.showRecap() }
        store.openSettings = { [weak self] in self?.showSettings() }
        observer = store.objectWillChange.receive(on: DispatchQueue.main).sink { [weak self] _ in self?.refreshStatus() }
        hotkeys = Hotkeys { [weak self] identifier in
            DispatchQueue.main.async {
                if identifier == 1 { self?.toggleVisibility() }
                else if identifier == 2 { self?.toggleMouseThrough() }
            }
        }
        if hotkeys?.failed == true { store.hotkeyWarning = "快捷键注册冲突，可使用菜单栏右键菜单" }
        refreshStatus()
        if CommandLine.arguments.contains("--smoke-test") {
            store.game = Game(id: "demo", date: "20261027", away: "勇士", home: "掘金", awayScore: 134, homeScore: 114, progress: "演示数据", status: .finished)
            statusClicked(nil)
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
                guard let self, self.statusItem.button != nil, self.popover.isShown else { fputs("Menu bar smoke test failed\n", stderr); exit(1) }
                print("PASS native menu bar and popover"); NSApp.terminate(nil)
            }
        } else { store.start() }
    }
    private func refreshStatus() {
        statusItem.button?.title = store.menuTitle.isEmpty ? "" : " " + store.menuTitle
        statusItem.button?.toolTip = (store.game?.title ?? "NBA 直播条") + "\n" + store.message + (store.scoreWarning.isEmpty ? "" : "\n" + store.scoreWarning)
        if shownGameID != store.game?.id {
            if shownGameID != nil { history?.close(); recap?.close() }
            shownGameID = store.game?.id
        }
    }
    @objc private func statusClicked(_ sender: Any?) {
        if NSApp.currentEvent?.type == .rightMouseUp { showMenu(); return }
        if NSApp.isHidden { NSApp.unhide(nil) }
        if popover.isShown { popover.performClose(nil) }
        else if let button = statusItem.button {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            popover.contentViewController?.view.window?.ignoresMouseEvents = mouseThrough
            store.popoverVisible = true
        }
    }
    private func showMenu() {
        guard let button = statusItem.button else { return }
        let menu = NSMenu()
        func item(_ title: String, _ action: Selector, key: String = "") {
            let entry = NSMenuItem(title: title, action: action, keyEquivalent: key); entry.target = self; menu.addItem(entry)
        }
        item("显示／隐藏直播面板", #selector(togglePanel))
        item("选择比赛…", #selector(pickerAction))
        item("球员统计…", #selector(statisticsAction))
        item("会话历史…", #selector(historyAction))
        item("刚才发生了什么…", #selector(recapAction))
        menu.addItem(.separator())
        item(NSApp.isHidden ? "恢复全部窗口" : "隐藏全部窗口", #selector(visibilityAction))
        item(mouseThrough ? "恢复鼠标操作" : "开启鼠标穿透", #selector(mouseThroughAction))
        item("设置…", #selector(settingsAction))
        menu.addItem(.separator()); item("退出 NBA 直播条", #selector(quitAction), key: "q")
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.minY), in: button)
    }
    func popoverDidClose(_ notification: Notification) { store.popoverVisible = false; store.setHover(false) }
    @objc private func togglePanel() { statusClicked(nil) }
    @objc private func pickerAction() { showPicker() }
    @objc private func statisticsAction() { showStatistics() }
    @objc private func historyAction() { showHistory() }
    @objc private func recapAction() { showRecap() }
    @objc private func settingsAction() { showSettings() }
    @objc private func visibilityAction() { toggleVisibility() }
    @objc private func mouseThroughAction() { toggleMouseThrough() }
    @objc private func quitAction() { NSApp.terminate(nil) }
    private func toggleVisibility() {
        if NSApp.isHidden { NSApp.unhide(nil); if statistics != nil { store.setStatisticsVisible(true) }; NSApp.activate(ignoringOtherApps: true) }
        else { popover.performClose(nil); if statistics != nil { store.setStatisticsVisible(false) }; NSApp.hide(nil) }
    }
    private func toggleMouseThrough() {
        mouseThrough.toggle(); store.setHover(false)
        popover.behavior = mouseThrough ? .applicationDefined : .transient
        popover.contentViewController?.view.window?.ignoresMouseEvents = mouseThrough
        statusItem.button?.toolTip = mouseThrough ? "鼠标穿透已开启 · 右键菜单或 ⌘⌥L 恢复" : store.menuTitle
    }
    private func open(_ window: inout NSWindow?, title: String, size: NSSize, content: () -> AnyView) {
        if let existing = window { existing.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); return }
        let controller = NSHostingController(rootView: content())
        let created = NSWindow(contentViewController: controller)
        created.title = title; created.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        created.setContentSize(size); created.isReleasedWhenClosed = false; created.delegate = self
        created.center(); window = created; created.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
    }
    private func showPicker() {
        open(&picker, title: "选择 NBA 比赛 · NBA 直播条", size: NSSize(width: 720, height: 650)) {
            AnyView(GamePicker(store: store) { [weak self] game in
                guard let self else { return }; self.store.select(game); self.picker?.close()
                if !self.popover.isShown { self.statusClicked(nil) }
            })
        }
        store.setPickerVisible(true)
    }
    private func showStatistics() {
        guard store.game != nil else { showPicker(); return }
        open(&statistics, title: "球员技术统计 · NBA 直播条", size: NSSize(width: 830, height: 620)) { AnyView(StatisticsView(store: store)) }
        store.setStatisticsVisible(true)
    }
    private func showHistory() {
        guard store.game != nil else { showPicker(); return }
        open(&history, title: "会话历史 · NBA 直播条", size: NSSize(width: 620, height: 600)) { AnyView(ReaderView(store: store, recap: false)) }
    }
    private func showRecap() {
        guard store.game != nil else { showPicker(); return }
        if let recap { recap.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); return }
        store.prepareRecap()
        open(&recap, title: "刚才发生了什么 · NBA 直播条", size: NSSize(width: 620, height: 600)) { AnyView(ReaderView(store: store, recap: true)) }
        if recap?.isVisible == true { store.markRecapRead() }
    }
    private func showSettings() { open(&settings, title: "设置 · NBA 直播条", size: NSSize(width: 470, height: 230)) { AnyView(SettingsView(store: store)) } }
    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow else { return }
        if window === picker { store.setPickerVisible(false); picker = nil }
        if window === statistics { store.setStatisticsVisible(false); statistics = nil }
        if window === history { history = nil }
        if window === recap { recap = nil }
        if window === settings { settings = nil }
    }
    func applicationWillTerminate(_ notification: Notification) { store.stop(); hotkeys = nil; NSStatusBar.system.removeStatusItem(statusItem) }
}

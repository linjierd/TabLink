import UIKit
import MetalKit

final class MainViewController: UIViewController {
    private var metalView: TouchMetalView!
    private var presenter: MetalPresenter?
    private var session: NativeSession?
    private var sessionGeneration = UUID()
    private let frameGate = NSLock()
    private var lastLink: PairingLink?
    private let panel = UIStackView()
    private let connectionScroll = UIScrollView()
    private let status = UILabel()
    private let hud = UILabel()
    private let updateStatus = UILabel()
    private let updateButton = UIButton(type: .system)
    private let updateModeButton = UIButton(type: .system)
    private let updateModeDetail = UILabel()
    private let checkUpdateButton = UIButton(type: .system)
    private let settings = UIButton(type: .system)
    private var settingsOpen = false
    private var isShowingFrames = false
    private var lastPresented = ProcessInfo.processInfo.systemUptime
    private var presentationTimer: Timer?
    override var prefersStatusBarHidden: Bool { true }
    override var prefersHomeIndicatorAutoHidden: Bool { isShowingFrames && !settingsOpen }
    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .all }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        metalView = TouchMetalView(frame: .zero, device: MTLCreateSystemDefaultDevice())
        metalView.translatesAutoresizingMaskIntoConstraints = false
        metalView.colorPixelFormat = .bgra8Unorm
        metalView.clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
        metalView.framebufferOnly = true
        metalView.preferredFramesPerSecond = UIScreen.main.maximumFramesPerSecond
        metalView.isMultipleTouchEnabled = false
        view.addSubview(metalView)
        NSLayoutConstraint.activate([metalView.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            metalView.trailingAnchor.constraint(equalTo: view.trailingAnchor), metalView.topAnchor.constraint(equalTo: view.topAnchor),
            metalView.bottomAnchor.constraint(equalTo: view.bottomAnchor)])
        do { presenter = try MetalPresenter(view: metalView) }
        catch { status.text = "此设备无法使用 Metal 显示画面。" }
        metalView.imageSize = { [weak self] in self?.presenter?.imageSize ?? .zero }
        metalView.onMouse = { [weak self] in self?.session?.sendMouse($0) }
        presenter?.onPresented = { [weak self] frame, time in
            DispatchQueue.main.async {
                guard let self else { return }
                self.session?.presented(frame, time: time)
                // Session validates connection identity before changing the UI through onStatus.
            }
        }
        presenter?.onFailure = { [weak self] message in
            DispatchQueue.main.async { self?.stop(message: message) }
        }
        makePanel()
        presentationTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self, self.isShowingFrames,
                  ProcessInfo.processInfo.systemUptime - self.lastPresented > 5 else { return }
            self.isShowingFrames = false
            self.status.text = "暂时没有新画面。请检查电脑是否暂停采集；可在这里重新连接。"
            self.showPanel()
        }
        NotificationCenter.default.addObserver(self, selector: #selector(backgrounded), name: UIApplication.didEnterBackgroundNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(updateStateChanged), name: StableUpdateManager.stateChanged, object: nil)
        refreshUpdateState()
    }

    private func makePanel() {
        panel.axis = .vertical; panel.spacing = 16; panel.alignment = .fill
        panel.translatesAutoresizingMaskIntoConstraints = false
        panel.backgroundColor = UIColor(white: 0.10, alpha: 0.97)
        panel.layer.cornerRadius = 20
        panel.isLayoutMarginsRelativeArrangement = true
        panel.layoutMargins = UIEdgeInsets(top: 24, left: 24, bottom: 24, right: 24)
        let title = UILabel(); title.text = "TabLink 平板副屏"; title.textColor = .white
        title.font = .preferredFont(forTextStyle: .title2)
        let detail = UILabel(); detail.numberOfLines = 0; detail.textColor = .lightGray
        detail.text = "在 Windows TabLink 中启动原生网络会话，然后扫描二维码。\n连接同一 Wi-Fi，或使用已建立的 USB 网络共享路径。iPad 不需要 ADB。"
        detail.font = .preferredFont(forTextStyle: .body)
        status.textColor = .white; status.font = .preferredFont(forTextStyle: .subheadline); status.numberOfLines = 0
        if status.text == nil { status.text = "尚未连接 · 需要 iOS / iPadOS 17 或更高版本" }
        updateStatus.textColor = .lightGray; updateStatus.font = .preferredFont(forTextStyle: .footnote); updateStatus.numberOfLines = 0
        updateModeButton.showsMenuAsPrimaryAction = true; updateModeButton.contentHorizontalAlignment = .leading
        updateModeButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        updateModeButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        updateModeDetail.textColor = .lightGray; updateModeDetail.font = .preferredFont(forTextStyle: .footnote)
        updateModeDetail.numberOfLines = 0
        checkUpdateButton.setTitle("立即检查正式版更新", for: .normal)
        checkUpdateButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        checkUpdateButton.contentHorizontalAlignment = .leading
        checkUpdateButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        checkUpdateButton.addTarget(self, action: #selector(checkStableUpdate), for: .touchUpInside)
        updateButton.setTitle("手动前往 App Store 更新", for: .normal); updateButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        updateButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        updateButton.addTarget(self, action: #selector(openStableUpdate), for: .touchUpInside)
        [title, detail, status, updateModeButton, updateModeDetail, updateStatus, checkUpdateButton, updateButton].forEach(panel.addArrangedSubview)
        panel.addArrangedSubview(button("扫描电脑二维码", action: #selector(scan)))
        panel.addArrangedSubview(button("粘贴连接链接", action: #selector(paste)))
        panel.addArrangedSubview(button("重新连接本次配对", action: #selector(reconnect)))
        panel.addArrangedSubview(button("断开连接", action: #selector(disconnect)))
        connectionScroll.translatesAutoresizingMaskIntoConstraints = false; view.addSubview(connectionScroll)
        let content = UIView(); content.translatesAutoresizingMaskIntoConstraints = false; connectionScroll.addSubview(content)
        content.addSubview(panel)
        NSLayoutConstraint.activate([connectionScroll.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            connectionScroll.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            connectionScroll.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 52),
            connectionScroll.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -28),
            content.leadingAnchor.constraint(equalTo: connectionScroll.contentLayoutGuide.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: connectionScroll.contentLayoutGuide.trailingAnchor),
            content.topAnchor.constraint(equalTo: connectionScroll.contentLayoutGuide.topAnchor),
            content.bottomAnchor.constraint(equalTo: connectionScroll.contentLayoutGuide.bottomAnchor),
            content.widthAnchor.constraint(equalTo: connectionScroll.frameLayoutGuide.widthAnchor),
            content.heightAnchor.constraint(greaterThanOrEqualTo: connectionScroll.frameLayoutGuide.heightAnchor),
            panel.centerXAnchor.constraint(equalTo: content.centerXAnchor), panel.centerYAnchor.constraint(equalTo: content.centerYAnchor),
            panel.topAnchor.constraint(greaterThanOrEqualTo: content.topAnchor, constant: 16),
            panel.bottomAnchor.constraint(lessThanOrEqualTo: content.bottomAnchor, constant: -16),
            panel.widthAnchor.constraint(lessThanOrEqualToConstant: 520),
            panel.leadingAnchor.constraint(greaterThanOrEqualTo: content.leadingAnchor, constant: 16),
            panel.trailingAnchor.constraint(lessThanOrEqualTo: content.trailingAnchor, constant: -16)])
        let contentHeight = content.heightAnchor.constraint(equalTo: connectionScroll.frameLayoutGuide.heightAnchor)
        contentHeight.priority = .defaultLow; contentHeight.isActive = true
        let preferredWidth = panel.widthAnchor.constraint(equalToConstant: 520); preferredWidth.priority = .defaultHigh; preferredWidth.isActive = true
        hud.textColor = .white; hud.backgroundColor = UIColor(white: 0, alpha: 0.6)
        hud.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular); hud.numberOfLines = 2
        hud.translatesAutoresizingMaskIntoConstraints = false; view.addSubview(hud)
        settings.setTitle("设置", for: .normal); settings.tintColor = .white
        settings.backgroundColor = UIColor(white: 0.15, alpha: 0.8); settings.layer.cornerRadius = 12
        settings.translatesAutoresizingMaskIntoConstraints = false; settings.addTarget(self, action: #selector(toggleSettings), for: .touchUpInside)
        view.addSubview(settings)
        NSLayoutConstraint.activate([settings.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12),
            settings.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            settings.widthAnchor.constraint(equalToConstant: 60), settings.heightAnchor.constraint(equalToConstant: 40),
            hud.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 10),
            hud.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -4),
            hud.trailingAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -10)])
    }

    private func button(_ title: String, action: Selector) -> UIButton {
        let button = UIButton(type: .system); button.setTitle(title, for: .normal)
        button.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        button.addTarget(self, action: action, for: .touchUpInside); return button
    }

    func openPairingURL(_ url: URL) { loadViewIfNeeded(); accept(url.absoluteString) }
    private func accept(_ text: String) {
        do { connect(try PairingLink(text.trimmingCharacters(in: .whitespacesAndNewlines))) }
        catch { status.text = "连接码无效。请复制或扫描电脑端当前的原生客户端二维码。"; showPanel() }
    }
    private func profileData() throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(DisplayProfile.read(window: view.window))
    }
    private func connect(_ link: PairingLink) {
        guard presenter != nil, let data = try? profileData() else { status.text = "无法初始化本机显示器。"; return }
        stop(message: "正在连接…")
        let generation = UUID(); frameGate.lock(); sessionGeneration = generation; frameGate.unlock()
        let next = NativeSession(link: link, profile: data); session = next; lastLink = link; settingsOpen = false
        next.onFrame = { [weak self] frame in
            guard let self else { return }
            self.frameGate.lock(); defer { self.frameGate.unlock() }
            guard self.sessionGeneration == generation else { return }; self.presenter?.offer(frame)
        }
        next.onPresentation = { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.sessionGeneration == generation else { return }
                self.lastPresented = ProcessInfo.processInfo.systemUptime
            }
        }
        next.onInactive = { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.sessionGeneration == generation else { return }
                UIApplication.shared.isIdleTimerDisabled = false
            }
        }
        next.onStatus = { [weak self] text, displaying in
            DispatchQueue.main.async {
                guard let self, self.sessionGeneration == generation else { return }
                self.status.text = text; self.hud.text = text
                self.isShowingFrames = displaying && ProcessInfo.processInfo.systemUptime - self.lastPresented <= 5
                if self.isShowingFrames {
                    if !self.settingsOpen { self.connectionScroll.isHidden = true }
                } else { self.showPanel() }
                self.setNeedsUpdateOfHomeIndicatorAutoHidden()
            }
        }
        next.onReset = { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.sessionGeneration == generation else { return }
                self.metalView.releaseTouch(); self.presenter?.clear(); self.isShowingFrames = false; self.showPanel()
            }
        }
        UIApplication.shared.isIdleTimerDisabled = true; next.start()
    }
    private func stop(message: String) {
        metalView?.releaseTouch(); frameGate.lock(); sessionGeneration = UUID(); frameGate.unlock(); session?.stop(); session = nil
        presenter?.clear(); isShowingFrames = false; settingsOpen = false
        UIApplication.shared.isIdleTimerDisabled = false; status.text = message; hud.text = nil; showPanel()
    }
    private func showPanel() { connectionScroll.isHidden = false; setNeedsUpdateOfHomeIndicatorAutoHidden() }
    @objc private func toggleSettings() {
        settingsOpen.toggle(); connectionScroll.isHidden = isShowingFrames && !settingsOpen
        if settingsOpen { metalView.releaseTouch() }; setNeedsUpdateOfHomeIndicatorAutoHidden()
    }
    @objc private func disconnect() { stop(message: "已断开。电脑端会释放触控状态。") }
    @objc private func reconnect() {
        guard let lastLink else { status.text = "请先扫描或粘贴电脑的连接码。"; return }; connect(lastLink)
    }
    @objc private func paste() {
        // Read only after the user's explicit action; iOS may show its paste permission dialog.
        guard let text = UIPasteboard.general.string else { status.text = "剪贴板中没有连接链接。"; return }; accept(text)
    }
    @objc private func scan() {
        let scanner = QRCodeViewController()
        scanner.onCode = { [weak self] text in self?.dismiss(animated: true) { self?.accept(text) } }
        present(UINavigationController(rootViewController: scanner), animated: true)
    }
    @objc private func updateStateChanged() { refreshUpdateState() }
    private func refreshUpdateState() {
        let manager = StableUpdateManager.shared
        let update = manager.state
        updateStatus.text = update.text
        updateButton.isHidden = update.installerURL == nil || manager.updateMode == .never
        checkUpdateButton.isEnabled = manager.updateMode != .never
        updateModeButton.setTitle("更新方式：\(manager.updateMode.title)", for: .normal)
        updateModeButton.menu = UIMenu(title: "正式版更新方式", children: StableUpdateMode.allCases.map { mode in
            UIAction(title: mode.title, state: mode == manager.updateMode ? .on : .off) { [weak self] _ in
                StableUpdateManager.shared.setUpdateMode(mode)
                self?.refreshUpdateState()
            }
        })
        switch manager.updateMode {
        case .automatic:
            updateModeDetail.text = "自动检查正式版；下载与安装由 App Store 和系统的自动更新设置处理，TabLink 不会静默打开商店或自行安装。"
        case .downloadThenAsk:
            updateModeDetail.text = "自动检查更新信息；iOS 不允许应用预下载或替换自身，只有你点击按钮后才会打开 App Store。"
        case .never:
            updateModeDetail.text = "不发起更新网络请求，也不打开 App Store 更新入口。"
        }
    }
    @objc private func checkStableUpdate() { StableUpdateManager.shared.checkNow() }
    @objc private func openStableUpdate() { StableUpdateManager.shared.openInstaller() }
    @objc private func backgrounded() {
        if presentedViewController != nil { dismiss(animated: false) }
        stop(message: "应用进入后台，已释放连接。回到前台后可重新连接本次配对。")
    }
    override func viewDidAppear(_ animated: Bool) { super.viewDidAppear(animated); sendProfile() }
    override func viewWillTransition(to size: CGSize, with coordinator: UIViewControllerTransitionCoordinator) {
        metalView?.releaseTouch()
        super.viewWillTransition(to: size, with: coordinator)
        coordinator.animate(alongsideTransition: nil) { [weak self] _ in self?.sendProfile() }
    }
    private func sendProfile() {
        metalView?.preferredFramesPerSecond = view.window?.screen.maximumFramesPerSecond ?? 60
        if let data = try? profileData() { session?.updateProfile(data) }
    }
    deinit { presentationTimer?.invalidate(); session?.stop(); NotificationCenter.default.removeObserver(self) }
}

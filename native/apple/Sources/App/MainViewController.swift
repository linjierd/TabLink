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
    private let languageButton = UIButton(type: .system)
    private let languageDetail = UILabel()
    private let settings = UIButton(type: .system)
    private let titleLabel = UILabel()
    private let detailLabel = UILabel()
    private let scanButton = UIButton(type: .system)
    private let pasteButton = UIButton(type: .system)
    private let reconnectButton = UIButton(type: .system)
    private let disconnectButton = UIButton(type: .system)
    private var settingsOpen = false
    private var isShowingFrames = false
    private var localStatusKey: String? = "status.notConnected"
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
        catch { localStatusKey = "status.metalUnavailable"; status.text = L10n.text("status.metalUnavailable") }
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
            self.localStatusKey = "status.noFrames"; self.status.text = L10n.text("status.noFrames")
            self.showPanel()
        }
        NotificationCenter.default.addObserver(self, selector: #selector(backgrounded), name: UIApplication.didEnterBackgroundNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(updateStateChanged), name: StableUpdateManager.stateChanged, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(languageChanged), name: L10n.languageChanged, object: nil)
        applyLocalizedText()
        refreshUpdateState()
    }

    private func makePanel() {
        panel.axis = .vertical; panel.spacing = 16; panel.alignment = .fill
        panel.translatesAutoresizingMaskIntoConstraints = false
        panel.backgroundColor = UIColor(white: 0.10, alpha: 0.97)
        panel.layer.cornerRadius = 20
        panel.isLayoutMarginsRelativeArrangement = true
        panel.layoutMargins = UIEdgeInsets(top: 24, left: 24, bottom: 24, right: 24)
        titleLabel.textColor = .white
        titleLabel.font = .preferredFont(forTextStyle: .title2)
        detailLabel.numberOfLines = 0; detailLabel.textColor = .lightGray
        detailLabel.font = .preferredFont(forTextStyle: .body)
        status.textColor = .white; status.font = .preferredFont(forTextStyle: .subheadline); status.numberOfLines = 0
        updateStatus.textColor = .lightGray; updateStatus.font = .preferredFont(forTextStyle: .footnote); updateStatus.numberOfLines = 0
        updateModeButton.showsMenuAsPrimaryAction = true; updateModeButton.contentHorizontalAlignment = .leading
        updateModeButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        updateModeButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        updateModeDetail.textColor = .lightGray; updateModeDetail.font = .preferredFont(forTextStyle: .footnote)
        updateModeDetail.numberOfLines = 0
        checkUpdateButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        checkUpdateButton.contentHorizontalAlignment = .leading
        checkUpdateButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        checkUpdateButton.addTarget(self, action: #selector(checkStableUpdate), for: .touchUpInside)
        updateButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        updateButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        updateButton.addTarget(self, action: #selector(openStableUpdate), for: .touchUpInside)
        languageButton.showsMenuAsPrimaryAction = true; languageButton.contentHorizontalAlignment = .leading
        languageButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        languageButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        languageDetail.textColor = .lightGray; languageDetail.font = .preferredFont(forTextStyle: .footnote)
        languageDetail.numberOfLines = 0
        configure(scanButton, action: #selector(scan)); configure(pasteButton, action: #selector(paste))
        configure(reconnectButton, action: #selector(reconnect)); configure(disconnectButton, action: #selector(disconnect))
        [titleLabel, detailLabel, status, languageButton, languageDetail, updateModeButton, updateModeDetail,
         updateStatus, checkUpdateButton, updateButton, scanButton, pasteButton, reconnectButton, disconnectButton]
            .forEach(panel.addArrangedSubview)
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
        settings.tintColor = .white
        settings.backgroundColor = UIColor(white: 0.15, alpha: 0.8); settings.layer.cornerRadius = 12
        settings.translatesAutoresizingMaskIntoConstraints = false; settings.addTarget(self, action: #selector(toggleSettings), for: .touchUpInside)
        view.addSubview(settings)
        NSLayoutConstraint.activate([settings.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12),
            settings.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            settings.widthAnchor.constraint(greaterThanOrEqualToConstant: 84), settings.heightAnchor.constraint(equalToConstant: 40),
            hud.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 10),
            hud.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -4),
            hud.trailingAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -10)])
    }

    private func configure(_ button: UIButton, action: Selector) {
        button.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        button.addTarget(self, action: action, for: .touchUpInside)
    }

    func openPairingURL(_ url: URL) { loadViewIfNeeded(); accept(url.absoluteString) }
    private func accept(_ text: String) {
        do { connect(try PairingLink(text.trimmingCharacters(in: .whitespacesAndNewlines))) }
        catch { localStatusKey = "status.invalidPairing"; status.text = L10n.text("status.invalidPairing"); showPanel() }
    }
    private func profileData() throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(DisplayProfile.read(window: view.window))
    }
    private func connect(_ link: PairingLink) {
        guard presenter != nil, let data = try? profileData() else {
            localStatusKey = "status.displayInitFailed"; status.text = L10n.text("status.displayInitFailed"); return
        }
        stop(message: L10n.text("status.connecting"), localizationKey: "status.connecting")
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
                self.localStatusKey = nil; self.status.text = text; self.hud.text = text
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
    private func stop(message: String, localizationKey: String? = nil) {
        metalView?.releaseTouch(); frameGate.lock(); sessionGeneration = UUID(); frameGate.unlock(); session?.stop(); session = nil
        presenter?.clear(); isShowingFrames = false; settingsOpen = false
        UIApplication.shared.isIdleTimerDisabled = false; localStatusKey = localizationKey
        status.text = message; hud.text = nil; showPanel()
    }
    private func showPanel() { connectionScroll.isHidden = false; setNeedsUpdateOfHomeIndicatorAutoHidden() }
    @objc private func toggleSettings() {
        settingsOpen.toggle(); connectionScroll.isHidden = isShowingFrames && !settingsOpen
        if settingsOpen { metalView.releaseTouch() }; setNeedsUpdateOfHomeIndicatorAutoHidden()
    }
    @objc private func disconnect() { stop(message: L10n.text("status.disconnected"), localizationKey: "status.disconnected") }
    @objc private func reconnect() {
        guard let lastLink else {
            localStatusKey = "status.needPairing"; status.text = L10n.text("status.needPairing"); return
        }; connect(lastLink)
    }
    @objc private func paste() {
        // Read only after the user's explicit action; iOS may show its paste permission dialog.
        guard let text = UIPasteboard.general.string else {
            localStatusKey = "status.clipboardEmpty"; status.text = L10n.text("status.clipboardEmpty"); return
        }; accept(text)
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
        updateModeButton.setTitle(L10n.text("update.modeLabel", localizedTitle(manager.updateMode)), for: .normal)
        updateModeButton.menu = UIMenu(title: L10n.text("update.menuTitle"), children: StableUpdateMode.allCases.map { mode in
            UIAction(title: localizedTitle(mode), state: mode == manager.updateMode ? .on : .off) { [weak self] _ in
                StableUpdateManager.shared.setUpdateMode(mode)
                self?.refreshUpdateState()
            }
        })
        switch manager.updateMode {
        case .automatic:
            updateModeDetail.text = L10n.text("update.detail.automatic")
        case .downloadThenAsk:
            updateModeDetail.text = L10n.text("update.detail.downloadThenAsk")
        case .never:
            updateModeDetail.text = L10n.text("update.detail.never")
        }
    }
    private func localizedTitle(_ mode: StableUpdateMode) -> String {
        switch mode {
        case .automatic: return L10n.text("update.mode.automatic")
        case .downloadThenAsk: return L10n.text("update.mode.downloadThenAsk")
        case .never: return L10n.text("update.mode.never")
        }
    }
    private func applyLocalizedText() {
        titleLabel.text = L10n.text("app.title")
        detailLabel.text = L10n.text("app.intro")
        if session == nil, let localStatusKey { status.text = L10n.text(localStatusKey) }
        settings.setTitle(L10n.text("settings.title"), for: .normal)
        checkUpdateButton.setTitle(L10n.text("action.checkUpdate"), for: .normal)
        updateButton.setTitle(L10n.text("action.openAppStore"), for: .normal)
        scanButton.setTitle(L10n.text("action.scan"), for: .normal)
        pasteButton.setTitle(L10n.text("action.paste"), for: .normal)
        reconnectButton.setTitle(L10n.text("action.reconnect"), for: .normal)
        disconnectButton.setTitle(L10n.text("action.disconnect"), for: .normal)
        languageButton.setTitle("\(L10n.text("language.label"))：\(L10n.language.title)", for: .normal)
        languageButton.menu = UIMenu(title: L10n.text("language.label"), children: AppLanguage.allCases.map { language in
            UIAction(title: language.title, state: language == L10n.language ? .on : .off) { _ in L10n.setLanguage(language) }
        })
        languageDetail.text = L10n.text("language.detail")
    }
    @objc private func languageChanged() {
        applyLocalizedText(); refreshUpdateState(); session?.refreshLocalizedHostStatus()
    }
    @objc private func checkStableUpdate() { StableUpdateManager.shared.checkNow() }
    @objc private func openStableUpdate() { StableUpdateManager.shared.openInstaller() }
    @objc private func backgrounded() {
        if presentedViewController != nil { dismiss(animated: false) }
        stop(message: L10n.text("status.backgrounded"), localizationKey: "status.backgrounded")
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

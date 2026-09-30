import UIKit
import AVFoundation

final class QRCodeViewController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var onCode: ((String) -> Void)?
    private let capture = AVCaptureSession()
    private let queue = DispatchQueue(label: "TabLink.Camera")
    private var preview: AVCaptureVideoPreviewLayer?
    private let explanation = UILabel()
    private var accepted = false
    private var visible = true

    override func viewDidLoad() {
        super.viewDidLoad(); title = "扫描电脑二维码"; view.backgroundColor = .black
        navigationItem.leftBarButtonItem = UIBarButtonItem(barButtonSystemItem: .cancel, target: self, action: #selector(cancel))
        explanation.text = "只扫描电脑端 TabLink 原生连接二维码。"; explanation.textColor = .white
        explanation.numberOfLines = 0; explanation.textAlignment = .center
        explanation.backgroundColor = UIColor(white: 0, alpha: 0.7)
        explanation.translatesAutoresizingMaskIntoConstraints = false; view.addSubview(explanation)
        NSLayoutConstraint.activate([explanation.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            explanation.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -16),
            explanation.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -24)])
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized: configure()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                DispatchQueue.main.async { if granted { self?.configure() } else { self?.unavailable() } }
            }
        default: unavailable()
        }
    }
    private func unavailable() { explanation.text = "无法使用摄像头。返回后选择“粘贴连接链接”，或在系统设置中允许相机权限。" }
    private func configure() {
        guard visible else { return }
        let preview = AVCaptureVideoPreviewLayer(session: capture); preview.videoGravity = .resizeAspectFill
        view.layer.insertSublayer(preview, at: 0); self.preview = preview; preview.frame = view.bounds
        queue.async { [weak self] in
            guard let self, let device = AVCaptureDevice.default(for: .video),
                  let input = try? AVCaptureDeviceInput(device: device) else {
                DispatchQueue.main.async { self?.unavailable() }; return
            }
            self.capture.beginConfiguration()
            let output = AVCaptureMetadataOutput()
            guard self.capture.canAddInput(input), self.capture.canAddOutput(output) else {
                self.capture.commitConfiguration(); DispatchQueue.main.async { self.unavailable() }; return
            }
            self.capture.addInput(input); self.capture.addOutput(output)
            guard output.availableMetadataObjectTypes.contains(.qr) else {
                self.capture.commitConfiguration(); DispatchQueue.main.async { self.unavailable() }; return
            }
            output.setMetadataObjectsDelegate(self, queue: .main); output.metadataObjectTypes = [.qr]
            self.capture.commitConfiguration(); self.capture.startRunning()
        }
    }
    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard visible, !accepted else { return }
        for object in metadataObjects {
            guard let code = object as? AVMetadataMachineReadableCodeObject, let text = code.stringValue else { continue }
            guard (try? PairingLink(text)) != nil else { explanation.text = "这不是有效的 TabLink 原生连接码。"; continue }
            accepted = true; queue.async { [capture] in capture.stopRunning() }; onCode?(text); return
        }
    }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); preview?.frame = view.bounds }
    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated); visible = false; queue.async { [capture] in capture.stopRunning() }
    }
    @objc private func cancel() { dismiss(animated: true) }
}

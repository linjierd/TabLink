import { display } from '@kit.ArkUI';
import { common } from '@kit.AbilityKit';
import { Session } from '../protocol/Session';
import { readDisplayProfile } from '../protocol/DisplayProfile';
import { StableUpdateManager, StableUpdateStatus } from '../update/StableUpdateManager';
import {
  StableUpdateMode, UPDATE_MODE_AUTOMATIC, UPDATE_MODE_DOWNLOAD_THEN_ASK
} from '../update/StableUpdatePolicy';

class VideoSurfaceController extends XComponentController {
  created: (surface: string) => void = (_surface: string) => {};
  destroyed: () => void = () => {};
  onSurfaceCreated(surfaceId: string): void { this.created(surfaceId); }
  onSurfaceDestroyed(_surfaceId: string): void { this.destroyed(); }
}

@Entry
@Component
struct Index {
  @State private pairingText: string = '';
  @State private statusText: string = '粘贴电脑上此设备专属的 TabLink 连接信息';
  @State private active: boolean = false;
  @State private surfaceReady: boolean = false;
  @State private updateText: string = '正式版更新：正在安全检查…';
  @State private updateInstallerUrl: string = '';
  @State private updateModeIndex: number = 0;
  @StorageLink('foreground') @Watch('onForegroundChanged') private foreground: boolean = true;
  private controller: VideoSurfaceController = new VideoSurfaceController();
  private surfaceId: string = '';
  private session: Session | undefined = undefined;
  private activeUri: string = '';
  private profileKey: string = '';
  private rotationTimer: number = -1;
  private displayChanged: (id: number) => void = (_id: number) => this.onDisplayChanged();
  private updateManager: StableUpdateManager | undefined = undefined;
  private updateModeSelectionRevision: number = 0;

  aboutToAppear(): void {
    this.controller.created = (id: string) => { this.surfaceId = id; this.surfaceReady = true; };
    this.controller.destroyed = () => { this.surfaceId = ''; this.surfaceReady = false; this.stop('原生显示面已关闭'); };
    display.on('change', this.displayChanged);
    this.updateManager = new StableUpdateManager(getContext(this) as common.UIAbilityContext, (status: StableUpdateStatus) => {
      this.updateText = status.text; this.updateInstallerUrl = status.installerUrl;
      this.updateModeIndex = status.mode === UPDATE_MODE_AUTOMATIC ? 0 :
        status.mode === UPDATE_MODE_DOWNLOAD_THEN_ASK ? 1 : 2;
    });
    this.updateManager.start();
  }
  aboutToDisappear(): void {
    this.updateModeSelectionRevision++;
    display.off('change', this.displayChanged); this.updateManager?.stop(); this.stop('已退出副屏');
  }
  onBackPress(): boolean { if (this.active) { this.stop(); return true; } return false; }
  private onForegroundChanged(): void {
    if (this.foreground) this.updateManager?.checkIfNeeded();
    else this.stop('应用进入后台，已释放副屏连接');
  }
  private updateModeForIndex(index: number): StableUpdateMode {
    if (index === 0) return 'automatic';
    if (index === 1) return 'downloadThenAsk';
    return 'never';
  }
  private updateModeHint(): string {
    if (this.updateModeIndex === 0) return '自动检查；安装时机由应用市场的自动更新设置管理。';
    if (this.updateModeIndex === 1) return '自动检查并准备可信入口；仅在你点按钮后打开应用市场。';
    return '不连接博客或 GitHub 更新地址，也不检查新版本。';
  }
  private chooseUpdateMode(index: number): void {
    const revision: number = ++this.updateModeSelectionRevision;
    this.updateModeIndex = index;
    this.updateManager?.setMode(this.updateModeForIndex(index)).then((applied: StableUpdateMode) => {
      if (revision !== this.updateModeSelectionRevision) return;
      this.updateModeIndex = applied === UPDATE_MODE_AUTOMATIC ? 0 :
        applied === UPDATE_MODE_DOWNLOAD_THEN_ASK ? 1 : 2;
    }).catch((_error: Error) => {
      if (revision !== this.updateModeSelectionRevision) return;
      this.updateModeIndex = 2;
      this.updateText = '正式版更新：设置保存失败，已停止联网检查';
      this.updateInstallerUrl = '';
    });
  }
  private stop(message: string = '已停止连接'): void {
    this.activeUri = '';
    if (this.rotationTimer !== -1) { clearTimeout(this.rotationTimer); this.rotationTimer = -1; }
    const previous = this.session; this.session = undefined;
    previous?.stop(); this.active = false; this.statusText = message;
  }
  private async connect(uri: string): Promise<void> {
    if (!this.surfaceReady || !this.foreground) { this.statusText = '请等待原生显示面就绪'; return; }
    this.stop();
    try {
      this.profileKey = JSON.stringify(readDisplayProfile());
      this.activeUri = uri;
      const current = new Session(this.surfaceId, (message: string, isActive: boolean) => {
        if (this.session !== current) return;
        this.statusText = message; this.active = isActive;
        if (!isActive) { this.session = undefined; this.activeUri = ''; }
      });
      this.session = current;
      this.active = true;
      await current.connect(uri);
    } catch (error) {
      // Session reports its own failure. A replaced session cannot overwrite
      // the status of a newer orientation reconnect.
      if (!this.session) { this.active = false; this.statusText = (error as Error).message; }
    }
  }
  private onDisplayChanged(): void {
    if (!this.active || !this.activeUri || !this.foreground) return;
    let next: string;
    try { next = JSON.stringify(readDisplayProfile()); }
    catch (error) { this.stop((error as Error).message); return; }
    if (next === this.profileKey) return;
    this.profileKey = next;
    if (this.rotationTimer !== -1) clearTimeout(this.rotationTimer);
    const uri = this.activeUri;
    const old = this.session; this.session = undefined; old?.stop();
    this.statusText = '屏幕方向改变，正在重新协商全屏尺寸…';
    // Retain only this existing pairing in memory. No discovery or other-device
    // scanning occurs. A stopped/hidden session cancels this timer.
    this.rotationTimer = setTimeout(() => {
      this.rotationTimer = -1;
      if (this.foreground && this.activeUri === uri) this.connect(uri);
    }, 700);
  }
  build() {
    Stack({ alignContent: Alignment.Top }) {
      XComponent({ type: XComponentType.SURFACE, controller: this.controller })
        .width('100%').height('100%').backgroundColor(Color.Black)
      if (!this.active) {
        Column({ space: 18 }) {
          Text('TabLink').fontSize(32).fontWeight(FontWeight.Bold).fontColor('#EAF4FF')
          Text('鸿蒙原生副屏').fontSize(18).fontColor('#AAC9E9')
          TextArea({ placeholder: 'tablink://connect?...', text: this.pairingText })
            .height(112).maxLength(1024).onChange((value: string) => this.pairingText = value)
          Button('连接电脑').enabled(this.surfaceReady && this.foreground)
            .onClick(() => { const uri = this.pairingText; this.pairingText = ''; this.connect(uri); })
          Text(this.statusText).fontSize(15).fontColor('#D4E3F3')
          Row({ space: 12 }) {
            Text('更新方式').fontSize(13).fontColor('#D4E3F3')
            Select([
              { value: '自动更新' },
              { value: '自动下载后手动安装' },
              { value: '从不更新' }
            ]).selected(this.updateModeIndex).onSelect((index: number) => this.chooseUpdateMode(index))
          }.width('100%')
          Text(this.updateModeHint()).fontSize(12).fontColor('#A9BDD2')
          Text(this.updateText).fontSize(13).fontColor('#A9BDD2')
          Button('立即检查更新').enabled(this.updateModeIndex !== 2)
            .onClick(() => this.updateManager?.checkNow())
          if (this.updateInstallerUrl.length > 0) {
            Button('前往应用市场更新').onClick(() => {
              this.updateManager?.openInstaller().catch((_error: Error) => {
                this.updateText = '正式版更新：无法打开系统应用市场'; this.updateInstallerUrl = '';
              });
            })
          }
          Text('同一 Wi-Fi 或已建立的 USB 网络连接。每台设备使用电脑分配的独立连接信息。')
            .fontSize(13).fontColor('#A9BDD2')
          Text('硬件解码提交可统计；实际屏幕呈现仍需真机验证。')
            .fontSize(12).fontColor('#E2BE78')
        }.padding(28).width('90%').constraintSize({ maxWidth: 640 })
          .backgroundColor('#F0101B31').borderRadius(20).margin({ top: 56 })
      } else {
        Row({ space: 12 }) {
          Text(this.statusText).fontSize(12).fontColor(Color.White).layoutWeight(1)
          Button('停止').fontSize(13).onClick(() => this.stop())
        }.padding(10).width('100%').backgroundColor('#A0000000')
      }
    }.width('100%').height('100%').backgroundColor(Color.Black)
      .expandSafeArea([SafeAreaType.SYSTEM], [SafeAreaEdge.TOP, SafeAreaEdge.BOTTOM])
  }
}

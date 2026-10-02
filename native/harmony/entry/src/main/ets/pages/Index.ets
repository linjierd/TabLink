import { display } from '@kit.ArkUI';
import { common } from '@kit.AbilityKit';
import { Session } from '../protocol/Session';
import { readDisplayProfile } from '../protocol/DisplayProfile';
import { StableUpdateManager, StableUpdateStatus } from '../update/StableUpdateManager';
import {
  StableUpdateMode, UPDATE_MODE_AUTOMATIC, UPDATE_MODE_DOWNLOAD_THEN_ASK
} from '../update/StableUpdatePolicy';
import {
  AppLanguage, AppLocalization, LANGUAGE_ENGLISH, LANGUAGE_SYSTEM, LANGUAGE_ZH_HANS
} from '../localization/AppLocalization';

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
  @State private statusText: string = '';
  @State private active: boolean = false;
  @State private surfaceReady: boolean = false;
  @State private updateText: string = '';
  @State private updateInstallerUrl: string = '';
  @State private updateModeIndex: number = 0;
  @State private languageModeIndex: number = 0;
  @State private languageRevision: number = 0;
  @State private languageNotice: string = '';
  @StorageLink('foreground') @Watch('onForegroundChanged') private foreground: boolean = true;
  private controller: VideoSurfaceController = new VideoSurfaceController();
  private surfaceId: string = '';
  private session: Session | undefined = undefined;
  private activeUri: string = '';
  private profileKey: string = '';
  private rotationTimer: number = -1;
  private displayChanged: (id: number) => void = (_id: number) => this.onDisplayChanged();
  private updateManager: StableUpdateManager | undefined = undefined;
  private localization: AppLocalization | undefined = undefined;
  private lastUpdateStatus: StableUpdateStatus | undefined = undefined;
  private lastStatusKey: string = 'status_paste_pairing';
  private lastStatusArguments: string[] = [];
  private updateModeSelectionRevision: number = 0;
  private languageSelectionRevision: number = 0;
  private appearanceRevision: number = 0;

  aboutToAppear(): void {
    const revision: number = ++this.appearanceRevision;
    this.controller.created = (id: string) => { this.surfaceId = id; this.surfaceReady = true; };
    this.controller.destroyed = () => { this.surfaceId = ''; this.surfaceReady = false; this.stop('status_surface_closed'); };
    display.on('change', this.displayChanged);
    this.localization = new AppLocalization(getContext(this) as common.UIAbilityContext);
    this.localization.initialize().then((language: AppLanguage) => {
      if (revision !== this.appearanceRevision) return;
      this.languageModeIndex = this.languageIndex(language);
      this.languageRevision++;
      this.relocalizeVisibleState();
      this.startUpdateManager();
    }).catch((_error: Error) => {
      if (revision !== this.appearanceRevision) return;
      this.languageModeIndex = 0;
      this.languageRevision++;
      this.relocalizeVisibleState();
      this.startUpdateManager();
    });
  }
  aboutToDisappear(): void {
    this.appearanceRevision++;
    this.updateModeSelectionRevision++;
    this.languageSelectionRevision++;
    display.off('change', this.displayChanged); this.updateManager?.stop(); this.stop('status_exited');
  }
  onBackPress(): boolean { if (this.active) { this.stop(); return true; } return false; }
  private onForegroundChanged(): void {
    if (this.foreground) {
      this.localization?.refreshSystemLanguage();
      this.languageRevision++;
      this.relocalizeVisibleState();
      this.updateManager?.checkIfNeeded();
    } else this.stop('status_backgrounded');
  }
  private startUpdateManager(): void {
    if (this.updateManager) return;
    this.updateManager = new StableUpdateManager(getContext(this) as common.UIAbilityContext, (status: StableUpdateStatus) => {
      this.lastUpdateStatus = status;
      this.updateText = this.l(status.messageKey, status.arguments_); this.updateInstallerUrl = status.installerUrl;
      this.updateModeIndex = status.mode === UPDATE_MODE_AUTOMATIC ? 0 :
        status.mode === UPDATE_MODE_DOWNLOAD_THEN_ASK ? 1 : 2;
    });
    this.updateManager.start();
  }
  private l(key: string, arguments_: string[] = []): string {
    if ((key === 'session_capture_paused_remote' || key === 'session_host_status_remote') && arguments_.length > 0) {
      return this.localization?.hostStatusText(key, arguments_[0]) ?? key;
    }
    return this.localization?.text(key, arguments_) ?? key;
  }
  private appTitle(): string {
    const _revision: number = this.languageRevision;
    return _revision >= 0 ? 'TabLink' : 'TabLink';
  }
  private setStatus(key: string, arguments_: string[] = []): void {
    this.lastStatusKey = key; this.lastStatusArguments = arguments_; this.statusText = this.l(key, arguments_);
  }
  private relocalizeVisibleState(): void {
    this.statusText = this.l(this.lastStatusKey, this.lastStatusArguments);
    if (this.lastUpdateStatus) this.updateText = this.l(this.lastUpdateStatus.messageKey, this.lastUpdateStatus.arguments_);
    else this.updateText = this.l('update_checking');
    if (this.languageNotice.length > 0) this.languageNotice = this.l('language_save_failed');
  }
  private languageIndex(language: AppLanguage): number {
    if (language === LANGUAGE_ZH_HANS) return 1;
    if (language === LANGUAGE_ENGLISH) return 2;
    return 0;
  }
  private languageForIndex(index: number): AppLanguage {
    if (index === 1) return LANGUAGE_ZH_HANS;
    if (index === 2) return LANGUAGE_ENGLISH;
    return LANGUAGE_SYSTEM;
  }
  private chooseLanguage(index: number): void {
    const manager = this.localization;
    if (!manager) return;
    const requested: AppLanguage = this.languageForIndex(index);
    const revision: number = ++this.languageSelectionRevision;
    this.languageModeIndex = index;
    manager.setLanguage(requested).then((applied: AppLanguage) => {
      if (revision !== this.languageSelectionRevision) return;
      this.languageModeIndex = this.languageIndex(applied);
      this.languageNotice = applied === requested ? '' : this.l('language_save_failed');
      this.languageRevision++;
      this.relocalizeVisibleState();
    }).catch((_error: Error) => {
      if (revision !== this.languageSelectionRevision) return;
      this.languageModeIndex = 0; this.languageNotice = this.l('language_save_failed');
      this.languageRevision++;
      this.relocalizeVisibleState();
    });
  }
  private updateModeForIndex(index: number): StableUpdateMode {
    if (index === 0) return 'automatic';
    if (index === 1) return 'downloadThenAsk';
    return 'never';
  }
  private updateModeHint(): string {
    if (this.updateModeIndex === 0) return this.l('update_hint_automatic');
    if (this.updateModeIndex === 1) return this.l('update_hint_download_then_ask');
    return this.l('update_hint_never');
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
      this.lastUpdateStatus = new StableUpdateStatus('update_save_failed');
      this.updateText = this.l('update_save_failed');
      this.updateInstallerUrl = '';
    });
  }
  private stop(messageKey: string = 'status_stopped'): void {
    this.activeUri = '';
    if (this.rotationTimer !== -1) { clearTimeout(this.rotationTimer); this.rotationTimer = -1; }
    const previous = this.session; this.session = undefined;
    previous?.stop(); this.active = false; this.setStatus(messageKey);
  }
  private async connect(uri: string): Promise<void> {
    if (!this.surfaceReady || !this.foreground) { this.setStatus('status_wait_surface'); return; }
    this.stop();
    try {
      this.profileKey = JSON.stringify(readDisplayProfile());
      this.activeUri = uri;
      const current = new Session(this.surfaceId, (messageKey: string, arguments_: string[], isActive: boolean) => {
        if (this.session !== current) return;
        this.setStatus(messageKey, arguments_); this.active = isActive;
        if (!isActive) { this.session = undefined; this.activeUri = ''; }
      });
      this.session = current;
      this.active = true;
      await current.connect(uri);
    } catch (_error) {
      // Session emits a localized, credential-free failure before it stops.
    }
  }
  private onDisplayChanged(): void {
    if (!this.active || !this.activeUri || !this.foreground) return;
    let next: string;
    try { next = JSON.stringify(readDisplayProfile()); }
    catch (_error) { this.stop('session_invalid_data'); return; }
    if (next === this.profileKey) return;
    this.profileKey = next;
    if (this.rotationTimer !== -1) clearTimeout(this.rotationTimer);
    const uri = this.activeUri;
    const old = this.session; this.session = undefined; old?.stop();
    this.setStatus('status_rotating');
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
          Text(this.appTitle()).fontSize(32).fontWeight(FontWeight.Bold).fontColor('#EAF4FF')
          Text(this.l('app_subtitle')).fontSize(18).fontColor('#AAC9E9')
          TextArea({ placeholder: this.l('pairing_placeholder'), text: this.pairingText })
            .height(112).maxLength(1024).onChange((value: string) => this.pairingText = value)
          Button(this.l('action_connect')).enabled(this.surfaceReady && this.foreground)
            .onClick(() => { const uri = this.pairingText; this.pairingText = ''; this.connect(uri); })
          Text(this.statusText).fontSize(15).fontColor('#D4E3F3')
          Row({ space: 12 }) {
            Text(this.l('language_label')).fontSize(13).fontColor('#D4E3F3')
            Select([
              { value: this.l('language_system') },
              { value: this.l('language_zh_hans') },
              { value: this.l('language_english') }
            ]).selected(this.languageModeIndex).onSelect((index: number) => this.chooseLanguage(index))
          }.width('100%')
          if (this.languageNotice.length > 0) {
            Text(this.languageNotice).fontSize(12).fontColor('#E2BE78')
          }
          Row({ space: 12 }) {
            Text(this.l('update_mode_label')).fontSize(13).fontColor('#D4E3F3')
            Select([
              { value: this.l('update_mode_automatic') },
              { value: this.l('update_mode_download_then_ask') },
              { value: this.l('update_mode_never') }
            ]).selected(this.updateModeIndex).onSelect((index: number) => this.chooseUpdateMode(index))
          }.width('100%')
          Text(this.updateModeHint()).fontSize(12).fontColor('#A9BDD2')
          Text(this.updateText).fontSize(13).fontColor('#A9BDD2')
          Button(this.l('action_check_update')).enabled(this.updateModeIndex !== 2)
            .onClick(() => this.updateManager?.checkNow())
          if (this.updateInstallerUrl.length > 0) {
            Button(this.l('action_open_store')).onClick(() => {
              this.updateManager?.openInstaller().catch((_error: Error) => {
                this.lastUpdateStatus = new StableUpdateStatus('update_open_store_failed');
                this.updateText = this.l('update_open_store_failed'); this.updateInstallerUrl = '';
              });
            })
          }
          Text(this.l('connection_hint')).fontSize(13).fontColor('#A9BDD2')
          Text(this.l('decoder_evidence')).fontSize(12).fontColor('#E2BE78')
        }.padding(28).width('90%').constraintSize({ maxWidth: 640 })
          .backgroundColor('#F0101B31').borderRadius(20).margin({ top: 56 })
      } else {
        Row({ space: 12 }) {
          Text(this.statusText).fontSize(12).fontColor(Color.White).layoutWeight(1)
          Button(this.l('action_stop')).fontSize(13).onClick(() => this.stop())
        }.padding(10).width('100%').backgroundColor('#A0000000')
      }
    }.width('100%').height('100%').backgroundColor(Color.Black)
      .expandSafeArea([SafeAreaType.SYSTEM], [SafeAreaEdge.TOP, SafeAreaEdge.BOTTOM])
  }
}

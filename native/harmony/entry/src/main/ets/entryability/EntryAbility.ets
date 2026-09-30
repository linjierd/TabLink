import { UIAbility } from '@kit.AbilityKit';
import { window } from '@kit.ArkUI';
import { BusinessError } from '@kit.BasicServicesKit';

export default class EntryAbility extends UIAbility {
  private mainWindow: window.Window | undefined = undefined;
  onCreate(): void { AppStorage.setOrCreate('foreground', true); }
  onWindowStageCreate(stage: window.WindowStage): void { this.prepareWindow(stage); }
  private async prepareWindow(stage: window.WindowStage): Promise<void> {
    try {
      this.mainWindow = await stage.getMainWindow();
      await this.mainWindow.setWindowLayoutFullScreen(true);
      await this.mainWindow.setWindowSystemBarEnable([]);
      await this.mainWindow.setWindowKeepScreenOn(true);
      await stage.loadContent('pages/Index');
    } catch (_error) {
      // Load the connection page even if the device refuses a window option.
      // Profile validation remains active; no success or presentation is invented.
      stage.loadContent('pages/Index').catch((_failure: BusinessError) => {});
    }
  }
  onForeground(): void {
    AppStorage.setOrCreate('foreground', true);
    this.mainWindow?.setWindowKeepScreenOn(true).catch((_error: BusinessError) => {});
  }
  onBackground(): void {
    // ArkUI observes this and releases the socket/decoder instead of silently
    // keeping a remote Windows output active while the app is hidden.
    AppStorage.setOrCreate('foreground', false);
    this.mainWindow?.setWindowKeepScreenOn(false).catch((_error: BusinessError) => {});
  }
  onWindowStageDestroy(): void { AppStorage.setOrCreate('foreground', false); this.mainWindow = undefined; }
}

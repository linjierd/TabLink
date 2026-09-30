import { display } from '@kit.ArkUI';

export class DisplayMode {
  width: number = 0;
  height: number = 0;
  refreshRate: number = 60;
  modeId: number = 0;
}
export class DisplayProfile {
  width: number = 0;
  height: number = 0;
  rotation: number = 0;
  activeModeId: number = 0;
  refreshRate: number = 60;
  nativeWidth: number = 0;
  nativeHeight: number = 0;
  supportedModes: DisplayMode[] = [];
  clientPlatform: string = 'harmony';
  progressEvidence: string = 'render-submitted';
}
export function readDisplayProfile(): DisplayProfile {
  const physical = display.getDefaultDisplaySync();
  const profile = new DisplayProfile();
  profile.width = physical.width; profile.height = physical.height;
  profile.rotation = physical.rotation;
  profile.refreshRate = physical.refreshRate;
  profile.nativeWidth = (physical.rotation % 2) === 1 ? physical.height : physical.width;
  profile.nativeHeight = (physical.rotation % 2) === 1 ? physical.width : physical.height;
  if (profile.width < 240 || profile.height < 240 || profile.width > 8192 || profile.height > 8192 ||
    profile.width % 2 !== 0 || profile.height % 2 !== 0 || profile.refreshRate < 24 || profile.refreshRate > 240) {
    throw new Error('当前显示尺寸或刷新率不适合原生副屏');
  }
  // API 12 exposes current refreshRate, not a public exhaustive mode list.
  // Advertise only the observed physical mode instead of inventing 90/120 Hz.
  const mode = new DisplayMode();
  mode.width = profile.nativeWidth; mode.height = profile.nativeHeight; mode.refreshRate = profile.refreshRate;
  profile.supportedModes = [mode];
  return profile;
}

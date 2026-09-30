import { common } from '@kit.AbilityKit';
import { preferences } from '@kit.ArkData';
import { http } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import {
  includedByDigest, isAllowedHarmonyInstallerUrl, parseSignedEnvelope, parseVerifiedPayload, selectHarmonyUpdate,
  STABLE_MANIFEST_URL, StableArtifact
} from './StableUpdatePolicy';
import { createStableCohortId, stableCohortDigest, verifyStableEnvelope } from './StableUpdateCrypto';

const CURRENT_VERSION: string = '0.8.0';
const CURRENT_BUILD: number = 800;
const CHECK_INTERVAL_MS: number = 6 * 60 * 60 * 1000;
const MAX_ENVELOPE_BYTES: number = 384 * 1024;

export class StableUpdateStatus {
  text: string;
  installerUrl: string;
  constructor(text: string, installerUrl: string = '') { this.text = text; this.installerUrl = installerUrl; }
}

/** Checks in the background and exposes only panel state. It never installs a HAP. */
export class StableUpdateManager {
  private context: common.UIAbilityContext;
  private changed: (status: StableUpdateStatus) => void;
  private timer: number = -1;
  private checking: boolean = false;
  private lastSuccessMs: number = 0;
  private installerUrl: string = '';

  constructor(context: common.UIAbilityContext, changed: (status: StableUpdateStatus) => void) {
    this.context = context; this.changed = changed;
  }

  start(): void {
    if (this.timer === -1) this.timer = setInterval(() => this.checkIfNeeded(), 60 * 60 * 1000);
    this.checkIfNeeded(true);
  }

  stop(): void {
    if (this.timer !== -1) { clearInterval(this.timer); this.timer = -1; }
  }

  checkIfNeeded(force: boolean = false): void {
    if (this.checking || (!force && Date.now() - this.lastSuccessMs < CHECK_INTERVAL_MS)) return;
    this.checking = true;
    this.runCheck().catch((_error: Error) => {
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('正式版更新：暂时无法完成安全检查'));
    }).finally(() => { this.checking = false; });
  }

  async openInstaller(): Promise<void> {
    if (!this.installerUrl) return;
    // The signed HTTPS AppGallery URL is handed to the system. AppGallery (or
    // its registered system handler) owns installation and automatic updates.
    await this.context.startAbility({ action: 'ohos.want.action.viewData', uri: this.installerUrl });
  }

  private async runCheck(): Promise<void> {
    const client = http.createHttp();
    try {
      const response = await client.request(STABLE_MANIFEST_URL, {
        method: http.RequestMethod.GET,
        header: { 'Accept': 'application/json' },
        connectTimeout: 30000,
        readTimeout: 30000,
        expectDataType: http.HttpDataType.STRING
      });
      if (response.responseCode !== http.ResponseCode.OK || typeof response.result !== 'string') throw new Error('更新服务器响应无效');
      const envelopeText: string = response.result;
      const encoder: util.TextEncoder = new util.TextEncoder();
      if (encoder.encodeInto(envelopeText).length > MAX_ENVELOPE_BYTES) throw new Error('更新清单过大');

      const envelope = parseSignedEnvelope(envelopeText);
      const verifiedPayloadText = await verifyStableEnvelope(envelope);
      // Parse artifact URLs and versions only after the pinned signature passes.
      const manifest = parseVerifiedPayload(verifiedPayloadText);
      const cohortId = await this.cohortId();
      const digest = await stableCohortDigest(cohortId, manifest.releaseId);
      const included = includedByDigest(digest, manifest.rolloutPercentage);
      const artifact = selectHarmonyUpdate(manifest, CURRENT_VERSION, CURRENT_BUILD, included);
      this.lastSuccessMs = Date.now();

      const published = manifest.artifacts.find((value: StableArtifact) => value.platform === 'harmony');
      if (!published) {
        this.installerUrl = '';
        this.changed(new StableUpdateStatus('正式版更新：HarmonyOS NEXT 版本尚未发布'));
      } else if (!artifact) {
        this.installerUrl = '';
        this.changed(new StableUpdateStatus(`正式版更新：当前 ${CURRENT_VERSION} 已是最新`));
      } else if (!artifact.installerUrl || !isAllowedHarmonyInstallerUrl(artifact.installerUrl)) {
        this.installerUrl = '';
        this.changed(new StableUpdateStatus('正式版更新：新版缺少可信应用市场地址'));
      } else {
        this.installerUrl = artifact.installerUrl;
        this.changed(new StableUpdateStatus(`正式版更新：应用市场已有 ${artifact.version.text}`, artifact.installerUrl));
      }
    } finally {
      client.destroy();
    }
  }

  private async cohortId(): Promise<string> {
    const store = await preferences.getPreferences(this.context, { name: 'stable-update' });
    const existing = await store.get('cohort-id', '') as string;
    if (/^[0-9a-f]{32}$/.test(existing)) return existing;
    const created = createStableCohortId();
    await store.put('cohort-id', created); await store.flush();
    return created;
  }

}

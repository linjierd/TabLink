import { common } from '@kit.AbilityKit';
import { preferences } from '@kit.ArkData';
import { http } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import {
  compareManifestWithFloor, compareManifestWithLegacyFloor, comparePublishedAtUtc, includedByDigest,
  isAllowedHarmonyInstallerUrl, newestConflictPublishedAt, parseSignedEnvelope, parseStoredDecisionFloor,
  parseStoredUpdateMode, parseVerifiedPayload, requiresNewerUpdateProtocol, selectHarmonyUpdate,
  selectNewestVerifiedManifest, serializeConflictFloor, serializeDecisionFloor,
  STABLE_MANIFEST_FALLBACK_URL, STABLE_MANIFEST_URL, StableArtifact, StableUpdateMode,
  StableUpdatePreferenceQueue, UPDATE_MODE_AUTOMATIC, UPDATE_MODE_DOWNLOAD_THEN_ASK, UPDATE_MODE_NEVER,
  UPDATE_PROTOCOL_VERSION, VerifiedStableManifest
} from './StableUpdatePolicy';
import {
  createStableCohortId, stableCohortDigest, stableDecisionDigest, verifyStableEnvelope
} from './StableUpdateCrypto';

const CURRENT_VERSION: string = '0.8.0';
const CURRENT_BUILD: number = 800;
const CHECK_INTERVAL_MS: number = 6 * 60 * 60 * 1000;
const MAX_ENVELOPE_BYTES: number = 384 * 1024;
const UPDATE_PREFERENCES_NAME: string = 'stable-update';
const UPDATE_MODE_KEY: string = 'update-mode';
const FLOOR_RECORD_KEY: string = 'manifest-floor-v1';
const LEGACY_FLOOR_PUBLISHED_KEY: string = 'manifest-floor-published';
const LEGACY_FLOOR_RELEASE_KEY: string = 'manifest-floor-release';
const PREFERENCE_WRITES: StableUpdatePreferenceQueue = new StableUpdatePreferenceQueue();

export class StableUpdateStatus {
  messageKey: string;
  arguments_: string[];
  installerUrl: string;
  mode: StableUpdateMode;
  constructor(messageKey: string, arguments_: string[] = [], installerUrl: string = '', mode: StableUpdateMode = 'never') {
    this.messageKey = messageKey; this.arguments_ = arguments_; this.installerUrl = installerUrl; this.mode = mode;
  }
}

/**
 * Checks signed stable metadata in the background and exposes only panel state.
 * It never downloads or installs a HAP. AppGallery and the user's system store
 * settings remain the only installation mechanism.
 */
export class StableUpdateManager {
  private context: common.UIAbilityContext;
  private changed: (status: StableUpdateStatus) => void;
  private timer: number = -1;
  private checking: boolean = false;
  private initialized: boolean = false;
  private stopped: boolean = false;
  private lastSuccessMs: number = 0;
  private installerUrl: string = '';
  private mode: StableUpdateMode = 'never';
  private generation: number = 0;
  private pendingCheck: boolean = false;
  private cancelActiveRequest: (() => void) | undefined = undefined;
  private modeRequestRevision: number = 0;
  private runtimeConflictFloorUtc: string = '';

  constructor(context: common.UIAbilityContext, changed: (status: StableUpdateStatus) => void) {
    this.context = context; this.changed = changed;
  }

  start(): void {
    this.stopped = false;
    if (this.timer === -1) this.timer = setInterval(() => this.checkIfNeeded(), 60 * 60 * 1000);
    const generation = this.generation;
    this.initialize(generation).catch((_error: Error) => {
      if (this.stopped || generation !== this.generation) return;
      this.initialized = true;
      this.mode = 'never';
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_read_failed', [], '', this.mode));
    });
  }

  stop(): void {
    this.stopped = true;
    this.generation++;
    this.modeRequestRevision++;
    this.pendingCheck = false;
    this.cancelActiveRequest?.();
    this.cancelActiveRequest = undefined;
    if (this.timer !== -1) { clearInterval(this.timer); this.timer = -1; }
  }

  currentMode(): StableUpdateMode { return this.mode; }

  async setMode(mode: StableUpdateMode): Promise<StableUpdateMode> {
    if (mode !== UPDATE_MODE_AUTOMATIC && mode !== UPDATE_MODE_DOWNLOAD_THEN_ASK && mode !== UPDATE_MODE_NEVER) {
      mode = 'never';
    }
    const requested: StableUpdateMode = mode;
    const revision: number = ++this.modeRequestRevision;
    // Disable the current generation before waiting for storage. A requested
    // enabled mode is applied only after its exact value is durably flushed.
    this.mode = 'never';
    this.initialized = true;
    this.installerUrl = '';
    this.generation++;
    this.cancelActiveRequest?.();
    this.cancelActiveRequest = undefined;
    this.pendingCheck = false;
    const result = await PREFERENCE_WRITES.enqueueMode(requested, async (selected: StableUpdateMode): Promise<boolean> => {
      try {
        const store = await preferences.getPreferences(this.context, { name: UPDATE_PREFERENCES_NAME });
        try {
          await store.put(UPDATE_MODE_KEY, selected);
          await store.flush();
          return true;
        } catch (_error) {
          // If writing the requested mode fails, make a best-effort durable
          // Never write inside the same serialized operation.
          try {
            await store.put(UPDATE_MODE_KEY, UPDATE_MODE_NEVER);
            await store.flush();
          } catch (_ignored) { }
          return false;
        }
      } catch (_error) { return false; }
    });
    if (this.stopped || revision !== this.modeRequestRevision || !PREFERENCE_WRITES.isLatestMode(result.sequence)) return this.mode;
    if (!result.saved) {
      this.mode = 'never';
      this.pendingCheck = false;
      this.generation++;
      this.changed(new StableUpdateStatus('update_save_failed', [], '', this.mode));
      return this.mode;
    }
    this.mode = requested;
    if (requested === UPDATE_MODE_NEVER) {
      this.changed(new StableUpdateStatus('update_disabled', [], '', requested));
      return this.mode;
    }
    this.lastSuccessMs = 0;
    const messageKey = requested === UPDATE_MODE_AUTOMATIC ? 'update_checking_store' : 'update_preparing_manual';
    this.changed(new StableUpdateStatus(messageKey, [], '', requested));
    this.checkIfNeeded(true);
    return this.mode;
  }

  checkIfNeeded(force: boolean = false): void {
    if (!this.initialized || this.stopped || this.mode === UPDATE_MODE_NEVER ||
      (!force && Date.now() - this.lastSuccessMs < CHECK_INTERVAL_MS)) return;
    if (this.checking) { if (force) this.pendingCheck = true; return; }
    this.checking = true;
    const generation = ++this.generation;
    this.runCheck(generation).catch((_error: Error) => {
      if (!this.isCurrent(generation)) return;
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_check_failed', [], '', this.mode));
    }).finally(() => {
      this.checking = false;
      if (this.pendingCheck && !this.stopped && this.mode !== UPDATE_MODE_NEVER) {
        this.pendingCheck = false;
        this.checkIfNeeded(true);
      }
    });
  }

  checkNow(): void {
    if (!this.initialized || this.mode === UPDATE_MODE_NEVER) {
      this.changed(new StableUpdateStatus('update_disabled', [], '', this.mode));
      return;
    }
    this.lastSuccessMs = 0;
    this.checkIfNeeded(true);
  }

  async openInstaller(): Promise<void> {
    if (this.mode === UPDATE_MODE_NEVER || !this.installerUrl) return;
    // Called only by the user's explicit button action. AppGallery (or its
    // registered system handler) owns confirmation, installation and automatic updates.
    await this.context.startAbility({ action: 'ohos.want.action.viewData', uri: this.installerUrl });
  }

  private async initialize(generation: number): Promise<void> {
    const modeSequence: number = PREFERENCE_WRITES.currentModeSequence();
    const parsed = await PREFERENCE_WRITES.runExclusive(async (): Promise<StableUpdateMode> => {
      const store = await preferences.getPreferences(this.context, { name: UPDATE_PREFERENCES_NAME });
      const exists = await store.has(UPDATE_MODE_KEY);
      const stored = exists ? await store.get(UPDATE_MODE_KEY, '') : '';
      const selected = parseStoredUpdateMode(stored, exists);
      if (exists && stored !== selected) {
        // Canonicalize damaged or unknown values to the fail-closed choice.
        await store.put(UPDATE_MODE_KEY, UPDATE_MODE_NEVER);
        await store.flush();
      }
      return selected;
    });
    if (this.stopped || generation !== this.generation) return;
    if (!PREFERENCE_WRITES.isLatestMode(modeSequence)) {
      // Another manager instance queued a newer choice while this read was in
      // flight. Re-read behind that serialized write instead of applying stale
      // preferences or leaving this instance uninitialized.
      await this.initialize(generation);
      return;
    }
    this.mode = parsed;
    this.initialized = true;
    if (this.mode === UPDATE_MODE_NEVER) {
      this.changed(new StableUpdateStatus('update_disabled', [], '', this.mode));
      return;
    }
    this.changed(new StableUpdateStatus('update_checking', [], '', this.mode));
    this.checkIfNeeded(true);
  }

  private isCurrent(generation: number): boolean {
    return !this.stopped && this.initialized && this.mode !== UPDATE_MODE_NEVER && generation === this.generation;
  }

  private async runCheck(generation: number): Promise<void> {
    const candidates: VerifiedStableManifest[] = [];
    const sources: string[] = [STABLE_MANIFEST_URL, STABLE_MANIFEST_FALLBACK_URL];
    for (let index = 0; index < sources.length; index++) {
      if (!this.isCurrent(generation)) return;
      try {
        candidates.push(await this.fetchVerifiedManifest(sources[index]));
      } catch (_error) {
        // One failed transport or signature does not make the other mirror trusted.
        // At least one independently verified pinned-signer payload is still required.
      }
    }
    if (!this.isCurrent(generation)) return;
    const conflictPublishedAtUtc = newestConflictPublishedAt(candidates);
    if (conflictPublishedAtUtc) {
      await this.acceptConflictFloor(conflictPublishedAtUtc);
      throw new Error('同一发布时间的已签名更新决定冲突；已阻断该时间及更早决定');
    }
    const selected = selectNewestVerifiedManifest(candidates);
    await this.acceptAntiReplayFloor(selected);
    if (!this.isCurrent(generation)) return;

    const manifest = selected.manifest;
    if (requiresNewerUpdateProtocol(manifest)) {
      this.lastSuccessMs = Date.now();
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_protocol_required',
        [manifest.minimumProtocolVersion.toString(), UPDATE_PROTOCOL_VERSION.toString()], '', this.mode));
      return;
    }
    const cohortId = await this.cohortId();
    const digest = await stableCohortDigest(cohortId, manifest.releaseId);
    const included = includedByDigest(digest, manifest.rolloutPercentage);
    const artifact = selectHarmonyUpdate(manifest, CURRENT_VERSION, CURRENT_BUILD, included);
    this.lastSuccessMs = Date.now();

    const published = manifest.artifacts.find((value: StableArtifact) => value.platform === 'harmony');
    if (manifest.rolloutPercentage === 0) {
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_paused', [], '', this.mode));
    } else if (!published) {
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_not_published', [], '', this.mode));
    } else if (!included) {
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_cohort_deferred', [], '', this.mode));
    } else if (!artifact) {
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_current', [CURRENT_VERSION], '', this.mode));
    } else if (!artifact.installerUrl || !isAllowedHarmonyInstallerUrl(artifact.installerUrl)) {
      this.installerUrl = '';
      this.changed(new StableUpdateStatus('update_untrusted_store', [], '', this.mode));
    } else {
      this.installerUrl = artifact.installerUrl;
      const messageKey = this.mode === UPDATE_MODE_AUTOMATIC ?
        'update_available_automatic' : 'update_available_manual';
      this.changed(new StableUpdateStatus(messageKey, [artifact.version.text], artifact.installerUrl, this.mode));
    }
  }

  private async fetchVerifiedManifest(sourceUrl: string): Promise<VerifiedStableManifest> {
    const client = http.createHttp();
    this.cancelActiveRequest = () => client.destroy();
    try {
      const response = await client.request(sourceUrl, {
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
      // Artifact URLs and release decisions are parsed only after the pinned
      // public key verifies the exact payload bytes.
      const manifest = parseVerifiedPayload(verifiedPayloadText);
      return { sourceUrl, payloadText: verifiedPayloadText, manifest };
    } finally {
      client.destroy();
      this.cancelActiveRequest = undefined;
    }
  }

  private async acceptAntiReplayFloor(candidate: VerifiedStableManifest): Promise<void> {
    const decisionSha256: string = await stableDecisionDigest(candidate.manifest);
    if (this.runtimeConflictFloorUtc &&
      comparePublishedAtUtc(candidate.manifest.publishedAtUtc, this.runtimeConflictFloorUtc) <= 0) {
      throw new Error('更新清单未晚于本次运行已记录的签名冲突');
    }
    await PREFERENCE_WRITES.runExclusive(async (): Promise<void> => {
      const store = await preferences.getPreferences(this.context, { name: UPDATE_PREFERENCES_NAME });
      const hasRecord = await store.has(FLOOR_RECORD_KEY);
      if (hasRecord) {
        const floor = parseStoredDecisionFloor(await store.get(FLOOR_RECORD_KEY, ''));
        const comparison = compareManifestWithFloor(candidate.manifest, decisionSha256, floor);
        if (comparison === 0) return;
      } else {
        // Migrate the preview implementation without weakening its timestamp.
        // A same-time legacy record cannot authenticate the full signed decision,
        // so only a strictly newer publication may replace it.
        const hasPublished = await store.has(LEGACY_FLOOR_PUBLISHED_KEY);
        const hasRelease = await store.has(LEGACY_FLOOR_RELEASE_KEY);
        if (hasPublished !== hasRelease) throw new Error('更新防回退记录不完整');
        if (hasPublished) {
          const published = await store.get(LEGACY_FLOOR_PUBLISHED_KEY, '') as string;
          const release = await store.get(LEGACY_FLOOR_RELEASE_KEY, '') as string;
          compareManifestWithLegacyFloor(candidate.manifest, published, release);
        }
      }
      await store.put(FLOOR_RECORD_KEY, serializeDecisionFloor(candidate.manifest.publishedAtUtc, decisionSha256));
      await store.flush();
    });
    if (this.runtimeConflictFloorUtc &&
      comparePublishedAtUtc(candidate.manifest.publishedAtUtc, this.runtimeConflictFloorUtc) > 0) {
      this.runtimeConflictFloorUtc = '';
    }
  }

  private async acceptConflictFloor(publishedAtUtc: string): Promise<void> {
    if (!this.runtimeConflictFloorUtc || comparePublishedAtUtc(publishedAtUtc, this.runtimeConflictFloorUtc) > 0) {
      this.runtimeConflictFloorUtc = publishedAtUtc;
    }
    await PREFERENCE_WRITES.runExclusive(async (): Promise<void> => {
      const store = await preferences.getPreferences(this.context, { name: UPDATE_PREFERENCES_NAME });
      if (await store.has(FLOOR_RECORD_KEY)) {
        const floor = parseStoredDecisionFloor(await store.get(FLOOR_RECORD_KEY, ''));
        const comparison = comparePublishedAtUtc(floor.publishedAtUtc, publishedAtUtc);
        if (comparison > 0 || comparison === 0 && floor.blocked) return;
      } else {
        const hasPublished = await store.has(LEGACY_FLOOR_PUBLISHED_KEY);
        const hasRelease = await store.has(LEGACY_FLOOR_RELEASE_KEY);
        if (hasPublished !== hasRelease) throw new Error('更新防回退记录不完整');
        if (hasPublished) {
          const legacyPublished = await store.get(LEGACY_FLOOR_PUBLISHED_KEY, '') as string;
          const legacyRelease = await store.get(LEGACY_FLOOR_RELEASE_KEY, '') as string;
          if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(legacyRelease))
            throw new Error('更新防回退记录损坏');
          if (comparePublishedAtUtc(legacyPublished, publishedAtUtc) > 0) return;
        }
      }
      await store.put(FLOOR_RECORD_KEY, serializeConflictFloor(publishedAtUtc));
      await store.flush();
    });
  }

  private async cohortId(): Promise<string> {
    return PREFERENCE_WRITES.runExclusive(async (): Promise<string> => {
      const store = await preferences.getPreferences(this.context, { name: UPDATE_PREFERENCES_NAME });
      const existing = await store.get('cohort-id', '') as string;
      if (/^[0-9a-f]{32}$/.test(existing)) return existing;
      const created = createStableCohortId();
      await store.put('cohort-id', created); await store.flush();
      return created;
    });
  }
}

export const STABLE_MANIFEST_URL: string = 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json';
export const STABLE_MANIFEST_FALLBACK_URL: string = 'https://github.com/linjierd/TabLink/releases/latest/download/manifest.json';
export const STABLE_SIGNER_SPKI_BASE64: string = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A==';
export const UPDATE_PROTOCOL_VERSION: number = 1;

export const UPDATE_MODE_AUTOMATIC: string = 'automatic';
export const UPDATE_MODE_DOWNLOAD_THEN_ASK: string = 'downloadThenAsk';
export const UPDATE_MODE_NEVER: string = 'never';
export type StableUpdateMode = 'automatic' | 'downloadThenAsk' | 'never';
const BLOCKED_DECISION_SHA256: string = '0000000000000000000000000000000000000000000000000000000000000000';

export interface SignedEnvelopeText { payload: string; signature: string; }
export interface StableVersion { major: number; minor: number; patch: number; text: string; }
export interface StableArtifact {
  platform: string; version: StableVersion; build: number; url: string; size: number;
  sha256: string; installerUrl?: string; notes?: string;
}
export interface StableManifest {
  releaseId: string; publishedAtUtc: string; rolloutPercentage: number;
  minimumProtocolVersion: number; artifacts: StableArtifact[];
}

export interface VerifiedStableManifest {
  sourceUrl: string;
  payloadText: string;
  manifest: StableManifest;
}

export interface StableDecisionFloor {
  schema: number;
  publishedAtUtc: string;
  decisionSha256: string;
  blocked: boolean;
}

export interface StableUpdateModeWriteResult {
  sequence: number;
  mode: StableUpdateMode;
  saved: boolean;
}

/**
 * Preferences exposes asynchronous put/flush operations. All manager instances
 * share one of these queues so an older save can never finish after a newer
 * selection and replace it on disk.
 */
export class StableUpdatePreferenceQueue {
  private tail: Promise<void> = Promise.resolve();
  private sequence: number = 0;

  runExclusive<T>(operation: () => Promise<T>): Promise<T> {
    const work: Promise<T> = this.tail.then(operation);
    this.tail = work.then(() => {}, () => {});
    return work;
  }

  enqueueMode(mode: StableUpdateMode, persist: (requested: StableUpdateMode) => Promise<boolean>): Promise<StableUpdateModeWriteResult> {
    const sequence = ++this.sequence;
    return this.runExclusive(async (): Promise<StableUpdateModeWriteResult> => {
      let saved: boolean = false;
      try { saved = await persist(mode); }
      catch (_error) { saved = false; }
      return { sequence, mode, saved };
    });
  }

  currentModeSequence(): number { return this.sequence; }
  isLatestMode(sequence: number): boolean { return sequence === this.sequence; }
}

/** An absent preference is the first-run default. Any persisted unknown value fails closed. */
export function parseStoredUpdateMode(value: unknown, exists: boolean): StableUpdateMode {
  if (!exists) return 'automatic';
  if (value === UPDATE_MODE_AUTOMATIC || value === UPDATE_MODE_DOWNLOAD_THEN_ASK || value === UPDATE_MODE_NEVER) {
    return value as StableUpdateMode;
  }
  return 'never';
}

/**
 * Both transports are untrusted mirrors. Only already verified payloads enter
 * this function. The newest signed publication wins. Two different signed
 * decisions with the same publication time are rejected instead of choosing
 * a mirror by order.
 */
export function selectNewestVerifiedManifest(candidates: VerifiedStableManifest[]): VerifiedStableManifest {
  if (candidates.length < 1) throw new Error('没有可用的已签名更新清单');
  let newest: VerifiedStableManifest = candidates[0];
  candidates.slice(1).forEach((candidate: VerifiedStableManifest) => {
    if (candidate.manifest.publishedAtUtc > newest.manifest.publishedAtUtc) newest = candidate;
  });
  if (newestConflictPublishedAt(candidates)) throw new Error('同一发布时间存在冲突的已签名更新决定');
  return newest;
}

export function newestConflictPublishedAt(candidates: VerifiedStableManifest[]): string {
  if (candidates.length < 2) return '';
  let newest: VerifiedStableManifest = candidates[0];
  candidates.slice(1).forEach((candidate: VerifiedStableManifest) => {
    if (candidate.manifest.publishedAtUtc > newest.manifest.publishedAtUtc) newest = candidate;
  });
  const publishedAtUtc = newest.manifest.publishedAtUtc;
  const decision = canonicalStableDecision(newest.manifest);
  for (const candidate of candidates) {
    if (candidate.manifest.publishedAtUtc === publishedAtUtc &&
      canonicalStableDecision(candidate.manifest) !== decision) return publishedAtUtc;
  }
  return '';
}

/**
 * Canonical signed release decision. Artifact download URLs are mirror
 * locations and are deliberately excluded; installerUrl remains part of the
 * decision because it is the action exposed to the user. Artifact order is not
 * significant in the signed schema, so it is normalized by platform.
 */
export function canonicalStableDecision(manifest: StableManifest): string {
  const artifacts = manifest.artifacts.slice().sort((left: StableArtifact, right: StableArtifact): number =>
    left.platform === right.platform ? 0 : left.platform < right.platform ? -1 : 1);
  return JSON.stringify([
    1,
    'stable',
    manifest.releaseId,
    manifest.publishedAtUtc,
    manifest.rolloutPercentage,
    manifest.minimumProtocolVersion,
    artifacts.map((artifact: StableArtifact) => [
      artifact.platform,
      artifact.version.text,
      artifact.build,
      artifact.size,
      artifact.sha256.toUpperCase(),
      artifact.installerUrl ?? null,
      artifact.notes ?? null
    ])
  ]);
}

export function parseStoredDecisionFloor(value: unknown): StableDecisionFloor {
  if (typeof value !== 'string' || value.length < 2 || value.length > 1024) throw new Error('更新防回退记录损坏');
  try {
    rejectDuplicateObjectKeys(value);
    const parsed = JSON.parse(value) as unknown;
    if (!isRecord(parsed)) throw new Error('更新防回退记录损坏');
    requireExactKeys(parsed, ['schema', 'publishedAtUtc', 'decisionSha256'], ['blocked']);
    if (requireInteger(parsed.schema, 'schema') !== 1) throw new Error('更新防回退记录损坏');
    const publishedAtUtc = requireString(parsed.publishedAtUtc, 'publishedAtUtc');
    parseStrictUtcTimestamp(publishedAtUtc);
    const decisionSha256 = requireString(parsed.decisionSha256, 'decisionSha256').toUpperCase();
    if (!/^[0-9A-F]{64}$/.test(decisionSha256)) throw new Error('更新防回退记录损坏');
    const blocked = Object.prototype.hasOwnProperty.call(parsed, 'blocked') ? parsed.blocked : false;
    if (typeof blocked !== 'boolean') throw new Error('更新防回退记录损坏');
    if (blocked && decisionSha256 !== BLOCKED_DECISION_SHA256) throw new Error('更新防回退记录损坏');
    return { schema: 1, publishedAtUtc, decisionSha256, blocked };
  } catch (_error) { throw new Error('更新防回退记录损坏'); }
}

export function serializeDecisionFloor(publishedAtUtc: string, decisionSha256: string): string {
  parseStrictUtcTimestamp(publishedAtUtc);
  const digest = decisionSha256.toUpperCase();
  if (!/^[0-9A-F]{64}$/.test(digest)) throw new Error('更新决定摘要无效');
  return JSON.stringify({ schema: 1, publishedAtUtc, decisionSha256: digest, blocked: false });
}

export function serializeConflictFloor(publishedAtUtc: string): string {
  parseStrictUtcTimestamp(publishedAtUtc);
  return JSON.stringify({ schema: 1, publishedAtUtc, decisionSha256: BLOCKED_DECISION_SHA256, blocked: true });
}

export function comparePublishedAtUtc(left: string, right: string): number {
  const leftMs = parseStrictUtcTimestamp(left), rightMs = parseStrictUtcTimestamp(right);
  return leftMs === rightMs ? 0 : leftMs < rightMs ? -1 : 1;
}

export function compareManifestWithFloor(manifest: StableManifest, decisionSha256: string, floor: StableDecisionFloor): number {
  const digest = decisionSha256.toUpperCase();
  if (!/^[0-9A-F]{64}$/.test(digest) || floor.schema !== 1 || typeof floor.blocked !== 'boolean' ||
    !/^[0-9A-F]{64}$/.test(floor.decisionSha256) ||
    (floor.blocked && floor.decisionSha256 !== BLOCKED_DECISION_SHA256)) {
    throw new Error('更新防回退记录损坏');
  }
  let floorMs: number;
  try { floorMs = parseStrictUtcTimestamp(floor.publishedAtUtc); }
  catch (_error) { throw new Error('更新防回退记录损坏'); }
  const manifestMs = parseStrictUtcTimestamp(manifest.publishedAtUtc);
  if (floor.blocked) {
    if (manifestMs <= floorMs) throw new Error('更新清单未晚于已记录的签名冲突');
    return 1;
  }
  if (manifestMs < floorMs) throw new Error('拒绝早于已接受版本的更新清单');
  if (manifestMs === floorMs && digest !== floor.decisionSha256) throw new Error('更新防回退记录与清单冲突');
  return manifestMs === floorMs ? 0 : 1;
}

/** A one-way migration gate for the old timestamp/releaseId floor. */
export function compareManifestWithLegacyFloor(manifest: StableManifest, floorPublishedAtUtc: string, floorReleaseId: string): number {
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(floorReleaseId)) throw new Error('更新防回退记录损坏');
  let floorMs: number;
  try { floorMs = parseStrictUtcTimestamp(floorPublishedAtUtc); }
  catch (_error) { throw new Error('更新防回退记录损坏'); }
  const manifestMs = parseStrictUtcTimestamp(manifest.publishedAtUtc);
  if (manifestMs < floorMs) throw new Error('拒绝早于已接受版本的更新清单');
  if (manifestMs === floorMs) throw new Error('旧版更新防回退记录无法验证同一发布时间的完整决定');
  return 1;
}

export function requiresNewerUpdateProtocol(manifest: StableManifest): boolean {
  return manifest.minimumProtocolVersion > UPDATE_PROTOCOL_VERSION;
}

export function parseStableVersion(text: string): StableVersion {
  if (!/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(text)) throw new Error('正式版本号必须是 major.minor.patch');
  const parts = text.split('.').map(value => Number(value));
  if (parts.some(value => !Number.isSafeInteger(value))) throw new Error('正式版本号超出范围');
  return { major: parts[0], minor: parts[1], patch: parts[2], text };
}

export function compareStableVersions(left: StableVersion, right: StableVersion): number {
  if (left.major !== right.major) return left.major < right.major ? -1 : 1;
  if (left.minor !== right.minor) return left.minor < right.minor ? -1 : 1;
  return left.patch === right.patch ? 0 : left.patch < right.patch ? -1 : 1;
}

export function parseSignedEnvelope(text: string): SignedEnvelopeText {
  if (text.length < 2 || text.length > 384 * 1024) throw new Error('更新清单大小无效');
  rejectDuplicateObjectKeys(text);
  const value = JSON.parse(text) as unknown;
  if (!isRecord(value)) throw new Error('签名清单必须是对象');
  requireExactKeys(value, ['payload', 'signature']);
  const payload = requireString(value.payload, 'payload');
  const signature = requireString(value.signature, 'signature');
  if (!isCanonicalBase64(payload) || !isCanonicalBase64(signature)) throw new Error('签名清单必须使用规范 Base64');
  return { payload, signature };
}

export function parseVerifiedPayload(text: string, nowMs: number = Date.now()): StableManifest {
  if (text.length < 2 || text.length > 256 * 1024) throw new Error('更新负载大小无效');
  rejectDuplicateObjectKeys(text);
  const root = JSON.parse(text) as unknown;
  if (!isRecord(root)) throw new Error('更新负载必须是对象');
  requireExactKeys(root, ['schema', 'channel', 'releaseId', 'publishedAtUtc', 'rolloutPercentage', 'minimumProtocolVersion', 'artifacts']);
  if (requireInteger(root.schema, 'schema') !== 1 || root.channel !== 'stable') throw new Error('只接受 schema 1 stable 正式版');
  const releaseId = requireString(root.releaseId, 'releaseId');
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(releaseId)) throw new Error('releaseId 无效');
  const publishedAtUtc = requireString(root.publishedAtUtc, 'publishedAtUtc');
  if (!/^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$/.test(publishedAtUtc)) throw new Error('发布时间必须是整秒 UTC RFC3339');
  const published = parseStrictUtcTimestamp(publishedAtUtc);
  if (published > nowMs + 24 * 3600 * 1000 || published < nowMs - 366 * 24 * 3600 * 1000) throw new Error('发布时间异常或清单已经过期');
  const rolloutPercentage = requireInteger(root.rolloutPercentage, 'rolloutPercentage');
  if (rolloutPercentage < 0 || rolloutPercentage > 100) throw new Error('发布比例无效');
  const minimumProtocolVersion = requireInteger(root.minimumProtocolVersion, 'minimumProtocolVersion');
  if (minimumProtocolVersion < 1) throw new Error('最低协议版本无效');
  if (!Array.isArray(root.artifacts) || root.artifacts.length < 1 || root.artifacts.length > 32) throw new Error('更新安装包列表无效');
  const platforms: Set<string> = new Set();
  const artifacts: StableArtifact[] = root.artifacts.map((raw: unknown) => {
    if (!isRecord(raw)) throw new Error('artifact 必须是对象');
    requireExactKeys(raw, ['platform', 'version', 'build', 'url', 'size', 'sha256'], ['installerUrl', 'notes']);
    const platform = requireString(raw.platform, 'platform');
    if (!['windows-x64', 'android', 'ios', 'harmony'].includes(platform) || platforms.has(platform)) throw new Error('平台无效或重复');
    platforms.add(platform);
    const version = parseStableVersion(requireString(raw.version, 'version'));
    const build = requireInteger(raw.build, 'build'); if (build < 1) throw new Error('构建号无效');
    const url = requireArtifactHttps(requireString(raw.url, 'url'));
    const size = requireInteger(raw.size, 'size'); if (size < 1 || size > 8 * 1024 * 1024 * 1024) throw new Error('安装包大小无效');
    const sha256 = requireString(raw.sha256, 'sha256').toUpperCase(); if (!/^[0-9A-F]{64}$/.test(sha256)) throw new Error('SHA-256 无效');
    const installerUrl = raw.installerUrl === undefined ? undefined : requireArtifactHttps(requireString(raw.installerUrl, 'installerUrl'));
    const notes = raw.notes === undefined ? undefined : requireString(raw.notes, 'notes'); if ((notes?.length ?? 0) > 4096) throw new Error('更新说明过长');
    return { platform, version, build, url, size, sha256, installerUrl, notes };
  });
  return { releaseId, publishedAtUtc, rolloutPercentage, minimumProtocolVersion, artifacts };
}

export function includedByDigest(digest: Uint8Array, percentage: number): boolean {
  if (percentage >= 100) return true;
  if (percentage <= 0) return false;
  if (digest.length < 4) throw new Error('cohort digest 长度无效');
  const value = (((digest[0] << 24) >>> 0) + (digest[1] << 16) + (digest[2] << 8) + digest[3]) >>> 0;
  return value % 100 < percentage;
}

export function selectHarmonyUpdate(manifest: StableManifest, currentVersion: string, currentBuild: number, cohortIncluded: boolean): StableArtifact | undefined {
  if (!cohortIncluded || manifest.minimumProtocolVersion > UPDATE_PROTOCOL_VERSION) return undefined;
  const artifact = manifest.artifacts.find(value => value.platform === 'harmony');
  if (!artifact || artifact.build <= currentBuild || compareStableVersions(artifact.version, parseStableVersion(currentVersion)) <= 0) return undefined;
  return artifact;
}

export function isAllowedHarmonyInstallerUrl(value: string): boolean {
  let parsed: URL;
  try { parsed = new URL(value); } catch (_error) { return false; }
  if (parsed.protocol !== 'https:' || !parsed.hostname || parsed.username || parsed.password || parsed.hash) return false;
  const host = parsed.hostname.toLowerCase();
  if (host === 'appgallery.huawei.com' || host.endsWith('.appgallery.huawei.com') ||
    host === 'appgallery.cloud.huawei.com' || host.endsWith('.appgallery.cloud.huawei.com')) return true;
  const paths = parsed.searchParams.getAll('path');
  let parameterCount = 0;
  parsed.searchParams.forEach((_parameterValue: string, _parameterName: string) => { parameterCount++; });
  return host === 'linjie.space' && parsed.pathname === '/download/api/download' && parameterCount === 1 &&
    paths.length === 1 && paths[0].startsWith('TabLink/stable/');
}

function requireArtifactHttps(value: string): string {
  let parsed: URL;
  try { parsed = new URL(value); } catch (_error) { throw new Error('安装地址无效'); }
  if (parsed.protocol !== 'https:' || !parsed.hostname || parsed.username || parsed.password || parsed.hash) throw new Error('安装地址必须是无凭据和片段的 HTTPS');
  return parsed.toString();
}

function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value); }
function requireString(value: unknown, name: string): string { if (typeof value !== 'string' || value.length === 0) throw new Error(`${name} 必须是非空字符串`); return value; }
function requireInteger(value: unknown, name: string): number { if (typeof value !== 'number' || !Number.isSafeInteger(value)) throw new Error(`${name} 必须是整数`); return value; }
function requireExactKeys(value: Record<string, unknown>, required: string[], optional: string[] = []): void {
  const keys = Object.keys(value), allowed = new Set([...required, ...optional]);
  if (required.some(key => !Object.prototype.hasOwnProperty.call(value, key)) || keys.some(key => !allowed.has(key))) throw new Error('JSON 字段缺失或未知');
}
function isCanonicalBase64(value: string): boolean {
  if (value.length === 0 || value.length % 4 !== 0 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) return false;
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  if (value.endsWith('==')) return alphabet.indexOf(value[value.length - 3]) % 16 === 0;
  if (value.endsWith('=')) return alphabet.indexOf(value[value.length - 2]) % 4 === 0;
  return true;
}

function parseStrictUtcTimestamp(value: string): number {
  const match = /^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})Z$/.exec(value);
  if (!match) throw new Error('发布时间必须是整秒 UTC RFC3339');
  const year = Number(match[1]), month = Number(match[2]), day = Number(match[3]);
  const hour = Number(match[4]), minute = Number(match[5]), second = Number(match[6]);
  const result = Date.UTC(year, month - 1, day, hour, minute, second, 0);
  const date = new Date(result);
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day ||
    date.getUTCHours() !== hour || date.getUTCMinutes() !== minute || date.getUTCSeconds() !== second ||
    date.getUTCMilliseconds() !== 0) throw new Error('发布时间无效');
  return result;
}

export function rejectDuplicateObjectKeys(text: string): void {
  let index = 0, depth = 0;
  const whitespace = (): void => { while (index < text.length && /[ \t\r\n]/.test(text[index])) index++; };
  const consume = (value: string): boolean => { if (text[index] !== value) return false; index++; return true; };
  const string = (): string => {
    if (!consume('"')) throw new Error('JSON 字段名必须是字符串');
    const start = index - 1;
    while (index < text.length) {
      const character = text[index++];
      if (character === '"') return JSON.parse(text.slice(start, index)) as string;
      if (character === '\\') { if (index >= text.length) throw new Error('JSON 转义无效'); const escaped = text[index++]; if (escaped === 'u') { if (!/^[0-9A-Fa-f]{4}$/.test(text.slice(index, index + 4))) throw new Error('Unicode 转义无效'); index += 4; } }
    }
    throw new Error('JSON 字符串没有结束');
  };
  const enter = (): void => { depth++; if (depth > 32) throw new Error('JSON 嵌套过深'); };
  const value = (): void => {
    whitespace(); if (index >= text.length) throw new Error('JSON 意外结束');
    if (text[index] === '{') { object(); return; }
    if (text[index] === '[') { array(); return; }
    if (text[index] === '"') { string(); return; }
    const start = index; while (index < text.length && !/[,\]} \t\r\n]/.test(text[index])) index++;
    if (index === start) throw new Error('JSON 值无效');
  };
  const object = (): void => {
    enter(); index++; whitespace(); if (consume('}')) { depth--; return; }
    const keys: Set<string> = new Set();
    while (true) { whitespace(); const key = string(); if (keys.has(key)) throw new Error('JSON 包含重复字段'); keys.add(key); whitespace(); if (!consume(':')) throw new Error('JSON 对象缺少冒号'); value(); whitespace(); if (consume('}')) { depth--; return; } if (!consume(',')) throw new Error('JSON 对象分隔符无效'); }
  };
  const array = (): void => {
    enter(); index++; whitespace(); if (consume(']')) { depth--; return; }
    while (true) { value(); whitespace(); if (consume(']')) { depth--; return; } if (!consume(',')) throw new Error('JSON 数组分隔符无效'); }
  };
  value(); whitespace(); if (index !== text.length) throw new Error('JSON 包含尾随数据');
}

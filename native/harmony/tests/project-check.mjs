import assert from 'node:assert/strict';
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
const root = fileURLToPath(new URL('..', import.meta.url));
let count = 0;
function check(value, message) { assert(value, message); count++; }
function text(path) { return readFileSync(join(root, path), 'utf8'); }
function stringResources(path) {
  const entries = JSON.parse(text(path)).string;
  check(Array.isArray(entries) && entries.length > 0, `${path} has strings`);
  const names = entries.map(entry => entry.name);
  check(new Set(names).size === names.length, `${path} has no duplicate keys`);
  check(entries.every(entry => typeof entry.name === 'string' && entry.name.length > 0 &&
    typeof entry.value === 'string' && entry.value.length > 0), `${path} values are nonempty`);
  return new Set(names);
}
function walk(directory) {
  for (const item of readdirSync(directory, { withFileTypes: true })) {
    if (['build', '.hvigor', 'oh_modules', '.idea'].includes(item.name)) continue;
    const path = join(directory, item.name);
    if (item.isDirectory()) walk(path);
    else if (/\.json5?$/.test(item.name)) { JSON.parse(readFileSync(path, 'utf8')); count++; }
  }
}
walk(root); // All repository JSON5 files intentionally use the strict JSON subset.
const module = JSON.parse(text('entry/src/main/module.json5')).module;
const app = JSON.parse(text('AppScope/app.json5')).app;
check(app.versionName === '0.8.0' && app.versionCode === 800, 'Harmony version metadata 0.8.0 code 800');
check(module.deviceTypes.includes('tablet') && module.deviceTypes.includes('phone'), 'phone and tablet targets');
check(module.requestPermissions.length === 1 && module.requestPermissions[0].name === 'ohos.permission.INTERNET', 'only required network permission');
for (const source of JSON.parse(text('entry/src/main/resources/base/profile/main_pages.json')).src) {
  check(existsSync(join(root, 'entry/src/main/ets', source + '.ets')), 'page exists');
}
check(existsSync(join(root, 'entry/src/main/ets/entryability/EntryAbility.ets')), 'ability exists');
check(existsSync(join(root, 'AppScope/resources/base/media/app_icon.svg')), 'icon exists');
const baseStrings = stringResources('entry/src/main/resources/base/element/string.json');
const englishStrings = stringResources('entry/src/main/resources/en_US/element/string.json');
const chineseStrings = stringResources('entry/src/main/resources/zh_CN/element/string.json');
check(baseStrings.size === englishStrings.size && [...baseStrings].every(key => englishStrings.has(key)),
  'base and English resource keys match exactly');
check(baseStrings.size === chineseStrings.size && [...baseStrings].every(key => chineseStrings.has(key)),
  'English and Simplified Chinese resource keys match exactly');
const englishValues = new Map(JSON.parse(text('entry/src/main/resources/en_US/element/string.json')).string.map(entry => [entry.name, entry.value]));
const chineseValues = new Map(JSON.parse(text('entry/src/main/resources/zh_CN/element/string.json')).string.map(entry => [entry.name, entry.value]));
const placeholders = value => [...value.matchAll(/\{[0-9]+\}/g)].map(match => match[0]).sort();
check([...englishValues].every(([key, value]) => JSON.stringify(placeholders(value)) ===
  JSON.stringify(placeholders(chineseValues.get(key)))), 'English and Simplified Chinese format placeholders match');
const baseAppStrings = stringResources('AppScope/resources/base/element/string.json');
const englishAppStrings = stringResources('AppScope/resources/en_US/element/string.json');
const chineseAppStrings = stringResources('AppScope/resources/zh_CN/element/string.json');
check(baseAppStrings.size === englishAppStrings.size && [...baseAppStrings].every(key => englishAppStrings.has(key)) &&
  baseAppStrings.size === chineseAppStrings.size && [...baseAppStrings].every(key => chineseAppStrings.has(key)),
  'application-label resource keys match exactly');
const session = text('entry/src/main/ets/protocol/Session.ets');
check(session.indexOf('samePin(hex, pairing.cert)') < session.indexOf('sendJson(0x10'), 'pin before hello token');
check(session.includes('sendJson(0x14'), 'submitted progress type');
check(session.includes("features: ['render-submitted-v1']") && session.includes('status.protocol === 1') &&
  session.includes("status.features.includes('render-submitted-v1')") && session.includes('!this.submissionAckNegotiated'),
  'render-submitted declaration and host-echo gate');
check(!session.includes('sendJson(0x12'), 'no forged presentation ACK');
check(session.includes("evidence: 'render-submitted'"), 'honest progress evidence');
check(session.includes('hasFreshOutput(stats.submittedFrames, stats.lastPtsUs)'), 'new decoder needs a real valid output before progress');
check(session.includes('validateParameterSets(sps, pps)') && session.includes('validateAccessUnit(packet.payload)'), 'Annex B input is validated');
check(session.includes('localizeKnownHostStatus') && session.includes("'session_capture_unavailable'") &&
  session.includes("'session_host_status_remote'") && !session.includes("this.state('session_host_status'"),
  'host status uses stable local keys instead of presenting remote text directly');
check(text('entry/src/main/ets/entryability/EntryAbility.ets').includes('onWindowStageCreate(stage: window.WindowStage): void'), 'ability hook keeps platform void signature');
const native = text('entry/src/main/cpp/Decoder.cpp');
check(native.includes('OH_AVCodec_GetCapabilityByCategory(OH_AVCODEC_MIMETYPE_VIDEO_AVC, false, HARDWARE)'), 'hardware decoder required');
check(native.indexOf('Check(OH_VideoDecoder_RenderOutputBuffer') < native.indexOf('++submitted_'), 'count advances after successful surface submission');
check(native.includes('pendingPts_.erase(attr.pts)'), 'submitted PTS bound to actual input');
check(native.includes('width != self->width_ || height != self->height_'), 'reject changed native output dimensions');
check(native.indexOf('worker_.join()') < native.indexOf('OH_VideoDecoder_Destroy'), 'worker stopped before codec destruction');
check(!text('entry/src/main/ets/pages/Index.ets').includes('Web('), 'native page is not a WebView');
const policy = text('entry/src/main/ets/update/StableUpdatePolicy.ts');
const crypto = text('entry/src/main/ets/update/StableUpdateCrypto.ets');
const updates = text('entry/src/main/ets/update/StableUpdateManager.ets');
const page = text('entry/src/main/ets/pages/Index.ets');
const localization = text('entry/src/main/ets/localization/AppLocalization.ets');
check(policy.includes('https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'), 'official HTTPS stable manifest endpoint');
check(policy.includes('https://github.com/linjierd/TabLink/releases/latest/download/manifest.json'), 'GitHub stable release manifest fallback');
check(policy.includes('MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A=='), 'pinned stable P-256 SPKI');
check(policy.includes('平台无效或重复') && policy.includes("root.channel !== 'stable'"), 'stable-only and one artifact per platform');
check(policy.includes("if (!exists) return 'automatic'") && policy.includes("return 'never'"), 'first run defaults automatic and damaged mode fails closed');
check(policy.includes('selectNewestVerifiedManifest') && policy.includes('canonicalStableDecision') &&
  policy.includes('同一发布时间存在冲突') && policy.includes('compareManifestWithFloor'),
  'newest canonical signed decision, conflict rejection and anti-replay floor');
check(policy.includes('newestConflictPublishedAt') && policy.includes('serializeConflictFloor') &&
  policy.includes('更新清单未晚于已记录的签名冲突') && updates.includes('acceptConflictFloor') &&
  updates.includes('runtimeConflictFloorUtc'),
  'same-time signed conflicts persist across checks and restart until a newer decision');
check(crypto.includes("createAsyKeyGenerator('ECC256')") && crypto.includes("createVerify('ECC256|SHA256')"), 'isolated P-256 SHA-256 verifier');
check(crypto.includes('if (!valid) throw') && !crypto.includes('return true'), 'signature verification fails closed');
check(crypto.includes('stableDecisionDigest') && crypto.includes("createMd('SHA256')") && policy.includes('serializeDecisionFloor'),
  'complete canonical release decision is persisted as a SHA-256 floor');
check(updates.indexOf('await verifyStableEnvelope(envelope)') < updates.indexOf('parseVerifiedPayload(verifiedPayloadText)'), 'signature before payload parsing');
check(updates.includes("platform === 'harmony'") && updates.includes('includedByDigest'), 'Harmony artifact and stable cohort selection');
check(updates.includes('STABLE_MANIFEST_FALLBACK_URL') && updates.includes('selectNewestVerifiedManifest(candidates)'), 'both signed manifest transports participate in newest selection');
check(updates.includes("this.mode === UPDATE_MODE_NEVER") && updates.includes("'update_disabled'"), 'Never mode blocks update requests');
check(updates.includes('StableUpdatePreferenceQueue') && updates.includes('modeRequestRevision') && page.includes('updateModeSelectionRevision'),
  'mode writes are serialized and stale UI completions are ignored');
check(updates.includes('manifest-floor-v1') && updates.includes('stableDecisionDigest(candidate.manifest)'),
  'accepted publication and canonical decision digest share one persisted floor record');
check(updates.includes('requiresNewerUpdateProtocol(manifest)') && updates.includes("'update_protocol_required'"),
  'future update protocol has an explicit status after floor acceptance');
check(updates.includes("action: 'ohos.want.action.viewData'") && updates.includes('isAllowedHarmonyInstallerUrl') && policy.includes("host === 'linjie.space'"), 'signed AppGallery or official redirect link uses system handler');
check(!/installHap|bundleInstaller|BundleInstaller|requestPermissionsFromUser/.test(updates + crypto + policy), 'no self-install or invented market-update API');
check(page.includes("this.l('action_open_store')") && page.includes('this.updateManager?.checkIfNeeded()'), 'nonblocking localized panel state and foreground check');
check(page.includes("this.l('update_mode_automatic')") && page.includes("this.l('update_mode_download_then_ask')") &&
  page.includes("this.l('update_mode_never')") && page.includes('setMode('), 'three localized persisted update choices are exposed');
check(localization.includes("export type AppLanguage = 'system' | 'zhHans' | 'english'") &&
  localization.includes("LANGUAGE_PREFERENCES_NAME: string = 'ui-language'") &&
  localization.includes("LANGUAGE_PREFERENCE_KEY: string = 'app-language-v1'") &&
  localization.includes("setAppPreferredLanguage(preferred)"), 'system, Simplified Chinese and English choices are persisted and applied');
check(localization.includes("i18n.System.getSystemLanguage().toLowerCase()") &&
  localization.includes("systemLanguage.startsWith('zh') ? 'zh-CN' : 'en'") &&
  text('entry/src/main/resources/base/element/string.json').includes('"value": "Language"'),
  'system mode maps every zh locale to Simplified Chinese and every other locale to English');
check(localization.includes('hostStatusText') && localization.includes('this.isSimplifiedChinese()') &&
  localization.includes("'session_host_status_generic'") &&
  page.includes("key === 'session_capture_paused_remote'") && page.includes('hostStatusText(key, arguments_[0])'),
  'language-mismatched host free text is replaced locally without changing the session');
const localizedSources = [page, updates, session].join('\n');
const usedLocalizationKeys = new Set();
for (const pattern of [/this\.l\('([^']+)'/g, /new StableUpdateStatus\('([^']+)'/g,
  /this\.(?:state|fail|stop)\('([^']+)'/g]) {
  for (const match of localizedSources.matchAll(pattern)) usedLocalizationKeys.add(match[1]);
}
check([...usedLocalizationKeys].every(key => baseStrings.has(key)), 'every Harmony localization key exists in both languages');
check(['language_system', 'language_zh_hans', 'language_english', 'pairing_placeholder',
  'action_connect', 'status_paste_pairing', 'session_connecting', 'session_pin_mismatch',
  'session_host_rejected', 'session_invalid_data', 'session_network_error', 'update_mode_automatic',
  'update_mode_download_then_ask', 'update_mode_never', 'update_available_automatic',
  'update_available_manual', 'update_check_failed'].every(key => baseStrings.has(key)) &&
  ['session_connecting', 'session_pin_mismatch', 'session_host_rejected', 'session_invalid_data',
    'session_network_error', 'update_check_failed'].every(key => usedLocalizationKeys.has(key)) &&
  updates.includes("'update_available_automatic'") && updates.includes("'update_available_manual'"),
  'language, settings, pairing link, certificate trust, connection errors and update states are localized');
check(!page.includes('AlertDialog'), 'active projection has no update dialog');
console.log(`PASS ${count} project/resource/protocol source checks; not an SDK build, signature, or device validation.`);

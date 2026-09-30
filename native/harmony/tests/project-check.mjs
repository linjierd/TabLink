import assert from 'node:assert/strict';
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
const root = fileURLToPath(new URL('..', import.meta.url));
let count = 0;
function check(value, message) { assert(value, message); count++; }
function text(path) { return readFileSync(join(root, path), 'utf8'); }
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
const session = text('entry/src/main/ets/protocol/Session.ets');
check(session.indexOf('samePin(hex, pairing.cert)') < session.indexOf('sendJson(0x10'), 'pin before hello token');
check(session.includes('sendJson(0x14'), 'submitted progress type');
check(!session.includes('sendJson(0x12'), 'no forged presentation ACK');
check(session.includes("evidence: 'render-submitted'"), 'honest progress evidence');
check(session.includes('hasFreshOutput(stats.submittedFrames, stats.lastPtsUs)'), 'new decoder needs a real valid output before progress');
check(session.includes('validateParameterSets(sps, pps)') && session.includes('validateAccessUnit(packet.payload)'), 'Annex B input is validated');
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
check(policy.includes('https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'), 'official HTTPS stable manifest endpoint');
check(policy.includes('MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A=='), 'pinned stable P-256 SPKI');
check(policy.includes('平台无效或重复') && policy.includes("root.channel !== 'stable'"), 'stable-only and one artifact per platform');
check(crypto.includes("createAsyKeyGenerator('ECC256')") && crypto.includes("createVerify('ECC256|SHA256')"), 'isolated P-256 SHA-256 verifier');
check(crypto.includes('if (!valid) throw') && !crypto.includes('return true'), 'signature verification fails closed');
check(updates.indexOf('await verifyStableEnvelope(envelope)') < updates.indexOf('parseVerifiedPayload(verifiedPayloadText)'), 'signature before payload parsing');
check(updates.includes("platform === 'harmony'") && updates.includes('includedByDigest'), 'Harmony artifact and stable cohort selection');
check(updates.includes("action: 'ohos.want.action.viewData'") && updates.includes('isAllowedHarmonyInstallerUrl') && policy.includes("host === 'linjie.space'"), 'signed AppGallery or official redirect link uses system handler');
check(!/installHap|bundleInstaller|BundleInstaller|requestPermissionsFromUser/.test(updates + crypto + policy), 'no self-install or invented market-update API');
check(page.includes('前往应用市场更新') && page.includes('this.updateManager?.checkIfNeeded()'), 'nonblocking panel state and foreground check');
check(!page.includes('AlertDialog'), 'active projection has no update dialog');
console.log(`PASS ${count} project/resource/protocol source checks; not an SDK build, signature, or device validation.`);

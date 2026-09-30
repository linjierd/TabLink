import { cryptoFramework } from '@kit.CryptoArchitectureKit';
import { util } from '@kit.ArkTS';
import { SignedEnvelopeText, STABLE_SIGNER_SPKI_BASE64 } from './StableUpdatePolicy';

const MAX_PAYLOAD_BYTES: number = 256 * 1024;

/**
 * CryptoArchitectureKit boundary for the stable-update protocol.
 *
 * The caller must parse only the small outer envelope before this method. The
 * payload text is returned only after the pinned P-256 signature is valid.
 * Any SDK/encoding error fails closed; there is deliberately no unsigned path.
 */
export async function verifyStableEnvelope(envelope: SignedEnvelopeText): Promise<string> {
  const base64 = new util.Base64Helper();
  const payload: Uint8Array = base64.decodeSync(envelope.payload);
  const signature: Uint8Array = base64.decodeSync(envelope.signature);
  const spki: Uint8Array = base64.decodeSync(STABLE_SIGNER_SPKI_BASE64);
  if (payload.length < 1 || payload.length > MAX_PAYLOAD_BYTES || signature.length < 8 || signature.length > 144 || spki.length !== 91) {
    throw new Error('更新签名数据大小无效');
  }

  // Crypto Architecture Kit accepts an X.509 SubjectPublicKeyInfo DER blob in
  // convertKey. The release signature is ASN.1 DER ECDSA over the exact UTF-8
  // payload bytes, using P-256 and SHA-256.
  const generator = cryptoFramework.createAsyKeyGenerator('ECC256');
  const keyPair = await generator.convertKey({ data: spki }, null);
  const verifier = cryptoFramework.createVerify('ECC256|SHA256');
  await verifier.init(keyPair.pubKey);
  const valid = await verifier.verify({ data: payload }, { data: signature });
  if (!valid) throw new Error('更新清单签名验证失败');

  const decoder: util.TextDecoder = util.TextDecoder.create('utf-8', { fatal: true });
  return decoder.decodeToString(payload);
}

export async function stableCohortDigest(cohortId: string, releaseId: string): Promise<Uint8Array> {
  const encoder: util.TextEncoder = new util.TextEncoder();
  const digest = cryptoFramework.createMd('SHA256');
  await digest.update({ data: encoder.encodeInto(cohortId + '\n' + releaseId) });
  return (await digest.digest()).data;
}

export function createStableCohortId(): string {
  const bytes = cryptoFramework.createRandom().generateRandomSync(16).data;
  if (bytes.length !== 16) throw new Error('无法生成更新分组标识');
  let value = '';
  for (const byte of bytes) value += byte.toString(16).padStart(2, '0');
  return value;
}

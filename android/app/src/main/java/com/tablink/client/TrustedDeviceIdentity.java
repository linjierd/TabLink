package com.tablink.client;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.security.GeneralSecurityException;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.PrivateKey;
import java.security.PublicKey;
import java.security.Signature;
import java.security.spec.ECGenParameterSpec;

/** Non-exportable P-256 device identity held by Android Keystore. */
public final class TrustedDeviceIdentity {
    private static final String STORE = "AndroidKeyStore";
    private static final String ALIAS = "tablink-trusted-device-v1";
    private final PrivateKey privateKey;
    public final PublicKey publicKey;
    public final String deviceId;

    private TrustedDeviceIdentity(PrivateKey privateKey, PublicKey publicKey) throws GeneralSecurityException {
        this.privateKey = privateKey;
        this.publicKey = publicKey;
        deviceId = TrustedDeviceProtocol.deviceId(publicKey);
    }

    public static TrustedDeviceIdentity loadOrCreate() throws GeneralSecurityException, java.io.IOException {
        KeyStore store = KeyStore.getInstance(STORE);
        store.load(null);
        if (!store.containsAlias(ALIAS)) {
            KeyPairGenerator generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, STORE);
            generator.initialize(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .setUserAuthenticationRequired(false)
                    .build());
            generator.generateKeyPair();
        }
        java.security.Key key = store.getKey(ALIAS, null);
        java.security.cert.Certificate certificate = store.getCertificate(ALIAS);
        if (!(key instanceof PrivateKey) || certificate == null)
            throw new GeneralSecurityException("Android Keystore 中的 TabLink 设备身份无效");
        return new TrustedDeviceIdentity((PrivateKey) key, certificate.getPublicKey());
    }

    public String publicKeySpkiBase64() {
        return Base64.encodeToString(publicKey.getEncoded(), Base64.NO_WRAP);
    }

    public byte[] sign(String hostId, byte[] challenge) throws GeneralSecurityException {
        Signature signer = Signature.getInstance("SHA256withECDSA");
        signer.initSign(privateKey);
        signer.update(TrustedDeviceProtocol.transcript(hostId, deviceId, challenge));
        return signer.sign();
    }

    public static TrustedComputerForgetCoordinator.IdentityRemoval delete() {
        try {
            KeyStore store = KeyStore.getInstance(STORE);
            store.load(null);
            if (store.containsAlias(ALIAS)) store.deleteEntry(ALIAS);
        } catch (GeneralSecurityException | java.io.IOException failure) {
            // Verification below distinguishes an error before deletion from an
            // error reported after the alias was already removed.
        }
        try {
            KeyStore verified = KeyStore.getInstance(STORE);
            verified.load(null);
            return verified.containsAlias(ALIAS)
                    ? TrustedComputerForgetCoordinator.IdentityRemoval.PRESERVED
                    : TrustedComputerForgetCoordinator.IdentityRemoval.REMOVED;
        } catch (GeneralSecurityException | java.io.IOException uncertain) {
            return TrustedComputerForgetCoordinator.IdentityRemoval.UNKNOWN;
        }
    }
}

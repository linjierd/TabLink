package com.tablink.client;

import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.ReaderException;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;
import java.util.EnumMap;
import java.util.Map;

/** ZXing core only: no external scanning app, services, network or Google Play dependency. */
public final class QrCodeDecoder {
    private QrCodeDecoder() {}

    public static String decode(byte[] luminance, int width, int height) throws ReaderException {
        if (width <= 0 || height <= 0 || (long) width * height > luminance.length)
            throw new IllegalArgumentException("Invalid camera frame");
        PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(
                luminance, width, height, 0, 0, width, height, false);
        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        hints.put(DecodeHintType.CHARACTER_SET, "UTF-8");
        return new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)), hints).getText();
    }
}

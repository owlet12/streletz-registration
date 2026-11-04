// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeWriter;
import java.util.Arrays;

public final class ScannerGeometrySelfTest {
    private static int checks;
    public static void main(String[] args) throws Exception {
        int[] fit = ScannerGeometry.fit(1280, 720, 90, 1080, 1920);
        check(fit[0] == 1080 && fit[1] == 1920, "portrait preview preserves camera aspect");
        for (int rotation : new int[]{0, 90, 180, 270}) {
            boolean swap = rotation % 180 != 0;
            int vw = swap ? 600 : 800, vh = swap ? 800 : 600;
            ScannerGeometry.Crop center = ScannerGeometry.crop(800, 600, vw, vh,
                    (vw - 300) / 2.0, (vh - 300) / 2.0, (vw + 300) / 2.0, (vh + 300) / 2.0, rotation, false);
            check(center.left == 250 && center.top == 150 && center.width == 300 && center.height == 300, "center mapping at " + rotation);
            byte[] pixels = new byte[800 * 600]; Arrays.fill(pixels, (byte) 255);
            BitMatrix qr = new QRCodeWriter().encode("test-synthetic-QR", BarcodeFormat.QR_CODE, 180, 180);
            stamp(pixels, 800, qr, 310, 210);
            check("test-synthetic-QR".equals(decode(pixels, center)), "QR inside visible frame decodes at " + rotation);
            Arrays.fill(pixels, (byte) 255); stamp(pixels, 800, qr, 10, 10);
            try { decode(pixels, center); throw new AssertionError("QR outside frame was read"); }
            catch (com.google.zxing.NotFoundException expected) { checks++; }
        }
        ScannerGeometry.Crop shifted = ScannerGeometry.crop(800, 600, 600, 800, 100, 200, 300, 400, 90, false);
        check(shifted.left == 200 && shifted.top == 300 && shifted.width == 200 && shifted.height == 200, "off-center frame accounts for rotation");
        ScannerGeometry.Crop mirrored = ScannerGeometry.crop(800, 600, 800, 600, 100, 100, 300, 300, 0, true);
        check(mirrored.left == 500 && mirrored.top == 100 && mirrored.width == 200, "front camera mirror mapping");
        ScannerGeometry.Crop clipped = ScannerGeometry.crop(800, 600, 800, 600, -20, -20, 100, 100, 0, false);
        check(clipped.left == 0 && clipped.top == 0 && clipped.width == 100, "crop stays inside camera buffer");
        try { ScannerGeometry.crop(800, 600, 0, 0, 0, 0, 10, 10, 0, false); throw new AssertionError("invalid layout accepted"); }
        catch (IllegalArgumentException expected) { checks++; }
        System.out.println("ScannerGeometrySelfTest: " + checks + " checks passed (synthetic QR, no camera)");
    }
    private static void stamp(byte[] data, int stride, BitMatrix qr, int left, int top) {
        for (int y = 0; y < qr.getHeight(); y++) for (int x = 0; x < qr.getWidth(); x++)
            data[(y + top) * stride + x + left] = qr.get(x, y) ? 0 : (byte) 255;
    }
    private static String decode(byte[] pixels, ScannerGeometry.Crop crop) throws Exception {
        return new MultiFormatReader().decode(new BinaryBitmap(new HybridBinarizer(new PlanarYUVLuminanceSource(
                pixels, 800, 600, crop.left, crop.top, crop.width, crop.height, false)))).getText();
    }
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); checks++; }
}

// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

/** Maps the actual visible frame back into unrotated camera pixels. No Android dependency. */
final class ScannerGeometry {
    static final class Crop {
        final int left, top, width, height;
        Crop(int left, int top, int width, int height) {
            this.left = left; this.top = top; this.width = width; this.height = height;
        }
    }
    static Crop crop(int rawWidth, int rawHeight, int viewWidth, int viewHeight,
                     double left, double top, double right, double bottom, int rotation, boolean mirror) {
        if (rawWidth <= 0 || rawHeight <= 0 || viewWidth <= 0 || viewHeight <= 0
                || right <= left || bottom <= top || (rotation != 0 && rotation != 90 && rotation != 180 && rotation != 270))
            throw new IllegalArgumentException("Invalid preview geometry");
        double minX = 1, minY = 1, maxX = 0, maxY = 0;
        for (double x : new double[]{left / viewWidth, right / viewWidth}) {
            for (double y : new double[]{top / viewHeight, bottom / viewHeight}) {
                double u = mirror ? 1 - x : x, v = y, sx, sy;
                if (rotation == 90) { sx = v; sy = 1 - u; }
                else if (rotation == 180) { sx = 1 - u; sy = 1 - v; }
                else if (rotation == 270) { sx = 1 - v; sy = u; }
                else { sx = u; sy = v; }
                minX = Math.min(minX, sx); minY = Math.min(minY, sy);
                maxX = Math.max(maxX, sx); maxY = Math.max(maxY, sy);
            }
        }
        int l = Math.max(0, (int) Math.ceil(minX * rawWidth));
        int t = Math.max(0, (int) Math.ceil(minY * rawHeight));
        int r = Math.min(rawWidth, (int) Math.floor(maxX * rawWidth));
        int b = Math.min(rawHeight, (int) Math.floor(maxY * rawHeight));
        if (r <= l || b <= t) throw new IllegalArgumentException("Frame is outside preview");
        return new Crop(l, t, r - l, b - t);
    }

    static int[] fit(int rawWidth, int rawHeight, int rotation, int availableWidth, int availableHeight) {
        int width = rotation % 180 == 0 ? rawWidth : rawHeight;
        int height = rotation % 180 == 0 ? rawHeight : rawWidth;
        double scale = Math.min((double) availableWidth / width, (double) availableHeight / height);
        return new int[]{Math.max(1, (int) Math.round(width * scale)), Math.max(1, (int) Math.round(height * scale))};
    }
}

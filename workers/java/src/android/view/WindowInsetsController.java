/*
 * Decompiled with CFR 0.152.
 */
package android.view;

import android.os.CancellationSignal;
import android.view.WindowInsetsAnimationControlListener;
import android.view.WindowInsetsController$OnControllableInsetsChangedListener;
import android.view.animation.Interpolator;

public class WindowInsetsController {
    public int getSystemBarsAppearance() {
        return 0;
    }

    public int getSystemBarsBehavior() {
        return 0;
    }

    public void addOnControllableInsetsChangedListener(WindowInsetsController$OnControllableInsetsChangedListener windowInsetsController$OnControllableInsetsChangedListener) {
    }

    public void controlWindowInsetsAnimation(int n, long l, Interpolator interpolator, CancellationSignal cancellationSignal, WindowInsetsAnimationControlListener windowInsetsAnimationControlListener) {
    }

    public void hide(int n) {
    }

    public void removeOnControllableInsetsChangedListener(WindowInsetsController$OnControllableInsetsChangedListener windowInsetsController$OnControllableInsetsChangedListener) {
    }

    public void setSystemBarsAppearance(int n, int n2) {
    }

    public void setSystemBarsBehavior(int n) {
    }

    public void show(int n) {
    }
}


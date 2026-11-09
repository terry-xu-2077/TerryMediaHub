/*
 * Decompiled with CFR 0.152.
 */
package android.os;

public class Looper {
    private static final Looper MAIN = new Looper();

    public static Looper getMainLooper() {
        return MAIN;
    }

    public static Looper myLooper() {
        return MAIN;
    }

    public static void prepare() {
    }

    public Thread getThread() {
        return Thread.currentThread();
    }
}


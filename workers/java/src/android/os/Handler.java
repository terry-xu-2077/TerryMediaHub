/*
 * Decompiled with CFR 0.152.
 */
package android.os;

import android.os.Handler$Callback;
import android.os.Looper;
import android.os.Message;

public class Handler {
    public Handler() {
    }

    public Handler(Looper looper) {
    }

    public Handler(Looper looper, Handler$Callback handler$Callback) {
    }

    public Looper getLooper() {
        return Looper.getMainLooper();
    }

    public boolean post(Runnable runnable) {
        if (runnable != null) {
            runnable.run();
        }
        return true;
    }

    public boolean postDelayed(Runnable runnable, long l) {
        if (runnable != null) {
            runnable.run();
        }
        return true;
    }

    public Message obtainMessage(int n) {
        return new Msg(n, 0, 0, null);
    }

    public Message obtainMessage(int n, Object object) {
        return new Msg(n, 0, 0, object);
    }

    public Message obtainMessage(int n, int n2, int n3) {
        return new Msg(n, n2, n3, null);
    }

    public Message obtainMessage(int n, int n2, int n3, Object object) {
        return new Msg(n, n2, n3, object);
    }

    public boolean sendMessage(Message message) {
        return true;
    }

    public boolean sendMessageDelayed(Message message, long l) {
        return true;
    }

    public boolean sendEmptyMessageDelayed(int n, long l) {
        return true;
    }

    public void removeMessages(int n) {
    }

    public void removeMessages(int n, Object object) {
    }

    public void removeCallbacksAndMessages(Object object) {
    }

    private static class Msg
    extends Message {
        Msg(int n, int n2, int n3, Object object) {
            super(n, n2, n3, object);
        }
    }
}


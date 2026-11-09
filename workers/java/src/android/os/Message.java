/*
 * Decompiled with CFR 0.152.
 */
package android.os;

public class Message {
    public int what;
    public int arg1;
    public int arg2;
    public Object obj;
    public Object target;

    public Message() {
    }

    public Message(int n, int n2, int n3, Object object) {
        this.what = n;
        this.arg1 = n2;
        this.arg2 = n3;
        this.obj = object;
    }

    public static Message obtain() {
        return new Message();
    }
}


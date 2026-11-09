/*
 * Decompiled with CFR 0.152.
 */
package android.os;

import android.os.IInterface;
import android.os.Parcel;

public class Binder {
    public static long clearCallingIdentity() {
        return 0L;
    }

    public static void restoreCallingIdentity(long l) {
    }

    public boolean onTransact(int n, Parcel parcel, Parcel parcel2, int n2) {
        return false;
    }

    public String getInterfaceDescriptor() {
        return null;
    }

    public void attachInterface(IInterface iInterface, String string) {
    }
}


/*
 * Decompiled with CFR 0.152.
 */
package android.os;

import android.os.Bundle;
import android.os.IBinder;
import android.os.Parcelable;
import java.util.ArrayList;

public class Parcel {
    public static Parcel obtain() {
        return null;
    }

    public Bundle readBundle() {
        return null;
    }

    public IBinder readStrongBinder() {
        return null;
    }

    public Parcelable readParcelable(ClassLoader classLoader) {
        return null;
    }

    public int dataAvail() {
        return 0;
    }

    public int dataPosition() {
        return 0;
    }

    public int dataSize() {
        return 0;
    }

    public int readInt() {
        return 0;
    }

    public int[] createIntArray() {
        return null;
    }

    public Object[] createTypedArray(Parcelable$Creator creator) {
        return null;
    }

    public String readString() {
        return null;
    }

    public ArrayList createTypedArrayList(Parcelable$Creator creator) {
        return null;
    }

    public long readLong() {
        return 0L;
    }

    public void enforceInterface(String string) {
    }

    public void readException() {
    }

    public void recycle() {
    }

    public void setDataPosition(int n) {
    }

    public void writeBundle(Bundle bundle) {
    }

    public void writeInt(int n) {
    }

    public void writeIntArray(int[] nArray) {
    }

    public void writeInterfaceToken(String string) {
    }

    public void writeLong(long l) {
    }

    public void writeNoException() {
    }

    public void writeString(String string) {
    }

    public void writeStrongBinder(IBinder iBinder) {
    }
}


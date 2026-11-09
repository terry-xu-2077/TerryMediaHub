/*
 * Decompiled with CFR 0.152.
 */
package android.text;

public class TextUtils {
    public static boolean isEmpty(CharSequence charSequence) {
        return charSequence == null || charSequence.length() == 0;
    }

    public static boolean equals(CharSequence charSequence, CharSequence charSequence2) {
        if (charSequence == charSequence2) {
            return true;
        }
        if (charSequence == null || charSequence2 == null || charSequence.length() != charSequence2.length()) {
            return false;
        }
        return charSequence.toString().equals(charSequence2.toString());
    }

    public static String join(CharSequence charSequence, Iterable<?> iterable) {
        StringBuilder stringBuilder = new StringBuilder();
        boolean bl = true;
        for (Object obj : iterable) {
            if (!bl) {
                stringBuilder.append(charSequence);
            }
            stringBuilder.append(obj);
            bl = false;
        }
        return stringBuilder.toString();
    }

    public static String join(CharSequence charSequence, Object[] objectArray) {
        StringBuilder stringBuilder = new StringBuilder();
        for (int i = 0; i < objectArray.length; ++i) {
            if (i > 0) {
                stringBuilder.append(charSequence);
            }
            stringBuilder.append(objectArray[i]);
        }
        return stringBuilder.toString();
    }
}


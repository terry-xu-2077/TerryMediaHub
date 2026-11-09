/*
 * Decompiled with CFR 0.152.
 */
package android.app;

import android.os.Parcelable$Creator;

import android.media.AudioAttributes;
import android.os.Parcelable;
import android.widget.RemoteViews;

public class Notification {
    public static AudioAttributes AUDIO_ATTRIBUTES_DEFAULT = null;
    public static Parcelable$Creator CREATOR = null;
    public Notification publicVersion = null;
    public AudioAttributes audioAttributes = null;
    public RemoteViews headsUpContentView = null;
    public int color = 0;
    public int visibility = 0;
    public String category = null;
}


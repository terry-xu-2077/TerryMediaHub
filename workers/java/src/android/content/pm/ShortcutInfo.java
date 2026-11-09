/*
 * Decompiled with CFR 0.152.
 */
package android.content.pm;

import android.content.ComponentName;
import android.content.Intent;
import android.content.LocusId;
import android.os.PersistableBundle;
import android.os.UserHandle;
import java.util.Set;

public class ShortcutInfo {
    public ComponentName getActivity() {
        return null;
    }

    public Intent[] getIntents() {
        return null;
    }

    public LocusId getLocusId() {
        return null;
    }

    public PersistableBundle getExtras() {
        return null;
    }

    public UserHandle getUserHandle() {
        return null;
    }

    public boolean hasKeyFieldsOnly() {
        return false;
    }

    public boolean isCached() {
        return false;
    }

    public boolean isDeclaredInManifest() {
        return false;
    }

    public boolean isDynamic() {
        return false;
    }

    public boolean isEnabled() {
        return false;
    }

    public boolean isImmutable() {
        return false;
    }

    public boolean isPinned() {
        return false;
    }

    public int getDisabledReason() {
        return 0;
    }

    public int getRank() {
        return 0;
    }

    public CharSequence getDisabledMessage() {
        return null;
    }

    public CharSequence getLongLabel() {
        return null;
    }

    public CharSequence getShortLabel() {
        return null;
    }

    public String getId() {
        return null;
    }

    public String getPackage() {
        return null;
    }

    public Set getCategories() {
        return null;
    }

    public long getLastChangedTimestamp() {
        return 0L;
    }
}


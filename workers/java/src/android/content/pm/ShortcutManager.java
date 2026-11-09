/*
 * Decompiled with CFR 0.152.
 */
package android.content.pm;

import android.content.pm.ShortcutInfo;
import java.util.List;

public class ShortcutManager {
    public boolean addDynamicShortcuts(List list) {
        return false;
    }

    public boolean isRateLimitingActive() {
        return false;
    }

    public boolean setDynamicShortcuts(List list) {
        return false;
    }

    public boolean updateShortcuts(List list) {
        return false;
    }

    public int getIconMaxHeight() {
        return 0;
    }

    public int getIconMaxWidth() {
        return 0;
    }

    public int getMaxShortcutCountPerActivity() {
        return 0;
    }

    public List getDynamicShortcuts() {
        return null;
    }

    public List getManifestShortcuts() {
        return null;
    }

    public List getPinnedShortcuts() {
        return null;
    }

    public List getShortcuts(int n) {
        return null;
    }

    public void disableShortcuts(List list, CharSequence charSequence) {
    }

    public void enableShortcuts(List list) {
    }

    public void pushDynamicShortcut(ShortcutInfo shortcutInfo) {
    }

    public void removeAllDynamicShortcuts() {
    }

    public void removeDynamicShortcuts(List list) {
    }

    public void removeLongLivedShortcuts(List list) {
    }

    public void reportShortcutUsed(String string) {
    }
}


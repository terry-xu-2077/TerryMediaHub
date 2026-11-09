/*
 * Decompiled with CFR 0.152.
 */
package android.view.accessibility;

import android.view.View;
import android.view.accessibility.AccessibilityNodeInfo$AccessibilityAction;
import android.view.accessibility.AccessibilityNodeInfo$TouchDelegateInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import java.util.List;

public class AccessibilityNodeInfo {
    public AccessibilityNodeInfo getTraversalAfter() {
        return null;
    }

    public AccessibilityNodeInfo getTraversalBefore() {
        return null;
    }

    public AccessibilityNodeInfo$TouchDelegateInfo getTouchDelegateInfo() {
        return null;
    }

    public AccessibilityWindowInfo getWindow() {
        return null;
    }

    public boolean isContextClickable() {
        return false;
    }

    public boolean isHeading() {
        return false;
    }

    public boolean isImportantForAccessibility() {
        return false;
    }

    public boolean isScreenReaderFocusable() {
        return false;
    }

    public boolean isShowingHintText() {
        return false;
    }

    public boolean isTextEntryKey() {
        return false;
    }

    public boolean removeAction(AccessibilityNodeInfo$AccessibilityAction accessibilityNodeInfo$AccessibilityAction) {
        return false;
    }

    public boolean removeChild(View view) {
        return false;
    }

    public boolean removeChild(View view, int n) {
        return false;
    }

    public int getDrawingOrder() {
        return 0;
    }

    public int getMaxTextLength() {
        return 0;
    }

    public CharSequence getError() {
        return null;
    }

    public CharSequence getHintText() {
        return null;
    }

    public CharSequence getPaneTitle() {
        return null;
    }

    public CharSequence getStateDescription() {
        return null;
    }

    public CharSequence getTooltipText() {
        return null;
    }

    public String getUniqueId() {
        return null;
    }

    public List getActionList() {
        return null;
    }

    public List getAvailableExtraData() {
        return null;
    }

    public void addAction(AccessibilityNodeInfo$AccessibilityAction accessibilityNodeInfo$AccessibilityAction) {
    }

    public void setAvailableExtraData(List list) {
    }

    public void setContextClickable(boolean bl) {
    }

    public void setDrawingOrder(int n) {
    }

    public void setError(CharSequence charSequence) {
    }

    public void setHeading(boolean bl) {
    }

    public void setHintText(CharSequence charSequence) {
    }

    public void setImportantForAccessibility(boolean bl) {
    }

    public void setMaxTextLength(int n) {
    }

    public void setPaneTitle(CharSequence charSequence) {
    }

    public void setScreenReaderFocusable(boolean bl) {
    }

    public void setShowingHintText(boolean bl) {
    }

    public void setStateDescription(CharSequence charSequence) {
    }

    public void setTextEntryKey(boolean bl) {
    }

    public void setTooltipText(CharSequence charSequence) {
    }

    public void setTouchDelegateInfo(AccessibilityNodeInfo$TouchDelegateInfo accessibilityNodeInfo$TouchDelegateInfo) {
    }

    public void setTraversalAfter(View view) {
    }

    public void setTraversalAfter(View view, int n) {
    }

    public void setTraversalBefore(View view) {
    }

    public void setTraversalBefore(View view, int n) {
    }

    public void setUniqueId(String string) {
    }
}


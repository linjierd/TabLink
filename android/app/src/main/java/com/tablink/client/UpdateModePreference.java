package com.tablink.client;

import java.util.Map;

/** Canonical update-mode preference parsing shared by the activity and installer callback. */
final class UpdateModePreference {
    static final String PREFERENCES = "stable-updates";
    static final String MODE_KEY = "update-mode";
    static final String LEGACY_AUTO_KEY = "auto-download";
    private static volatile UpdateStateMachine.Mode runtimeMode;

    static final class Selection {
        final UpdateStateMachine.Mode mode;
        final boolean damaged;
        final boolean migrateLegacy;
        Selection(UpdateStateMachine.Mode mode, boolean damaged, boolean migrateLegacy) {
            this.mode = mode;
            this.damaged = damaged;
            this.migrateLegacy = migrateLegacy;
        }
    }

    private UpdateModePreference() { }

    static UpdateStateMachine.Mode parse(String stored, Boolean legacyAuto, boolean damaged) {
        if (damaged) return UpdateStateMachine.Mode.NEVER;
        if (stored != null) {
            try { return UpdateStateMachine.Mode.valueOf(stored); }
            catch (IllegalArgumentException invalid) { return UpdateStateMachine.Mode.NEVER; }
        }
        if (legacyAuto != null)
            return legacyAuto ? UpdateStateMachine.Mode.AUTOMATIC : UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK;
        return UpdateStateMachine.Mode.AUTOMATIC;
    }

    static Selection read(Map<String, ?> values) {
        if (values == null) return new Selection(UpdateStateMachine.Mode.NEVER, true, false);
        boolean hasMode = values.containsKey(MODE_KEY);
        boolean hasLegacy = values.containsKey(LEGACY_AUTO_KEY);
        Object mode = values.get(MODE_KEY);
        Object legacy = values.get(LEGACY_AUTO_KEY);
        if (hasMode) {
            if (!(mode instanceof String))
                return new Selection(UpdateStateMachine.Mode.NEVER, true, false);
            try {
                return new Selection(UpdateStateMachine.Mode.valueOf((String) mode), false, hasLegacy);
            } catch (IllegalArgumentException invalid) {
                return new Selection(UpdateStateMachine.Mode.NEVER, true, false);
            }
        }
        if (hasLegacy) {
            if (!(legacy instanceof Boolean))
                return new Selection(UpdateStateMachine.Mode.NEVER, true, false);
            return new Selection((Boolean) legacy ? UpdateStateMachine.Mode.AUTOMATIC
                    : UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK, false, true);
        }
        return new Selection(UpdateStateMachine.Mode.AUTOMATIC, false, false);
    }

    static boolean installsAllowed(String stored, Boolean legacyAuto, boolean damaged) {
        if (runtimeMode == UpdateStateMachine.Mode.NEVER) return false;
        return parse(stored, legacyAuto, damaged) != UpdateStateMachine.Mode.NEVER;
    }

    static void setRuntimeMode(UpdateStateMachine.Mode mode) {
        runtimeMode = mode == null ? UpdateStateMachine.Mode.NEVER : mode;
    }
}

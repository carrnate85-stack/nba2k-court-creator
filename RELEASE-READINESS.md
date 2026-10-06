# Final Hardening Audit

Date: 2026-10-06

Scope: finish the current worker request-file cleanup fix and run a final audit.
This bounded development pass is complete. This is not a claim that physical
input behavior, startup speed or NBA 2K27 compatibility has been fully accepted.

## Final Fix

Worker request files use create-new names, a retained read-sharing guard,
16 MiB/depth-32 JSON limits, and one preparation/queue/execution deadline.
Cleanup requires the original file identity and exact content. Changed,
replaced, shared or persistently locked files are preserved instead of blindly
deleted by filename. Cancellation removes known partial files; cleanup is
idempotent and does not sweep the temporary folder. The original shared-file
regression was reproduced before the fix and passes afterward.

## Verified

- Focused request-file regressions passed with real Python reads.
- Full off-screen native suite passed: 15 layouts, saves/recovery, hardwood
  switching, inline colors, logo transforms/history, failure recovery,
  full-resolution PNG/BC7 IFF exports and conversion round trips.
- Backend suite: 395 tests passed, one skipped platform-specific test.
- Compact/desktop light, dark and real-stock catalog screenshots inspected.
- Runtime diagnostics: x64 Python 3.12.14, Pillow 12.3.0, Shapely 2.1.2 and
  Windows Desktop Runtime 8.0.30 present; no missing native files.
- Release build published to `desktop`; all five native artifacts match the
  tested Release build. Existing desktop shortcut targets the local launcher.
- Version 1.5.2 package audit passed: 35 update files, 3,806,148 expanded bytes,
  2,203,175 archive bytes. These are update-package sizes, not the whole app.
- Same-size/timestamp DLL tampering rejected; rollback, seven personal files,
  original source bytes/timestamps preserved; temporary installation removed.
- Embedded DPI manifest verified: PerMonitorV2/PerMonitor, asInvoker.
- Whitespace/conflict-marker checks passed for the changes.

Evidence: `outputs/final-readiness-audit` and the smoke/check-release commands
documented in `NATIVE-STUDIO.md`. No app windows were opened. No commit, push,
public release or upload was performed; the existing dirty worktree is retained.

## Manual Acceptance Remaining

1. Real mouse/keyboard focus, dialogs and pointer gestures, including mixed-DPI
   monitors. Synthetic routed input and off-screen layouts do not replace this.
2. Measure a real cold launch through the desktop shortcut to the first usable
   court. No visible-startup measurement or Electron speed comparison is claimed.
3. Load the exported IFF in NBA 2K27 and inspect markings, alignment and artwork
   in-game. CRC/texture/scene/export round trips are not proof of in-game loading.

The app was deliberately left closed. No additional open-ended hardening work
is scheduled by this audit. Publishing/sharing the source or a release remains
a separate explicitly authorized operation.

# Accessibility and UI — 0.5.0-preview

Development and published Release demo UI: 16/16 automated UI Automation smoke checks passed in each run (`artifacts/reports/m4-ui-dev/ui-smoke-results.json` and `m4-ui-release/ui-smoke-results.json`). These exercise discovery, protected C: refusal, dialogs, simulation queue/confirmation/progress and navigation. They do not establish real operation accessibility or complete screen-reader support.

| Before | After | Reason |
|---|---|---|
| Confirmation lists operation details without whole layouts | Every affected disk includes before/after partitions and unallocated segments, exact byte boundaries and identifiers | Make scope review possible |
| Destructive confirmation could use primary default action | Destructive dialogs initially focus Cancel and do not make Execute the default button | Reduce accidental confirmation |
| Long dialog content could exceed available screen | Scrollable/resizable body capped to work area | Support long layout previews |
| Application palettes ignored system high contrast | Theme reacts to high-contrast changes and uses system colors | Improve contrast compatibility |
| Large decorative shadow across main content | Shadow removed | Reduce rendering work without changing functionality |

Applied the local emil-design-eng skill for UI polish. Full keyboard-only traversal, Narrator announcements, focus trapping/restoration, high-contrast visual inspection, and 125/150/175/200% display scaling remain **PENDING** in an interactive isolated VM. Smoke screenshots are automation artifacts, not certification of each display configuration. No performance comparison with the reference product is claimed.

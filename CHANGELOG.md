# Changelog

## Unreleased

### Added

- Dark interface with compact rounded controls and a native dark title bar.
- Ctrl+mouse wheel zoom, local-state save, and common reading shortcuts.
- Virtualized continuous scrolling with per-page text selection.
- Internal and external PDF links plus a Contents tab sourced from the document outline.

### Fixed

- Keep one stable scrollbar for the full document instead of resetting it for every page.

## 1.0.0 - 2026-07-20

### Added

- Fast native Windows PDF rendering with bounded page and text caches.
- Page navigation, direct page entry, zoom, fit modes, and rotation.
- Selectable text, clipboard copy, and on-demand document search.
- Local bookmarks, highlights, and anchored notes without changing source PDFs.
- Password-protected PDF support across rendering and text extraction.
- Per-user Windows Open with and Default Apps registration.
- Self-contained x64 portable ZIP and installer release targets.

### Hardened

- Friendly error states for damaged, unreadable, and unsupported PDF files.
- Search results capped at 10,000 entries to keep memory use predictable.
- Atomic versioned local state with migration and unreadable-state preservation.
- SHA-256 release checksums and automated artifact verification.

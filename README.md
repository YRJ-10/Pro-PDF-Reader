# Pro PDF Reader

Pro PDF Reader is a lightweight Windows PDF reader focused on fast startup and a clean basic reading experience.

## Phase 1 scope

- Native WPF app shell.
- Open PDF from the toolbar.
- Open PDF from a command-line file path, for future `Open with` support.
- Render the first page quickly.
- Basic previous/next page navigation.

## Phase 2 scope

- Keep up to five recently rendered pages in memory.
- Pre-render the next page, then the previous page, after the visible page is ready.
- Reuse in-progress renders when navigation reaches a page being prepared.
- Keep the current page visible while another page renders.
- Navigate with Left/Right, Page Up/Page Down, or Space.
- Show measured open and page-render time in the status bar.

## Phase 3 scope

- Extract positioned words after the first page is already visible.
- Select text by dragging across words or double-clicking a word.
- Copy with Ctrl+C or the page context menu.
- Select all text on the current page with Ctrl+A.
- Clear a selection with Escape.
- Cache extracted text for up to eight visited pages.

## Phase 4 scope

- Store one versioned local state file per PDF.
- Restore the last successfully viewed page when reopening a document.
- Debounce page-position writes to avoid unnecessary disk activity.
- Replace state files atomically so an interrupted save keeps the previous file intact.
- Preserve unreadable state files for recovery instead of silently deleting them.

## Phase 5 scope

- Bookmark or unbookmark the current page.
- Browse bookmarks in a lightweight collapsible side panel.
- Jump directly to a bookmarked page.
- Create a persistent highlight from selected text.
- Remove a highlight from its page context menu.
- Migrate phase 4 state files forward without losing the last reading position.

## Phase 6 scope

- Attach a local note to selected PDF text.
- Show note anchors and compact markers directly on the page.
- Browse notes in a dedicated side-panel tab.
- Jump to a note's source page.
- Edit or remove notes from the panel or page context menu.
- Migrate older local state to schema 3 without losing reading data.

## Performance direction

The first target metric is time-to-first-page:

```text
double-click PDF -> app window appears -> first page is visible
```

Features such as thumbnails, search indexing, annotations, bookmarks, and export should load after the first page or in later phases so startup stays fast.

Phase 2 background work deliberately starts only after the requested page is visible. The cache is limited to five pages to keep memory usage predictable.

Phase 3 keeps text extraction off the time-to-first-page path. Image-only or scanned PDFs need OCR, which is intentionally outside this phase.

Phase 4 state is stored under `%LocalAppData%\ProPdfReader\state\v1`. It remains local to the Windows account and never changes the source PDF.

Bookmarks and highlights use the same local state file. Ctrl+D toggles a page bookmark, while Ctrl+Shift+H highlights the current text selection.

Ctrl+Shift+N adds a note to the current text selection. Notes remain local and are included in the same atomic document-state file.

## Development

Build:

```powershell
dotnet build .\ProPdfReader\ProPdfReader.csproj
```

Run:

```powershell
dotnet run --project .\ProPdfReader\ProPdfReader.csproj -- "D:\path\file.pdf"
```

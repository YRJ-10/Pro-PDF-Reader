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

## Phase 7 scope

- Jump directly to a page by entering its number or pressing Ctrl+L.
- Zoom from 50% to 200% without reopening the PDF.
- Fit the page to the available width or the complete viewport.
- Rotate the page clockwise while keeping text selection and annotations aligned.
- Find text on demand with Ctrl+F and move between matches across pages.
- Keep search indexing lazy so it never delays the first visible page.

## Phase 8 scope

- Add a multi-resolution application and PDF file icon.
- Publish a self-contained Windows x64 build with ReadyToRun startup optimization.
- Produce a portable ZIP with registration and removal commands.
- Produce a per-user installer that does not require administrator access.
- Register Pro PDF Reader for Open with and the Windows Default Apps UI.
- Remove application registration cleanly during uninstall.

## Phase 9 scope

- Prompt for protected PDF passwords without persisting them.
- Keep rendering, text selection, and search available after unlocking.
- Show clear error states for damaged or unsupported PDFs.
- Cap search results at 10,000 entries to bound memory use.
- Open PDFs with Ctrl+O or by dropping one file onto the window.
- Generate SHA-256 checksums and verify release artifacts automatically.

## Phase 11 scope

- Scroll through one continuous document track while virtualizing off-screen pages.
- Preserve fast rendering with the existing five-page image cache and eight-page text cache.
- Select and annotate text independently on every realized page.
- Open internal PDF links and confirm external links before leaving the application.
- Read the PDF outline into a Contents tab when the document provides one.

## Phase 12 scope

- Offer highlight and underline annotations without modifying the source PDF.
- Provide File menu commands for opening, saving local state, and closing.
- Keep tooltips readable in the dark interface.
- Show a thin activity indicator for opening, navigation, and search work.
- Show recent local PDFs on a lightweight start screen.

## Phase 13 scope

- Put PDF Contents first in the document side panel.
- Add middle-mouse autoscroll for continuous reading.
- Build the application, PDF association, and installer icons from `appicon.png`.

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

Ctrl+F opens document search, Ctrl+L focuses the page number, and Ctrl+0 restores fit-width mode. Search and text extraction remain outside the startup path.

## Keyboard and mouse

- Scroll continuously through the complete document without resetting the scrollbar.
- Click the middle mouse button over the document to start or stop autoscroll; press Escape to stop.
- Use Ctrl+mouse wheel or Ctrl++/Ctrl+- to zoom.
- Use Left/Right for the previous or next page; Page Up/Page Down and Space scroll before changing pages.
- Use Home/End for the current page and Ctrl+Home/Ctrl+End for the first or last document page.
- Use Ctrl+O to open, Ctrl+F to find, Ctrl+L to enter a page, Ctrl+S to save local reading state, and Ctrl+W to close.
- Use Ctrl+D for bookmarks, Ctrl+Shift+H for highlights, Ctrl+Shift+U for underlines, and Ctrl+Shift+N for notes.

Windows chooses default applications through its own user-controlled Default Apps page. The installer registers Pro PDF Reader as a PDF candidate and can open that page, but it never replaces the current default silently.

Passwords are held only for the current document session so encrypted PDFs can be rendered and searched. They are never written to the local document-state file.

The v1.0 build targets 64-bit Windows 10 version 2004 or newer. Scanned image-only PDFs can be viewed, but text selection and search require an existing text layer; OCR is outside the v1.0 scope.

## Development

Build:

```powershell
dotnet build .\ProPdfReader\ProPdfReader.csproj
```

Run:

```powershell
dotnet run --project .\ProPdfReader\ProPdfReader.csproj -- "D:\path\file.pdf"
```

Create a self-contained portable ZIP:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\Publish.ps1 -SkipInstaller
```

Create both the portable ZIP and installer after installing Inno Setup 6 or 7:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\Publish.ps1
```

Verify checksums, metadata, and portable contents:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\Verify-Release.ps1
```

Release files and `SHA256SUMS.txt` are written under `artifacts/` and are intentionally ignored by Git. Public releases should be code-signed before distribution; unsigned local builds can trigger a Windows SmartScreen warning. See `RELEASE.md` for the full checklist.

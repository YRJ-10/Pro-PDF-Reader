# Pro PDF Reader

Pro PDF Reader is a lightweight Windows PDF reader focused on fast startup and a clean basic reading experience.

## Phase 1 scope

- Native WPF app shell.
- Open PDF from the toolbar.
- Open PDF from a command-line file path, for future `Open with` support.
- Render the first page quickly.
- Basic previous/next page navigation.

## Performance direction

The first target metric is time-to-first-page:

```text
double-click PDF -> app window appears -> first page is visible
```

Features such as thumbnails, search indexing, annotations, bookmarks, and export should load after the first page or in later phases so startup stays fast.

## Development

Build:

```powershell
dotnet build .\ProPdfReader\ProPdfReader.csproj
```

Run:

```powershell
dotnet run --project .\ProPdfReader\ProPdfReader.csproj -- "D:\path\file.pdf"
```

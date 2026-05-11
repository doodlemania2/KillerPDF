# KillerPDF — Copilot instructions

KillerPDF is a Windows-only WPF PDF editor distributed as a single, portable, Costura-bundled `KillerPDF.exe`. Targets `net48` so end users never need a runtime install. Built with the .NET 8+ SDK on Windows.

## Build / publish / release

- Dev build: `dotnet build` (from repo root). Project is `KillerPDF.csproj`.
- Publish (single-EXE bundle): `dotnet publish -c Release` → output in `bin/Release/net48/publish/KillerPDF.exe`.
  - Publish triggers the `BundleSource` MSBuild target which runs `build/bundle-source.ps1` to produce `KillerPDF-<Version>-src.zip` (GPL3 corresponding source) next to the EXE. It uses `git ls-files`, so an unstaged file will not be included.
  - Publish uses `Properties/PublishProfiles/FolderProfile1.pubxml` (net48, win-x64).
- Full release (Windows only, requires Certum cert + Windows SDK signtool): `./release.ps1` (or `./release.ps1 -SkipSign` for a test run). Builds, signs, prints SHA256, and reminds you to paste it into `pdf-landing/index.html` and the external `killer-tools-site` repo.
- No test suite or linter is configured. `dotnet build` warnings are the only lint signal — do not introduce new warnings (nullable reference types are enabled).

## Cross-platform caveat

The repo lives on macOS in this environment but the project **only builds on Windows** (`UseWPF=true`, `net48`, `win-x64`, native `pdfium.dll` P/Invoke). Do not expect `dotnet build` to succeed here; rely on careful reading instead of compile checks, or hand work off to a Windows runner.

## Architecture

- **Single-window WPF app**, no MVVM, no DI. Everything lives in two files:
  - `MainWindow.xaml.cs` (~3.5k lines) is intentionally a god-class that owns all UI state, tools, rendering, search, signatures, save/flatten, install/uninstall, dialogs, etc. New UI features go here unless there is a strong reason to split.
  - `EditingTypes.cs` holds the annotation/signature data model (`PageAnnotation` and subclasses: `TextAnnotation`, `InkAnnotation`, `HighlightAnnotation`, `TextEditAnnotation`, `SignatureAnnotation`; plus `SavedSignature`/`SerializablePoint` for JSON persistence).
- **Rendering vs. editing split** — three PDF libraries, each used for what it is best at. Don't swap them:
  - **Docnet.Core (PDFium)** — high-quality page raster for on-screen rendering and the "Save Flattened" 150 DPI export. Native `pdfium.dll` is embedded via Costura (`FodyWeavers.xml` → `Unmanaged64Assemblies`).
  - **PdfSharpCore** — page geometry, merging/splitting, and writing annotations into the saved PDF.
  - **PdfPig** (`UglyToad.PdfPig`, aliased as `PdfPigDoc`) — text extraction for search, drag-select copy, and font-name matching during inline text edits.
- **Annotation model** — `_annotations` is `Dictionary<int pageIndex, List<PageAnnotation>>`. The on-screen `AnnotationCanvas` is a transient WPF overlay; on save, annotations are baked into the PDF via PdfSharpCore. "Save Flattened" instead rasterizes pages through PDFium so nothing remains editable.
- **Custom chrome** — `WindowStyle=None` + `AllowsTransparency=True`. There is a `WM_GETMINMAXINFO` hook in `MainWindow_SourceInitialized` so maximize doesn't cover the taskbar; preserve it if you touch window sizing. All dialogs are custom dark-themed windows — do not use `MessageBox.Show` or `OpenFileDialog`'s default chrome for new UI; match the existing pattern.
- **Self-installer** — first-launch Install/Run dialog installs per-user to `%LOCALAPPDATA%\Programs\KillerPDF\` (no UAC), registers PDF file association, and writes an Add/Remove Programs entry; uninstall self-deletes via a deferred batch file. CLI: `KillerPDF.exe "file.pdf"` opens directly (used by the file association).
- **Password-protected PDFs** — prompt for password, write a decrypted copy to a temp file, edit/render against that for the session.
- **Signatures** persist to `signatures.json` next to the EXE (`AppDomain.CurrentDomain.BaseDirectory`). Both drawn (`Strokes`) and imported-image (`ImageData` = base64 PNG) variants share the `SavedSignature` type — `ImageData != null` is the discriminator.

## Conventions

- `net48` target with `LangVersion=latest` + `PolySharp` polyfills. Modern C# syntax is fine, **but** runtime APIs introduced after .NET Framework 4.8 are not. Notably: do not use `Math.Clamp` — use `Math.Min`/`Math.Max` (this regressed once; see CHANGELOG 1.1.0).
- Nullable reference types are enabled project-wide. New code must be null-annotated; do not silence `CS8602` etc. with `!` unless the invariant is genuinely guaranteed.
- XAML-defined controls that the codegen can't resolve are re-fetched in the constructor via `FindName(...)!` and stored in `_camelCase` fields (see the block under `// Manual element refs`). Follow that pattern for new named XAML elements rather than fighting the generator.
- UI palette is centralized in `MainWindow.xaml` resources (`BgDark`, `BgPanel`, `AccentGreen`, `DangerRed`, etc.). Use those brushes; don't hardcode new hex colors. Toolbar glyphs are `Segoe MDL2 Assets`.
- Track unsaved edits by setting `_isDirty = true` on any change that mutates the document, and route close/open paths through the existing dirty-check prompts.
- Versioning: bump `<Version>`, `<AssemblyVersion>`, `<FileVersion>` in `KillerPDF.csproj` together, add a `## [x.y.z] - YYYY-MM-DD` section to `CHANGELOG.md` (Keep a Changelog format, SemVer), and update the compare links at the bottom.

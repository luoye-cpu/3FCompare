# 3FCompare Avalonia Migration Plan

<div align="center">

**English** · [**简体中文**](07-Avalonia迁移规划.zh.md)

</div>

> Status: **migration complete (M0–M5 all passed)** | Created: 2026-08-22 | Completed: 2026-08-22
> WinForms version archived at tag `winforms-final`; prerequisite reading: `docs/02-系统架构.en.md`, section 3 "Hard Constraints" in this doc

## 1. Migration Motivation & Goals

**Motivation**
- Eliminate WinForms multi-HWND architecture chronic issues: DPI-switch white flash, drag tearing, scroll-wheel focus hack (IMessageFilter), IME Disable hack
- GPU composer-thread rendering: animations don't occupy UI thread, thumbnail/timeline animation smoothness improved
- Data binding replaces ~100 resource-key manual `ApplyLanguage()` refresh chains
- Keep Linux/macOS monitoring-center deployment possibility open
- Clean MIT licensing (vs. LakeUI's GPL/paid dual license)

**Non-goals (explicitly not doing)**
- No Core layer migration (engine/SyncController are UI-agnostic, keep untouched)
- No cross-platform first release — Windows first, architecture avoids platform-specific APIs in shared layer
- No changes to native backend FFF.Native interfaces (unless HWND hosting validation fails, see §4 risk R1)

## 2. Existing Asset Inventory (Migration Workload Baseline)

| Asset | Lines | Migration strategy |
|---|---|---|
| MainForm.cs | 1823 | **Rewrite** as MainWindow.axaml + ViewModel split |
| SettingsDialog.cs | 583 | **Rewrite** as axaml (layout system swap makes original TLP code obsolete) |
| Program.cs | 64 | Rewrite as Avalonia AppBuilder startup |
| TransportBar / TimelineView / CompareGridView + 6 other controls | ~1300 | **Rewrite one by one** as Avalonia Control + SkiaSharp custom-draw or axaml composition |
| WgcFrameCapture.cs (PrintWindow capture) | 124 | **Retain** (pure Win32, UI-framework-agnostic) |
| AppTheme.cs | 140 | Convert to Avalonia ResourceDictionary theme |
| LanguageManager.cs | 378 | **Retain**, wrap with IXamlLocalizableProvider or convert to .resx |
| LayoutConstants / Geometry / Dpi | ~210 | Dpi.cs **delete** (Avalonia has built-in DPI), rest retained |
| Core layer (Engine/Sync/Settings/Display) | — | **Zero changes** |

Total rewrite ≈ 4000 lines C# → approx 2500 lines C# + 1500 lines AXAML.

## 3. Hard Constraints (each is an acceptance gate)

1. **Video render hosting**: FFF.Native outputs D3D to a child HWND. Avalonia wraps that HWND with `NativeControlHost`.
   Acceptance: real-mode selftest `state=Playing` + screentest PNG shows picture.
2. **NativeAOT publish**: `PublishAot=true` must remain usable.
   Acceptance: `dotnet publish -r win-x64 -p:PublishAot=true` succeeds and runs correctly.
3. **Package size red line**: lite build ≤ 15MB (current 5.1MB; Skia native + Avalonia estimated +10MB, exceeding triggers re-evaluation).
4. **Feature parity checklist**: 9-stream grid, AB loop, offset alignment, probe, bookmarks, magnifier, difference overlay, bilingual, theme, full shortcut set, --selftest/--screentest/--autodemo automation modes.
5. **Test baseline**: Core.Tests 40 cases continuously pass (unaffected by migration); SmokeTests --demo passes.

## 4. Risk Register

| # | Risk | Probability | Mitigation |
|---|---|---|---|
| R1 | NativeControlHost cannot host D3D child window (focus/DPI/layering combo issues) | medium | **Week 0 PoC first**. Fail → fallback: add SwapChain shared-texture interface to kernel (needs FFF.Native changes, +2 weeks) |
| R2 | AOT under Avalonia compile warnings/runtime crashes | low | Officially fully enabled IsAotCompatible; verify together in PoC |
| R3 | ThumbnailPopup borderless topmost window TopLevel behavior differences in Avalonia | medium | use Popup/Window+ExtendClientAreaToTitleHint approach in PoC |
| R4 | Bilingual system + binding integration complexity | low | LanguageManager retained, bridge with IValueConverter |
| R5 | pack.ps1 full pipeline adaptation | certain | dedicated M5 milestone |

## 5. Milestone Breakdown

### M0 — PoC Validation Week (1 week, go/no-go decision) ✅ 2026-08-22 all pass (go)
- [x] New `src/3FCompare.Avalonia` experimental project (leave existing App untouched)
- [x] PoC-A: NativeControlHost wraps Win32 child window, paints a color block with GDI inside → validate hosting/focus/DPI
- [x] PoC-B: route a real FFF.Native session to that child window, selftest passes Playing (Debug + AOT dual verification, exit code 0)
- [x] PoC-C: AOT publish the experimental project and run (`dotnet publish -r win-x64` single-file 17.5MB, real-mode Playing)
- [x] **Decision point**: all three pass → **proceed to M1**
  - During the process, fixed a Core defect: `DxgiOutputInfo` ComImport dispatch in .NET 11 calls DXGI and returns
    INVALID_CALL; old loop only jumped out on NOT_FOUND → infinite loop (symptom: opening media hangs). Rewritten as bare
    vtable delegate invocation + hard iteration upper bound. WinForms brightness detection never actually worked before (silent null), same fix benefits it.
    Diagnostics tool: `tools/DxgiInteropProbe`.
  - ⚠ Size warning: AOT single-file 17.5MB (excl. ffmpeg/FFF.Native) already exceeds §3.3's 15MB red line → per agreement
    triggers re-evaluation (M5 decision: trim / adjust red line / accept).

### M1 — Skeleton & Infrastructure (1 week) ✅ 2026-08-22
- [x] App theme resources (AppTheme → ThemeResources code assembly + FluentTheme, dark-first)
- [x] LanguageManager bridge (link-time shared compilation + Loc indexer binding source + `{loc:Loc Key}` markup extension,
      auto-refresh on language change; added 3 missing keys: Msg_DemoModeMissingNative / Offset_ValueFmt / Diff_PercentFmt,
      WinForms version benefits too)
- [x] MainWindow skeleton: menu bar (item-by-item parity with WinForms) + grid container placeholder + bottom transport bar/timeline/status bar layout + right sidebar placeholder
- [x] MVVM foundation: CommunityToolkit.Mvvm 8.4.0, MainViewModel taking over MainForm state fields
- [x] Shortcut system (OnKeyDown replicating ProcessCmdKey full table: Space/Ctrl+S/←→/Shift+←→/↑↓/F11/Esc/O/B/P/F6/R/Delete/D1-D9)
- [x] Window geometry memory (position/size/maximized, clamped to work area on restore)

### M2 — Core Playback Surface (2 weeks) ✅ 2026-08-22
- [x] PlayerSurfaceHost: production-grade NativeControlHost — `CreateNativeControlCore` manages positioning/size/DPI;
      child window `WS_EX_TRANSPARENT` lets input tunnel back to Avalonia (solves airspace swallowing input, scroll/drag/click pass through);
      overlay rendered in subclassed WndProc GDI on WM_PAINT (same D3D window info layer pattern as WinForms)
- [x] CompareGridView: custom Control + Core.Display.GridLayout layout reuse, click select, single-view mode,
      2x1/2x2/3x3/Auto presets, empty-state localized prompts
- [x] SyncController wiring: PlaybackCoordinator fully ported (OpenFiles 9-stream clamping / WaitForOpenCompletionAsync
      15s polling / TryAutoPlayAfterOpen counter+callback queue / EngineEvent Dispatcher grouping / failed stream marking)
- [x] TransportBar (axaml composition: dual stepping/loop/add-remove streams/speed/color mode/HH:MM:SS:FF timecode)
- [x] TimelineView (DrawingContext custom-draw: ticks/playhead/loop interval/ScrubPreview throttle/right-click set A/B/A/B keys)
- [x] Snapshot polling (DispatcherTimer 16/250ms adaptive, TickLoop wrap-around, real-mode pseudo-speed corrected Seek)
- [x] File open (FilePicker multi-select) + drag-drop; scroll-wheel zoom (1.15/step, 1..32) + drag pan broadcasts SetViewTransform;
      full shortcut table wired to real actions; fullscreen HideChromeInFullscreen; status bar full info
- Verified: --selftest runs real pipeline all pass (ready wait / frame & second step position non-decreasing assertions / media info / auto-play), exit code 0

### M3 — Tool Panels & Dialogs (2 weeks) ✅ 2026-08-22
- [x] Sidebar ToolsSidebar (5 tabs + collapse button + magnifier permanent toggle + GridSplitter drag-width)
- [x] ProbePanel (TryReadPixel readout + JSON copy) / BookmarkPanel (add/delete/jump + JSON serialization/CSV export) /
      OffsetPanel (±frames/±100ms/align/zero) / MediaInfoPanel (full technical report) / AudioPanel (audio track/volume/mute)
- [x] AbSliderView (placeholder gradient + drag divider, same semantics as WinForms) / DiffOverlayView (96×N grid pixel-sampling heatmap,
      TryReadPixel direct sampling instead of WinForms DrawToBitmap sampling) / MagnifierOverlay
      (IsHitTestVisible=false instead of WS_EX_TRANSPARENT hack)
- [x] SettingsWindow (7 sections: language/hardware accel+GPU enumeration/stepping/window fullscreen/color/layout/FFmpeg path+detect test;
      diff-detection builds new AppSettings; FFmpeg change → restart confirmation flow)
- [x] MessageBox/PromptDialog equivalents (custom lightweight dialogs) + MaybeExitDemoMode missing-piece guide
- [x] Session save/load (.3fcs: paths/offsets/position/loop interval restore)
- [x] Selection coupling (probe/media info/audio/offset panels refresh with selected surface); probe hover read + magnifier follow (pointer tunneling)
- Verified: zero build errors + --selftest all pass (two rounds); panel interaction details left for GUI walkthrough

### M4 — Popups, Capture & Automation (1 week) ✅ 2026-08-22
- [x] ThumbnailPopup (borderless topmost + 250ms auto-hide + GDI→WriteableBitmap bitmap composition);
      timeline drag 150ms throttle frame-capture preview pipeline integrated
- [x] WgcFrameCapture link-time included (pure Win32, UI-framework-agnostic); --screentest captures real
      D3D picture + GDI overlays coexisting (1.4MB PNG, visually confirmed)
- [x] Frame export Ctrl+S (WgcFrameCapture → TryReadPixel per-pixel sampling fallback, same semantics as WinForms)
- [x] --selftest / --screentest (>1000B threshold passes) / --autodemo all ported, exit-code semantics match WinForms
      (note: selftest auto-play assertions must run **before** step assertions — StepFrame pauses playback, same as WinForms behavior)
- [x] Drag-drop open (already in M2)
- Verified: selftest two passes, screentest produces PNG visual confirmation, autodemo two streams 10s survival smoke

### M5 — Packaging, Release & Switch (1 week) ✅ 2026-08-22
- [x] pack.ps1 adapted: points to Avalonia project, EmbedFffNative resource embed + Program self-extract, usage text updated
- [x] Package size measurement vs 15MB red line (scope = dist artifact 7z, same scope as 5.1MB WinForms baseline):
      lite original 35.3MB (Skia/ANGLE native stack cost) → **7z 9.3MB ≤ 15MB ✓**; full exe 20.9MB (kernel embedded)
- [x] Full regression: Avalonia --selftest (Debug + AOT dual pass), --screentest (1.4MB PNG visual confirmation),
      --autodemo two-stream smoke, Core.Tests 40/40, SmokeTests --demo E3 pass
- [x] Feature parity walkthrough (§3.4): 9-stream grid / AB loop / offset alignment / probe / bookmarks / magnifier / difference overlay /
      bilingual (incl. runtime switching) / dark theme / full shortcut set / --selftest --screentest --autodemo — all implemented
- [x] Archive tag `winforms-final` then delete `src/3FCompare.App`; shared files (LanguageManager/WgcFrameCapture)
      moved into Avalonia project (namespace unchanged, zero reference changes)
- [x] slnx / README / PACKAGING_SPEC / docs/07 updated

**Total estimated duration: 8 weeks** (solo full-time; weekly buffer included. M0 fail → decision one week earlier with stop-loss)

## 6. Dual-Track Strategy During Migration

- `src/3FCompare.App` (WinForms) **stays releasable until M5** — bugs found during migration still fixed in the old version
- `src/3FCompare.Avalonia` new project runs in parallel, shares Core layer without conflicts
- At the end of each M milestone, run dual-version selftest side-by-side for comparison
- Branching strategy: `feature/avalonia-migration` long-lived branch, merged into main per M (maintaining large-granularity commit discipline)

## 7. Explicit Technology Mapping Table

| WinForms current | Avalonia target |
|---|---|
| Form / IMessageFilter scroll-wheel hack | Control built-in PointerWheelChanged (no global filter needed) |
| ImeMode.Disable hack | verified experimentally; TextInput event model expected to eliminate issue |
| PerMonitorV2 + AutoScaleMode.Dpi + Dpi.cs | framework built-in DPI scaling, Dpi.cs deleted |
| WinForms Timer (16/250ms adaptive) | DispatcherTimer same logic |
| OnPaint + OptimizedDoubleBuffer | SkiaSharp custom-draw controls (IRenderTargetBitmap) or axaml Shape composition |
| TLP/FlowLayoutPanel layout | Grid/StackPanel/DockPanel (SettingsDialog collapse issue gone) |
| Application.AddMessageFilter | no counterpart needed (events reach directly) |
| Control.MousePosition global mouse | Windows-specific code into platform dir or use Pointer event args |
| ShowDialog modal | Window.ShowDialog (async ShowDialog<T>) |

## 8. Decision Log

- 2026-08-22: plan created. Based on renderbench benchmark (vector drawing gain small, bitmap compositing gain large),
  custom-draw controls uniformly pick SkiaSharp instead of mixing axaml Shapes, keeping drawing code style consistent.
- 2026-08-22: M0 acceptance passed (go). All three PoCs pass; selftest added step-by-step logging + 25s watchdog (exit code 3 = hang diagnosis).
  Key finding: .NET 11 (11.0.100-preview.6) built-in COM interop breaks DXGI ComImport interface dispatch
  (returns INVALID_CALL even with vtable aligned; bare function-pointer call works) — Core's DXGI brightness probe
  therefore rewritten as bare vtable delegate scheme (`DxgiOutputInfo`). This was the single Core fix during M0, a bug fix.
- 2026-08-22: size measured — Avalonia AOT single-file 17.5MB > 15MB red line, re-evaluation deferred to M5.
- 2026-08-22: M5 red-line judgment: scope aligned (dist artifact 7z, same scope as WinForms 5.1MB baseline) — lite build
<div align="center">

# RecallOS

**A memory layer for Windows.**
Records what is on your screen, reads the text on it, and makes all of it searchable —
entirely on your own machine.

[![build](https://github.com/anshppatel4-crypto/recallos/actions/workflows/ci.yml/badge.svg)](https://github.com/anshppatel4-crypto/recallos/actions/workflows/ci.yml)
[![release](https://img.shields.io/badge/release-v1.0.0-6366f1)](https://github.com/anshppatel4-crypto/recallos/releases/latest)
[![platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-6366f1)](#download)
[![website](https://img.shields.io/badge/website-recallos-a855f7)](https://anshppatel4-crypto.github.io/recallos/docs/)
[![proprietary](https://img.shields.io/badge/license-proprietary-636b7c)](LICENSE)
[![local only](https://img.shields.io/badge/data-100%25%20local-34d399)](#privacy)

<img src="docs/main.png" width="880" alt="RecallOS main window" />

</div>

---

It is not a screenshot tool. A screenshot tool gives you files. RecallOS gives you a
searchable substrate: every frame is indexed by its text, labelled with the activity it
shows, placed on a timeline, and reachable by meaning as well as by wording.

| | |
|---|---|
| **Capture** | Win32 GDI `BitBlt` over the virtual screen, one monitor, or the foreground window. Manual, on a global hotkey, or continuously on a timer. |
| **Extract** | Tesseract OCR with word geometry, run off the capture path in a background queue. |
| **Store** | One SQLite database plus a date-foldered image tree, in a single portable folder. |
| **Search** | FTS5/BM25 keyword search, cosine similarity over chunk embeddings, or a blend of both. |
| **Replay** | The original screenshot beside its extracted text, with step-forward/back through time. |
| **Understand** | Per-frame activity labels and an activity histogram you can scrub and click. |

> **v1.0 is out.** More is coming — see the [roadmap](https://anshppatel4-crypto.github.io/recallos/docs/#roadmap).

---

## Download

### **[⬇ Download RecallOS-Setup.exe](https://github.com/anshppatel4-crypto/recallos/releases/download/v1.0.0/RecallOS-Setup.exe)**  ·  [Website](https://anshppatel4-crypto.github.io/recallos/docs/)

Run it. That is the whole process — it installs in a few seconds, needs **no administrator
rights**, adds Start Menu and Desktop shortcuts, registers an uninstaller in Add or Remove
Programs, and opens RecallOS when it finishes. The .NET runtime is bundled, so there is
nothing else to install.

### ⚠ If Windows says it blocked the app

This will happen the first time, and it does **not** mean anything is wrong with the file.

1. Click **More info** on the blue dialog — the button you need is hidden until you do
2. Click **Run anyway**

Or clear the block before opening it: right-click the downloaded file → **Properties** →
tick **Unblock** → **OK**. From PowerShell: `Unblock-File ~\Downloads\RecallOS-Setup.exe`

Windows stamps every downloaded file with a "mark of the web", and SmartScreen refuses
unsigned installers it has not seen before. Code signing is what removes this, and it is an
ongoing cost; every independent release without it behaves the same way.

Prefer no installer? [RecallOS-Portable.zip](https://github.com/anshppatel4-crypto/recallos/releases/download/v1.0.0/RecallOS-Portable.zip)
is the same application as a folder you can unzip and run.

### First run

RecallOS walks you through setup the first time it opens: what gets recorded, how to stop
it, how to find things again, how to switch on text search, and how to exclude windows you
never want captured. The Home screen then tracks what is still left to set up.

Reading the text inside your captures needs a language pack — one click from that
checklist, about 4 MB. It is the only time RecallOS uses the network, and frames captured
before you install it become searchable retroactively.

### Uninstall

Settings → Apps → RecallOS → Uninstall, like any other application. Your recordings live in
`%LOCALAPPDATA%\RecallOS`, deliberately separate from the installed program, so uninstalling
never touches them. Delete that folder if you want them gone too.

---

## Searching

Type words. Everything else is optional:

```
invoice                              words anywhere in the captured text
"pull request"                       an exact phrase
app:chrome                           only frames from a given process
title:github                         window titles containing this
after:yesterday before:2026-03-01    time bounds
on:2026-03-04                        a single day
after:7d   after:24h                 relative offsets
budget app:code after:monday         combined
```

Three ranking modes:

- **Words** — BM25 over the FTS5 index. Exact, fast, and the right answer when you remember
  an actual word you saw.
- **Meaning** — cosine similarity over chunk embeddings. Finds the frame when you remember
  the gist but not the wording.
- **Hybrid** (default) — both, min-max normalised and blended, with a bonus where they
  agree. The two fail in opposite directions, so together they cover more than either.

**Shortcuts** — `Ctrl+Shift+R` capture from anywhere · `Ctrl+F` focus search ·
`Alt+←/→` step through time · `F5` refresh · `Ctrl+Delete` delete the selected frame

<div align="center">
<img src="docs/welcome.png" width="440" alt="First run" />
<img src="docs/settings.png" width="440" alt="Settings" />
</div>

---

## Privacy

The design assumption is that a tool which records your screen has to be auditable by the
person it records.

- **Nothing leaves the machine.** No telemetry, no account, no sync. The only outbound
  request in the entire codebase is the language-pack download, behind an explicit button.
- **Exclusions are absolute.** Processes and window-title phrases on the exclusion list are
  checked before the screen is read, and again against the window that was actually in
  front at the instant of the grab. Excluded frames never reach the disk.
- **Recording is off until you turn it on**, pauses when you are away, and refuses to
  capture a locked session or a secure desktop.
- **The tray icon is visible whenever it runs.** A background recorder with no visible
  handle is exactly the thing people are right to distrust.
- **Limits are enforced, not suggested.** An age cap and a size cap both apply; oldest goes
  first. *Erase everything* deletes frames, text, index and images, then reclaims the space.

Everything lives in `%LOCALAPPDATA%\RecallOS`. Delete that folder and RecallOS has no memory
of anything.

---

## Architecture

```
RecallOS.App  (WPF, MVVM)          Views · ViewModels · tray · global hotkey
      │
RecallOS.Core                      the entire engine, no UI dependency
      ├── Capture/       GDI BitBlt, DPI-aware, perceptual hashing
      ├── Ocr/           Tesseract engine, text normalisation, chunking
      ├── Storage/       SQLite schema + migrations, repository, file store
      ├── Search/        query parser, FTS5 + vector retrieval, snippets
      ├── Intelligence/  embeddings, vector math, intent classification
      ├── Pipeline/      capture pipeline, OCR queue, scheduler
      └── Maintenance/   retention and eviction
```

Every service sits behind an interface and is registered with `TryAdd`, so a host or a test
can substitute its own implementation and have it win. `RecallOS.Core` has no reference to
WPF: the engine could be driven by a CLI or a service with no changes.

### Data model

```
frames ──┬── chunks ──── embeddings        settings
         └── frames_fts (FTS5, external content)
```

`frames` is the record of a moment: when, what image, which window and process, the
extracted text, an activity label. `chunks` are paragraph-sized spans of that text — the
unit of embedding, because a whole screenful averages into meaninglessness and a single line
carries too little. `frames_fts` is an external-content FTS5 index kept in step by triggers,
so the text is indexed without being stored twice.

### Decisions worth knowing

**Capture and OCR are split across a queue.** Grabbing pixels takes tens of milliseconds;
recognising a dense 4K screen takes seconds. Run together, a 5-second interval falls behind
within a minute. The synchronous stage does only what makes the moment durable — capture,
hash, write, insert — and returns. The frame appears in the timeline immediately, marked
pending; text, chunks, embeddings and labels fill in behind it.

**Change detection is perceptual, not byte-wise.** Ten minutes of reading one page produces
twenty byte-different but visually identical PNGs, because a caret blinks and a clock ticks.
A 64-bit dHash compared within a small Hamming distance catches those; a file hash never
would.

**Semantic search has no ANN index, deliberately.** At 256 dimensions a SIMD dot product is
a few nanoseconds, so a few hundred thousand chunks scan in well under a second. An
approximate index would add a structure to build, maintain and corrupt for no gain anyone
could perceive at this scale. `ScoreBySimilarityAsync` is the one place that changes if a
store ever outgrows that.

**The embedding provider is honest about what it is.** `HashingEmbeddingProvider` is feature
hashing over words, bigrams and character trigrams — fuzzy lexical matching that degrades
gracefully where exact matching returns nothing. It carries no learned semantics and will
not match *car* to *automobile*. Every vector is stored against a `ModelId`, so dropping in
a real sentence-transformer means registering a new provider and letting the backfill run:
old and new vectors coexist, are never compared, and the store stays searchable throughout.
No migration, no downtime.

**Record means record.** Deduplicating visually identical frames is an obvious way to
save disk, and it was the default until it was tested against how the feature reads: a
screen that is not changing much produces one frame and then nothing, which is
indistinguishable from a broken recorder. Deduplication is now opt-in, the capture interval
defaults to ten seconds rather than thirty, and a live countdown in the title bar shows the
recorder is still working between frames. Storage is bounded by the retention caps instead,
which prune predictably rather than silently declining to record.

**Rows are deleted before files.** A crash mid-sweep then leaves an orphaned image with no
row — invisible, and reclaimed next sweep. The opposite order would leave rows pointing at
files that no longer exist, which the user meets as broken results in their own history.

**The release is a folder, not a single file.** Single-file publishing is not used because
Tesseract's interop loader resolves its native libraries relative to `Assembly.Location`,
which is empty inside a single-file bundle; it then throws before any fallback and OCR never
initialises. This was found by shipping it and reading the log, and `build/publish.ps1` now
fails the build if the native libraries are missing rather than shipping an app whose text
search silently never works.

---

## Tests

135 tests run against the real SQLite schema, the real FTS5 index, real files on disk, the
real Win32 capture path and the real Tesseract engine rather than mocks — those layers are
almost entirely SQL and P/Invoke, so mocking them would test nothing that could break.

Coverage includes FTS trigger correctness (stale terms must not resurrect after an edit or
delete), `ulong` hash round-tripping through a signed integer column, cascade deletes,
embedding determinism across processes, perceptual-hash tolerances, query-parser hardening
against FTS operator injection, and a GDI object-leak check across repeated captures.

The privacy guarantees are tested as behaviour, not documentation: excluded processes and
title keywords are asserted to produce no database row and no file on disk, for manual
captures as well as automatic ones, and an idle or locked session is asserted never to reach
the screen at all. Recording is covered end to end — the scheduler keeps capturing until it
is paused, and text recorded to the screen is afterwards provably findable by searching for
it. Those tests drive a synthetic screen, so the suite never reads or stores a real desktop.

---

## License

RecallOS is proprietary software. © 2026 Ansh Patel. All rights reserved.

You are licensed to install and use the application on machines you own or control. The
source code is not distributed, and the software may not be copied, redistributed, modified
or reverse engineered. See [LICENSE](LICENSE) for the full terms.

Third-party components (Tesseract OCR, SQLite, .NET libraries) remain under their own
licenses.

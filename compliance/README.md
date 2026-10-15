# Pngr distribution licensing inventory

This directory contains public third-party compliance materials, not the private
implementation-provenance investigation. No proprietary disassembly is included.

- `THIRD-PARTY-NOTICES.txt`: generated desktop notice superset, with original
  upstream texts preserved byte-for-byte and duplicate texts grouped by SHA-256.
- `desktop-dependencies.json`: exact NuGet/native/framework versions, authoritative
  source URLs, original-text hashes, and reviewed RID dependency graphs extracted
  from the published v0.7.5 Velopack packages.
- `velopack-rust-dependencies.json`: exact native updater/setup dependency versions
  from Velopack 1.2.161's pinned upstream Cargo.lock and target dependency trees.
- `licenses/`: original license/NOTICE files referenced by these inventories.
- Website dependency licensing is maintained separately under `website/compliance/`.

`python3 scripts/licensing/generate.py --all` regenerates the desktop superset.
Packaging generates a RID-specific notice and machine-readable inventory before
signing. See `scripts/licensing/README.md` for validation and version updates.

## Distributed versions and notice obligations

The machine-readable inventory is authoritative for individual package names and
platform scope. Versions here describe v0.7.5's actual published packages, rather
than inferring the runtime version from a development SDK.

| Component | Exact version | Distribution and license/notice scope |
| --- | --- | --- |
| Avalonia, Desktop, Native, Win32, X11, Skia, Fluent theme, FreeDesktop, Remote.Protocol | 11.3.20 | Managed assemblies on all supported RIDs; AvaloniaNative dylib on macOS. MIT plus upstream NOTICE.md component terms, including WPF/SharpDX MIT, Silverlight MS-PL, and other included attributions. Preserve the original license and notices. |
| Avalonia.Controls.DataGrid | 11.3.7 | MIT; preserve copyright and permission notice. |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | Windows av_libglesv2.dll. ANGLE BSD-style license plus conservative vendor/runtime notice superset from the exact source commit, including optional components. Preserve required copyright, conditions, disclaimers, and component attributions. |
| SkiaSharp and NativeAssets.macOS/Win32 | 2.88.9 | MIT wrapper plus complete package-supplied THIRD-PARTY-NOTICES, covering Skia and its native components. Retain both. |
| HarfBuzzSharp and NativeAssets.macOS/Win32 | 8.3.1.1 | MIT wrapper plus complete package-supplied native notices. Retain both. |
| MicroCom.Runtime | 0.11.0 | MIT; package copyright metadata and upstream license agree. Exact upstream source revision is not recorded by this NuGet package; see limitations below. |
| Microsoft.Data.Sqlite / Core | 10.0.12 | MIT; preserve original license/copyright. Meta-package plus Microsoft.Data.Sqlite.dll. |
| SQLitePCLRaw.bundle_e_sqlite3 / config.e_sqlite3 / core / provider.e_sqlite3 | 3.0.5 | Apache-2.0. Preserve license and applicable attribution notices, identify modifications if made; these package binaries are redistributed unmodified. |
| SQLite | 3.53.4 | Native e_sqlite3. Package states public domain and references sqlite.org/copyright.html. Preserve that original statement for traceability. |
| Tmds.DBus.Protocol | 0.21.3 | MIT; preserve original COPYING. |
| Velopack managed library and native updater/setup | 1.2.161 | MIT framework plus native Rust component-specific licenses/notices. Exact versions and original texts for 242 third-party dependency entries are recorded separately. Some entries are target-specific; this is not an assertion that every entry is linked into every binary. |
| .NET runtime | 10.0.13 | Self-contained framework on macOS ARM64/x64 and Windows x64. Original LICENSE and THIRD-PARTY-NOTICES from each matching-version, matching-RID runtime pack cover platform-conditional native components. |
| ASP.NET Core runtime | 10.0.13 | Self-contained local web/API framework, same supported RIDs. Original LICENSE and THIRD-PARTY-NOTICES from each matching-version, matching-RID pack. |
| NSIS installer runtime, Modern UI/plugin and compression code | 3.12.0 | Windows installer wrapper. Original COPYING includes zlib/libpng, bzip2 and CPL-1.0 LZMA terms. Pngr uses LZMA compression. Unmodified source is available at the exact NSIS v312 tag/source archive linked in the notices and inventory. Preserve notices and source availability; review CPL obligations when changing the installer engine. |

MIT/BSD-style notice preservation and Apache attribution requirements are not
satisfied by merely listing an SPDX identifier or linking to a project homepage;
the original texts are included. This summary does not replace those texts.
No third-party license is represented as licensing Pngr's own source code.

## Development-only and non-distributed dependencies

Avalonia.BuildServices 11.3.2 is a build-time component, not a shipped assembly.
The .NET SDK, compiler/analyzers, test packages (including xUnit), NSIS compiler,
and Velopack packaging CLI are development tools; their executable code is not
assumed to ship merely because it is installed in CI. However, the .NET apphost,
Velopack updater/setup, and NSIS installer stub/plugins/compression *do* ship and
are covered above. Website tools and bundled browser code have a separate audit.

Restored Skia/HarfBuzz Linux and WebAssembly native-asset packages are not present
in the supported macOS/Windows release dependency graphs. Inventory entries mark
these as `restored-not-redistributed`. Future distributions for those platforms
need a separately reviewed profile. Unmodified operating-system frameworks and
system libraries linked at runtime are not represented as redistributed files.

## Coverage limits and follow-up questions

- The native Velopack inventory was reconstructed from the exact source commit
  declared by NuGet, Cargo.lock, and `cargo tree --locked -p velopack_bins
  --edges normal` for `aarch64-apple-darwin` and `x86_64-pc-windows-msvc` (Windows
  feature enabled). Normal dependencies are included; build/dev edges are
  excluded. Upstream release build feature configuration and final native
  linkage were not independently attested. Retaining a conservative normal
  dependency notice superset reduces omissions but is not that attestation.
- Native ANGLE vendor notices conservatively include optional Vulkan/X11 code;
  exact compiler feature/linkage attestations remain an upstream question.
- MicroCom.Runtime 0.11.0 has no NuGet repository commit or upstream release tag.
  Its package declares MIT and Copyright 2021 Nikita Tsukanov; the retained
  immutable upstream license agrees. The exact source revision is unknown.
- The `fs_at` 0.2.1 Rust crate declares Apache-2.0 but contains no license file;
  its pinned upstream commit also has none. The canonical Apache license and
  unmodified upstream package metadata are retained. Metadata identifies the
  author; copyright ownership is not inferred or invented.


These materials address concrete distribution-notice omissions. They are not a
legal certification, native binary SBOM attestation, or guarantee against claims.

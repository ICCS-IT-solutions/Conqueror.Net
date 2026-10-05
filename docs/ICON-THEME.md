# Icon theme

The folder pane renders its icons from an SVG theme rather than hand-drawn XAML geometry.
This document records where the artwork comes from, how it is curated, and the licensing
position — **read the licensing section before shipping this in anything commercial.**

## Source

`Icons_source/WinXPSVG-plasma5up-scalable-icontheme-blackysgate.de`

- Author: **Blackcrack**, <https://blackysgate.de>
- Format: KDE Plasma 5/6 icon theme. 6223 SVGs, ~403 MB unpacked.
- Every size bucket (`16x16`, `32x32`, `256x256`, …) is a **symbolic link** to a single
  `scalable/` folder, so the pack stores each icon once and serves it at any size.

`Icons_source/` is **gitignored**. It is not redistributed with this repository.

## Curating

Only a small subset is copied into `Assets/Icons/`, flattened to stable semantic keys:

```powershell
pwsh -File tools/import-icon-theme.ps1 -SourceRoot .\Icons_source\<theme-dir>
```

The script dereferences the symlinks (`Copy-Item` does not reliably do this; the script uses
`File.ReadAllBytes`) and reports the size and path count of every icon it copies. The current
selection is **19 icons, ~874 KB**.

`tools/verify-icon-manifest.ps1` re-checks the manifest against a pack without writing
anything, which is the quickest way to see what a candidate theme actually contains.

### Why so few, given the pack is so large

The obvious approach — embed all 6223 SVGs — is not viable, and a few individual icons are not
viable either. Measured, not assumed:

| Icon | Size | Verdict |
| --- | --- | --- |
| `mimetypes/image-x-icon.svg` | **759 KB** | Dropped. 63 paths for a `.ico` list icon. |
| `mimetypes/application-vnd.oasis.opendocument.spreadsheet.svg` | **784 KB** | Dropped, same for `.presentation`. |
| `mimetypes/application-msword.svg` | 345 KB | Dropped — embeds base64 **raster**, so not actually scalable. |
| `mimetypes/application-x-msdownload.svg` | 332 KB | Dropped — also embeds base64 raster. |
| `mimetypes/text-x-generic.svg` | — | Dropped; it is a symlink to `text-plain`, i.e. a duplicate. |

Icons were preferred when pure vector and in the 15–90 KB range. Office documents
(`.doc`/`.xlsx`/`.pptx`) all map to the clean 19 KB `document` page icon because the pack has
no lightweight distinctive icons for them — it only has the raster-baked or 784 KB monsters.

## Adding an icon

1. Pick the SVG in the pack.
2. Check it: no `<image ... base64>`, and under ~100 KB.
3. Add `key = path` to the manifest in `tools/import-icon-theme.ps1` and to the extension map
   in `FileBrowserUi/Icons/FileIconResolver.cs`.
4. Re-run the import script, then `dotnet build`.

Keys are simply the file names in `Assets/Icons/`; `FileIconResolver.UriFor` builds the
`avares://Conqueror.Net/Assets/Icons/<key>.svg` URI.

## Rendering

`Avalonia.Svg.Skia` (MIT) renders the vectors at runtime. `EnableCache="True"` is set on
every icon: the parsed vector is shared across all items showing the same icon, which is what
makes a folder of several hundred entries affordable when it only draws a dozen distinct
categories.

Sizes are per view mode: 48 px in Tiles, 32 px in Icons, 18 px in List and Details, 16 px in
the task pane. The upstream artwork is drawn on a 256×256 grid, so small sizes are scaled
down rather than purpose-drawn — at 18 px the PDF and Word logos stay recognisable, but the
detailed XP artwork is softer than the hand-tuned 16×16 rasters Windows shipped.

## Licensing — please read

**The pack's `COPYING` file states `CC BY-NC-SA` (Attribution-NonCommercial-ShareAlike).**

The theme's `Readme.md` contradicts this. The author writes that icons he made himself are
released **CC0** and "free usable for all of the World (and forbidden to sell and make money
with it!)", and that icons derived from others stay under their original copyright. The
author also states that KDE.org is the final arbiter of the copyright status.

That conflict is unresolved and it is not this repository's to resolve. What is certain:

1. **The NonCommercial term is in the shipped licence file.** Shipping this artwork in a
   product or service that could be used commercially is not safe on the face of the pack's
   own terms.
2. **Some icons are third-party.** The theme credits DeviantArt weather icons by
   `jackseller`, and includes WinNT/Wine icons plus manufacturer logos. Those carry their own
   rights. Manufacturer logos are trademarks.
3. **The artwork depicts Microsoft Windows.** XP icons are effectively recognisable Microsoft
   artwork. Trademark rights are separate from copyright and are not waived by any CC grant.

The 19 files in `Assets/Icons/` were copied in to demonstrate the integration. **Before this
project is published, redistributed or used commercially, replace them.** The plumbing —
`FileIconResolver`, the avares URI format, the view templates — is theme-agnostic, so
swapping in artwork you own means editing one manifest and re-running the import script.

Attribution for the demonstration artwork: icons by Blackcrack, from
<https://blackysgate.de>, used under the terms stated in that pack's `COPYING`.

# Website dependency notices

`THIRD-PARTY-NOTICES.txt` contains verbatim upstream license/notice files for
dependencies observed in emitted Webpack client chunks, plus Tailwind's generated
stylesheet. The static build publishes this file and `dependency-inventory.json`
at the site root; landing-page and documentation navigation link to the notices.
There are no downloaded font files: CSS requests locally installed system fonts.
Pngr's own branding and screenshots are covered by the separate artwork review;
this file makes no ownership assurance about those assets.

`browser-dependencies.json` records exact package versions, source locations, license
notice/attribution requirements, and
SHA-256 hashes of notice inputs. Next's vendored React canary is distinct from the
top-level React package. Where a vendored component supplies no own version, its
exact containing Next package identifies it; it must not be reported as the
version of a similarly named top-level package.

`dependency-inventory.json` also inventories every exact lockfile entry, separating
observed browser/styles code from build/server/test or tree-shaken dependencies.
The inventory's npm license field is metadata, not a legal certification. Native
Sharp/libvips and build-time CSS tooling are not shipped by this static export;
their presence on a build machine is not treated as browser redistribution.

## Review after a dependency change

1. Run `npm ci` and review package changes, including bundled/vendored software.
2. Run `node scripts/assets.mjs && npx next build --webpack` to regenerate the
   client-chunk evidence. This intentionally bypasses the normal build's lock guard
   while producing local review evidence, not a deployable compliance approval.
3. Run `npm run licenses:refresh` to generate proposed notices and inventories.
4. Review the diff and all new licenses/attributions; do not approve merely because
   a generator found a file. Commit reviewed records, then run `npm run build` and
   `npm test`. Normal builds fail on lock, notice-source, or uncovered chunk changes.

Additional authoritative license details:

- `upstream/Apache-2.0.txt` is copied unchanged from
  [the Apache Software Foundation](https://www.apache.org/licenses/LICENSE-2.0.txt).
  ZBSearch's npm LICENSE.md contains only the short source-file notice, so its own
  copyright attribution is retained alongside the complete Apache-2.0 license.
- Lucide's package metadata says ISC, but its complete shipped LICENSE also retains
  MIT terms for Feather-derived icons. Both texts and the combined requirement are
  recorded; metadata alone would miss this attribution.

The other retained external input is `react-remove-scroll-bar@2.3.8`:
its npm artifact declares MIT but omits LICENSE. Its npm-recorded gitHead
`b3b1287aad81def2e2ae707274b74531b61ddbaf` is unavailable in the upstream repository.
The included canonical MIT license is copied unchanged from the author's commit
[`7301c160fda44cb8cf2b9fdfde61efad35736196`](https://github.com/theKashey/react-remove-scroll-bar/blob/7301c160fda44cb8cf2b9fdfde61efad35736196/LICENSE).
Its 2025 copyright attribution is upstream text, not a reconstruction of the
unavailable release's historical notice. Confirmation of historical text remains
an upstream follow-up; the uncertainty is retained in the notice inventory.

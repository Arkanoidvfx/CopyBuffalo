# Typography

The interface uses bundled **IBM Plex Sans** for controls and prose, and **IBM Plex Mono** for code and paths. Static Regular and SemiBold files work without installing fonts and include Cyrillic coverage. Their shared baseline and line metrics keep mixed text/code cards consistent.

| Role | Size / line height |
| --- | --- |
| History entry | 14 / 20 DIP |
| Pinned tile | 12 / 15 DIP |
| Expanded text preview | 13 / 20 DIP |

Use the shared `TextFont` and `MonoFont` resources in `App.xaml`. Equivalent roles use the same size and line height in both families. Use Regular for body text and SemiBold for headings. Icons and special symbols retain their separate font fallbacks. Code detection remains a heuristic; plain multiline text does not always need monospace.

`TypographyChecks.cs` verifies that WPF loads the embedded families and correct weights, covers Russian/English alphabets and command punctuation, and renders synthetic samples at 100%, 125%, and 150% scale. These checks do not replace a live mixed-DPI monitor check.

The fonts are unmodified and distributed under SIL Open Font License 1.1. See [font sources and license](../Assets/Fonts/README.md).

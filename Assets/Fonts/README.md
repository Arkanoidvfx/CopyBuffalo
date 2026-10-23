# Bundled typefaces

Unmodified static TrueType fonts from [IBM Plex](https://github.com/IBM/plex/tree/763c36ef9117782905ae010056dfbe8fd2653a25), revision `763c36ef9117782905ae010056dfbe8fd2653a25`:

- `packages/plex-sans/fonts/complete/ttf/IBMPlexSans-Regular.ttf`
- `packages/plex-sans/fonts/complete/ttf/IBMPlexSans-SemiBold.ttf`
- `packages/plex-mono/fonts/complete/ttf/IBMPlexMono-Regular.ttf`
- `packages/plex-mono/fonts/complete/ttf/IBMPlexMono-SemiBold.ttf`

The upstream `LICENSE.txt` is preserved as `IBM-Plex-OFL.txt` (SIL Open Font License 1.1). All four fonts and the license are WPF resources in the executable; publishing additionally writes a readable `licenses/IBM-Plex-OFL.txt`. No font installation is needed. Icon and symbol fonts remain system fallbacks.

`TextFont` and `MonoFont` in `App.xaml` are the shared family tokens. Sans and Mono have the same x-height, cap height, baseline and line metrics. Use the same size and line height for equivalent content roles; use Regular for body text and SemiBold for headings. The static files avoid relying on WPF variable-font axis support.

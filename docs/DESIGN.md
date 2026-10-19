# Design system

ShellFlow's look is a calm, dark developer aesthetic: VS Code-like restraint with softer geometry, subtle depth and
a single pastel accent. The terminal is always the focus; chrome stays compact and quiet.

## Tokens

All colors are semantic tokens in `src/ShellFlow.App/Themes/Tokens.xaml` (dark = `Default`, light = `Light`).
Components never use raw hex values.

| Token | Dark | Light | Use |
| --- | --- | --- | --- |
| `SfWindowBackground` | `#181A20` | `#F3F4F7` | Window, title bar, status bar |
| `SfSurface` | `#222631` | `#FFFFFF` | Selected tab, cards, overlays |
| `SfSurfaceElevated` | `#292D38` | `#FFFFFF` | Palette, find bar, menus |
| `SfTerminalBackground` | `#111318` | `#FBFBFD` | Pane background |
| `SfTextPrimary` | `#F0F0F5` | `#1A1D24` | Primary text |
| `SfTextSecondary` | `#A0A6B5` | `#545A69` | Secondary text, icons |
| `SfTextTertiary` | `#878EA0` | `#686E7F` | Hints |
| `SfBorderSubtle` | `#343844` | `#DCDFE6` | Hairline borders |
| `SfAccent` | user choice | user choice | Focus, active pane, cursor, indicators |
| `SfSuccess` / `SfWarning` / `SfError` | `#A3DCC5` / `#E8BFA6` / `#F2828F` | `#257A5A` / `#A35A2E` / `#C23B4B` | Status |

Accents (dark / light): Periwinkle `#A6B7FF` / `#4A5FC1`, Sky `#91D8F7` / `#1A7299`, Mint `#A3DCC5` / `#257A5A`,
Peach `#E8BFA6` / `#A35A2E`. Light-theme accents are darker so text and indicators keep contrast on white.

**Contrast** (WCAG 2 ratios, computed from the token values). Dark theme on the window background: primary 15.3:1,
secondary 7.1:1, tertiary 5.3:1, accent 9.0:1 (tertiary drops to 4.6:1 on `SfSurface`). Light theme on `#F3F4F7`:
primary 15.3:1, secondary 6.3:1, tertiary 4.6:1, every accent ≥ 4.7:1. Terminal palettes: every normal ANSI color and
bright black is ≥ 4.9:1 against `#111318` (dark) and ≥ 4.7:1 against `#FBFBFD` (light); ANSI black is intended for
backgrounds. Bright variants are more saturated, not just lighter, so they stay distinct from the default foreground.

## Geometry and spacing

- Radii: small controls 6 px, tabs 7 px, panels/panes 9 px, overlays 12 px. Nothing is pill-shaped.
- Spacing scale: 2 · 4 · 6 · 8 · 12 · 16 · 24. Panes are separated by a 6 px gutter that doubles as the divider hit area.
- Title bar 40 px (tabs and toolbar share it with the brand and workspace switcher), status bar 26 px.
- Icon buttons 32 × 30 px; small (tab close) 22 × 22 px.

## Typography

- UI: the Windows system font (Segoe UI Variable on Windows 11), 12.5–13 px for chrome, 15 px section headers, 20 px page titles.
- Terminal: configurable; default `Cascadia Mono, Cascadia Code, Consolas, monospace` at 13 px, line height 1.2.
- Icons: Segoe Fluent Icons with Segoe MDL2 Assets as fallback — one consistent system icon family.

## States

Hover and pressed are translucent fills (`SfHover` 7 % white / 5 % ink); selection uses a raised surface plus an accent
indicator; focus uses WinUI's system focus visuals. Essential state is never color-only: the selected tab has a raised
background *and* an underline indicator; the active pane has an accent border *and* a filled cursor (inactive panes
show an outline cursor); the status bar always pairs its colored dot with a text label.

## Motion

| Interaction | Duration | Notes |
| --- | --- | --- |
| Hover / pressed fills | 120 ms | Brush transitions |
| Tab selection indicator | 160 ms | Opacity |
| Tab add / remove / reorder | system | WinUI theme transitions |
| Command palette, find bar | 140–160 ms | Fade + 6–8 px slide |
| Settings | 150–180 ms | Fade + 12 px slide |
| New pane | 180 ms | Fade-in |
| Renderer first paint | 140 ms | Fade-in |

The table above is the **Subtle** style. The default **Fluid** style is inspired by Hyprland:

| Interaction | Duration | Motion |
| --- | --- | --- |
| Window opens | 420 ms | Pop-in from 85 % scale with fade |
| New pane | 340 ms | Pop-in from 85 % scale with fade, from the pane's center |
| Split / close / keyboard resize / equalize | 380 ms | Panes glide from their old rectangle to the new one |
| Closing a pane | 170 ms | Shrinks to 90 % and fades out while neighbors glide into the space |
| Switching tabs | 420 ms | Horizontal slide, like changing workspaces; direction follows tab order |
| Command palette | 280 ms | Pop-in |
| Active pane | — | Diagonal two-accent gradient border |

Fluid motion uses Hyprland's popular `overshot` bezier, `cubic-bezier(0.05, 0.9, 0.1, 1.05)`: a fast start and a
soft settle with a hint of overshoot. Every animation runs on the compositor thread through UIElement facade properties
(scale, translation, opacity). Layout is applied immediately at its final size and only the pixels glide ("FLIP"), so
each terminal is resized once, not every frame, and typing is never delayed. Window resizing and divider dragging stay
1:1 with the pointer and are never animated.

Motion follows Windows *Animation effects* by default; Settings → Appearance → Animations can force it on or off, and
*Animation style* picks Fluid or Subtle. When animations are off, slides, pops, glides and list transitions are removed
(short color cross-fades on hover remain). Terminal text is never animated.

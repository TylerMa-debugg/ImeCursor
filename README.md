# ImeCursor

ImeCursor is a small Windows 10/11 tray utility. It draws a badge at the bottom-right corner of the text cursor (the blinking caret where you type). The badge shows which input method is active and whether it is set to Chinese (中) or English (英). When no text cursor can be found, for example on the desktop, the badge follows the mouse pointer instead (configurable).

| Badge | Meaning |
|---|---|
| `EN` (grey) | Plain keyboard layout (English US) |
| `微信 中` (red) | WeType (微信输入法) in Chinese mode |
| `微信 英` (blue) | WeType in English mode (after Shift or Ctrl+Space) |
| `微信 A` / `EN A` (amber) | Caps Lock is on, so you are typing uppercase English |
| `微信 ?` (grey) | The mode cannot be read (usually an app running as administrator) |

With `ShowImeName=0` the badge shows only `中` / `英` / `A` / `EN` / `?`. When the mode changes, the badge briefly grows to 1.4× its size. The tray icon shows the same short text in the same colour, and its tooltip gives the full description. With `TrayShowsState=0` (and before the first reading) the tray shows a neutral grey `输` icon.

## Download

Get `ImeCursor.zip` from the [Releases](../../releases) page, unzip it, and run `install.cmd` (step 2 below). The zip contains the ready-built `ImeCursor.exe`, the default `ImeCursor.ini`, `install.cmd` and this README. Nothing else is needed: it runs on the .NET Framework 4.x that ships with Windows 10 and 11.

Windows SmartScreen may warn about an unsigned download. Choose *More info > Run anyway*, or build the exe yourself from the source (step 1).

## Install

1. Optional, only when building from source: run `build.cmd`. It compiles `ImeCursor.exe` with the built-in .NET Framework 4.x `csc.exe`. No SDK or download is needed. Exit a running ImeCursor first, because a running exe cannot be overwritten.
2. Run `install.cmd`. It copies `ImeCursor.exe`, `ImeCursor.ini` and this README to `%LOCALAPPDATA%\Programs\ImeCursor` enables **Start with Windows**, and starts the program from there. An existing `ImeCursor.ini` in that folder is kept. You can also copy the files to any other folder you can write to by hand. Do not use `Program Files`, because the settings file and the log are written next to the exe.
3. That's it: ImeCursor now starts every time you sign in. To turn that off, untick **Start with Windows** in the tray menu.

Only one copy runs at a time.

**Uninstall:** untick **Start with Windows**, choose **Exit**, then delete the folder.

Tray menu:
- **Show badge**: turns the badge on or off. Double-clicking the tray icon does the same.
- **Start with Windows**: adds or removes the `ImeCursor` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, and marks it as enabled under `...\Explorer\StartupApproved\Run` (recent Windows 11 builds skip startup entries that are not marked there). If you turned it off in *Settings > Apps > Startup* or Task Manager, it shows as unticked, and ticking it turns it back on.
- **Open settings**: opens `ImeCursor.ini` in Notepad.
- **Reload settings**: applies changes made to the INI file. If the file cannot be read, for example because another program has it locked, the current settings stay in effect.
- **Restart as administrator**: lets the badge read the mode of elevated apps. See Limitations.
- **Exit**

## Settings (`ImeCursor.ini`, UTF-8, next to the exe)

If the file is missing, ImeCursor creates it with comments. A value that cannot be parsed is ignored and the default is used. A number outside the allowed range is clamped to that range. Both cases are written to `ImeCursor.log`. If the file was saved in an ANSI code page (such as GBK) instead of UTF-8, it is still read, and the log asks you to save it as UTF-8.

| Section / key | Default | Meaning |
|---|---|---|
| `[General] ShowImeName` | `1` | Show the input-method name before the mode |
| `PollMs` | `80` | How often the input mode is read, in ms (20–2000) |
| `Anchor` | `caret` | `caret` = bottom-right of the text cursor; `mouse` = bottom-right of the mouse pointer |
| `NoCaret` | `mouse` | With `Anchor=caret`, when no text cursor is found: `mouse` = show the badge at the pointer, `hide` = hide it |
| `CaretMs` | `50` | How often the text cursor position is read while it moves, in ms (15–500). When it has been still for a second it is read every 250 ms. |
| `FollowMs` | `15` | Fallback rate (ms, 5–200) for following the pointer. Pointer movement itself wakes ImeCursor immediately. |
| `Opacity` | `235` | Badge opacity, 30–255 |
| `FontName`, `FontSize` | `Microsoft YaHei UI`, `9.5` | Font size in points at 100%; it scales with the monitor's DPI |
| `OffsetX`, `OffsetY` | `auto` | Badge offset in logical px. At the text cursor: from its bottom-right corner, `auto` = 2 px. At the pointer: from the hotspot, `auto` = 0.36 × / 0.50 × the displayed pointer size (`CursorBaseSize` × DPI/96), just clear of the arrow |
| `HideInFullscreen` | `1` | Hide the badge over full-screen apps (games, videos, F11 browsers). Maximized windows are not treated as full screen. |
| `HideWhenCursorHidden` | `0` | Hide the badge while the pointer itself is hidden |
| `FlashOnChange`, `FlashMs`, `FlashScale` | `1`, `700`, `1.4` | Briefly enlarge the badge when the mode changes |
| `TrayShowsState` | `1` | Tray icon shows the current mode |
| `[Colors]` | `Chinese=#E5484D English=#2F80ED Keyboard=#6B7280 Caps=#D97706 Unknown=#6B7280 Text=#FFFFFF` | Colours as `#RRGGBB`. For more contrast with white text, try `Chinese=#DC2626 English=#2563EB Caps=#B45309` |
| `[Text]` | `Native=中 Alpha=英 Caps=A Unknown=?` | Mode glyphs. `Native.0411=あ`, `Native.0412=한` set the glyph for one language |
| `[Rules] <LANGID>` | `auto` | How Chinese or English mode is decided (see below) |
| `[Labels] <LANGID>` | `auto` | Short name shown before the mode, e.g. `0804=微信` |

### How the mode is detected

ImeCursor reads the keyboard layout of the foreground window's input thread (`GetKeyboardLayout`). It then asks the focused window's IME window for its open status and conversion mode, using `WM_IME_CONTROL` through `SendMessageTimeout` with a 50 ms timeout. This runs on a background thread, so the badge never waits on another app and never delays your typing. When you switch to another window, its mode is shown once two readings in a row agree, about 80 ms later. A newly opened window briefly reports a stale mode, so this avoids a wrong flash.

Rules, set per language under `[Rules]`:
- `open`: Chinese when the IME is open. Use this for **WeType**. WeType's conversion mode differs from app to app and does not show the mode, but its open status does.
- `open+conv`: Chinese when the IME is open and the conversion mode is native. Use this for Microsoft Pinyin and most other IMEs.
- `conv`: Chinese when the conversion mode is native.
- `keyboard`: treat the language as a plain keyboard layout.
- `auto` (default): `open` if the only enabled input method for the language is WeType, otherwise `open+conv`.

The text cursor is found in two ways, on a separate background thread: the Win32 system caret (`GetGUIThreadInfo`), which classic Win32, WinForms, WPF, Office and console apps use, and the accessibility (MSAA `OBJID_CARET`) caret, which Chrome, Edge and Electron apps such as VS Code expose. If the text cursor disappears for a moment, for example while you select text, the badge stays in place for half a second instead of jumping to the pointer.

Caps Lock always takes priority and shows `A`. The IME languages 0804, 0404, 0C04, 1004, 1404, 0411 and 0412 use `auto` unless the INI sets them. Deleting their line keeps `auto`; to make one a plain keyboard, set it to `keyboard`. All other languages are plain keyboard layouts.

With `auto`, the input-method name comes from the user's enabled input methods (`HKCU\Control Panel\International\User Profile`):
WeType → 微信, Microsoft Pinyin → 微软, Sogou → 搜狗, QQ → QQ, Baidu → 百度; anything else uses the first two characters of its description.

## Diagnostic modes

These modes do not create a tray icon and are not limited to one instance.

```
ImeCursor.exe --probe <log> <seconds> [app]   log every change: time, process, hkl, open, conv, caps, decided state, badge text
ImeCursor.exe --render <out.png>              every badge at 100/150/200% on light and dark backgrounds, plus the flash size and tray icons
ImeCursor.exe --bench <seconds> [out.txt]     cycle all states on the real overlay; print GDI/USER objects, handles, memory, CPU
ImeCursor.exe --caret <log> <seconds>         log every change of the foreground app's text cursor rectangle and its source
```

Setting the environment variable `IMECURSOR_PROFILE=1` makes the tray app write its own timing to `ImeCursor.log` every 10 s.

## Limitations

- **Admin windows show `?`.** Windows (UIPI) blocks a normal process from querying windows of elevated processes. Use *Restart as administrator* to fix this, but note that *Start with Windows* starts ImeCursor non-elevated.
- **Several input methods for one language.** If, for example, both WeType and Microsoft Pinyin are enabled for Chinese, another process cannot tell which one is active. The label then shows `中文`, and the `auto` rule falls back to `open+conv`, which is wrong for WeType. In that case set `[Labels]` and `[Rules]` for the language yourself.
- WeType remembers its mode per window, so the badge follows the focused window and can change when you switch apps. This is correct behaviour.
- **Apps that do not expose their text cursor.** Some apps draw their own caret without reporting it (some games, Java apps, and some UWP/WinUI apps). There the badge falls back to the mouse pointer (or hides with `NoCaret=hide`).
- In Chrome, Edge and Electron apps the text cursor is read through accessibility queries several times per second. This costs about 1–2% of one CPU core while such a window is in front. Set `Anchor=mouse` if you prefer the pointer, which costs almost nothing.
- The `auto` offset at the mouse pointer assumes the standard Windows arrow shape. With a custom pointer scheme, set `OffsetX` and `OffsetY` if the badge touches the pointer or sits too far away.
- Classic consoles (conhost) and Windows Terminal are supported. Some games and apps with unusual input handling may not answer IME queries. Those show `?`.
- The badge stays above normal and topmost windows, but not above system UI such as the Start menu.

## License

MIT. See [LICENSE](LICENSE).

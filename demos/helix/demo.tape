# Formatting T-SQL in Helix: one piped selection, then format on write.
# Rendered with VHS (https://github.com/charmbracelet/vhs).
#
#   npm run helix           regenerate docs/images/helix.gif and helix.png
#
# Read demos/helix/render.mjs before editing. The file this opens is the same
# demos/terminal/before.sql the other two demos use.

Output docs/images/helix.gif

Require hx

Set Shell bash
Set FontFamily "DejaVu Sans Mono"
Set FontSize 16
Set Width 1000
Set Height 560
Set Padding 22
Set Theme "Catppuccin Mocha"
Set TypingSpeed 55ms
Set WindowBar Colorful
Set BorderRadius 9
Set Margin 16
Set MarginFill "#1e1e2e"

Hide
Type "cd $DEMO_DIR" Enter
Type "clear" Enter
Show

Sleep 600ms

Type "hx orders.sql" Sleep 400ms Enter
Sleep 3000ms

# First act: lines 3 to 7 are one complete SELECT. `x` extends the selection a
# line at a time, and `|` pipes it through a command, replacing it with the
# output. No plugin, no configuration — this is Helix's own pipe.
Type "3G" Sleep 600ms
Type "xxxxx" Sleep 900ms

# Slower here, and a real pause before Enter. This is the one command a viewer has
# to actually read — the rest of the demo is motions, and a keypress they cannot
# make out is a keypress they cannot repeat.
Set TypingSpeed 130ms
Type "|maxdop"
Set TypingSpeed 55ms
Sleep 1600ms
Enter

# Escape closes the prompt, so the frame is the buffer rather than a completion
# list sitting over it.
Sleep 1800ms
Escape
Sleep 2200ms

# Second act: auto-format on write finishes the file — the statements outside the
# selection, and the indentation the whole procedure needs.
Type ":w" Sleep 700ms Enter
Sleep 2600ms

# gg collapses the selection and returns to the top, so the closing frame is the
# whole formatted file rather than a mid-file view under a block of highlight.
Type "gg" Sleep 400ms
Sleep 1200ms

Screenshot docs/images/helix.png

Sleep 2400ms

Type ":q" Enter
Sleep 900ms

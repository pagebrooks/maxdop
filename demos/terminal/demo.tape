# The terminal demo: one animated GIF and one still PNG, both produced by driving
# the real maxdop binary through a real shell. Rendered with VHS
# (https://github.com/charmbracelet/vhs).
#
#   npm run terminal            regenerate docs/images/demo.gif and demo.png
#
# Read demos/terminal/render.mjs before editing: the file this types at is written
# from before.sql, and the output is asserted against a committed snapshot, so a
# layout change fails the build rather than leaving the GIF advertising output the
# formatter stopped producing.

Output docs/images/demo.gif

Require maxdop

Set Shell bash
Set FontFamily "DejaVu Sans Mono"
Set FontSize 16
# Geometry is derived, not guessed. DejaVu Sans Mono advances 0.602em, and VHS's
# Width counts margin and padding, so this is 116 columns by 37 rows of content.
# Both numbers have a job: 116 clears the ugly input's 105-character line and the
# formatted output's 82, so nothing soft-wraps — a wrapped line in the formatted
# half reads as the formatter failing, which is the opposite of the point — and 37
# rows holds both blocks at once, so the `cat` that produced the mess is still on
# screen beside the result rather than having scrolled away. Height is then cut to
# what those rows actually occupy: dead space below the prompt costs nothing to a
# viewer watching full size and everything to a gallery thumbnail, where it shrinks
# the part anyone is trying to read.
#
# 34 rows is the whole demo — mess, result, and the CI gate — with nothing cleared
# in between. An earlier cut ran the gate on a fresh screen and the GIF spent its
# last seconds on four lines of text above 500 pixels of nothing, which is a poor
# place for a loop to rest.
Set Width 1200
Set Height 745
Set Padding 22
Set Theme "Catppuccin Mocha"
Set TypingSpeed 55ms
Set WindowBar Colorful
Set BorderRadius 9
Set Margin 16
Set MarginFill "#1e1e2e"

# Setup is hidden: a viewer should see a formatter, not a fixture being staged.
Hide
Type "cd $DEMO_DIR" Enter
Type "clear" Enter
Show

Sleep 700ms

Type "cat orders.sql" Sleep 400ms Enter
Sleep 2600ms

Type "maxdop orders.sql" Sleep 500ms Enter
Sleep 900ms

# The still preview is taken here rather than at the end, with the mess and the
# result both on screen and the CI lines not yet typed: in a gallery of thumbnails
# the transformation is the whole pitch, a frame showing only tidy SQL could have
# come from any editor, and two more commands would only shrink the type.
Screenshot docs/images/demo.png

Sleep 3200ms

# The rest of the pitch, and the reason this is not just another formatter: an
# exit code a pipeline can gate on.
Type "maxdop --write orders.sql" Sleep 400ms Enter
Sleep 1400ms

Type "maxdop --check orders.sql && echo 'CI: formatting is clean'" Sleep 400ms Enter
Sleep 3000ms

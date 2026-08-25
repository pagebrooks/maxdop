# Format on save in Neovim, recorded against the real editor and the real binary.
# Rendered with VHS (https://github.com/charmbracelet/vhs).
#
#   npm run nvim            regenerate docs/images/nvim.gif and nvim.png
#
# Read demos/nvim/render.mjs before editing. The file this opens is the same
# demos/terminal/before.sql the README's CLI demo uses, so both images show the
# same input becoming the same output and only the driver differs.

Output docs/images/nvim.gif

Require nvim

Set Shell bash
Set FontFamily "DejaVu Sans Mono"
Set FontSize 16
# 18 formatted lines plus Neovim's status and command lines have to be on screen
# at once, because the payoff is the whole buffer snapping into shape — a demo
# that scrolls at the moment of the reformat has hidden the thing it is showing.
# Width clears the formatted output's 82 columns plus the number column.
Set Width 1000
Set Height 560
Set Padding 22
Set Theme "Catppuccin Mocha"
Set TypingSpeed 55ms
Set WindowBar Colorful
Set BorderRadius 9
Set Margin 16
Set MarginFill "#1e1e2e"

# Staging is hidden: a viewer should see an editor, not a fixture being placed.
Hide
Type "cd $DEMO_DIR" Enter
Type "clear" Enter
Show

Sleep 600ms

Type "nvim orders.sql" Sleep 400ms Enter

# Long enough for the ugly file to be read, not just glimpsed. This is the half a
# viewer has to recognise — one run-on procedure, no indentation — or nothing that
# follows means anything.
Sleep 3000ms

# First act: the keymap. Lines 3 to 7 are one complete SELECT, so selecting them
# and pressing <leader>f formats that statement and nothing else. Choosing a whole
# statement is the point — a selection that is not one is refused rather than
# quietly widened, which is what the surrounding docs are about.
Type "3G" Sleep 500ms
Type "V" Sleep 400ms
Type "7G" Sleep 900ms

# Space is <leader> in the demo config.
Type " f" Sleep 200ms

# The SELECT becomes thirteen laid-out lines while the CREATE PROCEDURE line above
# it and the END below stay exactly as they were. That contrast is the whole point
# of this act and needs a beat to land.
Sleep 3400ms

# Second act: format on save finishes the job — the statements the selection did
# not cover, and the indentation the whole procedure needs.
Type ":w" Sleep 700ms Enter
Sleep 2600ms

# The still is taken here, on the finished buffer with the write reported.
Screenshot docs/images/nvim.png

Sleep 2600ms

Type ":q" Sleep 300ms Enter
Sleep 900ms

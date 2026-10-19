#!/usr/bin/env python3
"""Draw the DMG drag arrow between the two icon slots, in place.

build-dmg.sh positions the app and Applications icons by percentage, so the arrow that tells the user
to drag one onto the other has to be placed from the same numbers, after the background has been
scaled to the window size. Keeping it here instead of baked into images/social.png means the arrow
follows the percentages rather than drifting when the layout or the artwork changes; the artwork only
has to keep the light panel that the icons and the arrow are read against.

Style: solid block, near black. It reads on the light panel without borrowing the product colour,
which belongs to the icons rather than to a movement hint.

Usage: draw-dmg-arrow.py <background.png> <app-icon-x> <applications-icon-x> <icons-y> [icon-size]
"""

import sys

if len(sys.argv) not in (5, 6):
    print(__doc__.strip(), file=sys.stderr)
    sys.exit(2)

try:
    from PIL import Image, ImageDraw
except ImportError:
    print("draw-dmg-arrow.py: Pillow is required (python3 -m pip install Pillow)", file=sys.stderr)
    sys.exit(1)

background = sys.argv[1]
app_x, apps_x, icons_y = (int(value) for value in sys.argv[2:5])
icon_size = int(sys.argv[5]) if len(sys.argv) == 6 else 84

# Clear space between an icon and the arrow, so neither touches it.
pad = 10
# Solid block: this shaft, then a head this wide and this tall.
shaft_thickness = 12
head_width, head_height = 22, 30
# Near black. The panel behind the slots is light (measured L 183-223 at the current 10%/30%), so no
# shadow or outline is needed.
colour = (10, 10, 10)

left = app_x + icon_size // 2 + pad
right = apps_x - icon_size // 2 - pad

# An arrow squeezed below this would read as a dash, and would mean the layout lost the room the
# wider 10%/30% spacing exists to provide. Leave the background alone rather than draw a lie.
if right - left < 40:
    print("draw-dmg-arrow.py: only " + str(right - left) + "px between the icon slots; "
          + "leaving the background unchanged", file=sys.stderr)
    sys.exit(0)

image = Image.open(background)
if image.mode not in ("RGB", "RGBA"):
    image = image.convert("RGBA")
fill = colour + (255,) if image.mode == "RGBA" else colour

draw = ImageDraw.Draw(image)
head_left = right - head_width
draw.rectangle([left, icons_y - shaft_thickness // 2, head_left, icons_y + shaft_thickness // 2], fill=fill)
draw.polygon([(head_left, icons_y - head_height // 2), (right, icons_y), (head_left, icons_y + head_height // 2)], fill=fill)
image.save(background)

print("draw-dmg-arrow.py: arrow drawn x " + str(left) + ".." + str(right) + " at y " + str(icons_y)
      + " over " + background)

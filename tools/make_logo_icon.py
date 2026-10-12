# Usage: python tools/make_logo_icon.py src/PCButler/icon.png src/PCButler/logo.ico preview.png
# Builds logo.ico (the tray and the File Explorer menu): the logo on a transparent background, cropped tight, lines thickened so they survive 16 px.
import sys
from PIL import Image, ImageFilter
src, out, sheet = sys.argv[1:4]
im = Image.open(src).convert("RGBA")
r, g, b, a = im.split()
a = a.point(lambda v: 255 if v > 40 else v * 6)       # the art is drawn at ~70% opacity; make it solid
im = Image.merge("RGBA", (r, g, b, a))
im = im.crop(im.getbbox())
side = max(im.size) + 8
square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
square.paste(im, ((side - im.width) // 2, (side - im.height) // 2), im)
frames = []
for size in [16, 20, 24, 32, 40, 48, 64, 256]:
    art = square
    if size <= 32:                                    # thicker strokes for the small sizes
        grow = 9 if size <= 20 else 7
        alpha = art.split()[3].filter(ImageFilter.MaxFilter(grow))
        art = Image.new("RGBA", art.size, (0x5f, 0xd3, 0xa6, 255)); art.putalpha(alpha)
    frames.append(art.resize((size, size), Image.LANCZOS))
frames[-1].save(out, format="ICO", sizes=[f.size for f in frames], append_images=frames[:-1])
preview = Image.new("RGBA", (420, 140), (32, 32, 32, 255))
light = Image.new("RGBA", (420, 70), (240, 240, 240, 255)); preview.paste(light, (0, 70))
x = 4
for f in frames[:-1]:
    big = f.resize((f.width * 2, f.height * 2), Image.NEAREST)
    preview.paste(big, (x, 2), big); preview.paste(big, (x, 72), big); x += big.width + 6
preview.save(sheet)

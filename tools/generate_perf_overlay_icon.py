from pathlib import Path
from io import BytesIO
import struct
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent.parent
out = root / 'artifacts'
size = 16
im = Image.new('RGBA', (size, size), (44, 132, 242, 255))
d = ImageDraw.Draw(im)
navy = (22, 30, 48, 255)
panel = (30, 43, 66, 255)
teal = (63, 220, 190, 255)
blue = (67, 139, 255, 255)
slate = (89, 109, 143, 255)
white = (239, 245, 255, 255)

# Full-bleed square tile; every corner and edge has an opaque pixel.
d.rectangle((0, 0, 15, 15), fill=blue)
d.rectangle((1, 1, 14, 14), fill=navy)
d.rectangle((2, 2, 13, 12), fill=panel)
# Small status pixels
for x, color in ((3, teal), (5, blue), (7, slate)):
    d.point((x, 3), fill=color)
# Centered frame cadence trace
d.line([(3, 8), (5, 8), (6, 6), (8, 7), (10, 5), (12, 5)], fill=teal, width=1)
for x, y in ((6, 6), (10, 5), (12, 5)):
    d.point((x, y), fill=white)
# Baseline and five evenly spaced metric bars
d.line((3, 10, 12, 10), fill=slate, width=1)
for x, top, color in ((4, 11, blue), (6, 12, teal), (8, 11, blue), (10, 11, teal), (12, 12, blue)):
    d.line((x, top, x, 12), fill=color, width=1)

out.mkdir(exist_ok=True)
sizes = (16, 24, 32, 48, 64, 128, 256)
entries = []
for icon_size in sizes:
    scaled = im.resize((icon_size, icon_size), Image.Resampling.NEAREST)
    data = BytesIO()
    scaled.save(data, format='PNG')
    entries.append((icon_size, data.getvalue()))

# Assemble an ICO directly so every embedded size comes from nearest scaling.
directory_size = 6 + 16 * len(entries)
offset = directory_size
directory = bytearray()
payload = bytearray()
for icon_size, data in entries:
    dimension = 0 if icon_size == 256 else icon_size
    directory.extend(struct.pack(
        '<BBBBHHII', dimension, dimension, 0, 0, 1, 32, len(data), offset
    ))
    payload.extend(data)
    offset += len(data)

ico = struct.pack('<HHH', 0, 1, len(entries)) + directory + payload
(out / 'PerfOverlay-icon.ico').write_bytes(ico)
im.resize((256, 256), Image.Resampling.NEAREST).save(out / 'PerfOverlay-icon.png')

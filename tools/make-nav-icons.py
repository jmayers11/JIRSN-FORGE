"""Draws the extra JIRSN Forge nav icons in the house style: glossy orange, gunmetal panels, light-grey paper.

Each icon is drawn on a 512px canvas with masks, filled with vertical gradients, given a darker rim and a soft top
highlight, then reduced to 56px with LANCZOS (the existing set is 46-57px wide, ~54-61px tall, transparent).
"""
import math
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 512
OUT = 56

ORANGE = ((255, 150, 24), (214, 34, 0))
ORANGE_RIM = (150, 22, 0)
METAL = ((92, 108, 116), (34, 42, 48))
METAL_RIM = (20, 24, 28)
PAPER = ((250, 250, 250), (206, 208, 210))
PAPER_RIM = (150, 152, 156)


def gradient(top, bottom, box=(0, 0, S, S)):
    g = Image.new('RGBA', (S, S))
    d = ImageDraw.Draw(g)
    y0, y1 = box[1], box[3]
    for y in range(S):
        t = min(max((y - y0) / max(1, (y1 - y0)), 0), 1)
        d.line([(0, y), (S, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    return g


def mask():
    return Image.new('L', (S, S), 0)


def paint(canvas, m, palette, rim, rim_width=10, gloss=True):
    """Fill mask m with a gradient over its own bounds, a darker rim and a top gloss."""
    bbox = m.getbbox()
    if not bbox:
        return
    fill = gradient(*palette, box=bbox)
    edge = ImageChops.subtract(m, m.filter(ImageFilter.MinFilter(rim_width * 2 + 1)))
    layer = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    layer.paste(fill, (0, 0), m)
    layer.paste(Image.new('RGBA', (S, S), rim + (255,)), (0, 0), edge)
    if gloss:
        x0, y0, x1, y1 = bbox
        g = mask()
        ImageDraw.Draw(g).ellipse([x0 - (x1 - x0) * 0.2, y0 - (y1 - y0) * 0.55, x1 + (x1 - x0) * 0.2, y0 + (y1 - y0) * 0.45], fill=70)
        g = ImageChops.multiply(g, m.filter(ImageFilter.MinFilter(rim_width * 2 + 1)))
        layer.paste(Image.new('RGBA', (S, S), (255, 255, 255, 255)), (0, 0), g.filter(ImageFilter.GaussianBlur(6)))
    canvas.alpha_composite(layer)


def shadow(canvas):
    a = canvas.getchannel('A').filter(ImageFilter.GaussianBlur(10))
    sh = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    sh.putalpha(a.point(lambda v: int(v * 0.35)))
    out = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    out.alpha_composite(sh, (6, 10))
    out.alpha_composite(canvas)
    return out


def shape(fn):
    m = mask()
    fn(ImageDraw.Draw(m))
    return m


def stroke(points, width):
    return shape(lambda d: d.line(points, fill=255, width=width, joint='curve'))


def ring(cx, cy, r, w):
    return shape(lambda d: d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=255, width=w))


def disc(cx, cy, r):
    return shape(lambda d: d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255))


def rrect(box, r):
    return shape(lambda d: d.rounded_rectangle(box, r, fill=255))


def poly(points):
    return shape(lambda d: d.polygon(points, fill=255))


def union(*ms):
    out = mask()
    for m in ms:
        out = ImageChops.lighter(out, m)
    return out


def minus(a, b):
    return ImageChops.subtract(a, b)


def arrow_head(tip, angle, size):
    a = math.radians(angle)
    left = (tip[0] - size * math.cos(a - 0.5), tip[1] - size * math.sin(a - 0.5))
    right = (tip[0] - size * math.cos(a + 0.5), tip[1] - size * math.sin(a + 0.5))
    return poly([tip, left, right])


def arc(cx, cy, r, start, end, w):
    return shape(lambda d: d.arc([cx - r, cy - r, cx + r, cy + r], start, end, fill=255, width=w))


# ---------------------------------------------------------------- icons

def bell(c):
    body = union(
        shape(lambda d: d.chord([126, 70, 386, 330], 180, 360, fill=255)),
        poly([(126, 200), (386, 200), (420, 380), (92, 380)]),
        rrect((70, 360, 442, 412), 24))
    paint(c, disc(256, 430, 44), METAL, METAL_RIM, 8)
    paint(c, body, ORANGE, ORANGE_RIM)
    paint(c, rrect((236, 40, 276, 90), 16), ORANGE, ORANGE_RIM, 6, gloss=False)


def handover(c):  # Shift continuity: a clock with a circular hand-over arrow
    paint(c, disc(256, 256, 150), PAPER, PAPER_RIM, 8)
    paint(c, union(stroke([(256, 256), (256, 160)], 26), stroke([(256, 256), (320, 290)], 26)), METAL, METAL_RIM, 5, gloss=False)
    paint(c, disc(256, 256, 20), METAL, METAL_RIM, 5, gloss=False)
    a = union(arc(256, 256, 205, 200, 340, 44), arc(256, 256, 205, 20, 160, 44),
              arrow_head((256 + 205 * math.cos(math.radians(340)) + 10, 256 + 205 * math.sin(math.radians(340)) + 30), 75, 90),
              arrow_head((256 + 205 * math.cos(math.radians(160)) - 10, 256 + 205 * math.sin(math.radians(160)) - 30), 255, 90))
    paint(c, a, ORANGE, ORANGE_RIM, 8)


def horizon(c):
    paint(c, shape(lambda d: d.pieslice([116, 150, 396, 430], 180, 360, fill=255)), ORANGE, ORANGE_RIM)
    rays = union(*[stroke([(256 + 175 * math.cos(math.radians(a)), 290 + 175 * math.sin(math.radians(a))),
                           (256 + 235 * math.cos(math.radians(a)), 290 + 235 * math.sin(math.radians(a)))], 26)
                   for a in (195, 225, 255, 285, 315, 345)])
    paint(c, rays, ORANGE, ORANGE_RIM, 6, gloss=False)
    paint(c, rrect((40, 300, 472, 350), 18), METAL, METAL_RIM, 7)
    paint(c, rrect((110, 380, 402, 416), 16), METAL, METAL_RIM, 6, gloss=False)


def integrity(c):
    cyl = union(rrect((96, 90, 376, 420), 30), shape(lambda d: d.ellipse([96, 50, 376, 140], fill=255)),
                shape(lambda d: d.ellipse([96, 370, 376, 460], fill=255)))
    paint(c, cyl, METAL, METAL_RIM)
    bands = union(arc(236, 200, 140, 20, 160, 14), arc(236, 310, 140, 20, 160, 14))
    paint(c, minus(bands, mask()), PAPER, PAPER_RIM, 2, gloss=False)
    paint(c, shape(lambda d: d.ellipse([110, 60, 362, 128], fill=255)), PAPER, PAPER_RIM, 6, gloss=False)
    paint(c, disc(372, 372, 112), ORANGE, ORANGE_RIM)
    paint(c, stroke([(318, 372), (360, 414), (432, 330)], 34), PAPER, PAPER_RIM, 4, gloss=False)


def clipboard(c, mark):
    paint(c, rrect((92, 70, 420, 470), 30), METAL, METAL_RIM)
    paint(c, rrect((126, 110, 386, 440), 10), PAPER, PAPER_RIM, 6, gloss=False)
    paint(c, rrect((186, 40, 326, 120), 22), ORANGE, ORANGE_RIM, 8)
    mark(c)


def qms(c):
    def mark(c):
        for y in (190, 280, 370):
            paint(c, stroke([(160, y), (186, y + 24), (226, y - 24)], 22), ORANGE, ORANGE_RIM, 4, gloss=False)
            paint(c, rrect((250, y - 12, 356, y + 12), 10), METAL, METAL_RIM, 3, gloss=False)
    clipboard(c, mark)


def audit(c):
    def mark(c):
        for y in (180, 240, 300):
            paint(c, rrect((160, y - 11, 350, y + 11), 10), METAL, METAL_RIM, 3, gloss=False)
        paint(c, stroke([(360, 380), (450, 470)], 52), METAL, METAL_RIM, 8)
        paint(c, disc(310, 330, 78), PAPER, PAPER_RIM, 4, gloss=True)
        paint(c, ring(310, 330, 96, 38), ORANGE, ORANGE_RIM, 7)
    clipboard(c, mark)


def review(c):  # management review: a chart board on an easel
    paint(c, union(stroke([(256, 380), (256, 470)], 30), stroke([(256, 380), (150, 480)], 30), stroke([(256, 380), (362, 480)], 30)),
          METAL, METAL_RIM, 6, gloss=False)
    paint(c, rrect((56, 60, 456, 390), 26), ORANGE, ORANGE_RIM)
    paint(c, rrect((92, 100, 420, 350), 10), PAPER, PAPER_RIM, 6, gloss=False)
    bars = union(rrect((130, 270, 180, 330), 6), rrect((210, 220, 260, 330), 6), rrect((290, 170, 340, 330), 6))
    paint(c, bars, METAL, METAL_RIM, 5, gloss=False)
    paint(c, union(stroke([(140, 240), (230, 190), (300, 140), (380, 120)], 18), arrow_head((395, 116), -10, 60)), ORANGE, ORANGE_RIM, 4, gloss=False)


def commercial(c):  # customer & provider: two boxes exchanging
    paint(c, rrect((40, 250, 220, 430), 22), METAL, METAL_RIM)
    paint(c, rrect((292, 250, 472, 430), 22), METAL, METAL_RIM)
    paint(c, union(rrect((110, 250, 150, 330), 4), rrect((362, 250, 402, 330), 4)), PAPER, PAPER_RIM, 3, gloss=False)
    top = union(arc(256, 260, 150, 200, 330, 40), arrow_head((256 + 150 * math.cos(math.radians(330)) + 8, 260 + 150 * math.sin(math.radians(330)) + 26), 75, 86))
    paint(c, top, ORANGE, ORANGE_RIM, 7)
    paint(c, disc(256, 120, 1), ORANGE, ORANGE_RIM)


def scenarios(c):
    lines = union(stroke([(120, 256), (250, 256)], 34), stroke([(250, 256), (380, 120)], 34),
                  stroke([(250, 256), (390, 256)], 34), stroke([(250, 256), (380, 392)], 34))
    paint(c, lines, ORANGE, ORANGE_RIM, 6, gloss=False)
    paint(c, disc(100, 256, 62), METAL, METAL_RIM)
    for (x, y) in ((400, 110), (412, 256), (400, 402)):
        paint(c, disc(x, y, 52), ORANGE, ORANGE_RIM)


def inbox(c):  # My Work
    tray = minus(rrect((50, 250, 462, 450), 34), poly([(130, 250), (382, 250), (340, 330), (172, 330)]))
    paint(c, rrect((120, 60, 392, 300), 14), PAPER, PAPER_RIM, 6, gloss=False)
    for y in (120, 170, 220):
        paint(c, rrect((160, y - 10, 352, y + 10), 8), METAL, METAL_RIM, 3, gloss=False)
    paint(c, tray, ORANGE, ORANGE_RIM)


def factory(c):  # Plant Setup
    body = poly([(40, 450), (40, 250), (150, 320), (150, 250), (260, 320), (260, 200), (330, 200), (330, 90), (400, 90), (400, 200), (472, 200), (472, 450)])
    paint(c, body, METAL, METAL_RIM)
    for x in (80, 190, 300, 400):
        paint(c, rrect((x, 370, x + 50, 410), 6), ORANGE, ORANGE_RIM, 4, gloss=False)
    gear = union(disc(380, 300, 64), *[rrect((380 - 18 + 90 * math.cos(math.radians(a)) * 0, 300 - 92, 380 + 18, 300 + 92), 6).rotate(a, center=(380, 300)) for a in range(0, 180, 45)])
    paint(c, minus(gear, disc(380, 300, 26)), ORANGE, ORANGE_RIM, 7)


def truck(c):  # Shipping
    paint(c, rrect((30, 120, 320, 370), 18), ORANGE, ORANGE_RIM)
    cab = poly([(330, 190), (420, 190), (482, 270), (482, 370), (330, 370)])
    paint(c, cab, METAL, METAL_RIM)
    paint(c, poly([(352, 210), (410, 210), (452, 268), (352, 268)]), PAPER, PAPER_RIM, 4, gloss=False)
    for x in (120, 400):
        paint(c, disc(x, 390, 58), METAL, METAL_RIM, 8)
        paint(c, disc(x, 390, 22), PAPER, PAPER_RIM, 3, gloss=False)


def target(c):  # QMS planning: objectives
    paint(c, disc(236, 276, 196), ORANGE, ORANGE_RIM)
    paint(c, disc(236, 276, 140), PAPER, PAPER_RIM, 5, gloss=False)
    paint(c, disc(236, 276, 92), ORANGE, ORANGE_RIM, 6)
    paint(c, disc(236, 276, 40), PAPER, PAPER_RIM, 4, gloss=False)
    paint(c, union(stroke([(246, 266), (430, 82)], 26), poly([(400, 40), (470, 40), (470, 110), (430, 82)])), METAL, METAL_RIM, 6)


def acceptance(c):  # Product acceptance: a box with an approval stamp
    paint(c, poly([(60, 200), (300, 200), (300, 450), (60, 450)]), METAL, METAL_RIM)
    paint(c, poly([(60, 200), (130, 130), (370, 130), (300, 200)]), METAL, METAL_RIM, 8, gloss=False)
    paint(c, poly([(300, 200), (370, 130), (370, 380), (300, 450)]), METAL, METAL_RIM, 8, gloss=False)
    paint(c, rrect((140, 200, 220, 280), 4), PAPER, PAPER_RIM, 3, gloss=False)
    paint(c, disc(360, 350, 120), ORANGE, ORANGE_RIM)
    paint(c, stroke([(300, 352), (345, 397), (425, 307)], 36), PAPER, PAPER_RIM, 4, gloss=False)


def trace(c):  # Investigations: a magnifier over a branching trace
    lines = union(stroke([(70, 120), (200, 120)], 26), stroke([(200, 120), (300, 60)], 26), stroke([(200, 120), (300, 190)], 26), stroke([(70, 120), (70, 400)], 26))
    paint(c, lines, METAL, METAL_RIM, 5, gloss=False)
    for (x, y) in ((70, 120), (300, 60), (300, 190), (70, 400)):
        paint(c, disc(x, y, 34), METAL, METAL_RIM, 6)
    paint(c, stroke([(360, 380), (460, 480)], 56), METAL, METAL_RIM, 8)
    paint(c, disc(290, 310, 100), PAPER, PAPER_RIM, 4)
    paint(c, ring(290, 310, 118, 42), ORANGE, ORANGE_RIM, 7)


def safety(c):  # Safety rules: a hard hat
    paint(c, shape(lambda d: d.chord([96, 110, 416, 430], 180, 360, fill=255)), ORANGE, ORANGE_RIM)
    paint(c, rrect((40, 262, 472, 322), 26), ORANGE, ORANGE_RIM)
    paint(c, rrect((236, 100, 276, 270), 14), METAL, METAL_RIM, 5, gloss=False)
    paint(c, rrect((120, 360, 392, 410), 20), METAL, METAL_RIM, 6, gloss=False)


def access(c):  # Access control: a padlock with a key-hole
    paint(c, ring(256, 190, 110, 46), METAL, METAL_RIM, 8)
    paint(c, minus(rrect((90, 200, 422, 470), 36), mask()), ORANGE, ORANGE_RIM)
    paint(c, union(disc(256, 310, 36), poly([(236, 320), (276, 320), (290, 410), (222, 410)])), METAL, METAL_RIM, 5, gloss=False)


ICONS = {
    'acceptance': acceptance, 'trace': trace, 'safety': safety, 'access': access,
    'bell': bell, 'handover': handover, 'horizon': horizon, 'integrity': integrity, 'qms': qms, 'audit': audit,
    'review': review, 'commercial': commercial, 'scenarios': scenarios, 'inbox': inbox, 'factory': factory,
    'truck': truck, 'target': target,
}


def render(fn):
    c = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    fn(c)
    c = shadow(c)
    c = c.crop(c.getbbox())
    scale = OUT / max(c.size)
    return c.resize((max(1, round(c.width * scale)), max(1, round(c.height * scale))), Image.LANCZOS)


if __name__ == '__main__':
    out = sys.argv[1]
    for name, fn in ICONS.items():
        render(fn).save(f'{out}/icon-{name}.png', optimize=True)

"""Generate Eorzean Market Master's plugin icon.

    python assets/icon/generate_icon.py

Writes `images/icon.png` at the repository root: 512x512 RGBA, square, which
is what Dalamud requires (PluginImageCache.PluginIconWidth/Height = 512, and
`requireSquare: true` for icons -- an oversized or non-square icon is logged
as an error and then silently not displayed).

The mark is an original summoning bell -- fast shoulder, concave waist,
flared lip -- carrying a green price series struck across its face. Nothing
here is traced from or derived from Square Enix artwork or any other plugin's
assets; every shape is drawn from the numbers in this file.

Drawn at 4x and downsampled with LANCZOS, which is why the edges are clean
without any blur pass. The series is composited through an inset copy of the
bell silhouette and the discarded alpha is measured, so "the line stays inside
the bell" is verified on every run rather than eyeballed.
"""

from __future__ import annotations

import argparse
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

SIZE = 512
SUPERSAMPLE = 4
CANVAS = SIZE * SUPERSAMPLE

NAVY_TOP = (14, 26, 43)
NAVY_BOTTOM = (24, 42, 66)
GOLD = (232, 180, 84)
GOLD_HIGHLIGHT = (247, 219, 154)
GOLD_SHADOW = (176, 122, 40)
GREEN = (52, 160, 82)

CORNER_RADIUS = 104

# the price series, in 512-space. five points, one dip: enough to read as a
# market and not so much that it turns to noise at 64px.
SERIES = [(176, 310), (214, 280), (252, 292), (290, 242), (318, 216)]
SERIES_STROKE = 22
SERIES_DOT = 12
SERIES_INSET = 18  # clearance held between the series and the bell's edge


def px(value: float) -> int:
    return int(round(value * SUPERSAMPLE))


def cubic(p0, c0, c1, p1, steps):
    points = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        points.append((
            u ** 3 * p0[0] + 3 * u * u * t * c0[0] + 3 * u * t * t * c1[0] + t ** 3 * p1[0],
            u ** 3 * p0[1] + 3 * u * u * t * c0[1] + 3 * u * t * t * c1[1] + t ** 3 * p1[1],
        ))
    return points


def bell_profile():
    """The bell's left-hand outline, apex to lip.

    The concave waist into a flared lip is the whole trick: a dome joined
    straight to a skirt reads as a serving cloche, not a bell.
    """
    apex = (256.0, 116.0)
    waist = (148.0, 262.0)
    lip = (106.0, 352.0)
    shoulder = cubic(apex, (196.0, 116.0), (148.0, 176.0), waist, 60)
    flare = cubic(waist, (150.0, 306.0), (124.0, 316.0), lip, 40)
    return shoulder + flare[1:]


def bell_polygon(profile, inset=0.0):
    left = [(x + inset, y + (inset if y < 200 else 0)) for x, y in profile]
    right = [(512.0 - x, y) for x, y in reversed(left)]
    return left + right


def background():
    gradient = Image.new("RGB", (1, CANVAS))
    draw = ImageDraw.Draw(gradient)
    for y in range(CANVAS):
        t = y / (CANVAS - 1)
        draw.point((0, y), fill=tuple(
            int(NAVY_TOP[i] + (NAVY_BOTTOM[i] - NAVY_TOP[i]) * t) for i in range(3)))

    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, CANVAS - 1, CANVAS - 1], px(CORNER_RADIUS), fill=255)

    img = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    img.paste(gradient.resize((CANVAS, CANVAS)), (0, 0), mask)
    return img


def render():
    profile = bell_profile()
    img = background()

    # warm wash so the gold lifts off the navy instead of sitting flat on it
    glow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse(
        [px(66), px(78), px(446), px(458)], fill=GOLD + (46,))
    img = Image.alpha_composite(img, glow.filter(ImageFilter.GaussianBlur(px(62))))

    draw = ImageDraw.Draw(img)

    # hanging loop and yoke
    draw.ellipse([px(238), px(66), px(274), px(102)], outline=GOLD, width=px(13))
    draw.rounded_rectangle([px(244), px(96), px(268), px(124)], px(6), fill=GOLD)

    # body
    draw.polygon([(px(x), px(y)) for x, y in bell_polygon(profile)], fill=GOLD)

    # clapper before the rim, so the rim overlaps it and it reads as attached
    # rather than as a loose ball floating under the bell at 32px
    draw.ellipse([px(234), px(360), px(278), px(422)], fill=GOLD_SHADOW)
    draw.ellipse([px(238), px(368), px(274), px(416)], fill=GOLD)

    # rim and sound bow
    draw.rounded_rectangle([px(90), px(344), px(422), px(382)], px(19), fill=GOLD_HIGHLIGHT)
    draw.rounded_rectangle([px(90), px(344), px(422), px(356)], px(6), fill=GOLD_SHADOW)

    # the series, on its own layer so it can be clipped to the bell face
    series = Image.new("RGBA", img.size, (0, 0, 0, 0))
    sd = ImageDraw.Draw(series)
    sd.line([(px(x), px(y)) for x, y in SERIES],
            fill=GREEN + (255,), width=px(SERIES_STROKE), joint="curve")
    for x, y in SERIES:
        sd.ellipse([px(x - SERIES_DOT), px(y - SERIES_DOT),
                    px(x + SERIES_DOT), px(y + SERIES_DOT)], fill=GREEN + (255,))

    clip = Image.new("L", img.size, 0)
    clip_draw = ImageDraw.Draw(clip)
    clip_draw.polygon([(px(x), px(y)) for x, y in bell_polygon(profile, SERIES_INSET)],
                      fill=255)
    clip_draw.rectangle([0, px(340), CANVAS, CANVAS], fill=0)  # rim is not bell face

    before = sum(series.split()[3].getdata())
    clipped = Image.new("RGBA", img.size, (0, 0, 0, 0))
    clipped.paste(series, (0, 0), clip)
    after = sum(clipped.split()[3].getdata())
    lost = 0.0 if before == 0 else (before - after) / before

    img = Image.alpha_composite(img, clipped)
    return img.resize((SIZE, SIZE), Image.LANCZOS), lost


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default = os.path.normpath(os.path.join(here, "..", "..", "images", "icon.png"))

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("-o", "--out", default=default,
                        help="output path (default: %(default)s)")
    args = parser.parse_args()

    icon, lost = render()
    if lost > 0:
        print(f"error: {lost:.3%} of the series falls outside the bell", file=sys.stderr)
        return 1

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    icon.save(args.out, optimize=True)

    assert icon.size == (SIZE, SIZE) and icon.width == icon.height
    print(f"{args.out}: {icon.width}x{icon.height} {icon.mode} "
          f"{os.path.getsize(args.out)} bytes, series fully contained")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

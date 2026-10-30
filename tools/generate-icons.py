import argparse
import copy
import io
import math
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "packaging" / "icons" / "app-icon.svg"
SVG_NS = "http://www.w3.org/2000/svg"

# Plate bounds in source units. Each composition is the share of the output canvas the plate covers.
PLATE = (12.0, 12.0, 104.0)
FREEDESKTOP = 104 / 128
WINDOWS = 104 / 116
MACOS = 824 / 1024
ANDROID = 72 / 108
MASKABLE = 0.8
FULL = 1.0

SCALES = (100, 125, 150, 200, 400)
TARGET_SIZES = (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)
ICO_SIZES = TARGET_SIZES
ICNS_TYPES = (
    (b"icp4", 16), (b"icp5", 32), (b"icp6", 64), (b"ic07", 128), (b"ic08", 256), (b"ic09", 512),
    (b"ic10", 1024), (b"ic11", 32), (b"ic12", 64), (b"ic13", 256), (b"ic14", 512),
)
ANDROID_DENSITIES = {"mdpi": 108, "hdpi": 162, "xhdpi": 216, "xxhdpi": 324, "xxxhdpi": 432}
FLATPAK_SIZES = (16, 24, 32, 48, 64, 128, 256)

ET.register_namespace("", SVG_NS)


class Renderer:
    def __init__(self, resvg, work):
        self.resvg = resvg
        self.work = work
        self.source = ET.parse(SOURCE).getroot()

    def compose(self, coverage, plate="rounded", glyph=True, mono=False, shadow=False):
        root = copy.deepcopy(self.source)
        x, y, size = PLATE
        view = size / coverage
        left, top = x + (size - view) / 2, y + (size - view) / 2
        root.set("viewBox", f"{left:g} {top:g} {view:g} {view:g}")
        layers = {element.get("id"): element for element in root}
        if plate is None:
            root.remove(layers["plate"])
        else:
            for shape in layers["plate"]:
                if plate == "bleed":
                    shape.attrib.update(x=f"{left:g}", y=f"{top:g}", width=f"{view:g}", height=f"{view:g}", rx="0")
                elif plate == "squircle":
                    shape.tag = f"{{{SVG_NS}}}path"
                    for name in ("x", "y", "width", "height", "rx"):
                        shape.attrib.pop(name, None)
                    shape.set("d", squircle(x, y, size))
            if shadow:
                # Matches the drop shadow of the macOS icon template (10 px offset, 10 px blur at 1024 px).
                unit = view / 1024
                effect = ET.SubElement(root.find(f"{{{SVG_NS}}}defs"), f"{{{SVG_NS}}}filter",
                                       id="shadow", x="-0.1", y="-0.1", width="1.2", height="1.3")
                ET.SubElement(effect, f"{{{SVG_NS}}}feDropShadow", dx="0", dy=f"{10 * unit:g}",
                              stdDeviation=f"{5 * unit:g}", attrib={"flood-opacity": "0.3"})
                layers["plate"].set("filter", "url(#shadow)")
        if not glyph:
            root.remove(layers["glyph"])
        if mono:
            layers["mono"].attrib.pop("display")
        else:
            root.remove(layers["mono"])
        return root

    def fit_mono(self, coverage):
        # Square viewBox around the monochrome silhouette, measured from a 10 px/unit render.
        image = self.render(self.compose(FULL, plate=None, glyph=False, mono=True), 1040)
        left, top, right, bottom = image.getbbox()
        x, y, size = PLATE
        center_x, center_y = x + (left + right) / 20, y + (top + bottom) / 20
        view = max(right - left, bottom - top) / 10 / coverage
        root = self.compose(FULL, plate=None, glyph=False, mono=True)
        root.set("viewBox", f"{center_x - view / 2:.2f} {center_y - view / 2:.2f} {view:.2f} {view:.2f}")
        return root

    def render(self, root, width, height=None):
        source, output = self.work / "icon.svg", self.work / "icon.png"
        source.write_bytes(ET.tostring(root))
        subprocess.run([self.resvg, "--width", str(width), "--height", str(height or width), source, output], check=True)
        with Image.open(output) as image:
            return image.convert("RGBA")


def squircle(x, y, size, exponent=5, steps=256):
    radius = size / 2
    points = []
    for step in range(steps):
        angle = 2 * math.pi * step / steps
        cos, sin = math.cos(angle), math.sin(angle)
        points.append((x + radius + radius * math.copysign(abs(cos) ** (2 / exponent), cos),
                       y + radius + radius * math.copysign(abs(sin) ** (2 / exponent), sin)))
    return "M" + "L".join(f"{px:.3f} {py:.3f}" for px, py in points) + "Z"


def scaled(base, scale):
    return math.floor(base * scale / 100 + 0.5)


def png_bytes(image):
    buffer = io.BytesIO()
    image.save(buffer, "PNG", optimize=True)
    return buffer.getvalue()


def save(image, path, opaque=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    (image.convert("RGB") if opaque else image).save(path, "PNG", optimize=True)
    print(path.relative_to(ROOT))


def dib(image):
    size = image.width
    pixels = image.transpose(Image.Transpose.FLIP_TOP_BOTTOM)
    stride = (size + 31) // 32 * 4
    mask = bytearray(stride * size)
    for row in range(size):
        for column in range(size):
            if pixels.getpixel((column, row))[3] == 0:
                mask[row * stride + column // 8] |= 0x80 >> (column % 8)
    color = pixels.tobytes("raw", "BGRA")
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(color) + len(mask), 0, 0, 0, 0)
    return header + color + bytes(mask)


def write_ico(path, images):
    frames = [png_bytes(image) if image.width >= 256 else dib(image) for image in images]
    offset = 6 + 16 * len(images)
    entries = b""
    for image, frame in zip(images, frames):
        dimension = image.width if image.width < 256 else 0
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(frame), offset)
        offset += len(frame)
    path.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + entries + b"".join(frames))
    print(path.relative_to(ROOT))


def write_icns(path, images):
    chunks = b"".join(kind + struct.pack(">I", len(data) + 8) + data
                      for kind, size in ICNS_TYPES for data in [png_bytes(images[size])])
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)
    print(path.relative_to(ROOT))


def generate_freedesktop(renderer):
    icons = ROOT / "packaging" / "flatpak" / "icons"
    for size in FLATPAK_SIZES:
        save(renderer.render(renderer.compose(FREEDESKTOP), size), icons / f"{size}x{size}" / "im.hjc.LightPlayer.png")
    symbolic = renderer.fit_mono(14 / 16)
    symbolic.attrib.update(width="16", height="16")
    mono = symbolic.find(f"{{{SVG_NS}}}path")
    mono.attrib.pop("id")
    mono.set("fill", "#2e3436")
    for element in list(symbolic):
        if element is not mono:
            symbolic.remove(element)
    ET.indent(symbolic)
    path = icons / "symbolic" / "im.hjc.LightPlayer-symbolic.svg"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(ET.tostring(symbolic, encoding="unicode") + "\n", encoding="utf-8")
    print(path.relative_to(ROOT))


def generate_windows(renderer):
    write_ico(ROOT / "src" / "LightStudio.LightPlayer" / "Assets" / "light-logo.ico",
              [renderer.render(renderer.compose(WINDOWS), size) for size in ICO_SIZES])

    images = ROOT / "packaging" / "LightPlayerMSIX" / "Images"
    icon = renderer.compose(WINDOWS)
    for size in TARGET_SIZES:
        image = renderer.render(icon, size)
        for suffix in ("", "_altform-unplated", "_altform-lightunplated"):
            save(image, images / f"Square44x44Logo.targetsize-{size}{suffix}.png")

    # Tiles and the splash screen center the icon at a share of their height on a transparent background.
    layouts = {
        "Square44x44Logo": (44, 44, 1.0),
        "StoreLogo": (50, 50, 1.0),
        "Square71x71Logo": (71, 71, 0.66),
        "Square150x150Logo": (150, 150, 0.5),
        "Wide310x150Logo": (310, 150, 0.5),
        "Square310x310Logo": (310, 310, 0.4),
        "SplashScreen": (620, 300, 0.5),
    }
    for name, (width, height, share) in layouts.items():
        for scale in SCALES:
            canvas_width, canvas_height = scaled(width, scale), scaled(height, scale)
            size = scaled(height * share, scale)
            canvas = Image.new("RGBA", (canvas_width, canvas_height))
            canvas.alpha_composite(renderer.render(icon, size), ((canvas_width - size) // 2, (canvas_height - size) // 2))
            save(canvas, images / f"{name}.scale-{scale}.png")

    badge = renderer.fit_mono(22 / 24)
    for scale in SCALES:
        save(renderer.render(badge, scaled(24, scale)), images / f"BadgeLogo.scale-{scale}.png")


def generate_macos(renderer):
    icon = renderer.compose(MACOS, plate="squircle", shadow=True)
    write_icns(ROOT / "packaging" / "macos" / "AppIcon.icns",
               {size: renderer.render(icon, size) for size in sorted({size for _, size in ICNS_TYPES})})


def generate_ios(renderer):
    save(renderer.render(renderer.compose(FULL, plate="bleed"), 1024),
         ROOT / "src" / "LightStudio.LightPlayer.iOS" / "Assets.xcassets" / "AppIcon.appiconset" / "Icon.png",
         opaque=True)


def generate_android(renderer):
    resources = ROOT / "src" / "LightStudio.LightPlayer.Android" / "Resources"
    layers = {
        "background": (renderer.compose(ANDROID, plate="bleed", glyph=False), True),
        "foreground": (renderer.compose(ANDROID, plate=None), False),
        "monochrome": (renderer.compose(ANDROID, plate=None, glyph=False, mono=True), False),
    }
    for density, size in ANDROID_DENSITIES.items():
        for layer, (root, opaque) in layers.items():
            save(renderer.render(root, size), resources / f"mipmap-{density}" / f"ic_launcher_{layer}.png", opaque)


def generate_web(renderer):
    icons = ROOT / "src" / "LightStudio.LightPlayer.Web" / "wwwroot" / "icons"
    icon = renderer.compose(FREEDESKTOP)
    for size in (192, 512):
        save(renderer.render(icon, size), icons / f"icon-{size}.png")
    save(renderer.render(renderer.compose(MASKABLE, plate="bleed"), 512), icons / "icon-maskable-512.png", opaque=True)
    save(renderer.render(renderer.compose(FULL, plate="bleed"), 180), icons / "apple-touch-icon.png", opaque=True)


TARGETS = {
    "freedesktop": generate_freedesktop,
    "windows": generate_windows,
    "macos": generate_macos,
    "ios": generate_ios,
    "android": generate_android,
    "web": generate_web,
}


def main():
    parser = argparse.ArgumentParser(description="Render the app icon into the assets of every package type.")
    parser.add_argument("targets", nargs="*", metavar="target",
                        help=f"package type to generate: {', '.join(TARGETS)} (default: all)")
    parser.add_argument("--resvg", default="resvg", help="path to the resvg executable")
    arguments = parser.parse_args()
    unknown = sorted(set(arguments.targets) - TARGETS.keys())
    if unknown:
        parser.error(f"unknown target: {', '.join(unknown)}")
    resvg = shutil.which(arguments.resvg)
    if resvg is None:
        parser.error(f"resvg not found: {arguments.resvg}")
    with tempfile.TemporaryDirectory() as work:
        renderer = Renderer(resvg, Path(work))
        for target in arguments.targets or TARGETS:
            TARGETS[target](renderer)


if __name__ == "__main__":
    main()

"""Strip the flat/gradient backdrop from a character-pool source image, leaving a
transparent PNG so Vidu generates the scene behind the character instead of
animating a flat backdrop baked into the art. Local/offline only - no API, no cost.

Usage: python remove-background.py <in.png> <out.png> [low] [high]

Method: sample the border pixels as the background reference color, threshold by
per-pixel distance from that color, then keep only the connected background regions
that touch the image edge (so same-colored pixels *inside* the character - teeth,
eye highlights - are never cleared). Edge pixels get a soft alpha ramp between
`low` and `high` distance so the cutout doesn't have a hard/aliased edge.
"""
import sys

import numpy as np
from PIL import Image
from scipy import ndimage


def remove_background(path, out_path, low=25, high=60):
    im = Image.open(path).convert("RGB")
    arr = np.array(im).astype(np.float32)
    h, w, _ = arr.shape

    border = np.concatenate([
        arr[0, :, :], arr[-1, :, :], arr[:, 0, :], arr[:, -1, :]
    ])
    bg_ref = np.median(border, axis=0)

    dist = np.sqrt(((arr - bg_ref) ** 2).sum(axis=2))

    # Generous background candidate mask (includes soft-edge halo up to `high`)
    candidate = dist < high
    labeled, _ = ndimage.label(candidate)
    border_labels = set(labeled[0, :].tolist()) | set(labeled[-1, :].tolist()) \
        | set(labeled[:, 0].tolist()) | set(labeled[:, -1].tolist())
    border_labels.discard(0)
    bg_region = np.isin(labeled, list(border_labels))

    alpha = np.full((h, w), 255, dtype=np.float32)
    ramp = np.clip((dist - low) / (high - low), 0, 1) * 255
    alpha[bg_region] = ramp[bg_region]

    rgba = np.dstack([arr, alpha]).astype(np.uint8)
    Image.fromarray(rgba, mode="RGBA").save(out_path)
    print(f"{path} -> {out_path}  bg_ref={bg_ref.round(1).tolist()}  transparent_px={(alpha < 10).sum()}")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python remove-background.py <in.png> <out.png> [low] [high]")
        sys.exit(1)
    src, dst = sys.argv[1], sys.argv[2]
    args = [float(a) for a in sys.argv[3:5]]
    remove_background(src, dst, *args)

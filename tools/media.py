"""Real public media for FrameReader's benchmark and real-world audio check (Blender open movies, CC-BY; public-domain
music from Wikimedia Commons), pinned by SHA-256 and cached in samples/realworld/ (gitignored).

Usage: python tools/media.py name ... --download   (fetch and verify; prints the paths)
       python tools/media.py --list"""
import argparse
import hashlib
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / "samples" / "realworld"

# name: (url, sha256). Add a line (download once, `sha256sum` it). The same files and hashes as ShotDetector's
# tools/realworld.py, so both repositories' caches hold the same bytes.
VIDEOS = {
    # Big Buck Bunny, the full film: 640x360 h264, 24 fps, 10 minutes, animation with hard cuts.
    "bbb_640x360_h264.mp4": ("https://archive.org/download/BigBuckBunny_124/Content/big_buck_bunny_720p_surround.mp4",
                             "46f62396c755e1ed0ab856a1521378d54196e125ef1a1643a199af087a15046b"),
    # Elephants Dream, the full film: 426x240 low-bitrate h264, 24 fps, 11 minutes, dark scenes and fades.
    "elephants_dream_426x240_h264.mp4": ("https://archive.org/download/ElephantsDream/ed_1024_512kb.mp4",
                                         "fd6dc0e9da3a2f63487957325983f197f23c6ff1cd2b29bb71e64db567e0994c"),
    # Tears of Steel, the full film: 1280x534 h264 in a mov, 24 fps, 12 minutes, live action with fades.
    "tears_of_steel_1280x534_h264.mov": ("https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov",
                                         "efa9062d9cdb7a338e40ad530dfdf234806743f29ae6a1a136b97ece4e588e8f"),
    # The Sintel trailer at 1080p h264, 24 fps, 52 seconds: the benchmark's full-HD case.
    "sintel_trailer_1920x1080_h264.mp4": ("https://download.blender.org/durian/trailer/sintel_trailer-1080p.mp4",
                                          "34bbd52a4b89fdf63c8ace50b268da26653a59508288100cd3c23de276db7931"),
}

# Opus and Vorbis, which none of the videos has.
AUDIO = {
    # Elephants Dream as Wikimedia Commons has it: 1080p VP9 with 5.1 Opus at 48 kHz in a webm, 11 minutes (840 MB).
    "elephants_dream_1920x1080_vp9.webm": ("https://upload.wikimedia.org/wikipedia/commons/a/a2/Elephants_Dream_%282006%29.webm",
                                           "d1ff59c1b046e907c1fb3c5d86cd048017879978b1f0469d025cbe2c3685648e"),
    # "Going Some" by Marcel Francis Dumas (1909, public domain), Wikimedia Commons: stereo Opus at 48 kHz, 2.5 minutes.
    "going_some_1909.opus": ("https://upload.wikimedia.org/wikipedia/commons/b/bf/Going_Some_by_Marcel_Francis_Dumas_%281909%29.opus",
                             "fe05690df51cc82ed8c550cf8423b9d02e2a2ba29a0745854fd6d0a468694e43"),
    # "Maple Leaf Rag" (Scott Joplin, public domain), Wikimedia Commons: stereo Vorbis in Ogg at 44.1 kHz, 3 minutes.
    "maple_leaf_rag.ogg": ("https://upload.wikimedia.org/wikipedia/commons/a/a6/Maple_Leaf_RagQ.ogg",
                           "8f3f2d28e5d27e290475ec92a723d1ba4d7b9448cc4553390af6c578cad10387"),
}


def fetch(name: str) -> Path:
    url, sha = VIDEOS.get(name) or AUDIO[name]
    path = CACHE / name
    if not path.exists():
        CACHE.mkdir(parents=True, exist_ok=True)
        req = urllib.request.Request(url, headers={"User-Agent": "FrameReader real-world media"})
        # A few tries: archive.org and Wikimedia answer 500 or drop the connection now and then.
        for attempt in range(1, 5):
            print(f"downloading {url}" + (f" (try {attempt})" if attempt > 1 else ""), flush=True)
            try:
                with urllib.request.urlopen(req, timeout=60) as r, open(path, "wb") as f:
                    while chunk := r.read(1 << 20):
                        f.write(chunk)
                break
            except (urllib.error.URLError, OSError) as e:
                path.unlink(missing_ok=True)
                if attempt == 4:
                    fail(f"{name}: download failed: {e}")
                time.sleep(20 * attempt)
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != sha:
        path.unlink()
        fail(f"{name}: SHA-256 {actual}, expected {sha} (deleted; the source changed?)")
    return path


def fail(message: str):
    if "GITHUB_ACTIONS" in __import__("os").environ:
        print("::error title=media.py::" + message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A"))
    sys.exit(message)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="*")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--download", action="store_true", help="download and verify; print the paths")
    args = ap.parse_args()
    if args.list:
        print("\n".join([*VIDEOS, *AUDIO]))
        return
    unknown = [n for n in args.names if n not in VIDEOS and n not in AUDIO]
    if unknown or not args.names or not args.download:
        sys.exit(f"usage: media.py name ... --download; known: {', '.join([*VIDEOS, *AUDIO])}")
    print("\n".join(str(fetch(n)) for n in args.names))


if __name__ == "__main__":
    main()

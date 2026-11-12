"""Prefetch the official Huawei Smart Audio artwork used by the Windows app."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
from http.client import HTTPException
import io
import json
from pathlib import Path, PurePosixPath
import re
import struct
import sys
from tempfile import TemporaryDirectory
from time import sleep
from urllib.error import HTTPError
from urllib.request import HTTPRedirectHandler, Request, build_opener
from zipfile import BadZipFile, ZipFile
import zlib


CDN = "https://smarthome-drcn.dbankcdn.cn/device/guide/AAM001"
DEFAULT_OUTPUT = Path(__file__).resolve().parents[2] / "HuaweiPods" / "Assets" / "Official"
MAX_CONFIG = 512 * 1024
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_EXPANDED = 128 * 1024 * 1024
MAX_PNG = 16 * 1024 * 1024
SUB_MODEL = re.compile(r"[0-9A-F]{2}")
SHA256 = re.compile(r"[0-9a-f]{64}")

# HuaweiDeviceInfoRoutePolicy.kt plus FreeBuds 3 from OfficialImageCatalogPolicy.kt.
MODELS = {
    "000027": "HUAWEI FreeBuds 3",
    "000129": "HUAWEI FreeBuds 4",
    "000135": "HUAWEI FreeBuds 4E",
    "000139": "HUAWEI Eyewear",
    "000141": "HUAWEI FreeBuds 5",
    "000145": "HUAWEI FreeBuds 5i",
    "000149": "HUAWEI FreeBuds Pro 3",
    "00014F": "HUAWEI Eyewear 2",
    "000153": "HUAWEI FreeBuds 6i",
    "000155": "HUAWEI FreeBuds 6",
    "00015D": "HUAWEI FreeArc",
    "000163": "HUAWEI FreeBuds 7i",
    "000167": "HUAWEI FreeClip 2",
    "000169": "HUAWEI FreeBuds SE 4 ANC",
    "00016D": "HUAWEI FreeBuds Pro 5",
    "000173": "HUAWEI FreeClip 2 典藏版",
    "000174": "HUAWEI FreeBuds 7",
}

# The first three roles preserve the upstream OfficialSmartAudioArchive preference order.
ROLE_NAMES = {
    "hero": ("iv_device_logo.png", "device_icon.png", "mediacontroller_icon.png",
             "ota_device_icon.png", "ota_device.png"),
    "left": ("find_left_icon.png", "find_left.png", "left_ear.png", "left_ear_icon.png"),
    "right": ("find_right_icon.png", "find_right.png", "right_ear.png", "right_ear_icon.png"),
    "case": ("find_box_icon.png", "find_box.png", "charging_case_icon.png", "charging_case.png"),
}


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


def require(condition, message):
    if not condition:
        raise ValueError(message)


def download(url, limit, accept):
    for attempt in range(3):
        try:
            return download_once(url, limit, accept)
        except (OSError, HTTPException) as error:
            if attempt == 2 or (isinstance(error, HTTPError) and 400 <= error.code < 500 and error.code != 429):
                raise
            sleep(0.5 * (attempt + 1))


def download_once(url, limit, accept):
    opener = build_opener(NoRedirects)
    request = Request(url, headers={"Accept": accept})
    with opener.open(request, timeout=25) as response:
        require(response.status == 200 and response.geturl() == url, f"Invalid response: {url}")
        length = response.headers.get("Content-Length")
        expected_hash = response.headers.get("x-amz-content-sha256", "").strip().lower()
        require(length is None or 0 < int(length) <= limit, f"Invalid content length: {url}")
        data = response.read(limit + 1)
    require(0 < len(data) <= limit, f"Resource size outside bounds: {url}")
    require(length is None or len(data) == int(length), f"Incomplete response: {url}")
    digest = hashlib.sha256(data).hexdigest()
    if SHA256.fullmatch(expected_hash):
        require(digest == expected_hash, f"Huawei CDN SHA-256 mismatch: {url}")
    return data, digest


def fetch_config(model_id):
    config_url = f"{CDN}/{model_id}/{model_id}.json"
    data, digest = download(config_url, MAX_CONFIG, "application/json")
    config = json.loads(data.decode("utf-8-sig"))
    require(config["modelId"] == model_id, f"Unexpected model ID: {config_url}")
    require(config["marketingName"].strip() == MODELS[model_id], f"Unexpected product name: {config_url}")
    return config, digest


def png_dimensions(data):
    require(45 <= len(data) <= MAX_PNG and data[:8] == b"\x89PNG\r\n\x1a\n", "Invalid PNG signature or size")
    offset = 8
    dimensions = None
    has_image = False
    while offset + 12 <= len(data):
        size, kind = struct.unpack_from(">I4s", data, offset)
        end = offset + 12 + size
        require(end <= len(data) and kind.isalpha(), "Invalid PNG chunk")
        expected = struct.unpack_from(">I", data, end - 4)[0]
        require(zlib.crc32(memoryview(data)[offset + 4:end - 4]) == expected, "Invalid PNG chunk CRC")
        if offset == 8:
            require(kind == b"IHDR" and size == 13, "Missing PNG header")
            width, height, depth, color, compression, filtering, interlace = struct.unpack_from(">IIBBBBB", data, offset + 8)
            require(0 < width <= 4096 and 0 < height <= 4096, "Invalid PNG dimensions")
            depths = {0: (1, 2, 4, 8, 16), 2: (8, 16), 3: (1, 2, 4, 8), 4: (8, 16), 6: (8, 16)}
            require(depth in depths.get(color, ()) and compression == filtering == 0 and interlace <= 1, "Invalid PNG header")
            dimensions = (width, height)
        elif kind == b"IHDR":
            raise ValueError("Repeated PNG header")
        elif kind == b"IDAT":
            has_image = True
        elif kind == b"IEND":
            require(has_image and size == 0 and end == len(data), "Invalid PNG ending")
            return dimensions
        offset = end
    raise ValueError("Missing PNG ending")


def extract_artwork(archive_data, key, output):
    images = {}
    with ZipFile(io.BytesIO(archive_data)) as archive:
        members = archive.infolist()
        require(0 < len(members) <= 2048, f"Invalid ZIP entry count: {key}")
        require(sum(member.file_size for member in members) <= MAX_EXPANDED, f"Expanded ZIP too large: {key}")
        names = {}
        for member in members:
            name = member.filename.replace("\\", "/")
            path = PurePosixPath(name)
            require(name and not path.is_absolute() and ":" not in name and ".." not in path.parts,
                    f"Unsafe ZIP entry: {member.filename}")
            if not member.is_dir():
                names.setdefault(path.name.lower(), []).append(member)

        for role, candidates in ROLE_NAMES.items():
            for name in candidates:
                if name not in names:
                    continue
                member = min(names[name], key=lambda entry: (len(entry.filename), entry.filename))
                require(0 < member.file_size <= MAX_PNG, f"Invalid image size: {member.filename}")
                # ZipFile checks the CRC32 when reading the complete entry.
                data = archive.read(member)
                require(len(data) == member.file_size, f"Incomplete ZIP entry: {member.filename}")
                width, height = png_dimensions(data)
                file_name = f"{key}_{role}.png"
                (output / file_name).write_bytes(data)
                images[role] = {"file": file_name, "width": width, "height": height,
                                "sha256": hashlib.sha256(data).hexdigest(), "sourceEntry": member.filename}
                break
    require("hero" in images, f"Missing official hero artwork: {key}")
    return images


def fetch_variant(model_id, sub_id, config, config_sha, output):
    key = f"{model_id}_{sub_id}"
    info = config["resourceMap"][sub_id]
    name = f"{key}.zip"
    require(info.get("resourceKey", sub_id) == sub_id and info["resourceRemark"] == name, f"Invalid resource path: {key}")
    declared_kib = int(info["resourceFileSizeKb"])
    require(1 <= declared_kib <= MAX_ARCHIVE // 1024, f"Invalid advertised resource size: {key}")
    description = info["resourceDesc"].strip()
    require(0 < len(description) <= 80 and all(char.isprintable() for char in description), f"Invalid resource description: {key}")

    archive_url = f"{CDN}/{model_id}/{name}"
    archive_data, archive_digest = download(archive_url, MAX_ARCHIVE, "application/zip, application/octet-stream")
    declared_bytes = declared_kib * 1024
    require(abs(len(archive_data) - declared_bytes) <= max(64 * 1024, declared_bytes // 100),
            f"Archive size differs from official metadata: {len(archive_data)} vs {declared_bytes} bytes")
    images = extract_artwork(archive_data, key, output)
    dimensions = ", ".join(f'{role} {image["width"]}x{image["height"]}' for role, image in images.items())
    print(f"{key}: {dimensions}", flush=True)
    return {"modelId": model_id, "subModelId": sub_id, "description": description,
            "marketingName": MODELS[model_id], "isCatalogDefault": sub_id == config.get("defaultSubModelId"),
            "configSha256": config_sha, "archiveUrl": archive_url,
            "archiveSha256": archive_digest, "images": images}


def verify_cache(output):
    index = json.loads((output / "index.json").read_text(encoding="utf-8"))
    require(index["schemaVersion"] == 1 and index["entries"], "Invalid artwork index")
    identities = set()
    image_count = 0
    for entry in index["entries"]:
        model_id, sub_id = entry["modelId"], entry["subModelId"]
        require(model_id in MODELS and SUB_MODEL.fullmatch(sub_id), "Invalid artwork identity")
        require((model_id, sub_id) not in identities and "hero" in entry["images"], "Duplicate identity or missing hero")
        identities.add((model_id, sub_id))
        for role, image in entry["images"].items():
            name = f"{model_id}_{sub_id}_{role}.png"
            require(role in ROLE_NAMES and image["file"] == name, "Invalid artwork filename")
            data = (output / name).read_bytes()
            require(hashlib.sha256(data).hexdigest() == image["sha256"], f"SHA-256 mismatch: {name}")
            require(png_dimensions(data) == (image["width"], image["height"]), f"Dimensions mismatch: {name}")
            image_count += 1
    print(f"Verified {image_count} PNGs for {len(identities)} variants across {len({model for model, _ in identities})} models.")


def prefetch(output):
    output.mkdir(parents=True, exist_ok=True)
    # Keep the installed index intact while downloading and checking its replacement.
    with TemporaryDirectory(prefix=".prefetch-", dir=output.parent) as temporary, ThreadPoolExecutor(max_workers=6) as executor:
        staging = Path(temporary)
        jobs = []
        for model_id, (config, config_sha) in zip(MODELS, executor.map(fetch_config, MODELS), strict=True):
            sub_ids = sorted(sub_id for sub_id in config["resourceMap"] if SUB_MODEL.fullmatch(sub_id))
            require(sub_ids, f"No official artwork variants for {model_id}")
            for sub_id in sub_ids:
                job = executor.submit(fetch_variant, model_id, sub_id, config, config_sha, staging)
                jobs.append((f"{model_id}_{sub_id}", job))
        entries = []
        for key, job in jobs:
            try:
                entries.append(job.result())
            except (OSError, HTTPException, ValueError, KeyError, BadZipFile) as error:
                print(f"Skipped {key}: {error}", file=sys.stderr, flush=True)
        index = {"schemaVersion": 1, "entries": entries}
        (staging / "index.json").write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        verify_cache(staging)
        for entry in entries:
            for image in entry["images"].values():
                name = image["file"]
                (staging / name).replace(output / name)
        (staging / "index.json").replace(output / "index.json")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="destination for the official PNG cache and index")
    parser.add_argument("--verify", action="store_true", help="verify an existing cache without downloading")
    args = parser.parse_args()
    if args.verify:
        verify_cache(args.output)
    else:
        prefetch(args.output)

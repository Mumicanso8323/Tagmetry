#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NOTICE_PATH = ROOT / "THIRD_PARTY_NOTICES.md"
LICENSES_DIR = ROOT / "LICENSES"

LICENSE_FILE_CANDIDATES = [
    "LICENSE",
    "LICENSE.txt",
    "LICENSE.md",
    "COPYING",
    "COPYING.txt",
    "NOTICE",
    "NOTICE.txt",
]


@dataclass(frozen=True)
class Component:
    name: str
    version: str
    source: str
    license_name: str
    license_url: str
    homepage: str
    notes: str


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Generate THIRD_PARTY_NOTICES and LICENSES files.")
    p.add_argument("--root", default=str(ROOT))
    return p.parse_args()


def read_csproj_packages(root: Path) -> list[tuple[str, str]]:
    packages: set[tuple[str, str]] = set()
    for csproj in root.glob("src/**/*.csproj"):
        tree = ET.parse(csproj)
        for node in tree.findall(".//PackageReference"):
            include = node.attrib.get("Include")
            version = node.attrib.get("Version") or (node.findtext("Version") or "")
            if include and version:
                packages.add((include.strip(), version.strip()))
    return sorted(packages, key=lambda x: (x[0].lower(), x[1]))


def fetch_nuspec_metadata(package_id: str, version: str) -> dict[str, str]:
    package_l = package_id.lower()
    version_l = version.lower()
    url = f"https://api.nuget.org/v3-flatcontainer/{package_l}/{version_l}/{package_l}.nuspec"
    try:
        with urllib.request.urlopen(url, timeout=20) as resp:
            data = resp.read()
    except Exception:
        return {
            "license_name": "UNKNOWN",
            "license_url": "",
            "homepage": "",
            "notes": "Unable to fetch nuspec metadata from nuget.org in this environment.",
        }

    root = ET.fromstring(data)

    def txt(xpath: str) -> str:
        node = root.find(xpath)
        return (node.text or "").strip() if node is not None and node.text else ""

    meta_prefix = ".//{*}metadata/"
    project_url = txt(meta_prefix + "{*}projectUrl")
    license_expr = txt(meta_prefix + "{*}license")
    license_url = txt(meta_prefix + "{*}licenseUrl")

    license_name = license_expr or "UNKNOWN"
    if license_expr and root.find(meta_prefix + "{*}license") is not None:
        lic = root.find(meta_prefix + "{*}license")
        if lic is not None and lic.attrib.get("type", "").lower() == "expression":
            license_name = f"SPDX: {license_expr}"
        elif lic is not None and lic.attrib.get("type", "").lower() == "file":
            license_name = f"License file: {license_expr}"

    return {
        "license_name": license_name,
        "license_url": license_url,
        "homepage": project_url,
        "notes": "",
    }


def detect_bundled_components(root: Path) -> list[Component]:
    third_party = root / "third_party"
    if not third_party.exists():
        return []

    components: list[Component] = []
    for item in sorted([p for p in third_party.iterdir() if p.is_dir()], key=lambda p: p.name.lower()):
        lic_file = next((item / n for n in LICENSE_FILE_CANDIDATES if (item / n).exists()), None)
        notes = "License file copied from bundled component." if lic_file else "No bundled license file detected."
        components.append(
            Component(
                name=item.name,
                version="bundled",
                source="Bundled",
                license_name="See bundled files" if lic_file else "UNKNOWN",
                license_url="",
                homepage="",
                notes=notes,
            )
        )
    return components


def safe_file_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "_", value)


def write_license_file(component: Component, out_dir: Path, root: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    file_name = f"{component.source}_{safe_file_name(component.name)}_{safe_file_name(component.version)}.txt"
    out = out_dir / file_name

    lines = [
        f"Component: {component.name}",
        f"Version: {component.version}",
        f"Source: {component.source}",
        f"License: {component.license_name}",
        f"License URL: {component.license_url or 'N/A'}",
        f"Homepage: {component.homepage or 'N/A'}",
        "",
    ]
    if component.notes:
        lines.append(f"Notes: {component.notes}")

    if component.source == "Bundled":
        bundled_dir = root / "third_party" / component.name
        lic_file = next((bundled_dir / n for n in LICENSE_FILE_CANDIDATES if (bundled_dir / n).exists()), None)
        if lic_file:
            lines.extend(["", "----- Bundled license text -----", ""])
            lines.append(lic_file.read_text(encoding="utf-8", errors="replace"))

    out.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def generate(root: Path) -> None:
    root = root.resolve()
    packages = read_csproj_packages(root)

    components: list[Component] = []
    for package_id, version in packages:
        meta = fetch_nuspec_metadata(package_id, version)
        components.append(
            Component(
                name=package_id,
                version=version,
                source="NuGet",
                license_name=meta["license_name"],
                license_url=meta["license_url"],
                homepage=meta["homepage"],
                notes=meta["notes"],
            )
        )

    components.extend(detect_bundled_components(root))
    components = sorted(components, key=lambda c: (c.source, c.name.lower(), c.version))

    if LICENSES_DIR.exists():
        for f in LICENSES_DIR.glob("*.txt"):
            f.unlink()
    LICENSES_DIR.mkdir(parents=True, exist_ok=True)

    for comp in components:
        write_license_file(comp, LICENSES_DIR, root)

    lines = [
        "# THIRD_PARTY_NOTICES",
        "",
        "This file is generated by `scripts/generate_third_party_notices.py`.",
        "",
        "## Included third-party components",
        "",
        "| Source | Name | Version | License | License URL |",
        "|---|---|---|---|---|",
    ]

    for comp in components:
        lines.append(
            f"| {comp.source} | {comp.name} | {comp.version} | {comp.license_name} | {comp.license_url or ''} |"
        )

    lines.extend([
        "",
        "## Per-component license files",
        "",
        "Detailed records are written to the `LICENSES/` directory.",
    ])

    NOTICE_PATH.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def main() -> int:
    args = parse_args()
    generate(Path(args.root))
    print(f"Generated {NOTICE_PATH} and LICENSES/*.txt")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

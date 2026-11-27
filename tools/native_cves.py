#!/usr/bin/env python3
"""Known CVEs in what FrameReader.Native ships: FFmpeg and the libraries linked into it.

The versions and CPEs come from tools/native/build-ffmpeg.sh (its `pkg` lines, the ones sbom.spdx.json is written
from), so the check follows the build. For each product, every CVE the NVD lists for it is fetched and its
configurations are matched against our pinned version here (exact versions and version ranges alike), rather than
trusting a server-side match. NuGet's audit and Dependabot see NuGet advisories only, not these libraries.

    python3 tools/native_cves.py [--json out.json] [--nvd-json fixture.json]

Prints one line per affecting CVE; with --json, writes them for .github/workflows/native-cves.yml, which opens an issue
per CVE not filed before. NVD_API_KEY (optional) raises NVD's rate limit; without it a request goes every 6 s.
--nvd-json reads a saved NVD response for every product instead of asking the NVD (tests).
"""
import argparse
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "native", "build-ffmpeg.sh")
NVD = "https://services.nvd.nist.gov/rest/json/cves/2.0"


def products(script=SCRIPT):
    """(name, version, vendor, product, linux_only) for each `pkg` line of the build script."""
    text = open(script, encoding="utf-8").read()
    values = dict(re.findall(r'^([A-Z0-9_]+)=(\S+)$', text, re.M))
    found = []
    lines = text.splitlines()
    for i, line in enumerate(lines):
        m = re.search(r'\bpkg \w+ (\S+) "\$(\w+)" \S+ "[^"]*" "\$\w+" (\w+):(\w+)', line)
        if m:
            # OpenSSL is linked on Linux only: its pkg line is in the script's `linux-*)` case.
            linux_only = "linux-*)" in line or (i > 0 and "linux-*)" in lines[i - 1])
            found.append((m.group(1), values[m.group(2)], m.group(3), m.group(4), linux_only))
    if not found:
        sys.exit(f"No pkg lines in {script}")
    return found


def _version_key(v):
    """Comparable version: numeric parts as numbers, a letter suffix (OpenSSL's 1.1.1w) after them."""
    parts = []
    for p in re.split(r"[.\-_]", v):
        m = re.match(r"^(\d+)([a-z]*)$", p, re.I)
        parts.append((int(m.group(1)), m.group(2).lower()) if m else (-1, p.lower()))
    return parts


def _cmp(a, b):
    ka, kb = _version_key(a), _version_key(b)
    n = max(len(ka), len(kb))
    ka += [(0, "")] * (n - len(ka))
    kb += [(0, "")] * (n - len(kb))
    return (ka > kb) - (ka < kb)


def affects(match, vendor, product, version):
    """Whether one NVD cpeMatch entry (vulnerable) covers vendor:product at version."""
    if not match.get("vulnerable"):
        return False
    parts = match.get("criteria", "").split(":")
    if len(parts) < 6 or parts[3] != vendor or parts[4] != product:
        return False
    cpe_version = parts[5]
    bounds = [k for k in ("versionStartIncluding", "versionStartExcluding", "versionEndIncluding",
                          "versionEndExcluding") if k in match]
    if cpe_version not in ("*", "-") and not bounds:
        return _cmp(cpe_version, version) == 0
    if cpe_version == "-":
        return False
    if "versionStartIncluding" in match and _cmp(version, match["versionStartIncluding"]) < 0:
        return False
    if "versionStartExcluding" in match and _cmp(version, match["versionStartExcluding"]) <= 0:
        return False
    if "versionEndIncluding" in match and _cmp(version, match["versionEndIncluding"]) > 0:
        return False
    if "versionEndExcluding" in match and _cmp(version, match["versionEndExcluding"]) >= 0:
        return False
    return True


def _matches(node):
    yield from node.get("cpeMatch", [])
    for child in node.get("children", []):
        yield from _matches(child)


def affecting(vulnerabilities, vendor, product, version):
    """The CVEs among an NVD response's vulnerabilities whose configurations cover our version."""
    hits = []
    for item in vulnerabilities:
        cve = item["cve"]
        if cve.get("vulnStatus") == "Rejected":
            continue
        nodes = [n for c in cve.get("configurations", []) for n in c.get("nodes", [])]
        if any(affects(m, vendor, product, version) for n in nodes for m in _matches(n)):
            hits.append(cve)
    return hits


def _severity(cve):
    metrics = cve.get("metrics", {})
    for key in ("cvssMetricV40", "cvssMetricV31", "cvssMetricV30", "cvssMetricV2"):
        for m in metrics.get(key, []):
            data = m.get("cvssData", {})
            score = data.get("baseScore")
            sev = data.get("baseSeverity") or m.get("baseSeverity")
            if score is not None:
                return f"{sev or '?'} {score}"
    return "not scored yet"


def fetch(vendor, product, key):
    """Every CVE the NVD lists for vendor:product, all versions (paged)."""
    out, start = [], 0
    while True:
        query = urllib.parse.urlencode({"virtualMatchString": f"cpe:2.3:a:{vendor}:{product}",
                                        "startIndex": start, "resultsPerPage": 2000})
        req = urllib.request.Request(f"{NVD}?{query}", headers={"User-Agent": "native-cves-check"})
        if key:
            req.add_header("apiKey", key)
        for attempt in range(5):
            try:
                with urllib.request.urlopen(req, timeout=120) as r:
                    page = json.load(r)
                break
            except Exception as e:  # NVD's API answers 403/503 under load: wait and try again
                if attempt == 4:
                    raise
                print(f"NVD: {e}; retrying", file=sys.stderr)
                time.sleep(30)
        out += page.get("vulnerabilities", [])
        start += page.get("resultsPerPage", 0)
        time.sleep(0.6 if key else 6)
        if start >= page.get("totalResults", 0) or not page.get("resultsPerPage"):
            return out


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--json", help="write the findings here")
    ap.add_argument("--nvd-json", help="a saved NVD response to use for every product instead of the NVD")
    ap.add_argument("--script", default=SCRIPT)
    args = ap.parse_args()
    key = os.environ.get("NVD_API_KEY", "")
    findings = []
    for name, version, vendor, product, linux_only in products(args.script):
        if args.nvd_json:
            vulns = json.load(open(args.nvd_json, encoding="utf-8"))["vulnerabilities"]
        else:
            vulns = fetch(vendor, product, key)
        hits = affecting(vulns, vendor, product, version)
        print(f"{name} {version} (cpe:2.3:a:{vendor}:{product}): {len(vulns)} CVEs listed, {len(hits)} affect it")
        for cve in hits:
            desc = next((d["value"] for d in cve.get("descriptions", []) if d.get("lang") == "en"), "")
            f = {"id": cve["id"], "component": name, "version": version, "linux_only": linux_only,
                 "severity": _severity(cve), "published": cve.get("published", "")[:10], "description": desc}
            findings.append(f)
            print(f"  {f['id']} ({f['severity']}, published {f['published']})")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(findings, fh, indent=1)


if __name__ == "__main__":
    main()

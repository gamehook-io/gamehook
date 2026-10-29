"""Resolve a public UTC calendar version and an updater-compatible package version."""
import datetime
import os
import re
import json
import subprocess


# Releases are tagged and versioned yyyy.m.N.
VERSION = re.compile(r"(20[0-9]{2})\.([1-9]|1[0-2])(?:\.([1-9][0-9]*))?")


def parse(value):
    """Return (year, month, revision or None) for a yyyy.m[.N] value, else None."""
    if match := VERSION.fullmatch(value):
        return int(match[1]), int(match[2]), int(match[3]) if match[3] else None
    return None


def resolve(value):
    parsed = parse(value)
    if parsed is None or parsed[2] is None:
        raise ValueError("Release version must use yyyy.m.N (N starts at 1).")
    year, month, revision = parsed
    if revision > 65534:
        raise ValueError("Monthly release number exceeds the assembly version limit.")
    version = f"{year}.{month}.{revision}"
    return version, version


def next_version(month, tags):
    year, number, _ = parse(month)
    found = (parse(tag) for tag in tags)
    numbers = [item[2] for item in found if item and item[2] and item[:2] == (year, number)]
    return f"{year}.{number}.{max(numbers, default=0) + 1}"


if __name__ == "__main__":
    value = os.environ.get("INPUT_VERSION", "")
    if not value:
        value = (os.environ["GITHUB_REF_NAME"]
                 if os.environ.get("GITHUB_REF", "").startswith("refs/tags/")
                 else datetime.datetime.now(datetime.timezone.utc).strftime("%Y.%m").replace(".0", "."))
    if (parsed := parse(value)) and parsed[2] is None:
        repo = os.environ["GITHUB_REPOSITORY"]
        tags = []
        # Include drafts and tags from failed runs so their numbers are not reused.
        for endpoint, field in (("releases", "tag_name"), ("tags", "name")):
            pages = json.loads(subprocess.check_output(
                ["gh", "api", "--paginate", "--slurp", f"repos/{repo}/{endpoint}?per_page=100"], text=True))
            tags.extend(item[field] for page in pages for item in page)
        value = next_version(value, tags)
    tag, version = resolve(value)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"value={version}\ntag={tag}\n")

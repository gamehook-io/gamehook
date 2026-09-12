"""Resolve a public UTC calendar version and an updater-compatible package version."""
import datetime
import os
import re
import json
import subprocess


def resolve(value):
    if not re.fullmatch(r"[0-9]{2}\.[0-9]{2}\.[1-9][0-9]*", value):
        raise ValueError("Release version must use yy.mm.N (N starts at 1).")
    calendar, revision = value.rsplit(".", 1)
    year, month = map(int, calendar.split("."))
    date = datetime.date(2000 + year, month, 1)
    if int(revision) > 65534:
        raise ValueError("Monthly release number exceeds the assembly version limit.")
    return value, f"{date.year}.{date.month}.{revision}"


def next_version(calendar, tags):
    resolve(f"{calendar}.1")
    pattern = re.compile(re.escape(calendar) + r"\.([1-9][0-9]*)")
    numbers = [int(match[1]) for tag in tags if (match := pattern.fullmatch(tag))]
    return f"{calendar}.{max(numbers, default=0) + 1}"


if __name__ == "__main__":
    value = os.environ.get("INPUT_VERSION", "")
    if not value:
        value = (os.environ["GITHUB_REF_NAME"]
                 if os.environ.get("GITHUB_REF", "").startswith("refs/tags/")
                 else datetime.datetime.now(datetime.timezone.utc).strftime("%y.%m"))
    if re.fullmatch(r"[0-9]{2}\.[0-9]{2}", value):
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

# SPDX-License-Identifier: GPL-3.0-only
# Copyright (c) 2026 hmodymac8-droid
"""One-off: turn Downloads Sorter's translations_ar.py into Translations/ar.json.

    py tools/convert_translations.py <path to translations_ar.py>

The two tables (fixed text and text with {0} placeholders) become one, since
the C# program looks both up the same way, and the old program's name is
replaced with the new one.
"""
import importlib.util
import json
import sys
from pathlib import Path

source = Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("translations_ar", source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def rename(text: str) -> str:
    return text.replace("Downloads Sorter", "PC Butler").replace("DownloadsSorter", "PCButler")


table = {}
for english, arabic in {**module.EXACT, **module.TEMPLATES}.items():
    table[rename(english)] = rename(arabic)
target = Path(__file__).resolve().parent.parent / "src" / "PCButler.Core" / "Translations" / "ar.json"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(dict(sorted(table.items())), ensure_ascii=False, indent=1), encoding="utf-8")
print(len(table), "translations ->", target)

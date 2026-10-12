# SPDX-License-Identifier: GPL-3.0-only
# Copyright (c) 2026 hmodymac8-droid
"""Add or update Arabic translations in src/PCButler.Core/Translations/ar.json.

    py tools/add_translations.py tools/new_strings.json

The argument is a JSON object of English text -> Arabic text (the English is
exactly what the code passes to Tr.T, with {0}, {1}... for inserted values).
The table is kept sorted so changes are easy to review.
"""
import json
import sys
from pathlib import Path

target = Path(__file__).resolve().parent.parent / "src" / "PCButler.Core" / "Translations" / "ar.json"
table = json.loads(target.read_text(encoding="utf-8"))
new = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
added = sum(1 for key in new if key not in table)
table.update(new)
target.write_text(json.dumps(dict(sorted(table.items())), ensure_ascii=False, indent=1), encoding="utf-8")
print(f"{added} added, {len(new) - added} updated, {len(table)} in all")

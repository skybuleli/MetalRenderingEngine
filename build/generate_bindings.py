#!/usr/bin/env python3
"""从 metal-shaderconverter 的 *.reflect.json 生成运行时 *.bindings.json。"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def generate_one(reflect_path: Path) -> Path:
    with reflect_path.open("r", encoding="utf-8") as f:
        reflection = json.load(f)

    shader_name = reflect_path.name.removesuffix(".reflect.json")
    resources = []
    for entry in reflection.get("TopLevelArgumentBuffer", []):
        resources.append(
            {
                "name": entry.get("Name", ""),
                "type": entry.get("Type", ""),
                "slot": entry.get("Slot", 0),
                "space": entry.get("Space", 0),
                "offset": entry.get("EltOffset", 0),
                "size": entry.get("Size", 0),
            }
        )

    metadata = {
        "version": 1,
        "shader": shader_name,
        "stage": reflection.get("ShaderType", ""),
        "argumentBufferBindPoint": 2,
        "resources": resources,
    }

    out_path = reflect_path.with_name(f"{shader_name}.bindings.json")
    with out_path.open("w", encoding="utf-8") as f:
        json.dump(metadata, f, ensure_ascii=False, indent=2)
        f.write("\n")
    return out_path


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: generate_bindings.py <shader-output-dir>", file=sys.stderr)
        return 2

    shader_dir = Path(argv[1])
    if not shader_dir.exists():
        print(f"[bindinggen] skip: {shader_dir} does not exist")
        return 0

    count = 0
    for reflect_path in sorted(shader_dir.glob("*.reflect.json")):
        out_path = generate_one(reflect_path)
        print(f"[bindinggen] ✅ {out_path}")
        count += 1

    print(f"[bindinggen] Done. {count} binding metadata file(s) generated.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))

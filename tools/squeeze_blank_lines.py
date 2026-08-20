#!/usr/bin/env python3
"""Squeeze blank lines inside method bodies (brace depth >= 2).

Rules:
  - A blank line at depth >= 2 is removed, UNLESS the next non-blank line is a
    comment (line or doc comment) - then exactly one blank line is kept above it.
  - Blank lines at depth 0/1 (between using statements / class members) are untouched.
  - String literals (regular, verbatim, raw triple-quote) and comment contents are
    passed through verbatim - blank lines inside them are preserved.

Usage: python tools/squeeze_blank_lines.py [--check] [files-or-dirs...]
"""
import sys
from pathlib import Path


def squeeze(source: str) -> str:
    out = []          # output lines
    pending_blanks = 0
    depth = 0
    state = "code"    # code | line_comment | block_comment | string | verbatim | raw(n) | char
    raw_delim = ""
    i = 0
    line = []
    line_has_code = False

    def flush_line(preserve_blank=False):
        nonlocal pending_blanks
        text = "".join(line)
        if not line_has_code and text.strip() == "":
            if preserve_blank or depth < 2:
                out.append("")
            else:
                pending_blanks += 1
            return
        is_comment = text.lstrip().startswith("//")
        if depth >= 2 and pending_blanks and not is_comment:
            pending_blanks = 0  # drop blanks: next real line is code
        elif pending_blanks:
            out.extend([""] * min(pending_blanks, 1) if depth >= 2 else [""] * pending_blanks)
            pending_blanks = 0
        out.append(text)

    n = len(source)
    while i < n:
        ch = source[i]
        nxt = source[i + 1] if i + 1 < n else ""

        if state == "code":
            if ch == "/" and nxt == "/":
                state = "line_comment"
                line.append("//")
                i += 2
                continue
            if ch == "/" and nxt == "*":
                state = "block_comment"
                line.append("/*")
                i += 2
                continue
            if ch == '"':
                if source.startswith('"""', i):
                    state = "raw"
                    raw_delim = '"""'
                    line.append('"""')
                    i += 3
                    continue
                if i > 0 and source[i - 1] == "@":
                    state = "verbatim"
                else:
                    state = "string"
                line.append(ch)
                i += 1
                continue
            if ch == "'":
                state = "char"
                line.append(ch)
                i += 1
                continue
            if ch == "{":
                depth += 1
                line_has_code = True
            elif ch == "}":
                depth -= 1
                line_has_code = True
            elif not ch.isspace():
                line_has_code = True
            if ch == "\n":
                flush_line()
                line = []
                line_has_code = False
            else:
                line.append(ch)
            i += 1
            continue

        if state == "line_comment":
            if ch == "\n":
                state = "code"
                flush_line()
                line = []
                line_has_code = False
            else:
                line.append(ch)
            i += 1
            continue

        if state == "block_comment":
            if ch == "*" and nxt == "/":
                line.append("*/")
                state = "code"
                i += 2
                continue
            if ch == "\n":
                flush_line()
                line = []
                line_has_code = False
            else:
                line.append(ch)
            i += 1
            continue

        if state == "string":
            if ch == "\\":
                line.append(ch + (nxt or ""))
                i += 2
                continue
            if ch == '"':
                state = "code"
            if ch == "\n":
                # unterminated string on this line (interpolated etc.) — treat as code
                state = "code"
                flush_line()
                line = []
                line_has_code = False
            else:
                line.append(ch)
                if ch != '"':
                    line_has_code = True
            i += 1
            continue

        if state == "verbatim":
            if ch == '"' and nxt == '"':
                line.append('""')
                i += 2
                continue
            if ch == '"':
                state = "code"
            if ch == "\n":
                flush_line()
                line = []
                line_has_code = False
            else:
                line.append(ch)
                line_has_code = True
            i += 1
            continue

        if state == "raw":
            if source.startswith(raw_delim, i):
                line.append(raw_delim)
                state = "code"
                i += len(raw_delim)
                continue
            if ch == "\n":
                flush_line(preserve_blank=True)
                line = []
                line_has_code = False
            else:
                line.append(ch)
                line_has_code = True
            i += 1
            continue

        if state == "char":
            if ch == "\\":
                line.append(ch + (nxt or ""))
                i += 2
                continue
            if ch == "'":
                state = "code"
            if ch == "\n":
                state = "code"
                flush_line()
                line = []
                line_has_code = False
            else:
                line.append(ch)
                line_has_code = True
            i += 1
            continue

    if line:
        flush_line()
    return "\n".join(out) + ("\n" if source.endswith("\n") else "")


def process_file(path: Path, check: bool) -> bool:
    text = path.read_text(encoding="utf-8")
    result = squeeze(text)
    if result == text:
        return False
    if not check:
        path.write_text(result, encoding="utf-8", newline="\n")
    return True


def main() -> int:
    args = sys.argv[1:]
    check = "--check" in args
    targets = [a for a in args if a != "--check"] or ["src", "tests", "tools"]
    changed = 0
    for target in targets:
        root = Path(target)
        files = [root] if root.is_file() else root.rglob("*.cs")
        for f in files:
            if "obj" in f.parts or "bin" in f.parts:
                continue
            if process_file(f, check):
                changed += 1
                print(("would change " if check else "changed     ") + str(f))
    print(f"{changed} file(s) {'need' if check else 'got'} blank-line squeezing")
    return 1 if (check and changed) else 0


if __name__ == "__main__":
    sys.exit(main())

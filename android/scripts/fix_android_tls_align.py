#!/usr/bin/env python3
"""Fix underaligned PT_TLS in Android ARM(aarch64/arm) executables.

Bionic requires the executable's ELF TLS segment (PT_TLS) to have at least
64-byte alignment on aarch64 (32 on arm). Toolchains that produce small TLS
sections (a plain `__thread int` section aligns to 8) emit PT_TLS p_align=8,
and Bionic's loader aborts at exec() time with:

  error: ".../libtor.so": executable's TLS segment is underaligned:
         alignment is 8 (skew 0), needs to be at least 64 for ARM64 Bionic

This is the same fix termux-elf-cleaner applies: bump PT_TLS p_align to the
required minimum, in place, keeping the file otherwise untouched.
"""

import os
import struct
import sys

PT_TLS = 7

# ELF program header layout offsets (p_type at 0).
# ELF64 phentsize=56, p_align at +48 (Q); ELF32 phentsize=32, p_align at +28 (I).
PALIGN_64 = 48
PALIGN_32 = 28


def fix_file(path: str):
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < 64 or data[:4] != b"\x7fELF":
        return "not ELF"

    ei_class = data[4]
    if ei_class == 2:
        # e_phoff (u64) @ 0x20, e_phentsize (u16) @ 0x36, e_phnum (u16) @ 0x38
        e_phoff = struct.unpack_from("<Q", data, 0x20)[0]
        phentsize, phnum = struct.unpack_from("<HH", data, 0x36)
        align_offset, align_fmt, minimum = PALIGN_64, "<Q", 64
    elif ei_class == 1:
        # e_phoff (u32) @ 0x1C, e_phentsize (u16) @ 0x2A, e_phnum (u16) @ 0x2C
        e_phoff = struct.unpack_from("<I", data, 0x1C)[0]
        phentsize, phnum = struct.unpack_from("<HH", data, 0x2A)
        align_offset, align_fmt, minimum = PALIGN_32, "<I", 32
    else:
        return "unknown ELF class"

    if phentsize < align_offset + struct.calcsize(align_fmt):
        return "short program header"

    changed = 0
    for i in range(phnum):
        base = e_phoff + i * phentsize
        if base + phentsize > len(data):
            break
        if struct.unpack_from("<I", data, base)[0] != PT_TLS:
            continue
        pos = base + align_offset
        align = struct.unpack_from(align_fmt, data, pos)[0]
        if align < minimum:
            align = minimum
            data = data[:pos] + struct.pack(align_fmt, align) + data[pos + struct.calcsize(align_fmt):]
            changed += 1

    if changed == 0:
        return "ok (no TLS bump needed)"

    # preserve permission bits
    mode = os.stat(path).st_mode
    with open(path, "wb") as f:
        f.write(data)
    os.chmod(path, mode)
    return f"fixed PT_TLS alignment ({changed} segment)"


def main(argv):
    rc = 0
    for path in argv[1:]:
        if not os.path.exists(path):
            print(f"skip (missing): {path}")
            continue
        try:
            print(f"{fix_file(path)}: {path}")
        except Exception as e:  # noqa: BLE001
            print(f"error ({e}): {path}")
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
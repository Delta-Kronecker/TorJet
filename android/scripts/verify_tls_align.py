#!/usr/bin/env python3
"""Verify that Android ARM(aarch64/arm) executables have Bionic-safe PT_TLS.

Bionic's loader aborts the executable at exec() time unless the ELF TLS
segment satisfies BOTH conditions (libc/bionic/bionic_elf_tls.cpp):

  1. p_align >= 64 on aarch64 (32 on arm): "TLS segment is underaligned:
     alignment is 8 (skew 0), needs to be at least 64 for ARM64 Bionic"
  2. skew = p_vaddr % max(1, p_align) == 0, otherwise the computed ABI and
     actual TPOFFs diverge and the abort message reports "skew <x>":
     abi_tpoff = align_checked(2*wordsize, {align, skew})
     actual_tpoff = align_checked((MAX_TLS_SLOT+1)*wordsize, {align, skew})
     which only match when skew == 0 (for align 64: both equal 64).

Bumping p_align post-link cannot fix condition 2 (p_vaddr is baked in), so the
only reliable fix is at link time (targeting API 29+ lets the NDK clang driver
link crtbegin_tls.o, whose 64-byte aligned placeholder makes the linker emit a
compliant segment). This script only VERIFIES; it returns nonzero if any
segment would still abort in Bionic.
"""

import os
import struct
import sys

PT_TLS = 7

# ELF program header layout offsets (p_type at 0).
# ELF64 phentsize=56, p_vaddr at +16, p_align at +48 (Q).
# ELF32 phentsize=32, p_vaddr at +8,  p_align at +28 (I).
PVADDR_64 = 16
PALIGN_64 = 48
PVADDR_32 = 8
PALIGN_32 = 28


def verify_file(path: str):
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < 64 or data[:4] != b"\x7fELF":
        return "not ELF"

    ei_class = data[4]
    if ei_class == 2:
        # e_phoff (u64) @ 0x20, e_phentsize (u16) @ 0x36, e_phnum (u16) @ 0x38
        e_phoff = struct.unpack_from("<Q", data, 0x20)[0]
        phentsize, phnum = struct.unpack_from("<HH", data, 0x36)
        vaddr_offset, align_offset, fmt, min_align = PVADDR_64, PALIGN_64, "<Q", 64
    elif ei_class == 1:
        # e_phoff (u32) @ 0x1C, e_phentsize (u16) @ 0x2A, e_phnum (u16) @ 0x2C
        e_phoff = struct.unpack_from("<I", data, 0x1C)[0]
        phentsize, phnum = struct.unpack_from("<HH", data, 0x2A)
        vaddr_offset, align_offset, fmt, min_align = PVADDR_32, PALIGN_32, "<I", 32
    else:
        return "unknown ELF class"

    if phentsize < align_offset + struct.calcsize(fmt):
        return "short program header"

    for i in range(phnum):
        base = e_phoff + i * phentsize
        if base + phentsize > len(data):
            break
        if struct.unpack_from("<I", data, base)[0] != PT_TLS:
            continue
        vaddr = struct.unpack_from(fmt, data, base + vaddr_offset)[0]
        align = struct.unpack_from(fmt, data, base + align_offset)[0]
        if align < min_align:
            return f"FAIL: PT_TLS p_align={align} < {min_align} (skew would abort exec)"
        skew = vaddr % max(1, align)
        if skew != 0:
            return (
                f"FAIL: PT_TLS p_align={align} but skew={skew} "
                f"(p_vaddr={vaddr:#x} % {align} != 0) -> Bionic aborts exec; "
                f"must fix at link time, not post-hoc"
            )
        return f"OK: PT_TLS p_align={align}, p_vaddr={vaddr:#x}, skew=0"

    return "OK (no PT_TLS segment)"


def main(argv):
    rc = 0
    for path in argv[1:]:
        if not os.path.exists(path):
            print(f"skip (missing): {path}")
            continue
        try:
            msg = verify_file(path)
        except Exception as e:  # noqa: BLE001
            print(f"error ({e}): {path}")
            rc = 1
            continue
        print(f"{msg}: {path}")
        if msg.startswith("FAIL"):
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
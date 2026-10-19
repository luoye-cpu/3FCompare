#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ABI smoke check for FFF3FPConfiguration against a built FFF.Native.dll.

Why this exists
---------------
FFF3FP_Create (identical logic in both kernel trees, 3FP/Api/PlayerApi.cpp)
returns InvalidArgument (-1) for TWO unrelated reasons:

    configuration->size < sizeof(FFF3FPConfiguration)   # the layout/size gate
    configuration->sdrScRgbMode > 1                     # the policy guard

Both produce the same code, so a single "mode 2 -> -1" observation proves
nothing: a mirror that is too short for the tree it was pointed at makes 0 and 1
fail and 2 "pass" for exactly the size reason. This version separates the two
conclusions with controls, and reports NOT JUDGED whenever the separation could
not be established (docs/45 P1-1).

Checks, in order
----------------
1. native size gate measured from the DLL itself: the `size` member is binary
   searched until the kernel stops answering InvalidArgument, which yields the
   built DLL's own sizeof(FFF3FPConfiguration) without trusting any transcript.
2. mirror agreement: ctypes sizeof of the selected mirror must equal that
   measured native sizeof, and its field count must equal the declaration count
   read from the header the mirror was transcribed from.
3. size-gate signature: with a correct mirror and size = native-1, mode 0 is
   rejected too - the evidence that -1 has two distinct causes.
4. positive control: at the correct size, sdrScRgbMode = 0 and 1 must be
   accepted (Success). "Accepted" here is what makes a later -1 mean a value,
   not the layout.
5. guard arm: only when 1-4 all hold is sdrScRgbMode = 2 -> InvalidArgument
   counted as proof that the guard is live in this binary.

The mirror follows the tree under test
--------------------------------------
The repo carries two kernel trees whose FFF3FPConfiguration differ, so no single
mirror is correct. The mirror is selected from FFF3FP_GetApiVersion() (override
with --api / --tree); each list below was transcribed field for field from that
tree's own header with
    awk '/^struct FFF3FPConfiguration/,/^};/' <header>
and the member count / ctypes sizeof were measured, not remembered:
  api 17 shipped      third_party/fff_project/FFF.Native/3FP/Api/FFF.Player.Api.h
                      15 fields -> 80 bytes   (historical)
  api 19 experimental 3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h
                      22 fields -> 112 bytes  (historical)
  api 20 shipped      third_party/fff_project/FFF.Native/3FP/Api/FFF.Player.Api.h
                      15 fields -> 80 bytes
  api 20 experimental 3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h
                      22 fields -> 112 bytes

Both trees now report PlayerApiVersion = 20 (each tree's Api/PlayerApi.cpp), so
the api number no longer identifies a layout on its own. Pass --tree to say which
mirror you mean; without it the mirror is picked by the sizeof measured from the
DLL (80 vs 112 bytes tells the two api 20 mirrors apart unambiguously), and an
ambiguous measurement is reported as NOT JUDGED rather than guessed.

Usage
-----
    py -3 tools/check_kernel_config_abi.py [path-to-FFF.Native.dll]
    py -3 tools/check_kernel_config_abi.py --dll <path> [--api 17|19] [--tree shipped|experimental]
    py -3 tools/check_kernel_config_abi.py --list-layouts

Exit codes
----------
  0 every check was judged and passed
  1 a check was judged and failed (layout drift, size mismatch, guard not live)
  2 usage error
  3 NOT JUDGED - the environment could not produce a verdict (missing/unloadable
    DLL, unknown api with no mirror, an only-environment result): never green.
"""

from __future__ import annotations

import argparse
import ctypes
import datetime
import hashlib
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Default target: the experimental tree's build output. The mirror is chosen from
# what the DLL reports, so this only names a starting point - pointing it at the
# shipped tree via the positional argument or --dll is equally supported.
DEFAULT_DLL = os.path.join(REPO, "3fp", "build", "FFF.Native.dll")

EXIT_OK = 0
EXIT_FAIL = 1
EXIT_USAGE = 2
EXIT_NOT_JUDGED = 3

# Probe buffer: the kernel copies its own sizeof(FFF3FPConfiguration) out of the
# pointer, so the buffer must be comfortably larger than any layout we test.
PROBE_BUFFER = 1024

_U32 = ctypes.c_uint32
_I32 = ctypes.c_int32
_F32 = ctypes.c_float
_P = ctypes.c_void_p

# api 17 - shipped tree
# third_party/fff_project/FFF.Native/3FP/Api/FFF.Player.Api.h
# 15 members; enum classes are ': std::uint32_t', pointers are 8 bytes on x64.
LAYOUT_17 = [
    ("size", _U32),
    ("version", _U32),
    ("outputWindow", _P),
    ("decodeMode", _U32),
    ("colorMode", _U32),
    ("sdrPeakNits", _F32),
    ("hdrPeakNits", _F32),
    ("sdrPaperWhiteNits", _F32),
    ("audioEndpointIdUtf8", _P),
    ("eventCallback", _P),
    ("eventCallbackContext", _P),
    ("videoScalingQuality", _U32),
    ("forceHdrOutput", _U32),
    ("preferredAdapterIndex", _I32),
    ("sdrScRgbMode", _U32),
]

# api 19 - experimental tree
# 3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h
# 22 members: the first 15 are declared character-for-character the same way in
# that header (checked against its own awk extraction, not assumed), plus the
# seven adaptive decode/upload knobs that only exist in this tree.
LAYOUT_19 = LAYOUT_17 + [
    ("adaptiveDownscaleBeforeUpload", _U32),
    ("adaptiveDownscaleDropPercent", _U32),
    ("adaptiveDecoderThreads", _U32),
    ("minSoftwareDecoderThreads", _U32),
    ("maxSoftwareDecoderThreads", _U32),
    ("decoderGrowDropPercent", _U32),
    ("decoderShrinkDropPercent", _U32),
]

# api 20 - shipped tree
# 22 fields -> 112 bytes: the shipping tree took upstream 578f80b (PR #31) on 2026-10-20,
# so both headers now declare the same struct. Spelled out rather than aliased, so drift
# in one tree shows up as its own edit.
LAYOUT_20_SHIPPED = [
    ("size", _U32),
    ("version", _U32),
    ("outputWindow", _P),
    ("decodeMode", _U32),
    ("colorMode", _U32),
    ("sdrPeakNits", _F32),
    ("hdrPeakNits", _F32),
    ("sdrPaperWhiteNits", _F32),
    ("audioEndpointIdUtf8", _P),
    ("eventCallback", _P),
    ("eventCallbackContext", _P),
    ("videoScalingQuality", _U32),
    ("forceHdrOutput", _U32),
    ("preferredAdapterIndex", _I32),
    ("sdrScRgbMode", _U32),
    ("adaptiveDownscaleBeforeUpload", _U32),
    ("adaptiveDownscaleDropPercent", _U32),
    ("adaptiveDecoderThreads", _U32),
    ("minSoftwareDecoderThreads", _U32),
    ("maxSoftwareDecoderThreads", _U32),
    ("decoderGrowDropPercent", _U32),
    ("decoderShrinkDropPercent", _U32),
]

# api 20 - experimental/kernel tree
# 3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h
# 22 members, transcribed field for field from that header; same struct as the shipped tree
# since 2026-10-20, but read from a different header on purpose.
# 22 fields -> 112 bytes.
LAYOUT_20_KERNEL = [
    ("size", _U32),
    ("version", _U32),
    ("outputWindow", _P),
    ("decodeMode", _U32),
    ("colorMode", _U32),
    ("sdrPeakNits", _F32),
    ("hdrPeakNits", _F32),
    ("sdrPaperWhiteNits", _F32),
    ("audioEndpointIdUtf8", _P),
    ("eventCallback", _P),
    ("eventCallbackContext", _P),
    ("videoScalingQuality", _U32),
    ("forceHdrOutput", _U32),
    ("preferredAdapterIndex", _I32),
    ("sdrScRgbMode", _U32),
    ("adaptiveDownscaleBeforeUpload", _U32),
    ("adaptiveDownscaleDropPercent", _U32),
    ("adaptiveDecoderThreads", _U32),
    ("minSoftwareDecoderThreads", _U32),
    ("maxSoftwareDecoderThreads", _U32),
    ("decoderGrowDropPercent", _U32),
    ("decoderShrinkDropPercent", _U32),
]

SHIPPED_HEADER = "third_party/fff_project/FFF.Native/3FP/Api/FFF.Player.Api.h"
EXPERIMENTAL_HEADER = "3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h"


class Layout(object):
    def __init__(self, api, tree, header_rel, fields):
        self.api = api
        self.tree = tree
        self.header = os.path.join(REPO, *header_rel.split("/"))
        self.fields = fields
        self.type = _make_struct(fields)
        self.size = ctypes.sizeof(self.type)
        self.field_count = len(fields)

    def describe(self):
        return "api %d / %s tree / %d fields / %d bytes (header: %s)" % (
            self.api, self.tree, self.field_count, self.size, self.header)


def _make_struct(fields, name="FFF3FPConfigurationMirror"):
    return type(name, (ctypes.Structure,), {"_fields_": list(fields)})


# Current mirrors, keyed by canonical tree name: both trees sit at api 20, so the
# tree is the only thing that tells the two layouts apart.
LAYOUTS_BY_TREE = {
    "shipped": Layout(20, "shipped", SHIPPED_HEADER, LAYOUT_20_SHIPPED),
    "experimental": Layout(20, "experimental", EXPERIMENTAL_HEADER, LAYOUT_20_KERNEL),
}

# Historical mirrors, keyed by api: kept so an older DLL can still be judged with
# an explicit --api instead of silently falling through to NOT JUDGED.
LAYOUTS_BY_API = {
    17: Layout(17, "shipped", SHIPPED_HEADER, LAYOUT_17),
    19: Layout(19, "experimental", EXPERIMENTAL_HEADER, LAYOUT_19),
}

ALL_LAYOUTS = sorted(list(LAYOUTS_BY_API.values()) + list(LAYOUTS_BY_TREE.values()),
                     key=lambda item: (item.api, item.tree))

# --tree values resolve to a canonical tree name, not to an api: the api is read
# from the DLL and only checked for agreement.
TREE_ALIASES = {
    "shipped": "shipped", "ship": "shipped", "third_party": "shipped",
    "experimental": "experimental", "exp": "experimental", "kernel": "experimental",
}

# Measuring sizeof needs a probe, but picking the mirror needs the measurement:
# the size gate is checked before any other member, and the first 15 members are
# declared identically in both trees, so this neutral probe measures either tree
# without having to know in advance which one it is pointed at.
MEASURE_TYPE = _make_struct(
    LAYOUT_17 + [("tail", ctypes.c_char * (PROBE_BUFFER - ctypes.sizeof(_make_struct(LAYOUT_17))))],
    name="FFF3FPConfigurationMeasureProbe")


def candidates_for(api, tree_name):
    """Mirror candidates for (api, tree); tree_name None means 'any tree'."""
    return [layout for layout in ALL_LAYOUTS
            if layout.api == api and (tree_name is None or layout.tree == tree_name)]

RESULT_NAMES = {
    0: "Success",
    -1: "InvalidArgument",
    -2: "InvalidState",
    -3: "BufferTooSmall",
    -4: "NativeFailure",
    -5: "FfmpegFailure",
    -6: "DeviceFailure",
    -7: "NotSupported",
}


def name_of(code):
    return RESULT_NAMES.get(code, "?%d" % code)


class Ledger(object):
    """Judged-pass / judged-fail / not-judged counters; green needs zero fails
    and zero not-judged, which is what keeps environment results off the pass
    side of the ledger."""

    def __init__(self):
        self.passed = 0
        self.failed = 0
        self.undetermined = 0

    def ok(self, label):
        self.passed += 1
        print("  %-46s %s" % (label, "OK"))

    def fail(self, label, detail=""):
        self.failed += 1
        print("  %-46s %s%s" % (label, "FAIL", " - " + detail if detail else ""))

    def undecided(self, label, detail=""):
        self.undetermined += 1
        print("  %-46s %s%s" % (label, "NOT JUDGED", " - " + detail if detail else ""))

    def exit_code(self):
        if self.failed:
            return EXIT_FAIL
        if self.undetermined:
            return EXIT_NOT_JUDGED
        return EXIT_OK


def fingerprint(path):
    with open(path, "rb") as handle:
        blob = handle.read()
    stat = os.stat(path)
    return "sha256:%s size:%d mtime:%s" % (
        hashlib.sha256(blob).hexdigest()[:16].upper(),
        stat.st_size,
        datetime.datetime.fromtimestamp(stat.st_mtime).strftime("%Y-%m-%d %H:%M:%S"),
    )


_HEADER_FIELD_RE = re.compile(
    r"^\s*(?:(?:std::)?(?:u?int(?:32|64)_t|float|double)"
    r"|void\s*\*|const\s+char\s*\*"
    r"|FFF3FP(?:DecodeMode|ColorMode|VideoScalingQuality|EventCallback))"
    r"\s+\w+\s*(?:=[^;]*)?;"
)


def count_header_fields(header_path):
    """Re-read the authoritative header so a mirror that drifts from the tree it
    claims to mirror is caught even before the DLL is loaded.

    Returns the list of declared member lines, or None when the header cannot be
    read or holds no FFF3FPConfiguration struct (the caller then reports the
    cross-check as NOT JUDGED instead of letting it pass silently)."""
    if not os.path.exists(header_path):
        return None
    names = []
    inside = False
    with open(header_path, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            stripped = line.strip()
            if not inside:
                if stripped.startswith("struct FFF3FPConfiguration"):
                    inside = True
                continue
            if stripped.startswith("};"):
                break
            if not stripped or stripped.startswith("//"):
                continue
            if _HEADER_FIELD_RE.match(stripped):
                names.append(stripped)
    if not inside or not names:
        return None
    return names


def bind(dll):
    dll.FFF3FP_GetApiVersion.restype = ctypes.c_uint32
    dll.FFF3FP_GetApiVersion.argtypes = []
    dll.FFF3FP_Create.restype = ctypes.c_int
    dll.FFF3FP_Create.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    dll.FFF3FP_Destroy.restype = None
    dll.FFF3FP_Destroy.argtypes = [ctypes.c_void_p]


def create(dll, probe_type, api, size, sdr_mode):
    """One FFF3FP_Create with known-good values; returns the raw result code.

    The probe type carries the mirror's prefix plus a tail, so `size` may be
    larger than the mirror while the buffer stays readable."""
    config = probe_type()
    values = (
        ("size", size),
        ("version", api),
        ("decodeMode", 1),           # CPU
        ("colorMode", 0),            # MapToSdr
        ("sdrPeakNits", 100.0),
        ("hdrPeakNits", 0.0),
        ("sdrPaperWhiteNits", 203.0),
        ("videoScalingQuality", 1),  # HighQuality
        ("forceHdrOutput", 0),
        ("preferredAdapterIndex", -1),
        ("sdrScRgbMode", sdr_mode),
    )
    for name, value in values:
        # --drop-fields can remove a member the probe no longer carries (in the
        # shipped mirror sdrScRgbMode IS the trailing member). Skipping the
        # missing one keeps the negative sample a JUDGED FAIL that reaches the
        # ledger instead of an AttributeError traceback that never prints a
        # verdict.
        if hasattr(config, name):
            setattr(config, name, value)
    handle = ctypes.c_void_p()
    result = dll.FFF3FP_Create(ctypes.byref(config), ctypes.byref(handle))
    if result == 0 and handle.value:
        dll.FFF3FP_Destroy(handle)
    return result


def measure_native_sizeof(dll, probe_type, api):
    """Binary search the `size` member for the smallest value the kernel accepts.

    FFF3FP_Create rejects size < sizeof(FFF3FPConfiguration) before it looks at
    any other field, so the  -1 / not -1 boundary is the built DLL's own sizeof.
    Returns (sizeof, log) or (None, log) when the boundary is not observable."""
    log = []

    def accepted(size):
        result = create(dll, probe_type, api, size, 0)
        log.append((size, result))
        return result != -1

    low, high = 1, PROBE_BUFFER
    top = accepted(high)
    if not top:
        return None, log
    if accepted(low):
        return None, log          # no size gate observable at all
    while high - low > 1:
        mid = (low + high) // 2
        if accepted(mid):
            high = mid
        else:
            low = mid
    return high, log


def main(argv):
    parser = argparse.ArgumentParser(
        description="ABI smoke check for FFF3FPConfiguration against a built FFF.Native.dll",
        epilog="exit 0 = judged pass, 1 = judged fail, 2 = usage, 3 = NOT JUDGED")
    parser.add_argument("dll_positional", nargs="?", help=argparse.SUPPRESS)
    parser.add_argument("--dll", help="path to FFF.Native.dll")
    parser.add_argument("--api", type=int, help="force the mirror for this PlayerApiVersion")
    parser.add_argument("--tree", choices=sorted(TREE_ALIASES), help="force the mirror by tree")
    parser.add_argument("--drop-fields", dest="drop_fields", type=int, default=0,
                        help="negative sample: drop the trailing N mirror fields")
    parser.add_argument("--list-layouts", dest="list_layouts", action="store_true",
                        help="print the mirrors and exit")
    args = parser.parse_args(argv)

    if args.list_layouts:
        for layout in ALL_LAYOUTS:
            print(layout.describe())
        return EXIT_OK
    if args.drop_fields < 0:
        print("usage error: --drop-fields must be >= 0")
        return EXIT_USAGE

    requested_tree = TREE_ALIASES.get(args.tree) if args.tree else None
    requested_api = args.api
    if requested_tree is not None:
        tree_api = LAYOUTS_BY_TREE[requested_tree].api
        if requested_api is not None and requested_api != tree_api:
            print("usage error: --api %d contradicts --tree %s (that tree is api %d)"
                  % (requested_api, args.tree, tree_api))
            return EXIT_USAGE
        requested_api = tree_api

    path = args.dll or args.dll_positional or DEFAULT_DLL
    print("dll    : %s" % path)
    if not os.path.exists(path):
        print("NOT JUDGED: dll not found - no ABI conclusion is possible")
        return EXIT_NOT_JUDGED
    print("        : %s" % fingerprint(path))
    try:
        dll = ctypes.CDLL(path)
    except OSError as error:
        print("NOT JUDGED: load failed - %s" % error)
        return EXIT_NOT_JUDGED
    for required in ("FFF3FP_GetApiVersion", "FFF3FP_Create", "FFF3FP_Destroy"):
        if not hasattr(dll, required):
            print("NOT JUDGED: export %s missing" % required)
            return EXIT_NOT_JUDGED
    bind(dll)

    api = dll.FFF3FP_GetApiVersion()
    print("FFF3FP_GetApiVersion = %d" % api)

    if requested_api is not None and requested_api != api:
        print("FAIL: mirror requested for api %d but the DLL reports api %d - "
              "mirror and target tree do not match" % (requested_api, api))
        return EXIT_FAIL

    candidates = candidates_for(api, requested_tree)
    if not candidates:
        if requested_tree is None:
            print("NOT JUDGED: no mirror transcribed for api %d. Add it from that tree's "
                  "FFF.Player.Api.h before trusting any verdict here." % api)
        else:
            print("NOT JUDGED: no mirror transcribed for api %d in the %s tree. Add it "
                  "from that tree's FFF.Player.Api.h before trusting any verdict here."
                  % (api, requested_tree))
        return EXIT_NOT_JUDGED

    ledger = Ledger()

    # 1. what the built DLL thinks sizeof(FFF3FPConfiguration) is. Measured with
    # the tree-neutral probe, before the mirror is chosen, so that the measurement
    # itself can pick between two mirrors that share an api number.
    native_size, search_log = measure_native_sizeof(dll, MEASURE_TYPE, api)
    if native_size is None:
        print("sizeof boundary not observable; probes: %s" % (search_log[-6:],))
        ledger.undecided("native sizeof measurable from the DLL",
                         "every probe returned InvalidArgument - the size gate is "
                         "not separable here")
        print("\nVERDICT: NOT JUDGED")
        return ledger.exit_code()

    if len(candidates) == 1:
        layout = candidates[0]
    else:
        sized = [item for item in candidates if item.size == native_size]
        if len(sized) != 1:
            print("NOT JUDGED: api %d carries %d mirrors (%s) and the measured native "
                  "sizeof %d bytes matches %d of them - pass --tree to say which one "
                  "you mean" % (api, len(candidates),
                                ", ".join("%s/%d bytes" % (item.tree, item.size)
                                          for item in candidates),
                                native_size, len(sized)))
            return EXIT_NOT_JUDGED
        layout = sized[0]
        print("mirror : api %d is shared by %d trees; picked %s from the measured "
              "native sizeof" % (api, len(candidates), layout.tree))
    print("mirror : %s" % layout.describe())

    fields = layout.fields
    if args.drop_fields:
        if args.drop_fields >= len(fields):
            print("usage error: --drop-fields %d leaves no fields" % args.drop_fields)
            return EXIT_USAGE
        fields = fields[:len(fields) - args.drop_fields]
        print("mirror : NEGATIVE SAMPLE, trailing %d field(s) dropped -> %d fields"
              % (args.drop_fields, len(fields)))
    mirror_type = _make_struct(fields)
    mirror_size = ctypes.sizeof(mirror_type)

    tail = PROBE_BUFFER - mirror_size
    if tail < 0:
        print("FAIL: mirror (%d bytes) is larger than the probe buffer (%d)"
              % (mirror_size, PROBE_BUFFER))
        return EXIT_FAIL
    probe_type = _make_struct(fields + [("tail", ctypes.c_char * tail)],
                              name="FFF3FPConfigurationProbe")

    print("sizeof(FFF3FPConfiguration) [ctypes mirror] = %d bytes" % mirror_size)
    print("sizeof(FFF3FPConfiguration) [measured dll]  = %d bytes" % native_size)

    # 2b. header cross-check: the mirror must be the tree's own field list.
    header_names = count_header_fields(layout.header)
    if header_names is None:
        ledger.undecided("mirror matches the tree's header",
                         "header unreadable: %s" % layout.header)
    elif len(header_names) != len(fields):
        ledger.fail("mirror matches the tree's header",
                    "mirror has %d fields, %s declares %d"
                    % (len(fields), os.path.basename(layout.header), len(header_names)))
    else:
        ledger.ok("mirror matches the tree's header (%d fields)" % len(header_names))

    # 2c. mirror vs the DLL's own layout. A short mirror is rejected by the size
    # gate before sdrScRgbMode is ever read, so nothing downstream can be
    # attributed to the guard.
    if mirror_size != native_size:
        cause = "MIRROR TOO SHORT" if mirror_size < native_size else "MIRROR TOO LARGE"
        ledger.fail("mirror layout == dll layout",
                    "%s: mirror %d bytes vs native %d bytes -> the kernel rejects on "
                    "size alone, so every -1 below is a SIZE rejection, not a "
                    "sdrScRgbMode rejection" % (cause, mirror_size, native_size))
        size_driven = create(dll, probe_type, api, mirror_size, 0)
        print("  size-gate proof: size=%d (this mirror) with sdrScRgbMode=0 -> %s (%d)"
              % (mirror_size, name_of(size_driven), size_driven))
        if size_driven == -1:
            print("  a valid sdrScRgbMode=0 is rejected at this size, therefore the "
                  "sdrScRgbMode=2 rejection further down would have meant the same thing")
        print("\nVERDICT: SIZE/ABI REJECTED - guard NOT JUDGED")
        ledger.undecided("sdrScRgbMode guard", "indistinguishable from the size gate")
        return ledger.exit_code()
    ledger.ok("mirror layout == dll layout (%d bytes)" % native_size)

    # 3. size-gate signature on its own: -1 has two causes in this build.
    short = create(dll, probe_type, api, native_size - 1, 0)
    short_mode2 = create(dll, probe_type, api, native_size - 1, 2)
    if short == -1 and short_mode2 == -1:
        ledger.ok("size gate live (size=%d -> InvalidArgument for a valid mode 0)"
                  % (native_size - 1))
        print("  both -1 paths exist: size=%d/mode=0 -> %s, size=%d/mode=2 -> %s"
              % (native_size - 1, name_of(short), native_size - 1, name_of(short_mode2)))
    else:
        ledger.undecided("size gate live",
                         "size=%d/mode=0 -> %s, size=%d/mode=2 -> %s"
                         % (native_size - 1, name_of(short), native_size - 1, name_of(short_mode2)))

    # 4. positive control at the correct size. This is the arm that makes the
    # guard test falsifiable: if 0/1 cannot build a session, a 2 rejection is
    # worth nothing.
    controls = {}
    control_green = True
    for mode in (0, 1):
        result = create(dll, probe_type, api, native_size, mode)
        controls[mode] = result
        if result == 0:
            ledger.ok("control: size=%d sdrScRgbMode=%d accepted" % (native_size, mode))
        elif result == -1:
            control_green = False
            ledger.fail("control: size=%d sdrScRgbMode=%d accepted" % (native_size, mode),
                        "rejected although the layout gate passed at this size - "
                        "field order or types drift, not the guard")
        else:
            control_green = False
            ledger.undecided("control: size=%d sdrScRgbMode=%d accepted" % (native_size, mode),
                             "%s (%d) is an environment result, not an ABI verdict"
                             % (name_of(result), result))

    # 5. the guard arm, only counted when the controls actually separated the
    # two -1 causes.
    guard = create(dll, probe_type, api, native_size, 2)
    if control_green and short == -1:
        if guard == -1:
            ledger.ok("guard: size=%d sdrScRgbMode=2 rejected for its VALUE" % native_size)
        elif guard == 0:
            ledger.fail("guard: size=%d sdrScRgbMode=2 rejected" % native_size,
                        "accepted - this binary has no sdrScRgbMode guard")
        else:
            ledger.undecided("guard: size=%d sdrScRgbMode=2" % native_size,
                             "%s (%d) is an environment result, not an ABI verdict"
                             % (name_of(guard), guard))
    else:
        ledger.undecided("guard: size=%d sdrScRgbMode=2" % native_size,
                         "controls did not establish that a correctly sized session can "
                         "be created (0/1 -> %s); the mode-2 result (%s) is therefore "
                         "not attributable to the guard"
                         % (", ".join("%s" % name_of(controls[mode]) for mode in (0, 1)),
                            name_of(guard)))

    code = ledger.exit_code()
    print("\ncounts: judged-pass=%d judged-fail=%d not-judged=%d"
          % (ledger.passed, ledger.failed, ledger.undetermined))
    if code == EXIT_OK:
        print("VERDICT: PASS - mirror of %d fields/%d bytes matches this tree's native "
              "layout, size gate and sdrScRgbMode guard are separately proven live"
              % (len(fields), native_size))
    elif code == EXIT_FAIL:
        print("VERDICT: FAIL - see the FAIL lines above")
    else:
        print("VERDICT: NOT JUDGED - the environment produced no ABI conclusion; "
              "this is not a pass")
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

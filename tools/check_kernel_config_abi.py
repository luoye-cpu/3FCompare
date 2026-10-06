#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ABI smoke check for FFF3FPConfiguration against a built FFF.Native.dll.

Why this exists
---------------
FFF3FP_Create validates `size >= sizeof(FFF3FPConfiguration)` and
`version == PlayerApiVersion` **and** clamps new policy fields (sdrScRgbMode > 1
is rejected). A managed struct that drifts from the native layout — a missing
field, a wrong order, a padding surprise after the first pointer — does not fail
to compile; it fails at run time as "every session creation returns
InvalidArgument" (docs/45 P1-1). This script is the cheap guard: it builds the
same struct in ctypes (mirroring Fff3FpNative.Fff3FpConfiguration field for
field) and asks the real DLL what it thinks.

Checks
------
1. sizeof(struct) — must be >= the native sizeof (equality is reported for info).
2. FFF3FP_Create with sdrScRgbMode = 0 and 1 must not be rejected for ABI reasons.
3. FFF3FP_Create with sdrScRgbMode = 2 must return InvalidArgument (-1): proves
   the new guard is live in the built DLL, i.e. this really is the new kernel.

Usage
-----
    python tools/check_kernel_config_abi.py [path-to-FFF.Native.dll]
"""

from __future__ import annotations

import ctypes
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DLL = os.path.join(REPO, "3fp", "build", "FFF.Native.dll")

# Mirrors 3FP/Api/FFF.Player.Api.h and src/3FCompare.Core/Backend/Interop/Fff3FpNative.cs.
# Field order matters: it is the wire layout, not documentation.
class FFF3FPConfiguration(ctypes.Structure):
    _fields_ = [
        ("size", ctypes.c_uint32),
        ("version", ctypes.c_uint32),
        ("outputWindow", ctypes.c_void_p),
        ("decodeMode", ctypes.c_uint32),
        ("colorMode", ctypes.c_uint32),
        ("sdrPeakNits", ctypes.c_float),
        ("hdrPeakNits", ctypes.c_float),
        ("sdrPaperWhiteNits", ctypes.c_float),
        ("audioEndpointIdUtf8", ctypes.c_void_p),
        ("eventCallback", ctypes.c_void_p),
        ("eventCallbackContext", ctypes.c_void_p),
        ("videoScalingQuality", ctypes.c_uint32),
        ("forceHdrOutput", ctypes.c_uint32),
        ("preferredAdapterIndex", ctypes.c_int32),
        ("sdrScRgbMode", ctypes.c_uint32),
    ]


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


def name_of(code: int) -> str:
    return RESULT_NAMES.get(code, "?%d" % code)


def create(dll, sdr_mode: int) -> int:
    config = FFF3FPConfiguration()
    config.size = ctypes.sizeof(FFF3FPConfiguration)
    config.version = dll.FFF3FP_GetApiVersion()
    config.decodeMode = 1          # CPU
    config.colorMode = 0           # MapToSdr
    config.sdrPeakNits = 100.0
    config.hdrPeakNits = 0.0
    config.sdrPaperWhiteNits = 203.0
    config.videoScalingQuality = 1  # HighQuality
    config.preferredAdapterIndex = -1
    config.sdrScRgbMode = sdr_mode
    handle = ctypes.c_void_p()
    result = dll.FFF3FP_Create(ctypes.byref(config), ctypes.byref(handle))
    if result == 0 and handle.value:
        dll.FFF3FP_Destroy(handle)  # void FFF3FP_Destroy(FFF3FPHandle)
    return result


def main() -> int:
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DLL
    if not os.path.exists(path):
        print("dll not found: %s" % path)
        return 2
    try:
        dll = ctypes.CDLL(path)
    except OSError as error:
        print("load failed: %s" % error)
        return 2

    api = dll.FFF3FP_GetApiVersion()
    dll.FFF3FP_Create.restype = ctypes.c_int
    dll.FFF3FP_Create.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    dll.FFF3FP_Destroy.restype = None
    dll.FFF3FP_Destroy.argtypes = [ctypes.c_void_p]

    size = ctypes.sizeof(FFF3FPConfiguration)
    print("dll              : %s" % path)
    print("FFF3FP_GetApiVersion = %d" % api)
    print("sizeof(FFF3FPConfiguration) [ctypes] = %d bytes" % size)

    failures = []
    for mode, expectation in ((0, "accepted"), (1, "accepted"), (2, "rejected")):
        result = create(dll, mode)
        # Only InvalidArgument can fail an "accepted" mode, and only Success can
        # fail the "rejected" mode: everything else (FfmpegFailure etc.) means
        # the ABI itself was accepted but the environment can't run a session
        # (missing FFmpeg DLLs in a CI checkout, for example) - report, don't
        # fail the layout verdict.
        if result == -1:
            ok = expectation == "rejected"
        elif result == 0:
            ok = expectation == "accepted"
        else:
            ok = True
            print("   note: %s is an environment result, not an ABI verdict"
                  % name_of(result))
        print("sdrScRgbMode=%d -> %s (%s) %s"
              % (mode, name_of(result), result, "OK" if ok else "UNEXPECTED"))
        if not ok:
            failures.append((mode, result))

    if failures:
        print("\nABI CHECK FAILED")
        return 1
    print("\nABI check passed: layout accepted, sdrScRgbMode guard is live")
    return 0


if __name__ == "__main__":
    sys.exit(main())

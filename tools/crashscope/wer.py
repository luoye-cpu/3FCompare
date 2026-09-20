"""Report.wer 解析（WER 归档）。

⚠ 关键坑：Sig 字段的**下标顺序在不同崩溃类型下不一样**，不能按 Sig[N] 固定下标取。
   - dxgi 型：Sig[3]=故障模块名称 Sig[6]=异常代码 Sig[7]=异常偏移
   - StackHash 型：Sig[3]=故障模块名称 Sig[6]=异常偏移 Sig[7]=异常代码 Sig[8]=异常数据
   必须按 Sig[N].Name 建映射后再取值。

用法：
    w = parse(os.path.join(wer_dir, "Report.wer"))
    w.exception_code, w.faulting_module, w.exception_offset
"""
from __future__ import annotations

import glob
import os
import re
from dataclasses import dataclass, field

# Sig[N].Name 的中英双语别名
_FIELD_ALIASES = {
    "faulting_module": ["故障模块名称", "Faulting module name"],
    "faulting_module_version": ["故障模块版本", "Faulting module version"],
    "faulting_module_timestamp": ["故障模块时间戳", "Faulting module timestamp"],
    "exception_code": ["异常代码", "Exception code"],
    "exception_offset": ["异常偏移", "Exception offset"],
    "exception_data": ["异常数据", "Exception data"],
    "app_name": ["应用程序名", "Faulting application name"],
    "app_version": ["应用程序版本", "Faulting application version"],
    "app_timestamp": ["应用程序时间戳", "Faulting application timestamp"],
}

_SIG_RE = re.compile(r"^Sig\[(\d+)\]\.Name=(.*)$")
_SIGVAL_RE = re.compile(r"^Sig\[(\d+)\]\.Value=(.*)$")


@dataclass
class WerReport:
    path: str
    dir: str = ""
    event_time: int = 0          # FILETIME
    event_type: str = ""
    app_path: str = ""
    fields: dict = field(default_factory=dict)
    loaded_modules: list = field(default_factory=list)
    raw: dict = field(default_factory=dict)

    def get(self, key: str) -> str:
        return self.fields.get(key, "")

    @property
    def exception_code(self) -> str:
        return self.get("exception_code")

    @property
    def exception_code_int(self):
        v = self.exception_code
        try:
            return int(v, 16)
        except (ValueError, TypeError):
            return None

    @property
    def faulting_module(self) -> str:
        return self.get("faulting_module")

    @property
    def exception_offset(self) -> str:
        return self.get("exception_offset")

    @property
    def exception_data(self) -> str:
        return self.get("exception_data")

    @property
    def is_stackhash(self) -> bool:
        """WER 无法把异常地址归入任何已加载模块 —— 野地址型崩溃。"""
        fm = self.faulting_module.lower()
        return fm.startswith("stackhash") or fm in ("", "unknown")

    @property
    def offset_is_rva(self) -> bool:
        """异常偏移是否为纯十六进制（即模块内 RVA）。"""
        o = self.exception_offset
        return bool(o) and not o.startswith("PCH_") and re.fullmatch(r"[0-9a-fA-F]+", o) is not None

    @property
    def offset_rva(self):
        if self.offset_is_rva:
            try:
                return int(self.exception_offset, 16)
            except ValueError:
                return None
        return None

    @property
    def pch_hint(self) -> str:
        """StackHash 型里 '异常偏移' 字段承载的 PCH 提示，形如 PCH_52_FROM_ntdll+0x161914。"""
        o = self.exception_offset
        return o if o.startswith("PCH_") else ""

    @property
    def av_kind(self):
        """访问违例性质：read/write/EXECUTE。仅当有异常数据字段时可判定。"""
        if self.exception_code.lower() not in ("c0000005",):
            return None
        d = self.exception_data
        if not d:
            return None
        try:
            v = int(d, 16)
        except ValueError:
            return None
        return {0: "read", 1: "write", 8: "EXECUTE"}.get(v, f"unknown({v})")

    def has_module(self, needle: str) -> bool:
        needle = needle.lower()
        return any(needle in m.lower().replace("\\", "/").rsplit("/", 1)[-1]
                   for m in self.loaded_modules)


def _decode(path: str) -> str:
    raw = open(path, "rb").read()
    # Report.wer 通常是 UTF-16LE（带 BOM），少数为 UTF-8/ANSI
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", "replace")
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("utf-16-le", "replace")


def parse(path: str) -> WerReport:
    text = _decode(path)
    rep = WerReport(path=path, dir=os.path.dirname(path))
    sig_name = {}
    sig_val = {}
    for line in text.splitlines():
        m = _SIG_RE.match(line)
        if m:
            sig_name[int(m.group(1))] = m.group(2).strip()
            continue
        m = _SIGVAL_RE.match(line)
        if m:
            sig_val[int(m.group(1))] = m.group(2).strip()
            continue
        if line.startswith("EventTime="):
            try:
                rep.event_time = int(line.split("=", 1)[1])
            except ValueError:
                pass
        elif line.startswith("EventType="):
            rep.event_type = line.split("=", 1)[1].strip()
        elif line.startswith("AppPath="):
            rep.app_path = line.split("=", 1)[1].strip()
        elif line.startswith("LoadedModule["):
            rep.loaded_modules.append(line.split("=", 1)[1].strip())

    # 按 Name 建反查表
    by_name = {}
    for idx, nm in sig_name.items():
        if idx in sig_val:
            by_name.setdefault(nm, sig_val[idx])
    for key, aliases in _FIELD_ALIASES.items():
        for a in aliases:
            if a in by_name:
                rep.fields[key] = by_name[a]
                break
    rep.raw = {f"Sig[{i}].{n}": sig_val.get(i, "") for i, n in sorted(sig_name.items())}
    return rep


def archive_dirs(archive_root: str | None = None) -> list:
    root = archive_root or r"C:\ProgramData\Microsoft\Windows\WER\ReportArchive"
    pat = os.path.join(root, "AppCrash_3FCompare.exe_*")
    return sorted(glob.glob(pat), key=os.path.getmtime)


def load_all(archive_root: str | None = None) -> list:
    out = []
    for d in archive_dirs(archive_root):
        w = os.path.join(d, "Report.wer")
        if os.path.exists(w):
            try:
                out.append(parse(w))
            except Exception:
                pass
    return out


def filetime_to_epoch(ft: int) -> float:
    """Windows FILETIME -> Unix epoch 秒。"""
    return ft / 1e7 - 11644473600.0

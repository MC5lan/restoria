# -*- coding: utf-8 -*-
"""资源门禁 —— 把"句柄有没有被释放"变成一条命令。

扫两类问题：
  1. LockBits 是否与 UnlockBits 成对，且 UnlockBits 在 finally 里
     —— LockBits 期间位图处于锁定状态，异常路径不释放 = 该位图永久不可用 + GDI+ 句柄泄漏
  2. 局部 Bitmap 是否有释放路径（Dispose / using / return 出去 / using 包裹）

为什么值得单独一道门禁：这类问题**不会立刻崩**。连续处理几十张之后 GDI+ 句柄耗尽，
表现为"跑到一半忽然全部保存失败"——属于最难现场复现的那种 bug。

放行办法：在该行或上一行加注释  // resource-ok: 理由
（人工确认过、确实不需要释放的，标注后即视为通过，避免脚本变成狼来了）

用法:
  python tools/check_resources.py            报告；0=干净，1=有错误
  python tools/check_resources.py --strict   有警告也返回 1（给构建流程用）
  python tools/check_resources.py --list     额外列出全部受保护的调用点
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'src')

# 声明 + 调用一起抓：BitmapData d = bmp.LockBits(...)
RE_LOCK = re.compile(r'(\w+)\s*=\s*[\w\.]*\.LockBits\s*\(')
RE_UNLOCK = re.compile(r'\.UnlockBits\s*\(\s*(\w+)\s*\)')
RE_BMP_DECL = re.compile(r'\bBitmap\s+(\w+)\s*=\s*')
RE_USING_BMP = re.compile(r'\busing\s*\(\s*Bitmap\s+(\w+)\s*=')
RE_OK = 'resource-ok'

STRICT = '--strict' in sys.argv
LIST = '--list' in sys.argv


def read(path):
    return io.open(path, encoding='utf-8-sig', errors='replace').read()


def cs_files():
    out = []
    for base, _dirs, files in os.walk(SRC):
        for f in files:
            if f.endswith('.cs'):
                out.append(os.path.join(base, f))
    return sorted(out)


def rel(path):
    return os.path.relpath(path, ROOT).replace(os.sep, '/')


def approved(lines, idx):
    """该行或上一行有 // resource-ok 即放行"""
    for k in (idx, idx - 1):
        if 0 <= k < len(lines) and RE_OK in lines[k]:
            return True
    return False


def check_lockbits(path, lines):
    """返回 (errors, warnings, oks)；每个元素是 (行号, 文本)"""
    errors, warnings, oks = [], [], []

    locks = []
    for i, line in enumerate(lines):
        # finditer 而不是 search：同一行可能有两个 LockBits（如 da/db 一起锁）
        for m in RE_LOCK.finditer(line):
            locks.append((i, m.group(1)))
    if not locks:
        return errors, warnings, oks

    unlocks = []
    for i, line in enumerate(lines):
        # 同理：`finally { a.UnlockBits(da); b.UnlockBits(db); }` 一行为两个解锁，
        # 用 search 只会抓到第一个，导致第二个 LockBits 被误报成"没有配对"。
        for m in RE_UNLOCK.finditer(line):
            unlocks.append((i, m.group(1)))

    used = set()
    for li, name in locks:
        # 往后找最近的同名 UnlockBits
        ui = None
        for k, (uj, uname) in enumerate(unlocks):
            if k in used:
                continue
            if uj > li and uname == name:
                ui, used_idx = uj, k
                used.add(k)
                break

        if ui is None:
            if not approved(lines, li):
                errors.append((li + 1, lines[li].strip()))
            continue

        # 判定 try/finally：UnlockBits 行本身或往上 3 行内有 finally 即可。
        # 不额外检查 try —— C# 语法要求 finally 必须配对 try，有 finally 就必然有。
        # （曾因为多加了"且中间有 try"这道检查而误报一大片：try 通常写在 LockBits
        #   之前即 `try { LockBits; ... } finally { Unlock }`，往后找是找不到的。）
        finally_ok = any(re.search(r'\bfinally\b', lines[k])
                         for k in range(ui, max(-1, ui - 3), -1))

        if finally_ok or approved(lines, li):
            oks.append((li + 1, name))
        else:
            warnings.append((li + 1, lines[li].strip()))

    return errors, warnings, oks


def check_bitmaps(path, lines):
    """局部 Bitmap 声明是否有释放痕迹。这是**提示**，不参与失败判定。

    为什么不做成硬判定：所有权可能通过 return 转移给调用方，
    静态分析分不清"转移"和"泄漏"。所以只报"连一点痕迹都没有"的。
    """
    hints = []
    text = '\n'.join(lines)
    for i, line in enumerate(lines):
        if RE_USING_BMP.search(line):
            continue                      # using 自动释放
        m = RE_BMP_DECL.search(line)
        if not m:
            continue
        name = m.group(1)
        if name in ('null',):
            continue
        # 释放痕迹：Dispose / 交给别人 / return 出去
        if re.search(r'\b' + re.escape(name) + r'\s*\.\s*Dispose\s*\(', text):
            continue
        if re.search(r'\breturn\s+' + re.escape(name) + r'\b', text):
            continue
        if re.search(r'\b' + re.escape(name) + r'\s*=\s*', line) and 'new Bitmap' not in line:
            continue
        if approved(lines, i):
            continue
        hints.append((i + 1, line.strip()))
    return hints


def main():
    files = cs_files()
    all_err, all_warn, all_hint, n_ok = [], [], [], 0
    n_lock = n_unlock = 0

    for path in files:
        text = read(path)
        lines = text.splitlines()
        n_lock += len(RE_LOCK.findall(text))
        n_unlock += len(RE_UNLOCK.findall(text))

        e, w, o = check_lockbits(path, lines)
        for idx, txt in e:
            all_err.append((rel(path), idx, txt))
        for idx, txt in w:
            all_warn.append((rel(path), idx, txt))
        n_ok += len(o)
        if LIST:
            for idx, name in o:
                print('  [ok] %s:%d  %s' % (rel(path), idx, name))
        for idx, txt in check_bitmaps(path, lines):
            all_hint.append((rel(path), idx, txt))

    print('资源门禁 —— 扫描 %d 个文件' % len(files))
    print('  LockBits %d 处 / UnlockBits %d 处 / 已受 finally 保护 %d 处' % (n_lock, n_unlock, n_ok))
    print()

    if all_err:
        print('[错误] LockBits 没有配对的 UnlockBits —— 位图会被永久锁死')
        for f, i, t in all_err:
            print('  %s:%d' % (f, i))
            print('      ' + t[:120])
        print()
    else:
        print('[错误] 无')
        print()

    if all_warn:
        print('[警告] UnlockBits 不在 finally 里 —— 异常路径会泄漏')
        for f, i, t in all_warn:
            print('  %s:%d' % (f, i))
            print('      ' + t[:120])
        print()
    else:
        print('[警告] 无')
        print()

    if all_hint:
        print('[提示] 局部 Bitmap 找不到释放痕迹（需人工确认，不计入判定）')
        for f, i, t in all_hint[:40]:
            print('  %s:%d' % (f, i))
            print('      ' + t[:120])
        if len(all_hint) > 40:
            print('  ...（还有 %d 处）' % (len(all_hint) - 40))
        print()
        print('  说明：所有权可能通过 return 转移给调用方，静态分析分不清"转移"与"泄漏"。')
        print('        确认无需释放的，在该行加注释  // resource-ok: 理由  即可消除。')
        print()

    print('结论：错误 %d，警告 %d，提示 %d' % (len(all_err), len(all_warn), len(all_hint)))
    if all_err:
        print('资源门禁未通过。')
        return 1
    if all_warn and STRICT:
        print('资源门禁未通过（--strict：警告也视为失败）。')
        return 1
    print('资源门禁通过。')
    return 0


if __name__ == '__main__':
    sys.exit(main())

# -*- coding: utf-8 -*-
"""verify_guard.py — 证明"倾斜检测的黑边保险"对归档输出零影响。

做法：同一批真实页、同一条极致档流水线，跑两遍：
  ① 保险开（默认）
  ② 保险关（PICTOOL_SKEW_NOGUARD=1）
逐文件比 MD5。逐字节相同 => 保险在归档路径上一次都没触发 =>
这次改动只影响"手动纠偏 / 直接对带黑边的原图检测"这条路径，归档不受牵连。

用法：
  python tools\\verify_guard.py
  python tools\\verify_guard.py --in D:\\lama\\_parallel\\in --exe D:\\lama\\dist\\PicTool.exe

退出码：0 = 全等；1 = 有差异/出错。
"""
import argparse
import hashlib
import os
import subprocess
import sys

# Force UTF-8 on stdout/stderr: this report is in Chinese and an English
# Windows console (code page 1252) cannot encode it, so the first print would
# raise UnicodeEncodeError and kill the check.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

DEFAULT_IN = r'D:\lama\_parallel\in'
DEFAULT_WORK = r'D:\lama\_parallel'
DEFAULT_EXE = r'D:\lama\dist\PicTool.exe'


def md5(path):
    h = hashlib.md5()
    with open(path, 'rb') as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def run(exe, src, dst, work, no_guard):
    if os.path.isdir(dst):
        for name in os.listdir(dst):
            p = os.path.join(dst, name)
            if os.path.isfile(p):
                os.remove(p)
    else:
        os.makedirs(dst)
    env = dict(os.environ)
    if no_guard:
        env['PICTOOL_SKEW_NOGUARD'] = '1'
    else:
        env.pop('PICTOOL_SKEW_NOGUARD', None)
    cmd = [exe, '--archive', src, dst, '--parallel=1', '--thorough']
    p = subprocess.Popen(cmd, cwd=work, env=env)
    rc = p.wait()
    n = len([f for f in os.listdir(dst) if os.path.isfile(os.path.join(dst, f))])
    return rc, n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--in', dest='src', default=DEFAULT_IN)
    ap.add_argument('--work', default=DEFAULT_WORK)
    ap.add_argument('--exe', default=DEFAULT_EXE)
    a = ap.parse_args()

    if not os.path.isfile(a.exe):
        print('FAIL exe not found: %s' % a.exe)
        return 1
    if not os.path.isdir(a.src):
        print('FAIL input dir not found: %s' % a.src)
        return 1

    outA = os.path.join(a.work, 'guard_on')
    outB = os.path.join(a.work, 'guard_off')

    print('verify_guard: 保险开 vs 保险关，同一批页同一档流水线')
    rc1, n1 = run(a.exe, a.src, outA, a.work, False)
    print('  guard ON   exit=%d files=%d' % (rc1, n1))
    rc2, n2 = run(a.exe, a.src, outB, a.work, True)
    print('  guard OFF  exit=%d files=%d' % (rc2, n2))

    if rc1 != 0 or rc2 != 0:
        print('RESULT: FAIL - pipeline exit code != 0')
        return 1
    if n1 != n2:
        print('RESULT: FAIL - file count differs %d vs %d' % (n1, n2))
        return 1

    names = sorted(f for f in os.listdir(outA) if os.path.isfile(os.path.join(outA, f)))
    diff = miss = 0
    for name in names:
        pa = os.path.join(outA, name)
        pb = os.path.join(outB, name)
        if not os.path.isfile(pb):
            miss += 1
            print('  [MISS] %s' % name)
            continue
        if md5(pa) != md5(pb):
            diff += 1
            print('  [DIFF] %s' % name)

    print('')
    if diff == 0 and miss == 0:
        print('RESULT: PASS - %d/%d 逐字节一致 => 黑边保险在归档路径上零触发' % (len(names), n1))
        return 0
    print('RESULT: FAIL - diff=%d miss=%d' % (diff, miss))
    return 1


if __name__ == '__main__':
    sys.exit(main())

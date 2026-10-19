# -*- coding: utf-8 -*-
"""四道门禁一把跑 —— 给"不读代码也能判断有没有改坏"用的。

为什么需要这个脚本（而不是在 PowerShell 里手敲）：
  `& exe --layout` 对 GUI 程序是**异步返回**的。于是"跑完立刻复制快照"复制到的
  是**上一次**的文件 —— 门禁会假装通过，而且是安静地假装。
  本脚本对每个门禁都做双重确认：等进程退出 + 等报告文件时间戳真的变了。

用法:
  python tools/gate.py                    # 四道全跑
  python tools/gate.py --selftest         # 只跑自检
  python tools/gate.py --layout --ai-fill
  python tools/gate.py --perf             # 只跑性能冒烟
  python tools/gate.py --regen-layout     # 把当前布局快照存为新基线（改 UI 后用）
  python tools/gate.py --regen-perf       # 重建性能基线（换机器/确认变快是有意的之后用）
  python tools/gate.py --quiet            # 只输出结论行
"""
import io
import os
import subprocess
import sys
import time

# Force UTF-8 on stdout/stderr before anything prints.
# Why this line exists: CI runs on an English Windows runner whose console code
# page is cp1252, which cannot encode this script's Chinese output. Without it
# the first print raises UnicodeEncodeError, the script dies with a traceback,
# and the gate reports failure for a machine with nothing wrong with it --
# worse than no gate, because it teaches people to ignore red builds.
# errors='replace' keeps output flowing on a console that cannot show a glyph.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEMP = os.environ.get('TEMP') or os.environ.get('TMP') or '/tmp'
# 构建产物：build.bat 输出 PicTool.exe，再按 appname.txt 复制出本地化名字，两个都认
EXE = os.path.join(ROOT, 'dist', 'PicTool.exe')
if not os.path.exists(EXE):
    _appname = os.path.join(ROOT, 'appname.txt')
    if os.path.exists(_appname):
        with io.open(_appname, encoding='utf-8-sig') as _f:
            _n = _f.readline().strip()
        if _n:
            EXE = os.path.join(ROOT, 'dist', _n)
GOLD = os.path.join(ROOT, 'tools', 'golden-layout.txt')
TEST_IMG = os.path.join(ROOT, '_probe', 'doc_test.png')

R_SELFTEST = os.path.join(TEMP, 'PicTool-selftest.txt')
R_LAYOUT = os.path.join(TEMP, 'PicTool-layout.txt')
R_AIFILL = os.path.join(TEMP, 'PicTool-ai-fill.txt')


def ensure_test_img():
    """返回固定测试页路径；仓库里没有就现场生成一份。

    为什么必须这样：AI 产物门禁与性能冒烟门禁都需要一张**固定**的测试页。
    以前这份图是手工放在仓库外的 `_probe\\doc_test.png`，从没进过版本库，
    于是别人 clone 下来跑门禁时两道直接"跳过"，而跳过被记成通过 ——
    门禁在最需要它的时候变成了装饰品。

    现在这张图入库了（`--make-fixture` 确定性生成，8bpp 灰阶，约 150KB），
    兜底生成是为了防止谁在改动中误删它。真生成不出来才判失败，不再静默跳过。
    """
    if os.path.exists(TEST_IMG):
        return TEST_IMG
    say('  固定测试页缺失，正在用 --make-fixture 现场生成…')
    try:
        subprocess.call([EXE, '--make-fixture', TEST_IMG],
                        cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception as ex:
        say('  生成失败：' + str(ex))
    if os.path.exists(TEST_IMG):
        say('  已生成：%s（%.0f KB）' % (
            os.path.relpath(TEST_IMG, ROOT), os.path.getsize(TEST_IMG) / 1024.0))
        return TEST_IMG
    return None

QUIET = '--quiet' in sys.argv
RESULTS = []


def say(msg=''):
    if not QUIET or msg.startswith('==') or msg.startswith('  '):
        print(msg)


def read_text(path):
    return io.open(path, encoding='utf-8-sig', errors='replace').read()


def run_gate(args, report, label, timeout=240):
    """启动 exe，等它真正退出、并等报告文件时间戳更新。返回 (耗时秒, 报告路径或 None)"""
    before = os.path.getmtime(report) if os.path.exists(report) else None
    t0 = time.time()
    try:
        subprocess.run([EXE] + args, cwd=ROOT,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       timeout=timeout)
    except subprocess.TimeoutExpired:
        print('  [超时] %s 超过 %d 秒' % (label, timeout))
        return time.time() - t0, None

    # 进程退出不代表文件写完（GUI 程序 + 非缓冲刷新），再确认一次时间戳
    while time.time() - t0 < timeout:
        if os.path.exists(report):
            if before is None or os.path.getmtime(report) > before:
                break
        time.sleep(0.1)
    else:
        print('  [异常] %s 的报告文件没有更新：%s' % (label, report))
        return time.time() - t0, None

    return time.time() - t0, report


# ---------------------------------------------------------------- 自检
def gate_selftest():
    say('== 门禁 1/4  算法自检 ==')
    dt, rpt = run_gate(['--selftest'], R_SELFTEST, '自检')
    if rpt is None:
        RESULTS.append(('自检', False, '报告未生成'))
        return
    text = read_text(rpt)
    total = passed = failed = None
    fails = []
    for line in text.splitlines():
        s = line.strip()
        if s.startswith('合计'):
            # 合计: 通过 56 项，失败 0 项
            digits = ''.join(ch if ch.isdigit() else ' ' for ch in s).split()
            if len(digits) >= 2:
                passed, failed = int(digits[0]), int(digits[1])
        if '[失败]' in s:
            fails.append(s)
    ok = (failed == 0) and passed is not None
    say('  通过 %s 项，失败 %s 项   %.1fs' % (passed, failed, dt))
    for f in fails:
        say('  ' + f)
    RESULTS.append(('自检', ok, '通过 %s / 失败 %s' % (passed, failed)))


# ---------------------------------------------------------------- 布局
def gate_layout(regen=False):
    say('== 门禁 2/4  界面布局 ==')
    dt, rpt = run_gate(['--layout'], R_LAYOUT, '布局快照')
    if rpt is None:
        RESULTS.append(('布局', False, '快照未生成'))
        return
    new = read_text(rpt).splitlines()

    if regen:
        if os.path.exists(GOLD):
            bak = GOLD + '.bak-' + time.strftime('%Y%m%d-%H%M%S')
            io.open(bak, 'w', encoding='utf-8-sig', newline='').write(
                io.open(GOLD, encoding='utf-8-sig').read())
            say('  旧基线已备份: ' + os.path.relpath(bak, ROOT))
        io.open(GOLD, 'w', encoding='utf-8-sig', newline='').write('\n'.join(new) + '\n')
        say('  ✅ 已重生成基线（%d 行）  %.1fs' % (len(new), dt))
        RESULTS.append(('布局', True, '基线已重生成 %d 行' % len(new)))
        return

    if not os.path.exists(GOLD):
        say('  ⚠️ 找不到基线 ' + os.path.relpath(GOLD, ROOT) + '，请先跑 --regen-layout')
        RESULTS.append(('布局', False, '缺基线'))
        return

    old = read_text(GOLD).splitlines()
    diff = [(i + 1, x, y) for i, (x, y) in enumerate(zip(new, old)) if x != y]
    ok = (len(new) == len(old)) and not diff
    say('  快照 %d 行 vs 基线 %d 行，差异 %d 处   %.1fs' % (len(new), len(old), len(diff), dt))
    if ok:
        say('  ✅ 逐行一致')
    else:
        for i, x, y in diff[:10]:
            say('  行%d' % i)
            say('    新: ' + x.strip())
            say('    旧: ' + y.strip())
        if len(diff) > 10:
            say('  ...（还有 %d 处）' % (len(diff) - 10))
    RESULTS.append(('布局', ok, '差异 %d 处' % len(diff)))


# ---------------------------------------------------------------- AI 产物
def gate_aifill():
    say('== 门禁 3/4  AI 产物 ==')
    test = ensure_test_img()
    if test is None:
        say('  失败：固定测试页不存在，且无法现场生成（跑 build.bat 后重试）')
        RESULTS.append(('AI产物', False, '缺固定测试页'))
        return
    out = os.path.join(TEMP, 'PicTool-ai-gate.png')
    args = ['--ai-fill', '_probe' + os.sep + 'doc_test.png', out, '200', '700', '900', '280']
    dt, rpt = run_gate(args, R_AIFILL, 'AI 补全')
    if rpt is None:
        RESULTS.append(('AI产物', False, '报告未生成'))
        return
    text = read_text(rpt)
    bad = ('失败' in text) or ('错误' in text and '无错误' not in text)
    ms = ''
    for line in text.splitlines():
        if '耗时' in line:
            ms = line.split(':', 1)[-1].strip()
            break
    say('  %s   耗时 %s   %.1fs' % ('❌ 报告里有失败字样' if bad else '✅ 正常', ms, dt))
    if bad:
        for line in text.splitlines()[:8]:
            say('  | ' + line.strip())
    RESULTS.append(('AI产物', not bad, '耗时 ' + ms if ms else ''))


# ---------------------------------------------------------------- 性能冒烟
# 为什么要有它：功能对 ≠ 性能对。某次改动让归档悄悄慢 20%，自检和布局基线都
# 不会报警 —— 只有拿固定测试页跟固定基线比才能发现（2026-09-30 定案）。
PERF_BASE = os.path.join(ROOT, 'tools', 'perf-baseline.txt')
PERF_TEST_IMG = os.path.join(ROOT, '_probe', 'doc_test.png')
PERF_OUT = os.path.join(TEMP, 'pictool-perf-out.png')
PERF_TOLERANCE = 1.15          # 比基线慢 15% 以上 = FAIL
PERF_FAST_SUSPECT = 0.70       # 比基线快 30% 以上 = 可疑（可能有步骤没跑），提示但不判失败


def machine_tag():
    """粗粒度机器标识：写进性能基线文件，用来判断基线是否来自本机。

    不用 CPU 型号（太敏感，换个后继架构就全废），只看位数与核数 ——
    目的只是"明显不是同一台机器"就提示重建，不是做绝对性能判定。
    """
    try:
        import platform
        bits = 64 if platform.architecture()[0] == '64bit' else 32
        return '%s-bit/%dcpu' % (bits, os.cpu_count() or 1)
    except Exception:
        return 'unknown'


def gate_perf(regen=False):
    say('== 门禁 4/4  性能冒烟 ==')
    perf_img = ensure_test_img()
    if perf_img is None:
        say('  失败：固定测试页不存在，且无法现场生成（跑 build.bat 后重试）')
        RESULTS.append(('性能', False, '缺固定测试页'))
        return

    # 跑 2 次取最小值：min 对后台抖动最稳（机器忙时也接近真实性能）
    times = []
    for i in range(2):
        if os.path.exists(PERF_OUT):
            try:
                os.remove(PERF_OUT)
            except OSError:
                pass
        t0 = time.time()
        try:
            subprocess.run([EXE, '--archive', PERF_TEST_IMG, PERF_OUT], cwd=ROOT,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=120)
        except subprocess.TimeoutExpired:
            say('  [超时] 性能基准第 %d 次超过 120 秒' % (i + 1))
            RESULTS.append(('性能', False, '基准跑超时'))
            return
        times.append(time.time() - t0)
    ms = int(min(times) * 1000)

    base = None
    base_tag = ''
    if os.path.exists(PERF_BASE) and not regen:
        try:
            raw = io.open(PERF_BASE, encoding='utf-8').read().strip()
            # 基线文件第一行是毫秒数，第二行（可选）是建基线时的机器标识。
            # 没有标识的老格式照样能读，只是跨机器时判据不可信 —— 见下面 tag 比对。
            lines = [l for l in raw.splitlines() if l.strip()]
            base = int(lines[0].strip())
            base_tag = lines[1].strip() if len(lines) > 1 else ''
        except (ValueError, IndexError, OSError):
            base = None

    if base is None:
        io.open(PERF_BASE, 'w', encoding='utf-8', newline='\n').write(
            '%d\n%s\n' % (ms, machine_tag()))
        say('  ✅ 已建基线: %d ms（固定测试页 2 次取最小）   总耗时 %.1fs' % (ms, sum(times)))
        RESULTS.append(('性能', True, '基线已建 %d ms' % ms))
        return

    # 跨机器的基线没有可比性：本机录的毫秒数放到别的 CPU 上毫无意义，
    # 直接拿来判 FAIL 会误报，判 PASS 则是把别人的机器当作自己的。
    # 这是"无法判定"，不是"失败" —— 判为无结论（○），并说明该怎么得到结论。
    tag = machine_tag()
    if base_tag and tag and base_tag != tag:
        say('  ○ 无结论：基线来自另一台机器（%s），当前是 %s' % (base_tag, tag))
        say('     性能基线只在本机有意义。要在这台机器上得到结论，请跑 --regen-perf。')
        RESULTS.append(('性能', 'skip', '基线跨机器（%s → %s），需 --regen-perf' % (base_tag, tag)))
        return

    say('  本次 %d ms vs 基线 %d ms（FAIL 线 +%d%%）   总耗时 %.1fs'
        % (ms, base, int(round((PERF_TOLERANCE - 1) * 100)), sum(times)))
    if ms > base * PERF_TOLERANCE:
        say('  ❌ 比基线慢 %d%% —— 最近某次改动让归档变慢了，请对照改动清单排查'
            % int((ms - base) * 100.0 / base))
        RESULTS.append(('性能', False, '%d ms 超基线 %d ms' % (ms, base)))
        return
    if ms < base * PERF_FAST_SUSPECT:
        say('  ⚠️ 比基线快 %d%%：异常变快（可能有步骤没跑？）。确认没问题后跑 '
            '--regen-perf 重建基线' % int((base - ms) * 100.0 / base))
        RESULTS.append(('性能', True, '%d ms（异常变快，请确认）' % ms))
        return
    if ms < base:
        # 曾经这里会自动把基线改小（"缓慢向好跟踪"）。那会让门禁越放越松：
        # 一次意外跳过的步骤就能永久降低门槛，之后再慢也追不回来。
        # 改为只提示，基线只在 --regen-perf 下显式重建。
        say('  （比基线快 %d ms：%d ms；基线未改动，确认后可用 --regen-perf 收紧）'
            % (base - ms, ms))
    RESULTS.append(('性能', True, '%d ms ≤ 基线 %d ms' % (ms, base)))


# ---------------------------------------------------------------- 主流程
def main():
    if not os.path.exists(EXE):
        print('找不到可执行文件：' + EXE)
        print('请先编译：在 PowerShell 里跑  "n" | & .\\build.bat')
        return 2

    args = sys.argv[1:]
    want = [a for a in args if a.startswith('--')]
    regen = '--regen-layout' in want
    regen_perf = '--regen-perf' in want
    run_all = not any(a in ('--selftest', '--layout', '--ai-fill', '--perf') for a in want)

    print('门禁检查 —— ' + time.strftime('%Y-%m-%d %H:%M:%S'))
    print('产物: ' + os.path.relpath(EXE, ROOT))
    print()

    if run_all or '--selftest' in want:
        gate_selftest()
    if run_all or '--layout' in want or regen:
        gate_layout(regen=regen)
    if run_all or '--ai-fill' in want:
        gate_aifill()
    if run_all or '--perf' in want or regen_perf:
        gate_perf(regen=regen_perf)

    print()
    print('== 结论 ==')
    allok = True
    inconclusive = []
    for name, ok, detail in RESULTS:
        #三态：通过 / 失败 / 无结论（判据在这台机器上不成立，不能算失败）
        # 无结论项用 ○ 标记，不让"无法判定"看起来像"有问题"，
        # 也不让它悄悄变成通过。
        if ok == 'skip':
            print('  ○ %-6s %s（无结论）' % (name, detail))
            inconclusive.append(name)
        else:
            print('  %s %-6s %s' % ('✅' if ok else '❌', name, detail))
            allok = allok and ok
    print()
    if allok and inconclusive:
        print('门禁通过，但有 %d 项无结论（%s）—— 这些判据在当前机器上不适用，'
              '请看上面说明。' % (len(inconclusive), '、'.join(inconclusive)))
        return 0
    print('门禁' + ('全部通过。' if allok else '有未通过项，请看上面明细。'))
    return 0 if allok else 1


if __name__ == '__main__':
    sys.exit(main())

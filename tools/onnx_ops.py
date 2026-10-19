#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
onnx_ops.py — ONNX 模型算子兼容性扫描器（零依赖，纯 Python 解析 protobuf）

用途：回答"这个模型能不能换进来"。
      本机 AI 运行时被钉死在 ONNX Runtime 1.14.1（Win7 能用到的最后一版），
      所以候选模型必须只用 ORT 1.14 CPU EP 实现了的算子 —— 否则要么加载即失败，
      要么加载成功但一推理就崩（LaMa 的 FFT 就是后者）。

用法：
    python tools/onnx_ops.py <model.onnx> [more.onnx ...]
    python tools/onnx_ops.py --diff <已知可用.onnx> <候选.onnx>
        --diff 会列出"候选比基线多出来的算子"——那些才是真正要核的，
        因为基线已经在本机跑通，它用到的算子集合就是安全集。

判定口径（保守，宁可报"需核实"也不误判为可用）：
    [不可用] 出现 FFT 家族算子 —— ORT 1.14 CPU 内核对 FFT 支持有已知问题（LaMa 死因）
    [不可用] 出现非标准域算子（非 ai.onnx / com.microsoft）—— ORT 无实现
    [风险]   出现注意力/采样/NMS 等 ORT 1.14 未收录或实现不完整的算子
    [需核实] 出现未能归类的算子
    [可用]   其余
"""

import os
import sys

# Force UTF-8 on stdout/stderr before anything prints: this report is in
# Chinese, and an English Windows console (code page 1252) cannot encode it,
# so the first print would raise UnicodeEncodeError and kill the scan.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

# ---------------------------------------------------------------- protobuf 解析
# 只实现 ONNX 实际用到的 wire type：0=varint 1=64bit 2=length 5=32bit

def _varint(buf, i):
    r = 0
    s = 0
    while True:
        b = buf[i]
        i += 1
        r |= (b & 0x7F) << s
        if not (b & 0x80):
            return r, i
        s += 7
        if s > 70:
            raise ValueError("varint too long")


def _fields(buf):
    """依次产出 (字段号, wire type, 值)。值：varint->int，length->bytes"""
    i = 0
    n = len(buf)
    while i < n:
        key, i = _varint(buf, i)
        fn = key >> 3
        wt = key & 7
        if wt == 0:
            v, i = _varint(buf, i)
            yield fn, wt, v
        elif wt == 2:
            ln, i = _varint(buf, i)
            yield fn, wt, buf[i:i + ln]
            i += ln
        elif wt == 5:
            yield fn, wt, buf[i:i + 4]
            i += 4
        elif wt == 1:
            yield fn, wt, buf[i:i + 8]
            i += 8
        else:
            raise ValueError("unsupported wire type %d" % wt)


def _text(b):
    return b.decode("utf-8", "replace")


# ModelProto.graph=7  ModelProto.opset_import=8
# GraphProto.node=1
# NodeProto.op_type=4  NodeProto.domain=7  NodeProto.attribute=5
# AttributeProto.g=6(单个子图)  AttributeProto.graphs=11(多个子图)

def _walk_attr(abuf, ops, depth):
    for fn, wt, v in _fields(abuf):
        if wt == 2 and fn in (6, 11):
            _walk_graph(v, ops, depth + 1)


def _walk_node(nbuf, ops, depth):
    op_type = None
    domain = ""
    attrs = []
    for fn, wt, v in _fields(nbuf):
        if wt != 2:
            continue
        if fn == 4:
            op_type = _text(v)
        elif fn == 7:
            domain = _text(v)
        elif fn == 5:
            attrs.append(v)
    if op_type:
        key = (domain or "ai.onnx") + "::" + op_type
        ops[key] = ops.get(key, 0) + 1
    if depth < 8:
        for a in attrs:
            _walk_attr(a, ops, depth)


def _walk_graph(gbuf, ops, depth):
    for fn, wt, v in _fields(gbuf):
        if fn == 1 and wt == 2:
            _walk_node(v, ops, depth + 1)


def parse_model(path):
    with open(path, "rb") as f:
        buf = f.read()
    ops = {}
    opsets = []
    graph_seen = False
    for fn, wt, v in _fields(buf):
        if wt != 2:
            continue
        if fn == 7:                       # graph
            graph_seen = True
            _walk_graph(v, ops, 0)
        elif fn == 8:                     # opset_import
            dom = ""
            ver = 0
            for ofn, owt, ov in _fields(v):
                if ofn == 1 and owt == 2:
                    dom = _text(ov)
                elif ofn == 2 and owt == 0:
                    ver = ov
            opsets.append((dom or "ai.onnx", ver))
    if not graph_seen:
        raise ValueError("没有解析到 graph（不是 ONNX 模型？）")
    return ops, opsets, len(buf)


# ---------------------------------------------------------------- 判定知识库

# FFT 家族：ORT 1.14 的 CPU 内核在这类算子上有已知问题（多线程访问冲突）
FFT_OPS = {
    "DFT": "FFT 家族（复数 DFT）",
    "STFT": "FFT 家族（短时傅里叶）",
    "ISTFT": "FFT 家族（逆短时傅里叶）",
    "RFFT": "FFT 家族（实数 FFT）",
    "IRFFT": "FFT 家族（逆实数 FFT）",
    "FFT": "FFT 家族",
    "IFFT": "FFT 家族",
    "MelWeightMatrix": "FFT 家族（梅尔滤波器组）",
    "HannWindow": "FFT 家族（窗函数）",
    "HammingWindow": "FFT 家族（窗函数）",
    "BlackmanWindow": "FFT 家族（窗函数）",
    "BartlettWindow": "FFT 家族（窗函数）",
    "KaiserWindow": "FFT 家族（窗函数）",
}

# 对 ORT 1.14 CPU EP 存疑的算子。注意：**可疑 ≠ 不可用**，
# 这些必须实测才算数，所以单列一级而不是直接判死。
RISKY_OPS = {
    "Attention": "注意力算子，非 1.14 默认域",
    "MultiHeadAttention": "opset 23 才加入标准域，1.14 没有",
    "GroupQueryAttention": "com.microsoft 新算子，需较新版本",
    "RotaryEmbedding": "新算子，1.14 没有",
    "MatMulNBits": "量化算子，需较新版本",
    "MatMulIntegerToFloat": "量化算子，需较新版本",
    "Einsum": "ORT 支持的方程子集有限；LaMa 拿它拼了 216 次手搓 DFT，高度可疑",
    "GridSample": "opset 16 加入，CPU 实现完整度需核实",
    "DeformConv": "可变形卷积，CPU 实现罕见",
    "Det": "行列式，CPU EP 覆盖情况不明",
    "Trilu": "较新算子",
    "NonMaxSuppression": "检测后处理，文档场景用不到且实现路径绕",
    "Multinomial": "随机采样，结果不确定",
    "RandomNormalLike": "随机算子，结果不确定",
}

# 在本机跑通过的算子集合（由已知可用模型推导），未归类也不算错，只是要人看
COMMON_OK = {
    "Conv", "ConvTranspose", "Gemm", "MatMul", "Add", "Sub", "Mul", "Div", "Pow",
    "Sqrt", "Abs", "Neg", "Exp", "Log", "Floor", "Ceil", "Round", "Clip", "Sign",
    "Relu", "LeakyRelu", "PRelu", "Elu", "Selu", "Gelu", "Sigmoid", "HardSigmoid",
    "Tanh", "HardSwish", "Softplus", "Softsign", "Erf", "Mish", "ThresholdedRelu",
    "BatchNormalization", "InstanceNormalization", "LayerNormalization",
    "GroupNormalization", "LRN", "Dropout",
    "MaxPool", "AveragePool", "GlobalAveragePool", "GlobalMaxPool",
    "Resize", "Upsample", "DepthToSpace", "SpaceToDepth", "Pad", "Tile",
    "Concat", "Split", "Slice", "Gather", "GatherElements", "GatherND",
    "ScatterND", "ScatterElements", "Expand", "Shape", "Size", "Reshape",
    "Transpose", "Squeeze", "Unsqueeze", "Flatten", "Identity", "Cast",
    "Constant", "ConstantOfShape", "Range", "Where", "Equal", "Greater",
    "Less", "GreaterOrEqual", "LessOrEqual", "Not", "And", "Or", "Xor",
    "Min", "Max", "Sum", "Mean", "ReduceSum", "ReduceMean", "ReduceMax",
    "ReduceMin", "ReduceProd", "ArgMax", "ArgMin", "CumSum", "OneHot",
    "Softmax", "LogSoftmax", "Hardmax", "ReverseSequence",
    "NonZero",
    # 三角函数 / 倒数：基础逐元素算子，ORT CPU 一直支持（LaMa 的手搓 DFT 会用到）
    "Cos", "Sin", "Tan", "Asin", "Acos", "Atan", "Cosh", "Sinh",
    "Asinh", "Acosh", "Atanh", "Reciprocal", "Celu", "IsNaN", "IsInf",
    "EyeLike", "Compress", "LpNormalization", "MeanVarianceNormalization",
    "SpaceToDepth", "DepthToSpace", "RoiAlign",
}

STD_DOMAINS = {"ai.onnx", "ai.onnx.ml", "com.microsoft"}


def verdict(ops, opsets, known_safe=None):
    """返回 (级别, [原因])；级别 0=可用 1=需核实 2=风险 3=不可用

    known_safe：来自 --diff 基线的算子名集合。基线已在目标机跑通，
    它的算子必然被 ORT 支持，所以一律视为安全 —— 避免拿不准的白名单误报。
    """
    safe = COMMON_OK if not known_safe else (COMMON_OK | known_safe)
    fatal, risky, unknown = [], [], []

    for k in sorted(ops):
        dom, op = k.split("::", 1)
        if op in safe:
            continue
        if op in FFT_OPS:
            fatal.append("%s —— %s" % (op, FFT_OPS[op]))
        elif dom not in STD_DOMAINS:
            fatal.append("%s（域 %s）—— 非标准域，ORT 无实现" % (op, dom))
        elif op in RISKY_OPS:
            risky.append("%s —— %s" % (op, RISKY_OPS[op]))
        else:
            unknown.append(op)

    for dom, ver in opsets:
        if dom == "ai.onnx" and ver > 19:
            risky.append("opset %d > 19 —— 超出 ORT 1.14 支持范围" % ver)

    if fatal:
        return 3, sorted(set(fatal))
    if risky:
        return 2, sorted(set(risky))
    if unknown:
        return 1, sorted(set(unknown))
    return 0, []


LEVEL_TXT = {0: "[可用]  ", 1: "[需核实]", 2: "[风险]  ", 3: "[不可用]"}


def report(path, baseline_ops=None):
    try:
        ops, opsets, size = parse_model(path)
    except Exception as e:
        print("=== %s ===" % path)
        print("  解析失败：%s" % e)
        return

    known_safe = None
    if baseline_ops:
        known_safe = set(k.split("::", 1)[1] for k in baseline_ops)
    lvl, why = verdict(ops, opsets, known_safe)
    print("=== %s  (%.1f MB) ===" % (path, size / 1048576.0))
    print("  opset   : %s" % ", ".join("%s %d" % (d, v) for d, v in opsets))
    print("  节点数  : %d（%d 种算子）" % (sum(ops.values()), len(ops)))
    print("  判定    : %s" % LEVEL_TXT[lvl].strip())
    for w in why[:12]:
        print("            - %s" % w)
    if len(why) > 12:
        print("            - ...另有 %d 项" % (len(why) - 12))

    if baseline_ops is not None:
        extra = {k: v for k, v in ops.items() if k not in baseline_ops}
        print("  比基线多: %s" % ("（无，算子集合被基线完全覆盖）" if not extra
                                 else ", ".join(sorted(extra))))

    print("  算子明细:")
    for k in sorted(ops, key=lambda x: (-ops[x], x)):
        print("    %-34s %d" % (k, ops[k]))
    print()


def main(argv):
    args = argv[1:]
    if not args:
        print(__doc__)
        return 2

    baseline = None
    if args[0] == "--diff":
        if len(args) < 3:
            print("用法：--diff <已知可用.onnx> <候选.onnx> ...")
            return 2
        bpath = args[1]
        try:
            baseline, _, _ = parse_model(bpath)
            print("基线（已知在本机跑通）：%s —— 其算子集合即安全集\n" % bpath)
        except Exception as e:
            print("基线解析失败：%s" % e)
            return 1
        args = args[2:]

    for p in args:
        if not os.path.isfile(p):
            print("找不到文件：%s\n" % p)
            continue
        report(p, baseline)

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

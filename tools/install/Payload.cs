// Payload.cs — 单文件安装包内嵌载荷的读写（安装程序侧：只读）。
//
// 为什么自己定一个容器格式，不用 zip：
//   解压要在**没装 .NET 4.8 的机器**上跑（那时只有系统自带的 .NET 4.0）。
//   System.IO.Compression.ZipArchive 是 .NET 4.5 才有的，用了就可能在老机器上
//   直接 TypeLoadException —— 安装器起不来等于装不了。DeflateStream 从 2.0 就有
//   （4.0 起位于 System.IO.Compression.dll），所以：能压的用 Deflate，压不动的原样存。
//
// 容器布局（整个载荷追加在 exe 末尾，PE 加载器会忽略尾部数据）：
//   payload 起点：
//     char[8]  "PTPK0001"
//     int32    entryCount
//     逐条： int32 nameLen / name(UTF-8, 相对路径) / int64 rawLen /
//            int32 method(0=store,1=deflate) / int64 storedLen / data
//   文件最末 32 字节（尾部索引）：
//     char[8]  "PTBOOT01"
//     int64    payloadOffset
//     int64    payloadLength
//     int32    containerVersion (1)
//     int32    reserved (0)
//
// ⚠️ 尾部索引"名义上"在文件最末，但**签名之后就不在末尾了**：
//    Authenticode 签名会在打包完成后往文件尾部追加证书表（PE 的 SECURITY 数据目录
//    指向它），于是真正的末尾变成了那几十 KB 的签名数据。所以读取时不能死认
//    "最后 32 字节"，必须**从末尾往回搜** FooterMagic —— 见 TryReadFooter。
//    （这条是 2026-09-21 验收时抓到的：打包时校验通过，签名后再打开就认不出载荷，
//      双击等于装不了。改完必须"先签名、再校验"地跑一遍。）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.IO.Compression;

namespace PicToolSetup
{
    class PayloadEntry
    {
        public string Name;
        public long RawLength;
        public int Method;
        public long StoredLength;
        public long DataOffset;   // 相对容器起点
    }

    /// <summary>只读的"文件中的一段"——避免把 180MB 载荷整个读进内存。</summary>
    class RegionStream : Stream
    {
        readonly FileStream fs;
        readonly long start;
        readonly long length;
        long pos;

        public RegionStream(FileStream file, long offset, long len)
        {
            fs = file; start = offset; length = len; pos = 0;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position
        {
            get { return pos; }
            set { pos = value; }
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long remain = length - pos;
            if (remain <= 0) return 0;
            if (count > remain) count = (int)remain;
            fs.Position = start + pos;
            int n = fs.Read(buffer, offset, count);
            if (n > 0) pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long p = pos;
            if (origin == SeekOrigin.Begin) p = offset;
            else if (origin == SeekOrigin.Current) p += offset;
            else p = length + offset;
            if (p < 0) p = 0;
            if (p > length) p = length;
            pos = p;
            return pos;
        }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    static class Payload
    {
        public const int FooterSize = 32;
        // 往回搜尾部索引的范围。签名追加的证书表通常只有几 KB（带时间戳的正式证书
        // 链也很少超过 32KB），256KB 留了很宽的余量，而且这里只读一次、不循环读盘。
        public const int FooterScanSize = 256 * 1024;
        static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("PTBOOT01");
        static readonly byte[] PakMagic = Encoding.ASCII.GetBytes("PTPK0001");

        static int ReadFull(Stream s, byte[] buf, int off, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, off + got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        /// <summary>读取自身尾部索引。不是"带载荷的单文件安装器"时返回 false。</summary>
        public static bool TryReadFooter(string exePath, out long offset, out long length)
        {
            offset = 0; length = 0;
            try
            {
                using (FileStream f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (f.Length < FooterSize) return false;

                    // 从末尾往回搜（原因见文件头注释：签名会往末尾追加证书表）。
                    // 找到最后一个"能自洽"的索引就用它：ver==1、长度正数、
                    // 并且载荷区间必须整段落在索引之前。
                    long scan = f.Length < FooterScanSize ? f.Length : FooterScanSize;
                    long scanStart = f.Length - scan;
                    byte[] buf = new byte[scan];
                    f.Position = scanStart;
                    int n = ReadFull(f, buf, 0, (int)scan);
                    for (long i = n - FooterSize; i >= 0; i--)
                    {
                        bool hit = true;
                        for (int k = 0; k < FooterMagic.Length; k++)
                        {
                            if (buf[i + k] != FooterMagic[k]) { hit = false; break; }
                        }
                        if (!hit) continue;

                        long off = BitConverter.ToInt64(buf, (int)i + 8);
                        long len = BitConverter.ToInt64(buf, (int)i + 16);
                        int ver = BitConverter.ToInt32(buf, (int)i + 24);
                        if (ver != 1) continue;
                        if (off <= 0 || len <= 0) continue;
                        if (off + len > scanStart + i) continue;

                        offset = off; length = len;
                        return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        /// <summary>列出载荷条目（只读头部信息，不读数据）。</summary>
        public static List<PayloadEntry> List(string exePath)
        {
            List<PayloadEntry> list = new List<PayloadEntry>();
            long off, len;
            if (!TryReadFooter(exePath, out off, out len)) return list;
            using (FileStream f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (RegionStream rs = new RegionStream(f, off, len))
            using (BinaryReader br = new BinaryReader(rs))
            {
                byte[] m = br.ReadBytes(8);
                for (int i = 0; i < PakMagic.Length; i++)
                    if (m.Length != 8 || m[i] != PakMagic[i]) return list;
                int count = br.ReadInt32();
                if (count <= 0 || count > 100000) return list;
                for (int i = 0; i < count; i++)
                {
                    PayloadEntry e = new PayloadEntry();
                    int nameLen = br.ReadInt32();
                    if (nameLen <= 0 || nameLen > 4096) return list;
                    e.Name = Encoding.UTF8.GetString(br.ReadBytes(nameLen)).Replace('/', '\\');
                    e.RawLength = br.ReadInt64();
                    e.Method = br.ReadInt32();
                    e.StoredLength = br.ReadInt64();
                    e.DataOffset = rs.Position;
                    list.Add(e);
                    rs.Position += e.StoredLength;
                }
            }
            return list;
        }

        /// <summary>把载荷解压到 destDir。onFile(当前文件序号, 总数, 文件名, 已拷字节, 总字节)。</summary>
        public static void Extract(string exePath, string destDir, Action<int, int, string, long, long> onFile)
        {
            List<PayloadEntry> entries = List(exePath);
            if (entries.Count == 0) throw new InvalidDataException("安装包内没有可解压的文件（载荷索引读不到）");

            long totalRaw = 0;
            foreach (PayloadEntry e in entries) totalRaw += e.RawLength;

            long off, len;
            if (!TryReadFooter(exePath, out off, out len)) throw new InvalidDataException("安装包尾部索引读不到");

            long done = 0;
            using (FileStream f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    PayloadEntry e = entries[i];
                    if (onFile != null) onFile(i + 1, entries.Count, e.Name, done, totalRaw);
                    string dst = Path.Combine(destDir, e.Name);
                    string dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    f.Position = off + e.DataOffset;
                    using (FileStream fo = new FileStream(dst, FileMode.Create, FileAccess.Write))
                    {
                        if (e.Method == 1)
                        {
                            using (RegionStream rs = new RegionStream(f, off + e.DataOffset, e.StoredLength))
                            using (DeflateStream ds = new DeflateStream(rs, CompressionMode.Decompress))
                            {
                                byte[] buf = new byte[256 * 1024];
                                int n;
                                while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                                {
                                    fo.Write(buf, 0, n);
                                    done += n;
                                }
                            }
                        }
                        else
                        {
                            byte[] buf = new byte[256 * 1024];
                            long remain = e.StoredLength;
                            while (remain > 0)
                            {
                                int want = (int)Math.Min(buf.Length, remain);
                                int n = f.Read(buf, 0, want);
                                if (n <= 0) throw new EndOfStreamException("载荷数据不完整：" + e.Name);
                                fo.Write(buf, 0, n);
                                remain -= n;
                                done += n;
                            }
                        }
                    }
                }
                if (onFile != null) onFile(entries.Count, entries.Count, "", totalRaw, totalRaw);
            }
        }
    }
}

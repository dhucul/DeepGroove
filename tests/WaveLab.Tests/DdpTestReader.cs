using System.Globalization;
using System.Text;
using Xunit;

namespace WaveLab.Tests;

// Test-side decoding of the DDP 2.00 interchange layout, independent of the writer helpers.
internal static class DdpTestReader
{
    internal sealed record Pq(string Track, int Index, int Sector, bool PreEmphasis, string Isrc, string Upc);

    internal static Pq[] ReadPq(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Assert.Equal(0, data.Length % 64);
        var entries = new List<Pq>();
        for (int at = 0; at < data.Length; at += 64)
        {
            string record = Encoding.ASCII.GetString(data, at, 64);
            Assert.StartsWith("VVVS", record);
            int sector = (Number(10, 2) * 60 + Number(12, 2)) * 75 + Number(14, 2);
            entries.Add(new Pq(record.Substring(4, 2), Number(6, 2), sector,
                record[16] == '1', record.Substring(20, 12).Trim(), record.Substring(32, 13).Trim()));
            int Number(int start, int length) => int.Parse(record.AsSpan(start, length), CultureInfo.InvariantCulture);
        }
        return entries.ToArray();
    }
}

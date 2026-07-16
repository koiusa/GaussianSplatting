using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace GaussianSplatting
{
    public static class PlyParser
    {
        // SH DC coefficient → linear RGB:  c = 0.5 + SH_C0 * f_dc
        private const float SH_C0 = 0.28209479177f;
        private const int VerticesPerBatch = 16 * 1024;
        private const int ParallelThreshold = 1024;

        public static GaussianSplatGPU[] Parse(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            var (count, props, dataPos) = ReadHeader(fs);
            if (count == 0 || props.Count == 0)
                throw new InvalidDataException("PLY header parse failed (no vertex element or properties).");

            // Build property-name → byte-offset lookup (all float, 4 bytes each)
            var propIdx = new Dictionary<string, int>(props.Count);
            for (int i = 0; i < props.Count; i++) propIdx[props[i]] = i;

            fs.Position = dataPos;
            int stride = checked(props.Count * 4);
            var splats = new GaussianSplatGPU[count];
            int batchCapacity = Math.Min(count, VerticesPerBatch);
            var batch = new byte[checked(batchCapacity * stride)];

            for (int first = 0; first < count; first += batchCapacity)
            {
                int batchCount = Math.Min(batchCapacity, count - first);
                FillBuffer(fs, batch, checked(batchCount * stride));

                // Reading remains sequential, while the CPU-heavy conversion of independent
                // vertices is spread across worker threads. Keeping a bounded batch avoids
                // loading the complete (often multi-GB) PLY payload into managed memory.
                if (batchCount >= ParallelThreshold && Environment.ProcessorCount > 1)
                {
                    Parallel.For(0, batchCount, i =>
                        splats[first + i] = BuildSplat(batch, i * stride, propIdx));
                }
                else
                {
                    for (int i = 0; i < batchCount; i++)
                        splats[first + i] = BuildSplat(batch, i * stride, propIdx);
                }
            }
            return splats;
        }

        private static GaussianSplatGPU BuildSplat(
            byte[] rows, int rowOffset, Dictionary<string, int> idx)
        {
            float Get(string name) =>
                idx.TryGetValue(name, out int i)
                    ? BitConverter.ToSingle(rows, rowOffset + i * 4)
                    : 0f;

            // Color from DC spherical harmonics
            float r = Mathf.Clamp01(0.5f + SH_C0 * Get("f_dc_0"));
            float g = Mathf.Clamp01(0.5f + SH_C0 * Get("f_dc_1"));
            float b = Mathf.Clamp01(0.5f + SH_C0 * Get("f_dc_2"));

            // Opacity: raw value is pre-sigmoid
            float opacity = 1f / (1f + Mathf.Exp(-Get("opacity")));

            // Scale: raw value is log-scale. 上限 3 (≈20 world unit) を超えるフローターを除外する
            float sx = Mathf.Exp(Mathf.Min(Get("scale_0"), 3f));
            float sy = Mathf.Exp(Mathf.Min(Get("scale_1"), 3f));
            float sz = Mathf.Exp(Mathf.Min(Get("scale_2"), 3f));

            // Rotation quaternion: rot_0=w, rot_1=x, rot_2=y, rot_3=z
            float qw = Get("rot_0"), qx = Get("rot_1"), qy = Get("rot_2"), qz = Get("rot_3");
            float len = Mathf.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
            if (len > 1e-6f) { qw /= len; qx /= len; qy /= len; qz /= len; }

            // 3DGS ワールド座標 (COLMAP: 右手系) → Unity (左手系) 変換
            // 右手系→左手系は奇数軸の反転が必要。Y+Z 反転 = X 軸鏡映と等価
            // 位置: (x, -y, -z)
            // クォータニオン: Y+Z 反転 → (w, +qx, -qy, -qz)
            return new GaussianSplatGPU
            {
                position = new Vector3(Get("x"), -Get("y"), -Get("z")),
                opacity  = opacity,
                rotation = new Vector4(qw, qx, -qy, -qz),
                scale    = new Vector3(sx, sy, sz),
                color    = new Vector3(r, g, b),
            };
        }

        private static (int count, List<string> props, long dataPos) ReadHeader(Stream fs)
        {
            int count = 0;
            var props = new List<string>();
            bool inVertex = false;

            while (true)
            {
                string line = ReadAsciiLine(fs)?.Trim();
                if (line == null || line == "end_header") break;

                if (line.StartsWith("element vertex ", StringComparison.Ordinal))
                {
                    if (int.TryParse(line.Substring("element vertex ".Length), out int n))
                        count = n;
                    inVertex = true;
                }
                else if (line.StartsWith("element ", StringComparison.Ordinal))
                {
                    inVertex = false;
                }
                else if (inVertex && line.StartsWith("property float ", StringComparison.Ordinal))
                {
                    props.Add(line.Substring("property float ".Length).Trim());
                }
            }
            return (count, props, fs.Position);
        }

        private static void FillBuffer(Stream fs, byte[] buf, int len)
        {
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, len - read);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
        }

        private static string ReadAsciiLine(Stream fs)
        {
            var sb = new StringBuilder(64);
            int b;
            while ((b = fs.ReadByte()) != -1)
            {
                if (b == '\n') return sb.ToString();
                if (b != '\r') sb.Append((char)b);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}

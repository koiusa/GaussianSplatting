using System;
using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// Compute Shader が使えないプラットフォーム向けの CPU ソーター。
    /// Array.Sort を使うため大量 splat では負荷が高い（目安: 50 万超で重くなる）。
    /// </summary>
    public sealed class CpuSorter : IGaussianSplatSorter
    {
        private ComputeBuffer _indexBuffer;
        private uint[]        _indices;
        private int           _allocatedCount;

        public bool          IsSupported      => true;
        public bool          IsBusy           => false;
        public ComputeBuffer SortedIndexBuffer => _indexBuffer;

        public void Initialize(int splatCount)
        {
            if (_allocatedCount == splatCount) return;
            _allocatedCount = splatCount;

            _indexBuffer?.Release();
            _indices     = new uint[splatCount];
            _indexBuffer = new ComputeBuffer(splatCount, sizeof(uint));
        }

        public void Sort(ComputeBuffer _splatBuffer, GaussianSplatGPU[] cpuSplats,
                         int splatCount, Vector3 cameraLocalPos, Vector3 cameraLocalForward,
                         ComputeBuffer sourceIndices = null)
        {
            Initialize(splatCount);

            for (uint i = 0; i < (uint)splatCount; i++) _indices[i] = i;

            Array.Sort(_indices, (a, b) =>
            {
                float da = Vector3.Dot(cpuSplats[a].position - cameraLocalPos, cameraLocalForward);
                float db = Vector3.Dot(cpuSplats[b].position - cameraLocalPos, cameraLocalForward);
                return db.CompareTo(da); // 遠→近（奥→手前）
            });

            _indexBuffer.SetData(_indices);
        }

        // CPU ソートは Sort() 内で同期的に完了するため分割の必要がない
        public void Tick() { }

        public void Dispose()
        {
            _indexBuffer?.Release();
            _indexBuffer = null;
            _indices     = null;
        }
    }
}

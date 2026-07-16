using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>安定な4-bit GPU Radix Sort。32-bit深度キーを8フレームで厳密に整列する。</summary>
    public sealed class GpuRadixSorter : IGaussianSplatSorter
    {
        private const int GroupSize = 128;
        private const int Radix = 16;
        private const int PassCount = 8;
        private const int MaxGroupsX = 65535;

        private readonly ComputeShader _cs;
        private readonly int _initialize;
        private readonly int _count;
        private readonly int _prefix;
        private readonly int _scatter;
        private ComputeBuffer _keysA, _keysB;
        private ComputeBuffer _payloadsA, _payloadsB;
        private ComputeBuffer _groupCounts, _groupOffsets;
        private int _capacity, _groupCapacity;
        private int _pendingPass, _countToSort, _groupCount, _groupsX, _groupsY;

        public bool IsSupported => SystemInfo.supportsComputeShaders;
        public bool IsBusy => _pendingPass >= 0;
        // Eight ping-pong passes finish in A.
        public ComputeBuffer SortedIndexBuffer => _payloadsA;

        public GpuRadixSorter(ComputeShader cs)
        {
            _cs = cs;
            _initialize = cs.FindKernel("InitializeKeys");
            _count = cs.FindKernel("CountDigits");
            _prefix = cs.FindKernel("PrefixDigits");
            _scatter = cs.FindKernel("ScatterDigits");
            _pendingPass = -1;
        }

        public void Initialize(int splatCount)
        {
            int needed = Mathf.Max(1, splatCount);
            int groups = Mathf.Max(1, Mathf.CeilToInt(needed / (float)GroupSize));
            if (_keysA == null || needed > _capacity)
            {
                _capacity = needed;
                ReleasePair(ref _keysA, ref _keysB);
                ReleasePair(ref _payloadsA, ref _payloadsB);
                _keysA = new ComputeBuffer(_capacity, sizeof(uint));
                _keysB = new ComputeBuffer(_capacity, sizeof(uint));
                _payloadsA = new ComputeBuffer(_capacity, sizeof(uint));
                _payloadsB = new ComputeBuffer(_capacity, sizeof(uint));
            }
            if (_groupCounts == null || groups > _groupCapacity)
            {
                _groupCapacity = groups;
                _groupCounts?.Release();
                _groupOffsets?.Release();
                _groupCounts = new ComputeBuffer(_groupCapacity * Radix, sizeof(uint));
                _groupOffsets = new ComputeBuffer(_groupCapacity * Radix, sizeof(uint));
            }
        }

        public void Sort(ComputeBuffer splatBuffer, GaussianSplatGPU[] cpuSplats, int splatCount,
                         Vector3 cameraLocalPos, Vector3 cameraLocalForward,
                         ComputeBuffer sourceIndices = null)
        {
            Initialize(splatCount);
            _countToSort = splatCount;
            _groupCount = Mathf.Max(1, Mathf.CeilToInt(splatCount / (float)GroupSize));
            _groupsX = Mathf.Min(_groupCount, MaxGroupsX);
            _groupsY = Mathf.CeilToInt(_groupCount / (float)_groupsX);

            _cs.SetBuffer(_initialize, "_SplatBuffer", splatBuffer);
            if (sourceIndices != null) _cs.SetBuffer(_initialize, "_SourceIndices", sourceIndices);
            _cs.SetBuffer(_initialize, "_OutputKeys", _keysA);
            _cs.SetBuffer(_initialize, "_OutputPayloads", _payloadsA);
            SetConstants(cameraLocalPos, cameraLocalForward, sourceIndices != null, 0);
            _cs.Dispatch(_initialize, _groupsX, _groupsY, 1);
            _pendingPass = 0;
        }

        public void Tick()
        {
            if (_pendingPass < 0) return;
            bool even = (_pendingPass & 1) == 0;
            ComputeBuffer inputKeys = even ? _keysA : _keysB;
            ComputeBuffer inputPayloads = even ? _payloadsA : _payloadsB;
            ComputeBuffer outputKeys = even ? _keysB : _keysA;
            ComputeBuffer outputPayloads = even ? _payloadsB : _payloadsA;
            int shift = _pendingPass * 4;

            BindPass(_count, inputKeys, inputPayloads, outputKeys, outputPayloads);
            SetPassConstants(shift);
            _cs.Dispatch(_count, _groupsX, _groupsY, 1);

            BindPass(_prefix, inputKeys, inputPayloads, outputKeys, outputPayloads);
            SetPassConstants(shift);
            _cs.Dispatch(_prefix, 1, 1, 1);

            BindPass(_scatter, inputKeys, inputPayloads, outputKeys, outputPayloads);
            SetPassConstants(shift);
            _cs.Dispatch(_scatter, _groupsX, _groupsY, 1);

            _pendingPass++;
            if (_pendingPass >= PassCount) _pendingPass = -1;
        }

        private void BindPass(int kernel, ComputeBuffer inputKeys, ComputeBuffer inputPayloads,
                              ComputeBuffer outputKeys, ComputeBuffer outputPayloads)
        {
            _cs.SetBuffer(kernel, "_InputKeys", inputKeys);
            _cs.SetBuffer(kernel, "_InputPayloads", inputPayloads);
            _cs.SetBuffer(kernel, "_OutputKeys", outputKeys);
            _cs.SetBuffer(kernel, "_OutputPayloads", outputPayloads);
            _cs.SetBuffer(kernel, "_GroupCounts", _groupCounts);
            _cs.SetBuffer(kernel, "_GroupOffsets", _groupOffsets);
        }

        private void SetPassConstants(int shift)
        {
            _cs.SetInt("_SplatCount", _countToSort);
            _cs.SetInt("_GroupCount", _groupCount);
            _cs.SetInt("_GroupsX", _groupsX);
            _cs.SetInt("_Shift", shift);
        }

        private void SetConstants(Vector3 pos, Vector3 forward, bool indexed, int shift)
        {
            SetPassConstants(shift);
            _cs.SetInt("_UseSourceIndices", indexed ? 1 : 0);
            _cs.SetVector("_CameraLocalPos", pos);
            _cs.SetVector("_CameraLocalForward", forward);
        }

        public void Dispose()
        {
            ReleasePair(ref _keysA, ref _keysB);
            ReleasePair(ref _payloadsA, ref _payloadsB);
            _groupCounts?.Release(); _groupCounts = null;
            _groupOffsets?.Release(); _groupOffsets = null;
            _capacity = _groupCapacity = 0;
            _pendingPass = -1;
        }

        private static void ReleasePair(ref ComputeBuffer a, ref ComputeBuffer b)
        {
            a?.Release(); b?.Release(); a = null; b = null;
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    /// <summary>
    /// GPU 上で視錐台カリングを行い、可視 splat のインデックスを圧縮したバッファへ Append する。
    /// 可視件数は AsyncGPUReadback でノンストール取得するため 1〜数フレーム遅れて反映される
    /// （表示のスムーズさを優先。GPU タイムアウト対策はソート側の分割実行 (GpuRadixSorter.Tick)
    /// で別途行っているため、ここでは同期ストールを入れる必要がない）。
    /// 読み取り完了前に新しいカリングを発行すると、バッファ内容と件数の世代がずれて不整合になるため、
    /// 前回の読み取りが完了するまでは <see cref="CanDispatch"/> が false を返し再発行をブロックする。
    /// さらに Dispatch() を呼んだ瞬間に Ready を落とし、対応する読み取りが完了するまでは
    /// 古い（別世代の）VisibleCount を新しいバッファ内容と組み合わせて使ってしまう事故を防ぐ。
    /// </summary>
    public sealed class GpuFrustumCuller
    {
        private readonly ComputeShader _cs;
        private readonly int           _kernel;

        private readonly ComputeBuffer[] _visibleIndices = new ComputeBuffer[2];
        private readonly ComputeBuffer[] _countStaging = new ComputeBuffer[2];
        private readonly bool[] _pendingReadback = new bool[2];
        private int           _allocatedCount;
        private bool _resultDirty;
        private int _latestSlot = -1;
        private int _generation;
        private int _nextSequence;
        private int _latestSequence = -1;

        /// <summary>直近発行分の非同期読み取りが完了し、VisibleCount / VisibleIndexBuffer が有効かどうか。</summary>
        public bool          Ready              { get; private set; }
        public int           VisibleCount       { get; private set; }
        public ComputeBuffer VisibleIndexBuffer => _latestSlot >= 0 ? _visibleIndices[_latestSlot] : null;

        /// <summary>前回発行したカリングの読み取りが完了しており、新規発行が可能かどうか。</summary>
        public bool CanDispatch => FindWritableSlot() >= 0;

        private const int GroupSize            = 64;
        private const int MaxGroupsPerDimension = 65535;

        public GpuFrustumCuller(ComputeShader cs)
        {
            _cs     = cs;
            _kernel = cs.FindKernel("CullSplats");
        }

        public void Initialize(int splatCount)
        {
            if (_visibleIndices[0] != null && _allocatedCount == splatCount) return;
            _allocatedCount = splatCount;
            _generation++;
            ReleaseBuffers();
            for (int i = 0; i < 2; i++)
            {
                _visibleIndices[i] = new ComputeBuffer(Mathf.Max(1, splatCount), sizeof(uint), ComputeBufferType.Append);
                _countStaging[i] = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            }

            Ready            = false;
            VisibleCount     = splatCount;
            _resultDirty     = false;
            _latestSlot = -1;
            _nextSequence = 0;
            _latestSequence = -1;
        }

        /// <summary>新しい可視件数が非同期読み取りで届いていれば true を一度だけ返す（消費後は false に戻る）。</summary>
        public bool ConsumeResultChanged()
        {
            if (!_resultDirty) return false;
            _resultDirty = false;
            return true;
        }

        public void Dispatch(ComputeBuffer splatBuffer, int splatCount,
                              Matrix4x4 localToWorld, Plane[] worldFrustumPlanes, float worldScale,
                              float cullMargin)
        {
            if (!CanDispatch) return;
            int slot = FindWritableSlot();
            if (slot < 0) return;
            _visibleIndices[slot].SetCounterValue(0);

            int totalGroups = Mathf.Max(1, Mathf.CeilToInt(splatCount / (float)GroupSize));
            int groupsX     = Mathf.Min(totalGroups, MaxGroupsPerDimension);
            int groupsY     = Mathf.CeilToInt(totalGroups / (float)groupsX);

            var planeVectors = new Vector4[6];
            for (int i = 0; i < 6; i++)
            {
                var pl = worldFrustumPlanes[i];
                planeVectors[i] = new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
            }

            _cs.SetBuffer(_kernel, "_SplatBuffer",    splatBuffer);
            _cs.SetBuffer(_kernel, "_VisibleIndices", _visibleIndices[slot]);
            _cs.SetMatrix("_LocalToWorld", localToWorld);
            _cs.SetVectorArray("_FrustumPlanes", planeVectors);
            _cs.SetFloat("_WorldScale",  worldScale);
            _cs.SetFloat("_CullMargin",  Mathf.Max(0f, cullMargin));
            _cs.SetInt("_SplatCount",    splatCount);
            _cs.SetInt("_GroupsX",       groupsX);
            _cs.Dispatch(_kernel, groupsX, groupsY, 1);

            ComputeBuffer.CopyCount(_visibleIndices[slot], _countStaging[slot], 0);

            _pendingReadback[slot] = true;
            int generation = _generation;
            int sequence = _nextSequence++;
            AsyncGPUReadback.Request(_countStaging[slot], r => OnReadback(r, slot, generation, sequence));
        }

        private void OnReadback(AsyncGPUReadbackRequest request, int slot, int generation, int sequence)
        {
            if (generation != _generation) return;
            _pendingReadback[slot] = false;
            if (request.hasError) return;

            var data = request.GetData<uint>();
            if (data.Length == 0) return;

            if (sequence > _latestSequence)
            {
                VisibleCount = Mathf.Min((int)data[0], _allocatedCount);
                _latestSlot = slot;
                _latestSequence = sequence;
                Ready = true;
                _resultDirty = true;
            }
        }

        private int FindWritableSlot()
        {
            for (int i = 0; i < 2; i++)
                if (!_pendingReadback[i] && i != _latestSlot) return i;
            return -1;
        }

        private void ReleaseBuffers()
        {
            for (int i = 0; i < 2; i++)
            {
                _visibleIndices[i]?.Release(); _visibleIndices[i] = null;
                _countStaging[i]?.Release(); _countStaging[i] = null;
                _pendingReadback[i] = false;
            }
        }

        public void Dispose()
        {
            _generation++;
            ReleaseBuffers();
            _allocatedCount  = 0;
            Ready            = false;
            _resultDirty     = false;
            _latestSlot = -1;
        }
    }
}

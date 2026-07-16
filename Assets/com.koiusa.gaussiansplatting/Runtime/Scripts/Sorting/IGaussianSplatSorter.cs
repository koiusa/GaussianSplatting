using System;
using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// プラットフォームごとに実装が異なるソーターの抽象。
    /// GPU Compute が使えない環境では CpuSorter に切り替わる。
    /// </summary>
    public interface IGaussianSplatSorter : IDisposable
    {
        bool IsSupported { get; }
        bool IsBusy { get; }

        /// <summary>
        /// splat 数が変わった場合のバッファ再確保。同じ数なら何もしない。
        /// </summary>
        void Initialize(int splatCount);

        /// <summary>
        /// カメラのローカル座標を基準に奥→手前順へのソートを開始する。
        /// GPU 実装は 1 フレームあたりの GPU 提出量（延いては GPU タイムアウト/クラッシュのリスク）を
        /// 抑えるため、ソート本体を即座に完了させず <see cref="Tick"/> に委ねる場合がある。
        /// その間 <see cref="SortedIndexBuffer"/> は途中経過を指す。
        /// </summary>
        /// <param name="splatBuffer">GPU 側の splat データバッファ</param>
        /// <param name="cpuSplats">CPU 側コピー（GPU ソーターは null でもよい）</param>
        /// <param name="splatCount">実際の splat 数（GPU カリング併用時は可視 splat 数）</param>
        /// <param name="cameraLocalPos">カメラ位置（GameObject のローカル空間）</param>
        /// <param name="cameraLocalForward">カメラ前方向（GameObject のローカル空間、正規化済み）</param>
        /// <param name="sourceIndices">
        /// 非 null の場合、splat ID を 0..splatCount-1 の連番ではなくこのバッファ（GPU カリング結果など）
        /// から読み取る。CPU ソーターは非対応のため無視する。
        /// </param>
        void Sort(ComputeBuffer splatBuffer, GaussianSplatGPU[] cpuSplats,
                  int splatCount, Vector3 cameraLocalPos, Vector3 cameraLocalForward,
                  ComputeBuffer sourceIndices = null);

        /// <summary>
        /// 分割実行中のソートを毎フレーム少しずつ進める。分割の必要がない実装（CPU ソーター等）では
        /// 何もしなくてよい。<see cref="Sort"/> の呼び出し有無に関わらず、毎フレーム呼ぶこと。
        /// </summary>
        void Tick();

        /// <summary>GPU 上でソート済みの index 配列（シェーダーに渡す）</summary>
        ComputeBuffer SortedIndexBuffer { get; }
    }
}

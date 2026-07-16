using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// 実行環境に応じた IGaussianSplatSorter を返すファクトリー。
    /// Compute Shader 非対応プラットフォームでは自動的に CpuSorter にフォールバックする。
    /// </summary>
    public static class SorterFactory
    {
        /// <param name="gpuSortShader">GaussianSplatSort.compute をアサインした ComputeShader。
        /// null の場合または Compute Shader 非対応の場合は CpuSorter を返す。</param>
        public static IGaussianSplatSorter Create(ComputeShader gpuSortShader)
        {
            if (gpuSortShader != null && SystemInfo.supportsComputeShaders)
                return new GpuRadixSorter(gpuSortShader);

            Debug.LogWarning("[GaussianSplatting] Compute Shader 非対応のため CpuSorter にフォールバックします。");
            return new CpuSorter();
        }
    }
}

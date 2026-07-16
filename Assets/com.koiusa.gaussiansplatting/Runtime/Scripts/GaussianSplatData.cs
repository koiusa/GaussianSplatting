using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting
{
    // GPU-side struct layout must match the shader's SplatData struct (64 bytes)
    [StructLayout(LayoutKind.Sequential)]
    public struct GaussianSplatGPU
    {
        public Vector3 position;   // 12 bytes
        public float   opacity;    //  4 bytes  → 16
        public Vector4 rotation;   // 16 bytes  wxyz
        public Vector3 scale;      // 12 bytes
        public float   pad;        //  4 bytes  → 48
        public Vector3 color;      // 12 bytes  linear RGB
        public float   pad2;       //  4 bytes  → 64
    }
}

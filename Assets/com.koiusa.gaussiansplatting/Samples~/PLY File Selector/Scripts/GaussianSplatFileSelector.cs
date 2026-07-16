using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GaussianSplatting.Samples
{
    /// <summary>A dependency-free IMGUI example for selecting and loading a binary Gaussian Splat PLY.</summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public sealed class GaussianSplatFileSelector : MonoBehaviour
    {
        [SerializeField] private string filePath = string.Empty;

        private GaussianSplatRenderer _renderer;
        private string _status = "Select a binary Gaussian Splat PLY file.";
        private bool _busy;
        private float _progress;

        private void Awake() => _renderer = GetComponent<GaussianSplatRenderer>();

        private void OnGUI()
        {
            const float width = 560f;
            GUILayout.BeginArea(new Rect(16f, 16f, width, 150f), GUI.skin.box);
            GUILayout.Label("Gaussian Splat PLY Loader");

            GUILayout.BeginHorizontal();
            GUI.enabled = !_busy;
            filePath = GUILayout.TextField(filePath, GUILayout.ExpandWidth(true));

#if UNITY_EDITOR
            if (GUILayout.Button("Browse...", GUILayout.Width(90f)))
            {
                string directory = string.IsNullOrWhiteSpace(filePath)
                    ? string.Empty
                    : Path.GetDirectoryName(filePath);
                string selected = EditorUtility.OpenFilePanel("Select Gaussian Splat PLY", directory, "ply");
                if (!string.IsNullOrEmpty(selected)) filePath = selected;
            }
#endif
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Load", GUILayout.Width(90f))) StartCoroutine(Load());
            if (GUILayout.Button("Clear", GUILayout.Width(90f)))
            {
                _renderer.Clear();
                _status = "Cleared.";
                _progress = 0f;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label(_status);
            if (_busy) GUILayout.HorizontalSlider(_progress, 0f, 1f);
            GUILayout.EndArea();
        }

        private IEnumerator Load()
        {
            if (_busy) yield break;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                _status = "File not found.";
                yield break;
            }

            _busy = true;
            _progress = 0f;
            _status = "Parsing PLY...";

            GaussianSplatGPU[] splats = null;
            Exception error = null;
            var task = Task.Run(() => PlyParser.Parse(filePath));
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted) error = task.Exception?.GetBaseException();
            else if (task.IsCanceled) error = new OperationCanceledException();
            else splats = task.Result;

            if (error == null)
            {
                _status = "Uploading to GPU...";
                yield return _renderer.LoadSplatsAsync(splats, filePath, value => _progress = value);
                error = string.IsNullOrEmpty(_renderer.LastLoadError)
                    ? null
                    : new InvalidOperationException(_renderer.LastLoadError);
            }

            _status = error == null
                ? $"Loaded {splats.Length:N0} splats."
                : $"Error: {error.Message}";
            _progress = error == null ? 1f : 0f;
            _busy = false;
        }
    }
}

using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// StreamingAssets/mercury2-config.json を非同期ロードする。
    /// WebGL でも Editor でも <see cref="UnityWebRequest"/> 経由で読み込み、同じコードパスを使う。
    /// 読み込み失敗時は null を返す（CPU 補填機能は使えないがゲームは続行可能）。
    /// </summary>
    public static class Mercury2ConfigLoader
    {
        public const string FileName = "mercury2-config.json";

        public static Mercury2Config Current { get; private set; }
        public static bool IsLoaded { get; private set; }
        public static event Action<Mercury2Config> Loaded;

        public static async Task<Mercury2Config> LoadAsync()
        {
            if (IsLoaded) return Current;

            var url = $"{Application.streamingAssetsPath}/{FileName}";
            // WebGL / Android 以外では file:// プレフィックスが必要な場合がある
            if (!url.Contains("://") && !url.StartsWith("file://"))
                url = "file://" + url;

            try
            {
                using var request = UnityWebRequest.Get(url);
                var op = request.SendWebRequest();
                while (!op.isDone) await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[Mercury2] Config not found at {url} ({request.error}). CPU fill will be disabled.");
                    IsLoaded = true;
                    Current = null;
                    Loaded?.Invoke(null);
                    return null;
                }

                var text = request.downloadHandler.text;
                var config = JsonUtility.FromJson<Mercury2Config>(text);
                if (config != null && !config.IsConfigured)
                {
                    Debug.LogWarning("[Mercury2] Config file loaded but apiKey/endpoint unfilled. CPU fill will be disabled.");
                }

                Current = config;
                IsLoaded = true;
                if (config != null && config.IsConfigured)
                    Debug.Log($"[Mercury2] Config loaded: endpoint={config.endpoint} model={config.model}");
                Loaded?.Invoke(config);
                return config;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mercury2] Config load failed: {e.Message}");
                IsLoaded = true;
                Current = null;
                return null;
            }
        }
    }
}

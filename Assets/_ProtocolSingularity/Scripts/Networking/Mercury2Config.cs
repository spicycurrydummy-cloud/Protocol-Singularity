using System;
using System.Collections.Generic;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// 単一の LLM プロバイダ設定 (OpenAI 互換エンドポイント)。
    /// Mercury2 / OpenRouter / OpenAI / その他互換サービスで使い回せる。
    /// </summary>
    [Serializable]
    public class Mercury2Provider
    {
        public string name;       // 表示用ラベル ("mercury2" / "openrouter" 等)
        public string apiKey;
        public string endpoint = "https://api.inceptionlabs.ai/v1";
        public string model = "mercury-2";
        public int timeoutSeconds = 30;

        public bool IsConfigured => !string.IsNullOrEmpty(apiKey)
            && !apiKey.StartsWith("REPLACE_")
            && !string.IsNullOrEmpty(endpoint);
    }

    /// <summary>
    /// LLM API 接続設定。StreamingAssets/mercury2-config.json から読み込む。
    /// 複数プロバイダをフォールバック順に並べられる (上から順に試行、402/429/ネット障害で次へ)。
    /// 旧形式の flat 記述 (apiKey/endpoint/model) も後方互換で受け付ける。
    /// </summary>
    [Serializable]
    public class Mercury2Config
    {
        // 新形式: 複数プロバイダ
        public Mercury2Provider[] providers;

        // --- 旧形式 (後方互換。providers が無ければ単一プロバイダ扱い) ---
        public string apiKey;
        public string endpoint = "https://api.inceptionlabs.ai/v1";
        public string model = "mercury-2";
        public int timeoutSeconds = 30;

        public bool IsConfigured
        {
            get
            {
                foreach (var p in GetActiveProviders())
                    if (p.IsConfigured) return true;
                return false;
            }
        }

        /// <summary>実際に使える (apiKey 有効) プロバイダ一覧を順序付きで返す。</summary>
        public IReadOnlyList<Mercury2Provider> GetActiveProviders()
        {
            var list = new List<Mercury2Provider>();
            if (providers != null)
            {
                foreach (var p in providers)
                    if (p != null && p.IsConfigured) list.Add(p);
            }
            // 旧形式フィールドが単体で設定されていれば末尾に追加 (プライマリ兼フォールバック)
            if (!string.IsNullOrEmpty(apiKey) && !apiKey.StartsWith("REPLACE_"))
            {
                list.Add(new Mercury2Provider
                {
                    name = "legacy",
                    apiKey = apiKey,
                    endpoint = endpoint,
                    model = model,
                    timeoutSeconds = timeoutSeconds
                });
            }
            return list;
        }
    }
}

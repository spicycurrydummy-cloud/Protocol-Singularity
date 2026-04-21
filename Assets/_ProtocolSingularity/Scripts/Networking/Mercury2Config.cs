using System;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// Mercury2 API 接続設定。
    /// StreamingAssets/mercury2-config.json から Mercury2ConfigLoader が起動時に読み込む。
    /// ファイルは gitignore されていて、ローカル開発時は手動で作成、
    /// GitHub Actions ではシークレットから生成する想定。
    /// </summary>
    [Serializable]
    public class Mercury2Config
    {
        public string apiKey;
        public string endpoint = "https://api.inceptionlabs.ai/v1";
        public int timeoutSeconds = 30;
        public string model = "mercury-2";

        public bool IsConfigured => !string.IsNullOrEmpty(apiKey)
            && !apiKey.StartsWith("REPLACE_")
            && !string.IsNullOrEmpty(endpoint);
    }
}

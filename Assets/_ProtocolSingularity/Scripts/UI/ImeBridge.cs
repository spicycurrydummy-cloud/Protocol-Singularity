using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProtocolSingularity.UI
{
    /// <summary>
    /// WebGL ビルドでの日本語 IME 入力を受け取るブリッジ。
    /// ホスト側の JS (ime-bridge.js) から <c>SendMessage("ImeBridge", "ReceiveImeText", string)</c> で呼ばれる。
    /// シーン中の任意 GameObject にアタッチし、name が "ImeBridge" であれば機能する。
    /// LobbyController の chat-input にフォーカスが当たっているとき、受信文字列を append する。
    /// </summary>
    public class ImeBridge : MonoBehaviour
    {
        public static ImeBridge Instance { get; private set; }

        public static event Action<string> TextReceived;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // 名前を "ImeBridge" に固定 (JS から SendMessage するとき必要)
            gameObject.name = "ImeBridge";
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>JS 側から SendMessage で呼び出される。</summary>
        public void ReceiveImeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextReceived?.Invoke(text);
        }
    }
}

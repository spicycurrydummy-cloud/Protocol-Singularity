using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ProtocolSingularity.UI
{
    /// <summary>
    /// WebGL ビルドでの日本語 IME 入力を受け取るブリッジ。
    /// UI Toolkit TextField にフォーカスが当たった瞬間に <see cref="EnableInput"/> を呼ぶと、
    /// ブラウザ側で隠し textarea にフォーカスが移り、compositionend で確定文字列が
    /// <see cref="TextReceived"/> イベント経由で届く。
    /// </summary>
    public class ImeBridge : MonoBehaviour
    {
        public static ImeBridge Instance { get; private set; }

        public static event Action<string> TextReceived;
        public static event Action Submitted;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void ImeBridge_Enable();
        [DllImport("__Internal")] private static extern void ImeBridge_Disable();
        [DllImport("__Internal")] private static extern int ImeBridge_IsAvailable();
#else
        private static void ImeBridge_Enable() { }
        private static void ImeBridge_Disable() { }
        private static int ImeBridge_IsAvailable() => 0;
#endif

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            gameObject.name = "ImeBridge";
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>JS (ime-bridge.js) から SendMessage で呼ばれる — IME 確定後の文字列。</summary>
        public void ReceiveImeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextReceived?.Invoke(text);
        }

        /// <summary>JS から Enter キー押下時に呼ばれる (変換中の Enter は除く)。</summary>
        public void ReceiveImeSubmit(string _)
        {
            Submitted?.Invoke();
        }

        /// <summary>
        /// UI Toolkit TextField が focus in したら呼ぶ。JS 側が隠し textarea にフォーカスを流し、
        /// IME 入力を受け付ける状態に遷移する。非 WebGL 環境では no-op。
        /// </summary>
        public static void EnableInput() => ImeBridge_Enable();

        /// <summary>focus out で呼ぶ。JS 側が canvas にフォーカスを戻す。</summary>
        public static void DisableInput() => ImeBridge_Disable();

        /// <summary>ブリッジが利用可能か (WebGL で ime-bridge.js がロード済みか) を返す。</summary>
        public static bool IsAvailable =>
#if UNITY_WEBGL && !UNITY_EDITOR
            ImeBridge_IsAvailable() != 0;
#else
            false;
#endif
    }
}

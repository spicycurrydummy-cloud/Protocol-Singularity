using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ProtocolSingularity.UI
{
    /// <summary>
    /// WebGL ビルドでの IME / 日本語入力を HTML overlay 経由で受け取るブリッジ。
    /// Unity の UI Toolkit TextField は WebGL で IME を扱えないため、画面下部の
    /// HTML input にユーザー入力を受け取り、<see cref="TextChanged"/> (value 全体)
    /// / <see cref="Submitted"/> (Enter) で Unity 側へ通知する。
    /// </summary>
    public class ImeBridge : MonoBehaviour
    {
        public static ImeBridge Instance { get; private set; }

        /// <summary>HTML input の全 value が変化するたびに発火 (IME 確定 / 直接入力 / Insert 後)。</summary>
        public static event Action<string> TextChanged;

        /// <summary>Enter キーで送信指示が来たときに発火。</summary>
        public static event Action Submitted;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void ImeBridge_Show();
        [DllImport("__Internal")] private static extern void ImeBridge_Hide();
        [DllImport("__Internal")] private static extern void ImeBridge_SetValue(string value);
        [DllImport("__Internal")] private static extern void ImeBridge_Insert(string value);
        [DllImport("__Internal")] private static extern int ImeBridge_IsAvailable();
#else
        private static void ImeBridge_Show() { }
        private static void ImeBridge_Hide() { }
        private static void ImeBridge_SetValue(string _) { }
        private static void ImeBridge_Insert(string _) { }
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

        /// <summary>JS (ime-bridge.js) からの SendMessage 受信口 — value 全体。</summary>
        public void ReceiveImeText(string text)
        {
            TextChanged?.Invoke(text ?? string.Empty);
        }

        /// <summary>JS からの Enter 送信通知。</summary>
        public void ReceiveImeSubmit(string _)
        {
            Submitted?.Invoke();
        }

        public static void Show() => ImeBridge_Show();
        public static void Hide() => ImeBridge_Hide();
        public static void SetValue(string value) => ImeBridge_SetValue(value ?? string.Empty);
        public static void Insert(string value) => ImeBridge_Insert(value ?? string.Empty);

        public static bool IsAvailable =>
#if UNITY_WEBGL && !UNITY_EDITOR
            ImeBridge_IsAvailable() != 0;
#else
            false;
#endif
    }
}

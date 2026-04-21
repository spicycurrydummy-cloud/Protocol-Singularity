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

        /// <summary>
        /// HTML overlay からの文字列をこのコールバック経由でアクティブなフィールドへ流す。
        /// TextField が FocusIn したら Bind、他フィールドへ切替わったら上書きで Bind。
        /// </summary>
        private static Action<string> _activeSetter;
        private static Action _activeSubmit;

        public static void BindActive(Action<string> setter, Action submit)
        {
            _activeSetter = setter;
            _activeSubmit = submit;
        }

        public static void UnbindActive(Action<string> setter)
        {
            if (ReferenceEquals(_activeSetter, setter)) _activeSetter = null;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void ImeBridge_Show();
        [DllImport("__Internal")] private static extern void ImeBridge_Hide();
        [DllImport("__Internal")] private static extern void ImeBridge_Place(float nx, float ny, float nw, float nh, float fontPx);
        [DllImport("__Internal")] private static extern void ImeBridge_SetValue(string value);
        [DllImport("__Internal")] private static extern void ImeBridge_Insert(string value);
        [DllImport("__Internal")] private static extern int ImeBridge_IsAvailable();
#else
        private static void ImeBridge_Show() { }
        private static void ImeBridge_Hide() { }
        private static void ImeBridge_Place(float _a, float _b, float _c, float _d, float _e) { }
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
            var t = text ?? string.Empty;
            _activeSetter?.Invoke(t);     // アクティブフィールドへ直接ルーティング (per-field 受信)
            TextChanged?.Invoke(t);       // グローバル購読者へも通知 (後方互換)
        }

        /// <summary>JS からの Enter 送信通知。</summary>
        public void ReceiveImeSubmit(string _)
        {
            _activeSubmit?.Invoke();
            Submitted?.Invoke();
        }

        public static void Show() => ImeBridge_Show();
        public static void Hide() => ImeBridge_Hide();
        public static void SetValue(string value) => ImeBridge_SetValue(value ?? string.Empty);
        public static void Insert(string value) => ImeBridge_Insert(value ?? string.Empty);

        /// <summary>
        /// フォーカス中の TextField の真上に HTML overlay を重ねる。
        /// 正規化 (0..1) された field の rect と対象 panel の rect を渡せば
        /// canvas サイズに対する位置を自動計算する。fontPx は panel 座標系の値で、
        /// JS 側で canvas CSS サイズに合わせて再スケールされる (0 で無視)。
        /// </summary>
        public static void PlaceOverField(UnityEngine.Rect fieldWorld, UnityEngine.Rect panelWorld, float fontPx = 0f)
        {
            if (panelWorld.width <= 0f || panelWorld.height <= 0f) return;
            float nx = fieldWorld.x / panelWorld.width;
            float ny = fieldWorld.y / panelWorld.height;
            float nw = fieldWorld.width / panelWorld.width;
            float nh = fieldWorld.height / panelWorld.height;
            // font size も panel 高さに対する比率として送る (JS 側で canvas 高さに乗算)
            float fontRatio = fontPx > 0f ? (fontPx / panelWorld.height) : 0f;
            ImeBridge_Place(nx, ny, nw, nh, fontRatio);
        }

        public static bool IsAvailable =>
#if UNITY_WEBGL && !UNITY_EDITOR
            ImeBridge_IsAvailable() != 0;
#else
            false;
#endif
    }
}

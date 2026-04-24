using UnityEngine.UIElements;

namespace ProtocolSingularity.Audio
{
    /// <summary>
    /// UXML 内の audio-mute / audio-volume コントロールを AudioManager にバインドし、
    /// 任意のルート配下の Button 要素に Tick SFX を配線するユーティリティ。
    /// 各画面 (Title / Lobby) の初期化フックから 1 回呼ぶだけで OK。
    /// </summary>
    public static class AudioUIBinder
    {
        // WebGL の Noto Sans JP では ♪ / ∅ が描画されない環境があるため ASCII で表示する。
        private const string MuteIconOn = "VOL";
        private const string MuteIconOff = "MUTE";

        public static void BindPanel(VisualElement root)
        {
            if (root == null) return;
            var am = AudioManager.Instance;
            var muteBtn = root.Q<Button>("audio-mute-btn");
            var volume = root.Q<Slider>("audio-volume");
            if (muteBtn != null)
            {
                muteBtn.text = am.Muted ? MuteIconOff : MuteIconOn;
                muteBtn.clicked += () =>
                {
                    am.Muted = !am.Muted;
                    muteBtn.text = am.Muted ? MuteIconOff : MuteIconOn;
                };
            }
            if (volume != null)
            {
                volume.SetValueWithoutNotify(am.BgmVolume);
                volume.RegisterValueChangedCallback(e =>
                {
                    am.BgmVolume = e.newValue;
                    am.SfxVolume = e.newValue; // 単一スライダでまとめて調整
                });
            }
        }

        /// <summary>
        /// 指定したルート配下の Button 全てに hover 時の Tick SFX を配線する。
        /// 呼び出し側は動的に生成される Button (Runtime で追加) にも都度この関数を呼ぶこと。
        /// </summary>
        public static void WireHoverTicks(VisualElement root)
        {
            if (root == null) return;
            root.Query<Button>().ForEach(WireHoverTick);
        }

        public static void WireHoverTick(Button btn)
        {
            if (btn == null) return;
            // 二重登録防止用に userData にマーカーを立てる
            if (btn.userData is string tag && tag == "tick-wired") return;
            btn.userData = "tick-wired";
            btn.RegisterCallback<MouseEnterEvent>(_ => AudioManager.Instance.PlaySfx(SfxKey.Tick));
            // 押下時は Click SFX を鳴らす (Button.clicked は enabled 時のみ発火)
            btn.clicked += () => AudioManager.Instance.PlaySfx(SfxKey.Click);
        }
    }
}

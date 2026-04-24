using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ProtocolSingularity.Audio
{
    public enum SfxKey
    {
        Tick,
        Modal,
        Loading,
        Accept,
        Deny,
        HackSuccess,
        HackFailed,
        Warning,
        Click,
    }

    /// <summary>
    /// BGM カテゴリ。ラウンド進行や失敗カウントで切り替える。
    /// </summary>
    public enum BgmPhase
    {
        None,
        Lobby,
        Round1,  // 1st (R1-R2)
        Round2,  // 2nd (R3 以降 / R2 終了後)
        Last,    // AI 失敗カウント 2 で切替
        Final,   // OVERRIDE (楽曲未実装)
    }

    /// <summary>
    /// シーン跨ぎで持続するオーディオマネージャ。
    /// Resources/Audio/SFX/ と Resources/Audio/BGM/{Lobby,1st,2nd,Last,Final}/ を走査して自動ロード。
    /// 音量/ミュートは PlayerPrefs に永続化。
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public const string PrefsBgmVolume = "audio.bgm.vol";
        public const string PrefsSfxVolume = "audio.sfx.vol";
        public const string PrefsMute = "audio.mute";

        private static AudioManager _instance;
        public static AudioManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[AudioManager]");
                    _instance = go.AddComponent<AudioManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // --- ロードしたクリップ ---
        private readonly Dictionary<SfxKey, AudioClip> _sfx = new();
        private readonly Dictionary<BgmPhase, List<AudioClip>> _bgm = new();

        // --- プレイヤ (SFX は使い回し、BGM は 2 本でクロスフェード) ---
        private readonly List<AudioSource> _sfxPool = new();
        private AudioSource _bgmA;
        private AudioSource _bgmB;
        private bool _bgmUseA; // 現在鳴ってるのが A ? B ?
        private AudioSource _sfxLoop; // Loading など持続音用

        // --- 設定 ---
        private float _bgmVolume = 0.55f;
        private float _sfxVolume = 0.8f;
        private bool _muted;

        // --- 状態 ---
        private BgmPhase _currentPhase = BgmPhase.None;
        private float _lastTickTime; // Tick 重なり防止用
        private Coroutine _bgmFadeCo;

        public float BgmVolume
        {
            get => _bgmVolume;
            set { _bgmVolume = Mathf.Clamp01(value); ApplyVolumes(); Save(); }
        }
        public float SfxVolume
        {
            get => _sfxVolume;
            set { _sfxVolume = Mathf.Clamp01(value); ApplyVolumes(); Save(); }
        }
        public bool Muted
        {
            get => _muted;
            set { _muted = value; ApplyVolumes(); Save(); }
        }
        public BgmPhase CurrentPhase => _currentPhase;

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            Load();
            LoadClips();
            SetupAudioSources();
        }

        private void LoadClips()
        {
            foreach (SfxKey k in System.Enum.GetValues(typeof(SfxKey)))
            {
                var name = SfxResourceName(k);
                var clip = Resources.Load<AudioClip>($"Audio/SFX/{name}");
                if (clip != null) _sfx[k] = clip;
                else Debug.LogWarning($"[AudioManager] SFX missing: Audio/SFX/{name}");
            }
            _bgm[BgmPhase.Lobby] = new List<AudioClip>(Resources.LoadAll<AudioClip>("Audio/BGM/Lobby"));
            _bgm[BgmPhase.Round1] = new List<AudioClip>(Resources.LoadAll<AudioClip>("Audio/BGM/1st"));
            _bgm[BgmPhase.Round2] = new List<AudioClip>(Resources.LoadAll<AudioClip>("Audio/BGM/2nd"));
            _bgm[BgmPhase.Last] = new List<AudioClip>(Resources.LoadAll<AudioClip>("Audio/BGM/Last"));
            _bgm[BgmPhase.Final] = new List<AudioClip>(Resources.LoadAll<AudioClip>("Audio/BGM/Final"));
        }

        private static string SfxResourceName(SfxKey k) => k switch
        {
            SfxKey.Tick => "Tick",
            SfxKey.Modal => "Modal",
            SfxKey.Loading => "Loading",
            SfxKey.Accept => "Accept",
            SfxKey.Deny => "Deny",
            SfxKey.HackSuccess => "Hack_Success",
            SfxKey.HackFailed => "Hack_Failed",
            SfxKey.Warning => "Warning",
            SfxKey.Click => "Click",
            _ => k.ToString(),
        };

        private void SetupAudioSources()
        {
            // SFX プール: 最大同時 6 音
            for (int i = 0; i < 6; i++)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                _sfxPool.Add(src);
            }
            _bgmA = gameObject.AddComponent<AudioSource>();
            _bgmA.playOnAwake = false; _bgmA.loop = true;
            _bgmB = gameObject.AddComponent<AudioSource>();
            _bgmB.playOnAwake = false; _bgmB.loop = true;
            _sfxLoop = gameObject.AddComponent<AudioSource>();
            _sfxLoop.playOnAwake = false; _sfxLoop.loop = true;
            ApplyVolumes();
        }

        private void ApplyVolumes()
        {
            float bgm = _muted ? 0f : _bgmVolume;
            float sfx = _muted ? 0f : _sfxVolume;
            if (_bgmA != null) _bgmA.volume = bgm;
            if (_bgmB != null) _bgmB.volume = bgm;
            if (_sfxLoop != null) _sfxLoop.volume = sfx;
            foreach (var s in _sfxPool) if (s != null) s.volume = sfx;
        }

        private void Load()
        {
            _bgmVolume = PlayerPrefs.GetFloat(PrefsBgmVolume, 0.55f);
            _sfxVolume = PlayerPrefs.GetFloat(PrefsSfxVolume, 0.8f);
            _muted = PlayerPrefs.GetInt(PrefsMute, 0) != 0;
        }

        private void Save()
        {
            PlayerPrefs.SetFloat(PrefsBgmVolume, _bgmVolume);
            PlayerPrefs.SetFloat(PrefsSfxVolume, _sfxVolume);
            PlayerPrefs.SetInt(PrefsMute, _muted ? 1 : 0);
            PlayerPrefs.Save();
        }

        // ================ Public API ================

        /// <summary>SFX 再生。Tick は連打防止で 50ms のクールダウン。</summary>
        public void PlaySfx(SfxKey key)
        {
            if (!_sfx.TryGetValue(key, out var clip) || clip == null) return;
            if (key == SfxKey.Tick)
            {
                if (Time.unscaledTime - _lastTickTime < 0.05f) return;
                _lastTickTime = Time.unscaledTime;
            }
            var src = GetFreeSfxSource();
            if (src == null) return;
            src.PlayOneShot(clip, _muted ? 0f : _sfxVolume);
        }

        private AudioSource GetFreeSfxSource()
        {
            foreach (var s in _sfxPool) if (!s.isPlaying) return s;
            return _sfxPool.Count > 0 ? _sfxPool[0] : null; // 全部使用中なら最古を上書き
        }

        /// <summary>指定 SFX をループ再生 (Loading 等の持続音用)。既に同じ clip が鳴ってれば何もしない。</summary>
        public void PlaySfxLoop(SfxKey key)
        {
            if (_sfxLoop == null) return;
            if (!_sfx.TryGetValue(key, out var clip) || clip == null) return;
            if (_sfxLoop.isPlaying && _sfxLoop.clip == clip) return;
            _sfxLoop.clip = clip;
            _sfxLoop.volume = _muted ? 0f : _sfxVolume;
            _sfxLoop.Play();
        }

        /// <summary>ループ中 SFX を停止。</summary>
        public void StopSfxLoop()
        {
            if (_sfxLoop != null && _sfxLoop.isPlaying) _sfxLoop.Stop();
        }

        /// <summary>BGM を指定カテゴリに切り替え。同じカテゴリなら何もしない。</summary>
        public void PlayBgm(BgmPhase phase)
        {
            if (phase == _currentPhase) return;
            if (phase == BgmPhase.None)
            {
                StopBgm();
                _currentPhase = BgmPhase.None;
                return;
            }
            if (!_bgm.TryGetValue(phase, out var list) || list == null || list.Count == 0)
            {
                Debug.LogWarning($"[AudioManager] No BGM clips for {phase}");
                return;
            }
            var clip = list[Random.Range(0, list.Count)];
            CrossfadeTo(clip);
            _currentPhase = phase;
        }

        private void StopBgm()
        {
            if (_bgmFadeCo != null) { StopCoroutine(_bgmFadeCo); _bgmFadeCo = null; }
            if (_bgmA != null) _bgmA.Stop();
            if (_bgmB != null) _bgmB.Stop();
        }

        private void CrossfadeTo(AudioClip next, float seconds = 1.2f)
        {
            if (_bgmFadeCo != null) StopCoroutine(_bgmFadeCo);
            _bgmFadeCo = StartCoroutine(CrossfadeCo(next, seconds));
        }

        private IEnumerator CrossfadeCo(AudioClip next, float seconds)
        {
            var incoming = _bgmUseA ? _bgmB : _bgmA;
            var outgoing = _bgmUseA ? _bgmA : _bgmB;
            float targetVol = _muted ? 0f : _bgmVolume;

            incoming.clip = next;
            incoming.volume = 0f;
            incoming.Play();
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / seconds);
                incoming.volume = targetVol * p;
                outgoing.volume = targetVol * (1f - p);
                yield return null;
            }
            outgoing.Stop();
            outgoing.volume = targetVol;
            incoming.volume = targetVol;
            _bgmUseA = !_bgmUseA;
            _bgmFadeCo = null;
        }
    }
}

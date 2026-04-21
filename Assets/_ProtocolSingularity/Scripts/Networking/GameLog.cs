using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// ゲーム進行ログを全クライアントに同期するシーン配置 NetworkBehaviour。
    /// リーダー選出・チーム提案・投票結果・ハック結果・OVERRIDE 結果などを
    /// リングバッファで保持し、UI に表示する。ホスト側の GSM から HostAppend を呼ぶ。
    /// </summary>
    public class GameLog : NetworkBehaviour
    {
        public const int Capacity = 40;
        public const int MaxChars = 64;

        public struct Entry : INetworkStruct
        {
            public NetworkString<_64> Text;
            public int Tick;
        }

        public static GameLog Instance { get; private set; }
        public static event Action Changed;

        [Networked, Capacity(Capacity), OnChangedRender(nameof(OnChanged))]
        public NetworkArray<Entry> Buffer => default;
        [Networked, OnChangedRender(nameof(OnChanged))] public int HeadIndex { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int TotalEntries { get; set; }

        public override void Spawned()
        {
            Instance = this;
            Changed?.Invoke();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        public void HostAppend(string text)
        {
            if (!HasStateAuthority) return;
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > MaxChars - 1) text = text.Substring(0, MaxChars - 1);
            var entry = new Entry { Text = text, Tick = Runner != null ? Runner.Tick : 0 };
            Buffer.Set(HeadIndex, entry);
            HeadIndex = (HeadIndex + 1) % Capacity;
            TotalEntries++;
        }

        public void HostClear()
        {
            if (!HasStateAuthority) return;
            for (int i = 0; i < Capacity; i++) Buffer.Set(i, default);
            HeadIndex = 0;
            TotalEntries = 0;
        }

        public IEnumerable<Entry> EnumerateInOrder(int max = Capacity)
        {
            int count = Mathf.Min(Mathf.Min(TotalEntries, Capacity), max);
            int start = (HeadIndex - count + Capacity) % Capacity;
            for (int i = 0; i < count; i++)
            {
                yield return Buffer[(start + i) % Capacity];
            }
        }

        private void OnChanged() => Changed?.Invoke();
    }
}

using System;
using System.Collections.Generic;
using Fusion;
using ProtocolSingularity.Core;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// チャットを扱うシーン配置 NetworkBehaviour。
    /// リングバッファで最新 <see cref="Capacity"/> 件を全クライアントに同期する。
    /// </summary>
    public class ChatManager : NetworkBehaviour
    {
        public struct Entry : INetworkStruct
        {
            public int Template;
            public PlayerRef Sender;
            public PlayerRef Target;
            public int Confidence;    // ChatConfidence enum
            public int Tick;          // 一意な識別子
            public NetworkString<_64> RawText; // Thought テンプレートのときの自由記述テキスト
        }

        public const int Capacity = 40;

        public static ChatManager Instance;
        public static event Action Changed;

        [Networked, Capacity(Capacity), OnChangedRender(nameof(OnChanged))]
        public NetworkArray<Entry> Buffer => default;

        [Networked, OnChangedRender(nameof(OnChanged))] public int HeadIndex { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int TotalMessages { get; set; }

        public override void Spawned()
        {
            Instance = this;
            Changed?.Invoke();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>ホスト専用: ログをクリア (ゲーム開始/終了時などに呼ぶ)。</summary>
        public void HostClear()
        {
            if (!HasStateAuthority) return;
            for (int i = 0; i < Capacity; i++) Buffer.Set(i, default);
            HeadIndex = 0;
            TotalMessages = 0;
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void Rpc_Send(PlayerRef sender, int templateInt, PlayerRef target, int confidenceInt,
            RpcInfo info = default)
        {
            if (!HasStateAuthority) return;
            var entry = new Entry
            {
                Template = templateInt,
                Sender = sender,
                Target = target,
                Confidence = Mathf.Clamp(confidenceInt, 0, (int)ChatConfidence.Certain),
                Tick = Runner != null ? Runner.Tick : 0,
                RawText = default,
            };
            Buffer.Set(HeadIndex, entry);
            HeadIndex = (HeadIndex + 1) % Capacity;
            TotalMessages++;
        }

        /// <summary>
        /// CPU の自由記述思考を送信する。RawText に 64 文字までの内容が入り、表示時は Thought テンプレとして扱われる。
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void Rpc_SendThought(PlayerRef sender, NetworkString<_64> rawText,
            RpcInfo info = default)
        {
            if (!HasStateAuthority) return;
            var entry = new Entry
            {
                Template = (int)ChatTemplate.Thought,
                Sender = sender,
                Target = PlayerRef.None,
                Confidence = 0,
                Tick = Runner != null ? Runner.Tick : 0,
                RawText = rawText,
            };
            Buffer.Set(HeadIndex, entry);
            HeadIndex = (HeadIndex + 1) % Capacity;
            TotalMessages++;
        }

        /// <summary>
        /// 古い順に最新 N 件までをリングバッファ物理インデックス付きで列挙する。
        /// </summary>
        public IEnumerable<(int ringIndex, Entry entry)> EnumerateInOrder(int max = Capacity)
        {
            int count = Mathf.Min(Mathf.Min(TotalMessages, Capacity), max);
            int start = (HeadIndex - count + Capacity) % Capacity;
            for (int i = 0; i < count; i++)
            {
                int idx = (start + i) % Capacity;
                yield return (idx, Buffer[idx]);
            }
        }

        private void OnChanged() => Changed?.Invoke();

        public static string FormatEntryText(Entry e, Func<PlayerRef, string> nameResolver)
        {
            var sender = nameResolver?.Invoke(e.Sender) ?? $"#{e.Sender.PlayerId}";
            var target = nameResolver?.Invoke(e.Target) ?? (e.Target == PlayerRef.None ? "-" : $"#{e.Target.PlayerId}");
            var t = (ChatTemplate)e.Template;
            if (t == ChatTemplate.Thought)
            {
                var raw = e.RawText.ToString();
                return $"{sender} >> {raw}";
            }
            var confEmote = t.UsesConfidence() ? " " + ((ChatConfidence)e.Confidence).ToEmote() : string.Empty;
            return t switch
            {
                ChatTemplate.SuspectAi        => $"{sender} > {target} は AI{confEmote}",
                ChatTemplate.TrustHuman       => $"{sender} > {target} は人類{confEmote}",
                ChatTemplate.IncludeInTeam    => $"{sender} > {target} をチームに入れるべき",
                ChatTemplate.ExcludeFromTeam  => $"{sender} > {target} をチームから外すべき",
                ChatTemplate.VoteApprove      => $"{sender} > この提案に賛成する",
                ChatTemplate.VoteReject       => $"{sender} > この提案に反対する",
                ChatTemplate.ClaimOracle      => $"{sender} > 自分は ORACLE だ",
                ChatTemplate.ClaimAdmin       => $"{sender} > 自分は ADMIN だ",
                ChatTemplate.BewareClaim      => $"{sender} > {target} のカミングアウトは警戒すべき",
                ChatTemplate.Agree            => $"{sender} > 同意",
                ChatTemplate.Disagree         => $"{sender} > 反対",
                ChatTemplate.Powerplay        => $"{sender} > パワープレイに警戒",
                _                             => $"{sender} > ...",
            };
        }
    }
}

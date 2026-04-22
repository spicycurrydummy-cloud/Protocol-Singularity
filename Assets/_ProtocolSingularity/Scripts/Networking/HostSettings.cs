using System;
using Fusion;
using ProtocolSingularity.Data;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// ホストのみが書き込み、全クライアントに同期されるゲーム設定。
    /// Lobby 〜 InGame を通して生存する。
    /// </summary>
    public class HostSettings : NetworkBehaviour
    {
        public static HostSettings Instance { get; private set; }
        public static event Action SettingsChanged;

        [Networked, OnChangedRender(nameof(OnChanged))] public int TargetPlayerCount { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int DiscussionSeconds { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int VoteSeconds { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int HackSeconds { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public NetworkBool EnableCpuFill { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public NetworkBool IncludeAgent { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public NetworkBool IncludeCipher { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public NetworkBool IncludeDrone { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public NetworkBool IncludeRadical { get; set; }

        public override void Spawned()
        {
            Instance = this;
            if (HasStateAuthority)
            {
                if (TargetPlayerCount == 0) TargetPlayerCount = 6;
                if (DiscussionSeconds == 0) DiscussionSeconds = 60;
                if (VoteSeconds == 0) VoteSeconds = 30;
                if (HackSeconds == 0) HackSeconds = 30;
                EnableCpuFill = true;
            }
            SettingsChanged?.Invoke();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
        public void Rpc_Update(int targetPlayers, int discussion, int vote, int hack, NetworkBool cpuFill)
        {
            TargetPlayerCount = Mathf.Clamp(targetPlayers, 6, 10);
            DiscussionSeconds = Mathf.Max(5, discussion);
            VoteSeconds = Mathf.Max(5, vote);
            HackSeconds = Mathf.Max(5, hack);
            EnableCpuFill = cpuFill;
        }

        public void ApplyFromScriptable(GameSettings src)
        {
            if (!HasStateAuthority || src == null) return;
            TargetPlayerCount = 6;
            DiscussionSeconds = src.teamProposalSeconds;
            VoteSeconds = src.approvalVoteSeconds;
            HackSeconds = src.hackingSeconds;
            EnableCpuFill = src.enableCpuFill;
        }

        private void OnChanged() => SettingsChanged?.Invoke();
    }
}

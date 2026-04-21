using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// プレイヤーごとにホストが Spawn する NetworkObject。
    /// 表示名・CPU フラグ・ホストフラグを全クライアントで同期する。
    /// InputAuthority は所有プレイヤー、StateAuthority はホスト。
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        [Networked, OnChangedRender(nameof(OnDisplayNameChanged))]
        public NetworkString<_32> DisplayName { get; set; }

        [Networked, OnChangedRender(nameof(OnDisplayNameChanged))]
        public NetworkBool IsCpu { get; set; }

        [Networked, OnChangedRender(nameof(OnDisplayNameChanged))]
        public NetworkBool IsHost { get; set; }

        public static readonly List<NetworkPlayer> All = new();
        public static event Action<NetworkPlayer> OnPlayerSpawned;
        public static event Action<NetworkPlayer> OnPlayerDespawned;
        public static event Action<NetworkPlayer> OnPlayerChanged;

        public PlayerRef OwnerPlayer => Object != null ? Object.InputAuthority : PlayerRef.None;

        public override void Spawned()
        {
            All.Add(this);

            if (HasStateAuthority)
            {
                IsHost = OwnerPlayer == Runner.LocalPlayer && Runner.IsServer;
                IsCpu = false;
            }

            OnPlayerSpawned?.Invoke(this);

            if (HasInputAuthority)
            {
                var sm = FusionSessionManager.Instance;
                var name = sm != null ? sm.LocalPlayerName : "OPERATOR";
                Rpc_SetDisplayName(name);
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            All.Remove(this);
            OnPlayerDespawned?.Invoke(this);
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
        public void Rpc_SetDisplayName(NetworkString<_32> name)
        {
            DisplayName = name;
        }

        /// <summary>
        /// ホストが CPU 用の NetworkPlayer を作る際に呼ぶ。
        /// </summary>
        public void ConfigureAsCpu(string cpuName)
        {
            if (!HasStateAuthority) return;
            IsCpu = true;
            DisplayName = cpuName;
        }

        private void OnDisplayNameChanged()
        {
            OnPlayerChanged?.Invoke(this);
        }
    }
}

using System;
using Fusion;
using ProtocolSingularity.Core;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// 全プレイヤーの表示名・CPU/Host フラグを一元管理するシーン配置 NetworkObject。
    /// プレハブテーブル依存を回避するため、Lobby/InGame シーンに直接配置する想定。
    /// </summary>
    public class PlayerRegistry : NetworkBehaviour
    {
        public struct Entry : INetworkStruct
        {
            public PlayerRef PlayerRef;
            public NetworkString<_32> DisplayName;
            public NetworkBool IsCpu;
            public NetworkBool IsHost;
        }

        public const int Capacity = 10;

        public static PlayerRegistry Instance;
        public static event Action Changed;

        [Networked, Capacity(Capacity), OnChangedRender(nameof(OnEntriesChanged))]
        public NetworkArray<Entry> Entries => default;

        [Networked, OnChangedRender(nameof(OnEntriesChanged))]
        public int Count { get; set; }

        [Networked] public PlayerRef HostPlayer { get; set; }

        public override void Spawned()
        {
            Instance = this;
            if (HasStateAuthority)
            {
                HostPlayer = Runner.LocalPlayer;
                TryRegisterLocal();
            }
            else
            {
                TryRegisterLocal();
            }
            OnEntriesChanged();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        private void TryRegisterLocal()
        {
            var sm = FusionSessionManager.Instance;
            if (sm == null || Runner == null) return;
            var local = Runner.LocalPlayer;
            var name = string.IsNullOrWhiteSpace(sm.LocalPlayerName)
                ? $"OPERATOR_{local.PlayerId:000}"
                : sm.LocalPlayerName;
            Rpc_RegisterSelf(local, name);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void Rpc_RegisterSelf(PlayerRef player, NetworkString<_32> name)
        {
            if (!HasStateAuthority) return;
            bool isHost = player == HostPlayer;
            AddOrUpdate(player, name, false, isHost);
        }

        public void OnPlayerLeft(PlayerRef pr)
        {
            if (!HasStateAuthority) return;
            int idx = FindIndex(pr);
            if (idx < 0) return;
            for (int i = idx; i < Count - 1; i++) Entries.Set(i, Entries[i + 1]);
            Count--;

            // ゲーム進行中にプレイヤーが抜けたら一旦全員ロビーへ戻す (セッション完結型のため)
            var gsm = GameStateManager.Instance;
            if (gsm != null && gsm.Phase != GamePhase.Lobby)
            {
                UnityEngine.Debug.Log($"[PlayerRegistry] Player {pr.PlayerId} left mid-game (phase={gsm.Phase}). Forcing lobby return.");
                gsm.HostReturnToLobby();
            }
        }

        public bool RegisterCpu(string name)
        {
            if (!HasStateAuthority) return false;
            if (Count >= Capacity) return false;

            // 未使用の CPU スロットを探して synthetic PlayerRef を割り当てる
            for (int slot = 0; slot < CpuPlayerRef.MaxCpuCount; slot++)
            {
                var pr = CpuPlayerRef.FromSlot(slot);
                if (FindIndex(pr) >= 0) continue;
                AddOrUpdate(pr, name, true, false);
                return true;
            }
            return false;
        }

        /// <summary>全 CPU エントリを登録簿から除去する。HostReturnToLobby / 新ゲーム開始前の整理用。</summary>
        public void ClearCpus()
        {
            if (!HasStateAuthority) return;
            int writeIdx = 0;
            for (int i = 0; i < Count; i++)
            {
                var e = Entries[i];
                if (e.IsCpu) continue;
                if (writeIdx != i) Entries.Set(writeIdx, e);
                writeIdx++;
            }
            for (int i = writeIdx; i < Count; i++)
                Entries.Set(i, default);
            Count = writeIdx;
        }

        private void AddOrUpdate(PlayerRef pr, NetworkString<_32> name, NetworkBool isCpu, NetworkBool isHost)
        {
            int idx = FindIndex(pr);
            if (idx >= 0)
            {
                var e = Entries[idx];
                e.DisplayName = name;
                e.IsCpu = isCpu;
                e.IsHost = isHost;
                Entries.Set(idx, e);
                return;
            }
            if (Count >= Capacity) return;
            Entries.Set(Count, new Entry
            {
                PlayerRef = pr,
                DisplayName = name,
                IsCpu = isCpu,
                IsHost = isHost
            });
            Count++;
        }

        public int FindIndex(PlayerRef pr)
        {
            for (int i = 0; i < Count; i++)
                if (Entries[i].PlayerRef == pr) return i;
            return -1;
        }

        private void OnEntriesChanged() => Changed?.Invoke();
    }
}
